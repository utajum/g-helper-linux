using HidSharp;
using HidSharp.Reports;

namespace GHelper.Linux.USB;

/// <summary>
/// Linux port of G-Helper's AsusHid.cs.
/// Handles HID device discovery and communication for ASUS AURA keyboards.
///
/// On Linux, HidSharpCore talks to /dev/hidraw* devices - but ONLY those
/// with a USB parent device. I2C-HID devices (like the FA608PP keyboard)
/// are invisible to HidSharp. When no USB device is found, we fall back
/// to HidrawHelper which scans /dev/hidraw* via native ioctl regardless
/// of bus type.
///
/// Requires udev rules for non-root access:
///   SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0b05", MODE="0666"
/// </summary>
public static class AsusHid
{
    public const int ASUS_ID = 0x0B05;
    public const byte INPUT_ID = 0x5A;
    public const byte AURA_ID = 0x5D;

    /// <summary>
    /// Main keyboard / lightbar AURA HID PIDs. Use this when sending keyboard
    /// RGB / lighting commands so we don't accidentally write to the rear-light
    /// device (which speaks the AURA report ID but expects a different protocol).
    /// </summary>
    public static readonly int[] MAIN_AURA_PIDS =
    {
        0x1A30, 0x1854, 0x1869, 0x1866, 0x19B6, 0x1822, 0x1837,
        0x184A, 0x183D, 0x8502, 0x1807, 0x17E0, 0x1ABE,
        0x1B4C, 0x1B6E, 0x1B2C, 0x8854, 0x1CE7, 0x1CE6, 0x1BF2, 0x1BF3,
        // Zenbook Duo detachable keyboards (UX8407)
        0x1CD7, 0x1CD8
    };

    /// <summary>
    /// Rear-light HID PIDs (currently Z13 only).
    /// </summary>
    public static readonly int[] REAR_LIGHT_PIDS = { 0x18C6 };

    /// <summary>
    /// Union of MAIN_AURA_PIDS and REAR_LIGHT_PIDS. Default device set used
    /// when callers don't specify a PID filter (preserves pre-split behaviour).
    /// </summary>
    public static readonly int[] ALL_PIDS = MAIN_AURA_PIDS.Concat(REAR_LIGHT_PIDS).ToArray();

    /// <summary>
    /// Serializes all ASUS HID traffic (Aura, input, Slash). Some models expose
    /// </summary>
    public static readonly object HidLock = new();

    private static HidStream? _auraStream;
    private static int _auraFeatLen;
    private static byte[]? _auraScratch;

    // Track whether we're using I2C-HID fallback (for logging / behavior)
    private static bool? _usingI2cFallback;

    /// <summary>
    /// Lazily open / cache the persistent AURA stream and its feature-report
    /// scratch buffer. Caller is responsible for null-checking
    /// <see cref="_auraStream"/> after.
    /// </summary>
    private static void EnsureAuraStream()
    {
        if (_auraStream != null)
            return;
        _auraStream = FindHidStream(AURA_ID, MAIN_AURA_PIDS);
        if (_auraStream == null)
            return;
        try
        {
            _auraFeatLen = _auraStream.Device.GetMaxFeatureReportLength();
        }
        catch
        {
            _auraFeatLen = 0;
        }
        _auraScratch = _auraFeatLen > 0 ? new byte[_auraFeatLen] : null;
    }

    /// <summary>
    /// Tear down the persistent AURA stream + scratch buffer (e.g. after I/O
    /// error, so the next write re-opens).
    /// </summary>
    private static void DisposeAuraStream()
    {
        try
        { _auraStream?.Dispose(); }
        catch { }
        _auraStream = null;
        _auraFeatLen = 0;
        _auraScratch = null;
    }

    /// <summary>
    /// Whether we are using the native I2C-HID hidraw path instead of HidSharp.
    /// This is the case for keyboards like the FA608PP that connect via I2C.
    /// </summary>
    public static bool UsingI2cHidraw
    {
        get
        {
            if (_usingI2cFallback == null)
            {
                // Trigger evaluation: check if HidSharp sees any USB AURA device
                bool hasUsb = false;
                try
                { hasUsb = FindDevices(AURA_ID).Any(); }
                catch { }
                _usingI2cFallback = !hasUsb && HidrawHelper.HasAsusAuraDevice();
            }
            return _usingI2cFallback.Value;
        }
    }

    /// <summary>
    /// Find all ASUS HID devices that support a given report ID.
    /// This only finds USB-HID devices (HidSharp limitation on Linux).
    /// For I2C-HID devices, use HidrawHelper directly.
    /// </summary>
    /// <param name="reportId">Report ID the device must expose (e.g. AURA_ID, INPUT_ID).</param>
    /// <param name="pids">
    /// Optional product-ID filter. When null, falls back to <see cref="ALL_PIDS"/>
    /// so legacy callers keep their original behaviour. Pass
    /// <see cref="MAIN_AURA_PIDS"/> for keyboard / lightbar lighting and
    /// <see cref="REAR_LIGHT_PIDS"/> for rear-light commands.
    /// </param>
    public static IEnumerable<HidDevice> FindDevices(byte reportId, int[]? pids = null)
    {
        var pidFilter = pids ?? ALL_PIDS;

        IEnumerable<HidDevice> allDevices;
        try
        {
            allDevices = DeviceList.Local.GetHidDevices(ASUS_ID);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Error enumerating HID devices: {ex.Message}");
            yield break;
        }

        var filteredDevices = new List<HidDevice>();
        foreach (var device in allDevices)
        {
            try
            {
                if (pidFilter.Contains(device.ProductID) &&
                    device.CanOpen &&
                    device.GetMaxFeatureReportLength() > 0)
                {
                    filteredDevices.Add(device);
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Error checking HID device {device.ProductID:X}: {ex.Message}");
            }
        }

        var seenUsbParents = new HashSet<string>();

        foreach (var device in filteredDevices)
        {
            bool isValid = false;
            try
            {
                isValid = device.GetReportDescriptor().TryGetReport(ReportType.Feature, reportId, out _);
            }
            catch { }

            if (!isValid)
                continue;

            if (HidrawHelper.IsDuplicateUsbDevice(device.DevicePath, seenUsbParents, "AsusHid.FindDevices"))
                continue;

            yield return device;
        }
    }

    /// <summary>
    /// Find and open an HID stream for the given report ID.
    /// </summary>
    public static HidStream? FindHidStream(byte reportId, int[]? pids = null)
    {
        try
        {
            var devices = FindDevices(reportId, pids);
            if (devices == null)
                return null;

            foreach (var device in devices)
                Helpers.Logger.WriteLine($"HID available: {device.DevicePath} {device.ProductID:X} len={device.GetMaxFeatureReportLength()}");

            return devices.FirstOrDefault()?.Open();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Error accessing HID device: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Write data to INPUT_ID devices via SetFeature.
    /// </summary>
    /// <param name="pids">Optional PID filter, see <see cref="FindDevices"/>.</param>
    public static void WriteInput(byte[] data, string? log = "USB", int[]? pids = null)
    {
        lock (HidLock)
            WriteInputLocked(data, log, pids);
    }

    private static void WriteInputLocked(byte[] data, string? log, int[]? pids)
    {
        // Try HidSharp (USB) first
        bool wroteAny = false;
        foreach (var device in FindDevices(INPUT_ID, pids))
        {
            try
            {
                using var stream = device.Open();
                var payload = new byte[device.GetMaxFeatureReportLength()];
                Array.Copy(data, payload, Math.Min(data.Length, payload.Length));
                stream.SetFeature(payload);
                wroteAny = true;
                if (log != null)
                    Helpers.Logger.WriteLine($"{log} {device.ProductID:X}|{device.GetMaxFeatureReportLength()}: {BitConverter.ToString(data)}");
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Error setting feature {device.DevicePath}: {BitConverter.ToString(data)} {ex.Message}");
            }
        }

        // Fallback: I2C-HID via native hidraw
        if (!wroteAny && HidrawHelper.HasAsusAuraDevice())
        {
            HidrawHelper.WriteAll(INPUT_ID, data, log);
        }
    }

    /// <summary>
    /// Write a single message to all AURA_ID devices.
    /// </summary>
    /// <param name="pids">Optional PID filter, see <see cref="FindDevices"/>.</param>
    public static void Write(byte[] data, string? log = "USB", int[]? pids = null)
    {
        Write(new List<byte[]> { data }, log, pids);
    }

    /// <summary>
    /// Write multiple messages to all AURA_ID devices.
    /// Falls back to native hidraw for I2C-HID devices.
    /// </summary>
    /// <param name="pids">Optional PID filter, see <see cref="FindDevices"/>.</param>
    public static void Write(List<byte[]> dataList, string? log = "USB", int[]? pids = null)
    {
        lock (HidLock)
            WriteLocked(dataList, log, pids);
    }

    private static void WriteLocked(List<byte[]> dataList, string? log, int[]? pids)
    {
        bool wroteToAny = false;

        // Try HidSharp (USB-HID) path first
        foreach (var device in FindDevices(AURA_ID, pids))
        {
            try
            {
                using var stream = device.Open();
                // Some keyboard controllers (e.g. G533QS firmware) silently
                // discard HID output reports shorter than the declared OUTPUT
                // report size. Pad every write to the device's max length so
                // short messages (AuraMessage 17B, SET 5B, APPLY 2B) are not
                // silently dropped. Trailing zeros match the HID descriptor
                // layout and are accepted by older controllers too.
                int maxLen = device.GetMaxOutputReportLength();
                foreach (var data in dataList)
                {
                    try
                    {
                        byte[] padded;
                        if (data.Length >= maxLen)
                        {
                            padded = data;
                        }
                        else
                        {
                            padded = new byte[maxLen];
                            Array.Copy(data, padded, data.Length);
                        }
                        stream.Write(padded);
                        wroteToAny = true;
                        if (log != null)
                            Helpers.Logger.WriteLine($"{log} {device.ProductID:X}: {BitConverter.ToString(data)}");
                    }
                    catch (Exception ex)
                    {
                        if (log != null)
                            Helpers.Logger.WriteLine($"Error writing {log} {device.ProductID:X}: {ex.Message} {BitConverter.ToString(data)}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                    Helpers.Logger.WriteLine($"Error opening {log} {device.ProductID:X}: {ex.Message}");
            }
        }

        // Fallback: I2C-HID via native hidraw (for devices like FA608PP)
        if (!wroteToAny)
        {
            if (HidrawHelper.WriteAll(AURA_ID, dataList, log))
            {
                if (log != null)
                    Helpers.Logger.WriteLine($"{log} [I2C-HID]: wrote {dataList.Count} messages via hidraw");
            }
        }
    }

    /// <summary>
    /// Send <paramref name="data"/> as a SetFeature report on the AURA stream.
    /// <para>Direct-RGB / per-key paths in modern AURA firmware prefer the
    /// feature-report transport over output reports - it's the same wire format
    /// asusctl/Armoury Crate use. Falls back to <see cref="HidrawHelper.WriteAll"/>
    /// for I2C-HID and unconditionally on stream open failure.</para>
    /// <para>The buffer is padded to the device's max feature-report length
    /// (typically 64 bytes for AURA endpoints) before submission.</para>
    /// </summary>
    public static void SetFeatureAura(byte[] data, bool retry = true)
    {
        lock (HidLock)
            SetFeatureAuraLocked(data, retry);
    }

    private static void SetFeatureAuraLocked(byte[] data, bool retry)
    {
        // I2C-HID path: HidrawHelper.WriteAll already uses HIDIOCSFEATURE
        if (UsingI2cHidraw)
        {
            HidrawHelper.WriteAll(AURA_ID, data, null);
            return;
        }

        EnsureAuraStream();
        if (_auraStream == null)
        {
            // Last resort: try I2C-HID even on Strix laptops if HidSharp lost the stream
            if (HidrawHelper.HasAsusAuraDevice())
            {
                HidrawHelper.WriteAll(AURA_ID, data, null);
                return;
            }
            Helpers.Logger.WriteLine("Aura stream not found (SetFeature)");
            return;
        }

        try
        {
            byte[] payload = data;
            if (_auraScratch != null && data.Length < _auraFeatLen)
            {
                Array.Clear(_auraScratch, 0, _auraFeatLen);
                Array.Copy(data, _auraScratch, data.Length);
                payload = _auraScratch;
            }
            _auraStream.SetFeature(payload);
        }
        catch (Exception ex)
        {
            int n = Math.Min(16, data.Length);
            Helpers.Logger.WriteLine(
                $"Error SetFeature on Aura HID: {ex.Message} {BitConverter.ToString(data, 0, n)}");
            DisposeAuraStream();
            if (retry)
                SetFeatureAura(data, false);
        }
    }

    /// <summary>
    /// Send the AURA capability query (SetFeature) and read back the response
    /// (GetFeature). Returns the 64-byte response or <c>null</c> on failure.
    /// <para>Linux delegates to <see cref="HidrawHelper.QueryAuraCapabilities"/>
    /// which uses raw hidraw <c>HIDIOCSFEATURE</c>/<c>HIDIOCGFEATURE</c> ioctls -
    /// HidSharp on Linux can't enumerate I2C-HID devices (e.g. ASUS TUF FA608PP),
    /// so the raw ioctl path is the universal way to reach AURA-capable nodes.
    /// The query format is fixed: <c>[AURA_ID, 0x05, 0x20, 0x31, 0x00, last]</c>;
    /// only the trailing byte varies between firmware revisions (0x20 by default,
    /// 0x1A on older Armoury Crate variants).</para>
    /// </summary>
    public static byte[]? AuraProbe(byte[] query, string log = "Aura Probe")
    {
        // Query format: [AURA_ID, 0x05, 0x20, 0x31, 0x00, last_byte].
        // We only need the last byte for HidrawHelper - it builds the query itself
        // (writes report ID into byte 0).
        byte lastByte = query.Length >= 6 ? query[5] : (byte)0x20;
        return HidrawHelper.QueryAuraCapabilities(lastByte);
    }

    /// <summary>
    /// Check if any ASUS AURA HID device is available (USB or I2C-HID).
    /// Returns false on Lenovo hardware where VID 0x0B05 devices are
    /// ITE keyboard controllers, not AURA endpoints.
    /// </summary>
    public static bool IsAvailable()
    {
        if (Helpers.AppConfig.IsLenovoDevice())
            return false;

        try
        {
            // Check USB-HID via HidSharp
            if (FindDevices(AURA_ID).Any())
                return true;
        }
        catch { }

        // Check I2C-HID via native hidraw
        try
        {
            return HidrawHelper.HasAsusAuraDevice();
        }
        catch { return false; }
    }
}
