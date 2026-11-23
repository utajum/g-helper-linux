using GHelper.Linux.Helpers;
using HidSharp;

namespace GHelper.Linux.Peripherals.Logitech.HidPP;

/// <summary>
/// Represents a single HID++ device (direct or receiver-connected) and provides
/// low-level message framing, request/response matching, feature discovery, and
/// typed feature requests for HID++ 1.0 and 2.0 protocols.
/// </summary>
public sealed class HidPPDevice : IDisposable
{
    public const ushort LOGITECH_VID = 0x046D;

    private const byte SHORT_REPORT_ID = 0x10;
    private const byte LONG_REPORT_ID = 0x11;

    private const int SHORT_MESSAGE_SIZE = 7;
    private const int LONG_MESSAGE_SIZE = 20;

    private const int MAX_READ_SIZE = 32;

    // Notifications use sw_id=0, so any reply with sw_id matching this value
    // is a response to our request.
    private const byte SW_ID = 0x0B;

    private const byte DJ_REPORT_ID = 0x20;

    private const byte ERROR_MSG_10 = 0x8F; // HID++ 1.0 error sub-ID
    private const byte ERROR_MSG_20 = 0xFF; // HID++ 2.0 error sub-ID

    private const int BT_REQUEST_TIMEOUT_MS = 4000;

    private static readonly Random _rng = new();

    private readonly HidDevice? _device;
    private Stream? _stream;
    private readonly object _ioLock = new();

    // Set on first write/read IOException (ENODEV, pipe broken, etc.).
    // Any I/O failure means the hidraw node is gone and the handle is dead.
    private bool _dead;

    /// <summary>True after the first I/O failure. The handle is dead and
    /// the device should be disconnected and re-detected.</summary>
    public bool IsDead => _dead;

    /// <summary>True when the device was opened via raw hidraw (BT).</summary>
    public bool IsRawStream => _device is null && _stream is not null;

    /// <summary>Full HID device path (e.g. /dev/hidraw3).</summary>
    public string DevicePath { get; }

    /// <summary>
    /// Device index on the receiver. 0xFF for direct (non-receiver) devices,
    /// 1-6 for devices paired to a Unifying/Bolt receiver.
    /// </summary>
    public byte DeviceIndex { get; }

    /// <summary>
    /// Detected HID++ protocol version. 1.0 for HID++ 1.0 devices, 2.0+ for
    /// modern devices. Set after calling <see cref="Ping"/>.
    /// </summary>
    public float ProtocolVersion { get; private set; }

    private readonly Dictionary<ushort, byte> _featureIndex = new();
    private readonly Dictionary<ushort, byte> _featureVersion = new();

    /// <summary>Discovered feature-ID to feature-index mapping.</summary>
    public IReadOnlyDictionary<ushort, byte> Features => _featureIndex;

    /// <summary>Gets HID++ reports that are not replies (receiver notifications); null drops them.</summary>
    public Action<byte[]>? Unsolicited { get; set; }

    /// <summary>Creates a wrapper for a HidSharp-enumerated device (USB).</summary>
    public HidPPDevice(HidDevice device, byte deviceIndex = 0xFF)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        DevicePath = device.DevicePath;
        DeviceIndex = deviceIndex;
    }

    /// <summary>Creates a wrapper for a raw hidraw path (BT or other non-USB).</summary>
    public HidPPDevice(string hidrawPath, byte deviceIndex = 0xFF)
    {
        _device = null;
        DevicePath = hidrawPath;
        DeviceIndex = deviceIndex;
    }

    /// <summary>Opens the HID stream for reading and writing.</summary>
    public void Open()
    {
        if (_device is not null)
        {
            var config = new OpenConfiguration();
            config.SetOption(OpenOption.Interruptible, true);
            config.SetOption(OpenOption.Exclusive, false);
            config.SetOption(OpenOption.Priority, 10);

            _stream = _device.Open(config);
            _stream.ReadTimeout = 2000;
            _stream.WriteTimeout = 2000;
        }
        else
        {
            var raw = new HidrawStream(DevicePath);
            raw.ReadTimeout = 2000;
            raw.WriteTimeout = 2000;
            _stream = raw;
        }
    }

    /// <summary>
    /// Sends a raw HID++ message and waits for the matching response.
    /// </summary>
    /// <param name="data">
    /// Payload bytes starting from the sub-ID / feature-index position (bytes [2..]).
    /// The report-ID and device-index are prepended automatically.
    /// </param>
    /// <param name="longMessage">Force a long (20-byte) report.</param>
    /// <param name="timeoutMs">Read timeout in milliseconds.</param>
    /// <returns>
    /// Response payload starting from byte [2] (sub-ID + address + params),
    /// or <c>null</c> on error/timeout.
    /// </returns>
    public byte[]? Request(byte[] data, bool longMessage = false, int timeoutMs = 0)
    {
        ArgumentNullException.ThrowIfNull(data);

        lock (_ioLock)
        {
            if (_stream is null)
                return null;

            // BT devices require long messages and longer timeouts.
            bool isBt = IsRawStream;
            bool useLong = longMessage || isBt || data.Length > (SHORT_MESSAGE_SIZE - 2);
            byte reportId = useLong ? LONG_REPORT_ID : SHORT_REPORT_ID;
            int msgSize = useLong ? LONG_MESSAGE_SIZE : SHORT_MESSAGE_SIZE;
            if (timeoutMs <= 0)
                timeoutMs = isBt ? BT_REQUEST_TIMEOUT_MS : 2000;

            // [reportId, deviceIndex, data..., padding]
            var msg = new byte[msgSize];
            msg[0] = reportId;
            msg[1] = DeviceIndex;
            int copyLen = Math.Min(data.Length, msgSize - 2);
            Buffer.BlockCopy(data, 0, msg, 2, copyLen);

            byte reqByte0 = data.Length > 0 ? data[0] : (byte)0;
            byte reqByte1 = data.Length > 1 ? data[1] : (byte)0;

            // Drain stale data before writing.
            DrainInput();

            try
            {
                _stream.Write(msg);
            }
            catch (IOException ex)
            {
                _dead = true;
                Logger.WriteLine($"HidPPDevice: write failed on {DevicePath}: {ex.Message}");
                return null;
            }

            // Read until matching response or wall-clock timeout.
            long deadline = Environment.TickCount64 + timeoutMs;
            int previousTimeout = _stream.ReadTimeout;
            _stream.ReadTimeout = timeoutMs;
            try
            {
                while (Environment.TickCount64 < deadline)
                {
                    var buf = new byte[MAX_READ_SIZE];
                    int bytesRead;
                    try
                    {
                        bytesRead = _stream.Read(buf, 0, buf.Length);
                    }
                    catch (TimeoutException) { return null; }
                    catch (IOException) { _dead = true; return null; }

                    if (bytesRead == 0)
                        return null; // poll timeout

                    // Skip non-HID++ reports (mouse input, keyboard, etc.).
                    if (!IsHidppReport(buf[0], bytesRead))
                        continue;

                    byte devIdx = buf[1];

                    // BT devices may respond with devnumber XOR 0xFF.
                    if (devIdx != DeviceIndex && devIdx != (byte)(DeviceIndex ^ 0xFF))
                    {
                        Unsolicited?.Invoke(buf[..bytesRead]);
                        continue;
                    }

                    byte subId = buf[2];
                    byte addr = buf[3];

                    // HID++ 1.0 error: report 0x10, sub-ID 0x8F
                    if (buf[0] == SHORT_REPORT_ID && subId == ERROR_MSG_10
                        && addr == reqByte0 && buf[4] == reqByte1)
                    {
                        return null;
                    }

                    // HID++ 2.0 error: sub-ID 0xFF
                    if (subId == ERROR_MSG_20 && addr == reqByte0 && buf[4] == reqByte1)
                    {
                        return null;
                    }

                    // Match: sub-ID and address match the request bytes.
                    if (subId == reqByte0 && addr == reqByte1)
                    {
                        // Receiver 0xB5 replies must also echo the sub-register.
                        if (reqByte0 == 0x83 && reqByte1 == 0xB5 && data.Length > 2 && buf[4] != data[2])
                            continue;
                        int respSize = (buf[0] == LONG_REPORT_ID)
                            ? LONG_MESSAGE_SIZE - 2
                            : SHORT_MESSAGE_SIZE - 2;
                        int payloadLen = Math.Min(respSize, bytesRead - 2);
                        var result = new byte[payloadLen];
                        Buffer.BlockCopy(buf, 2, result, 0, payloadLen);
                        return result;
                    }

                    // Unrelated HID++ message (notification).
                    Unsolicited?.Invoke(buf[..bytesRead]);
                    deadline = Environment.TickCount64 + timeoutMs;
                }
            }
            finally
            {
                _stream.ReadTimeout = previousTimeout;
            }

            return null;
        }
    }

    /// <summary>Returns true if the report ID is HID++ (0x10, 0x11, 0x20, 0x21) and the
    /// size matches the expected length. Non-HID++ reports (mouse input etc.) are skipped.</summary>
    private static bool IsHidppReport(byte reportId, int length)
    {
        return reportId switch
        {
            SHORT_REPORT_ID => length >= SHORT_MESSAGE_SIZE,
            LONG_REPORT_ID => length >= LONG_MESSAGE_SIZE,
            DJ_REPORT_ID => length >= 15,
            0x21 => length >= MAX_READ_SIZE,
            _ => false,
        };
    }

    /// <summary>Drains stale data from the input buffer.</summary>
    private void DrainInput()
    {
        if (_stream is null)
            return;
        int saved = _stream.ReadTimeout;
        _stream.ReadTimeout = 10;
        try
        {
            var buf = new byte[MAX_READ_SIZE];
            for (int i = 0; i < 8; i++)
            {
                int n;
                try
                { n = _stream.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n == 0)
                    break;
                if (IsHidppReport(buf[0], n))
                    Unsolicited?.Invoke(buf[..n]);
            }
        }
        finally
        {
            _stream.ReadTimeout = saved;
        }
    }

    /// <summary>Reads the next HID++ report (whole report), or null if none comes within the timeout.</summary>
    public byte[]? ReadReport(int timeoutMs)
    {
        lock (_ioLock)
        {
            if (_stream is null)
                return null;
            int saved = _stream.ReadTimeout;
            _stream.ReadTimeout = timeoutMs;
            try
            {
                var buf = new byte[MAX_READ_SIZE];
                int n;
                try
                { n = _stream.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { return null; }
                catch (IOException) { _dead = true; return null; }
                return n > 0 && IsHidppReport(buf[0], n) ? buf[..n] : null;
            }
            finally
            {
                _stream.ReadTimeout = saved;
            }
        }
    }

    /// <summary>
    /// Pings the device to determine its HID++ protocol version.
    /// Sends ROOT.GetProtocolVersion and parses the major.minor reply.
    /// Returns 1.0 if the device responds with INVALID_SUB_ID (HID++ 1.0 device).
    /// </summary>
    /// <returns>Protocol version as a float (e.g. 1.0, 2.0, 4.5), or 0 on failure.</returns>
    public float Ping()
    {
        byte funcByte = (byte)(0x10 | SW_ID);
        byte pingMark = (byte)(_rng.Next(1, 256));

        lock (_ioLock)
        {
            if (_stream is null)
                return 0;

            bool isBt = IsRawStream;

            // BT devices need long messages; USB can use short.
            byte reportId = isBt ? LONG_REPORT_ID : SHORT_REPORT_ID;
            int msgSize = isBt ? LONG_MESSAGE_SIZE : SHORT_MESSAGE_SIZE;
            var msg = new byte[msgSize];
            msg[0] = reportId;
            msg[1] = DeviceIndex;
            msg[2] = 0x00;        // feature index (ROOT)
            msg[3] = funcByte;    // function | SW_ID
            msg[4] = 0x00;
            msg[5] = 0x00;
            msg[6] = pingMark;

            DrainInput();

            try
            {
                _stream.Write(msg);
            }
            catch (IOException ex)
            {
                _dead = true;
                Logger.WriteLine($"HidPPDevice.Ping: write failed: {ex.Message}");
                return 0;
            }

            long deadline = Environment.TickCount64 + BT_REQUEST_TIMEOUT_MS;
            int previousTimeout = _stream.ReadTimeout;
            _stream.ReadTimeout = BT_REQUEST_TIMEOUT_MS;
            try
            {
                while (Environment.TickCount64 < deadline)
                {
                    var buf = new byte[MAX_READ_SIZE];
                    int bytesRead;
                    try
                    {
                        bytesRead = _stream.Read(buf, 0, buf.Length);
                    }
                    catch (TimeoutException) { return 0; }
                    catch (IOException) { _dead = true; return 0; }

                    if (bytesRead == 0)
                        return 0;

                    if (!IsHidppReport(buf[0], bytesRead))
                        continue;

                    byte devIdx = buf[1];
                    if (devIdx != DeviceIndex && devIdx != (byte)(DeviceIndex ^ 0xFF))
                        continue;

                    byte subId = buf[2];
                    byte addr = buf[3];

                    // HID++ 2.0+ response: feature 0x00, function matches, ping mark matches.
                    if (subId == 0x00 && addr == funcByte && buf[6] == pingMark)
                    {
                        ProtocolVersion = buf[4] + buf[5] / 10.0f;
                        return ProtocolVersion;
                    }

                    // HID++ 1.0 error: 0x8F with matching request bytes.
                    if (subId == ERROR_MSG_10 && buf[3] == 0x00 && buf[4] == funcByte)
                    {
                        if (buf[5] == (byte)Hidpp10Error.InvalidSubIdCommand)
                        {
                            ProtocolVersion = 1.0f;
                            return 1.0f;
                        }
                        return 0;
                    }
                }
            }
            finally
            {
                _stream.ReadTimeout = previousTimeout;
            }

            Logger.WriteLine($"HidPPDevice.Ping: no response on {DevicePath}");
            return 0;
        }
    }

    /// <summary>
    /// Queries the device to discover all supported features and populates
    /// the <see cref="Features"/> cache. Requires HID++ 2.0+.
    /// </summary>
    public void DiscoverFeatures()
    {
        _featureIndex.Clear();
        _featureVersion.Clear();
        _featureIndex[Feature.ROOT] = 0;

        // Find the index of FEATURE_SET (0x0001) via ROOT.
        byte[]? fsReply = FeatureRequest(Feature.ROOT, 0x00,
            (byte)(Feature.FEATURE_SET >> 8),
            (byte)(Feature.FEATURE_SET & 0xFF));
        if (fsReply is null || fsReply.Length < 3)
            return;

        byte fsIndex = fsReply[2]; // response: [subId, addr, featureIndex, ...]
        if (fsIndex == 0)
            return;

        _featureIndex[Feature.FEATURE_SET] = fsIndex;

        // FEATURE_SET.GetCount (function 0x00)
        byte[]? countReply = FeatureRequest(Feature.FEATURE_SET, 0x00);
        if (countReply is null || countReply.Length < 3)
            return;

        int count = countReply[2]; // response: [subId, addr, count, ...]

        // FEATURE_SET.GetFeatureID (function 0x10)
        for (byte i = 1; i <= count; i++)
        {
            byte[]? entry = FeatureRequest(Feature.FEATURE_SET, 0x10, i);
            if (entry is null || entry.Length < 4)
                continue;

            // Response: [subId, addr, featureIdHigh, featureIdLow, type, version, ...]
            ushort featureId = (ushort)((entry[2] << 8) | entry[3]);
            if (featureId != 0x0000)
            {
                _featureIndex[featureId] = i;
                if (entry.Length >= 6)
                    _featureVersion[featureId] = entry[5];
            }
        }

        Logger.WriteLine($"HidPPDevice: discovered {_featureIndex.Count} features on {DevicePath}");
    }

    /// <summary>
    /// Returns the feature index for a given feature ID, or -1 if not supported.
    /// </summary>
    public int GetFeatureIndex(ushort featureId)
    {
        return _featureIndex.TryGetValue(featureId, out byte idx) ? idx : -1;
    }

    /// <summary>Feature version from FEATURE_SET discovery, or 0 when unknown.</summary>
    public int GetFeatureVersion(ushort featureId)
    {
        return _featureVersion.TryGetValue(featureId, out byte v) ? v : 0;
    }

    /// <summary>
    /// Sends a HID++ 2.0 feature request. Looks up the feature index from the
    /// cache (or queries ROOT for it), constructs the request, and calls
    /// <see cref="Request"/>.
    /// </summary>
    /// <param name="featureId">The 16-bit feature ID (e.g. Feature.BATTERY_STATUS).</param>
    /// <param name="function">
    /// Function ID (high nibble). Typically 0x00, 0x10, 0x20, etc.
    /// </param>
    /// <param name="args">Optional parameter bytes.</param>
    /// <returns>Full response payload from byte [2] onward, or null.</returns>
    public byte[]? FeatureRequest(ushort featureId, byte function = 0x00, params byte[] args)
    {
        int idx = GetFeatureIndex(featureId);
        if (idx < 0)
        {
            if (featureId == Feature.ROOT)
            {
                idx = 0;
            }
            else
            {
                byte[]? rootReply = Request([
                    0x00, // ROOT feature index
                    (byte)(0x00 | SW_ID), // ROOT.GetFeature function + SW_ID
                    (byte)(featureId >> 8),
                    (byte)(featureId & 0xFF)
                ]);
                if (rootReply is null || rootReply.Length < 3 || rootReply[2] == 0)
                    return null;

                idx = rootReply[2];
                _featureIndex[featureId] = (byte)idx;
            }
        }

        // [featureIndex, (function | SW_ID), args...]
        byte funcByte = (byte)((function & 0xF0) | SW_ID);
        var data = new byte[2 + args.Length];
        data[0] = (byte)idx;
        data[1] = funcByte;
        if (args.Length > 0)
            Buffer.BlockCopy(args, 0, data, 2, args.Length);

        return Request(data);
    }

    /// <summary>
    /// Reads a HID++ 1.0 register. Sends sub-ID 0x81 (short) or 0x83 (long).
    /// </summary>
    /// <param name="register">Register address.</param>
    /// <param name="args">Optional parameter bytes (up to 3).</param>
    /// <returns>Response payload or null.</returns>
    public byte[]? ReadRegister(byte register, params byte[] args)
    {
        byte subId = (byte)(args.Length > 3 ? 0x83 : 0x81);
        var data = new byte[2 + args.Length];
        data[0] = subId;
        data[1] = register;
        if (args.Length > 0)
            Buffer.BlockCopy(args, 0, data, 2, args.Length);

        return Request(data, longMessage: args.Length > 3);
    }

    /// <summary>
    /// Reads a HID++ 1.0 long register (sub-ID 0x83) with up to 3 parameter bytes.
    /// Returns [0x83, register, data...] or null.
    /// </summary>
    public byte[]? ReadLongRegister(byte register, params byte[] args)
    {
        var data = new byte[2 + Math.Min(args.Length, 3)];
        data[0] = 0x83;
        data[1] = register;
        Array.Copy(args, 0, data, 2, data.Length - 2);
        return Request(data);
    }

    /// <summary>
    /// Writes a HID++ 1.0 register. Sends sub-ID 0x80 (short) or 0x82 (long).
    /// </summary>
    /// <param name="register">Register address.</param>
    /// <param name="args">Bytes to write.</param>
    /// <returns>True if the write appeared successful.</returns>
    public bool WriteRegister(byte register, params byte[] args)
    {
        byte subId = (byte)(args.Length > 3 ? 0x82 : 0x80);
        var data = new byte[2 + args.Length];
        data[0] = subId;
        data[1] = register;
        if (args.Length > 0)
            Buffer.BlockCopy(args, 0, data, 2, args.Length);

        return Request(data, longMessage: args.Length > 3) is not null;
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
