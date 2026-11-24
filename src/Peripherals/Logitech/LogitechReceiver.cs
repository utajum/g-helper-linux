using System.Collections.Concurrent;
using GHelper.Linux.Helpers;
using GHelper.Linux.Peripherals.Logitech.HidPP;
using HidSharp;

namespace GHelper.Linux.Peripherals.Logitech;

/// <summary>
/// Represents a plugged-in Logitech wireless receiver. Opens the receiver's
/// HID device, queries paired-device registers via HID++ 1.0, creates
/// <see cref="LogitechReceiverMouse"/> instances for each paired mouse,
/// and pairs or unpairs devices.
/// </summary>
public class LogitechReceiver : IDisposable
{
    private const byte REG_NOTIFICATIONS = 0x00;
    private const byte REG_CONNECTION = 0x02;
    private const byte REG_PAIRING = 0xB2;
    private const byte REG_PAIRING_INFO = 0xB5;
    private const byte REG_BOLT_DISCOVERY = 0xC0;
    private const byte REG_BOLT_PAIRING = 0xC1;

    private static int _pairing;

    private readonly ReceiverEntry _info;
    private HidDevice? _hidDevice;
    private HidPPDevice? _device;

    /// <summary>True while a pairing runs; background receiver scans wait, so no other register I/O reaches the receiver.</summary>
    public static bool PairingActive => Volatile.Read(ref _pairing) > 0;

    /// <summary>A paired device as the receiver lists it; Kind uses the receiver vocabulary (1 keyboard, 2 mouse).</summary>
    public record Slot(byte Index, string Name, ushort Wpid, int Kind);

    public enum PairError { None, Timeout, NotSupported, TooMany, Failed, Cancelled }

    /// <summary>Pair result; Index and Name are set on success.</summary>
    public record PairResult(PairError Error, byte Index = 0, string? Name = null);

    /// <summary>Bolt progress: the found device, and the passkey to type (Typed) or to click.</summary>
    public record PairStep(string? Name, string? Passkey, bool Typed);

    public LogitechReceiver(ReceiverEntry info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>Returns true if the receiver's HID device is present on the system.</summary>
    public bool IsConnected()
    {
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(HidPPDevice.LOGITECH_VID, _info.ProductId))
                return true;
        }
        catch
        {
        }
        return false;
    }

    /// <summary>Opens the receiver's HID++ node. False if it is missing or cannot be opened.</summary>
    public bool Open()
    {
        _hidDevice = FindReceiverHidDevice();
        if (_hidDevice is null)
        {
            Logger.WriteLine($"LogitechReceiver({_info.Name}): no HID device found");
            return false;
        }

        _device?.Dispose();
        _device = new HidPPDevice(_hidDevice, 0xFF);
        try
        {
            _device.Open();
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechReceiver({_info.Name}): open failed - {ex.Message}");
            _device.Dispose();
            _device = null;
            return false;
        }
    }

    /// <summary>Reads all paired slots. Call <see cref="Open"/> first.</summary>
    public List<Slot> ReadSlots()
    {
        var slots = new List<Slot>();
        for (byte devIdx = 1; devIdx <= _info.MaxDevices; devIdx++)
        {
            try
            {
                if (GetPairedDevice(devIdx) is { } p)
                    slots.Add(new Slot(devIdx, p.name, p.wpid, p.kind));
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechReceiver: error reading device index {devIdx}: {ex.Message}");
            }
        }
        return slots;
    }

    /// <summary>
    /// Opens the receiver and enumerates all paired devices.
    /// For each paired mouse, creates a <see cref="LogitechReceiverMouse"/>.
    /// </summary>
    public List<LogitechReceiverMouse> DiscoverDevices()
    {
        var mice = new List<LogitechReceiverMouse>();
        if (!Open())
            return mice;

        foreach (var slot in ReadSlots())
        {
            // Receiver pairing kind: 1 keyboard, 2 mouse, 8 trackball, 9 touchpad.
            if (slot.Kind is not (1 or 2 or 8 or 9))
            {
                Logger.WriteLine($"LogitechReceiver: skipping device '{slot.Name}' (kind={slot.Kind})");
                continue;
            }

            Logger.WriteLine($"LogitechReceiver: found paired device '{slot.Name}' (kind={slot.Kind}, wpid=0x{slot.Wpid:X4}) at index {slot.Index}");
            mice.Add(new LogitechReceiverMouse(_hidDevice!, slot.Index, slot.Name, slot.Wpid));
        }

        return mice;
    }

    /// <summary>
    /// Pairs a new device. Unifying-style receivers open the pairing lock (register 0xB2);
    /// Bolt runs discovery (0xC0), then pairs the found device (0xC1) with a passkey.
    /// Blocks until done, so call it off the UI thread. Call <see cref="Open"/> first.
    /// </summary>
    public PairResult Pair(int seconds, Action<PairStep> progress, CancellationToken ct)
    {
        if (_device is null)
            return new PairResult(PairError.Failed);

        Interlocked.Increment(ref _pairing);
        int? oldFlags = null;
        var queue = new ConcurrentQueue<byte[]>();
        try
        {
            oldFlags = EnableNotifications();
            // Register 0x02 byte 2: pairings left plus 5; below 5 means no limit.
            if (_device.ReadRegister(REG_CONNECTION) is { Length: >= 5 } conn)
                Logger.WriteLine($"LogitechReceiver({_info.Name}): pair start, {conn[3]} paired, pairings left {(conn[4] >= 5 ? conn[4] - 5 : -1)}");
            _device.Unsolicited = queue.Enqueue;
            var result = _info.Kind == ReceiverKind.Bolt
                ? PairBolt(seconds, queue, progress, ct)
                : PairUnifying(seconds, queue, ct);
            Logger.WriteLine($"LogitechReceiver({_info.Name}): pair result {result.Error}, index {result.Index}");
            return result;
        }
        finally
        {
            _device.Unsolicited = null;
            if (oldFlags is { } flags)
                WriteNotificationFlags(flags);
            Interlocked.Decrement(ref _pairing);
        }
    }

    /// <summary>Removes the device in a slot. Call <see cref="Open"/> first.</summary>
    public bool Unpair(byte index)
    {
        if (_device is null)
            return false;
        bool ok = _info.Kind == ReceiverKind.Bolt
            ? _device.WriteRegister(REG_BOLT_PAIRING, [0x03, index, 0, 0, 0, 0, 0, 0, 0, 0])
            : _device.WriteRegister(REG_PAIRING, 0x03, index, 0x00);
        Logger.WriteLine($"LogitechReceiver({_info.Name}): unpair index {index} {(ok ? "OK" : "failed")}");
        return ok;
    }

    private PairResult PairUnifying(int seconds, ConcurrentQueue<byte[]> queue, CancellationToken ct)
    {
        var session = new HidPPDecode.UnifyingPairing(ReadSlots().Select(s => s.Index).ToHashSet());
        if (!_device!.WriteRegister(REG_PAIRING, 0x01, 0x00, (byte)seconds))
            return new PairResult(PairError.Failed);

        try
        {
            // The 0x4A open notice must come within 2 s; then the lock stays open for the given time.
            long deadline = Environment.TickCount64 + 2000;
            bool open = false, closed = false;
            while (!session.Done && Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
            {
                if (NextReport(queue) is { } r)
                    session.Feed(r);
                if (session.Open && !open)
                {
                    open = true;
                    deadline = Environment.TickCount64 + (seconds + 5) * 1000L;
                }
                if (session.Closed && !closed)
                {
                    // The new device notice can come just after the lock closes.
                    closed = true;
                    deadline = Math.Min(deadline, Environment.TickCount64 + 1500);
                }
            }
        }
        finally
        {
            _device.WriteRegister(REG_PAIRING, 0x02, 0x00, 0x00);
        }

        if (session.Error != 0)
            return new PairResult(LockError(session.Error));
        if (!session.Open)
        {
            Logger.WriteLine($"LogitechReceiver({_info.Name}): the pairing lock did not open");
            return new PairResult(ct.IsCancellationRequested ? PairError.Cancelled : PairError.Failed);
        }
        byte index = session.Slot;
        if (index == 0)
            return new PairResult(ct.IsCancellationRequested ? PairError.Cancelled : PairError.Timeout);
        return new PairResult(PairError.None, index, ReadSlots().FirstOrDefault(s => s.Index == index)?.Name);
    }

    private PairResult PairBolt(int seconds, ConcurrentQueue<byte[]> queue, Action<PairStep> progress, CancellationToken ct)
    {
        if (!_device!.WriteRegister(REG_BOLT_DISCOVERY, (byte)seconds, 0x01))
            return new PairResult(PairError.Failed);

        int? counter = null;
        byte[]? address = null;
        byte auth = 0;
        int kind = 0;
        string? name = null;
        bool pairing = false, paired = false;
        try
        {
            // Discovery time plus time to enter the passkey.
            long deadline = Environment.TickCount64 + (seconds * 2 + 10) * 1000L;
            while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
            {
                var r = NextReport(queue);
                if (r is null)
                    continue;
                if (HidPPDecode.DecodeBoltDiscovery(r) is { } d && !pairing)
                {
                    counter ??= d.Counter;
                    if (d.Counter != counter)
                        continue;
                    if (d.Address is not null)
                        (address, auth, kind) = (d.Address, d.Auth, d.Kind);
                    name = d.Name ?? name;
                    if (address is null || name is null)
                        continue;
                    pairing = true;
                    progress(new PairStep(name, null, false));
                    // Entropy 20 for keyboards, 10 for other devices.
                    byte[] args = [0x01, 0x00, .. address, auth, (byte)(kind == 1 ? 20 : 10)];
                    if (!_device.WriteRegister(REG_BOLT_PAIRING, args))
                        return new PairResult(PairError.Failed);
                }
                else if (HidPPDecode.DecodeDiscoveryStatus(r) is { discovering: false } ds && !pairing)
                    return new PairResult(ds.error != 0 ? BoltError(ds.error) : PairError.Timeout);
                else if (HidPPDecode.DecodePasskey(r) is { } passkey)
                    progress(new PairStep(name, passkey, (auth & 0x01) != 0));
                else if (HidPPDecode.DecodePairingStatus(r) is { } ps)
                {
                    if (ps.error != 0)
                        return new PairResult(BoltError(ps.error));
                    if (ps.state == 2)
                    {
                        paired = true;
                        return new PairResult(PairError.None, ps.slot, name);
                    }
                }
            }
            return new PairResult(ct.IsCancellationRequested ? PairError.Cancelled : PairError.Timeout);
        }
        finally
        {
            if (!paired)
            {
                _device.WriteRegister(REG_BOLT_PAIRING, [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 20]);
                _device.WriteRegister(REG_BOLT_DISCOVERY, (byte)seconds, 0x02);
            }
        }
    }

    /// <summary>
    /// Turns on the wireless and software-present notification bits, so the receiver reports pairing.
    /// Returns the old flags when it changed them, so they can be put back.
    /// </summary>
    private int? EnableNotifications()
    {
        var reply = _device!.ReadRegister(REG_NOTIFICATIONS);
        if (reply is null || reply.Length < 5)
            return null;
        int flags = (reply[2] << 16) | (reply[3] << 8) | reply[4];
        int wanted = flags | 0x000900;
        return wanted != flags && WriteNotificationFlags(wanted) ? flags : null;
    }

    private bool WriteNotificationFlags(int flags) =>
        _device!.WriteRegister(REG_NOTIFICATIONS, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags);

    private byte[]? NextReport(ConcurrentQueue<byte[]> queue)
    {
        var r = queue.TryDequeue(out var queued) ? queued : _device!.ReadReport(250);
        // Receiver notices only (sub id 0x40 and up); device input reports are below.
        if (r is { Length: > 2 } && r[2] >= 0x40)
            Logger.WriteLine($"LogitechReceiver({_info.Name}): notice {Convert.ToHexString(r)}");
        return r;
    }

    private static PairError LockError(byte error) => error switch
    {
        0x01 or 0x06 => PairError.Timeout,
        0x02 => PairError.NotSupported,
        0x03 => PairError.TooMany,
        _ => PairError.Failed,
    };

    private static PairError BoltError(byte error) => error == 0x01 ? PairError.Timeout : PairError.Failed;

    /// <summary>
    /// Reads pairing information for a device index (1-based) via HID++ 1.0 long register 0xB5.
    /// Unifying-style receivers use sub 0x20+(n-1) and name 0x40+(n-1); Bolt uses 0x50+n and 0x60+n.
    /// </summary>
    /// <returns>Tuple of (name, receiver device kind, wirelessPID) or null if slot is empty.</returns>
    private (string name, int kind, ushort wpid)? GetPairedDevice(byte devIndex)
    {
        if (_device is null)
            return null;

        bool bolt = _info.Kind == ReceiverKind.Bolt;
        byte[]? pairingReply;
        try
        {
            pairingReply = _device.ReadLongRegister(REG_PAIRING_INFO, (byte)(bolt ? 0x50 + devIndex : 0x20 + devIndex - 1));
        }
        catch
        {
            return null;
        }

        // Reply [0x83, 0xB5, sub, data...]; decoders take it from the sub-register echo.
        if (pairingReply is null || pairingReply.Length < 3)
            return null;
        var info = HidPPDecode.DecodePairingInfo(pairingReply.AsSpan(2), bolt);
        if (info is null)
            return null;

        string name = "Logitech Device";
        try
        {
            byte[]? nameReply = bolt
                ? _device.ReadLongRegister(REG_PAIRING_INFO, (byte)(0x60 + devIndex), 0x01)
                : _device.ReadLongRegister(REG_PAIRING_INFO, (byte)(0x40 + devIndex - 1));
            if (nameReply is not null && nameReply.Length > 2
                && HidPPDecode.DecodeCodename(nameReply.AsSpan(2), bolt) is { } codename)
                name = codename;
        }
        catch
        {
        }

        return (name, info.Value.kind, info.Value.wpid);
    }

    /// <summary>Finds the receiver's own HID++ node (report length 20 or more).</summary>
    private HidDevice? FindReceiverHidDevice()
    {
        try
        {
            // HidSharp gives paired-device nodes the receiver's USB id too; keep only the receiver's nodes.
            var nodes = DeviceList.Local.GetHidDevices(HidPPDevice.LOGITECH_VID, _info.ProductId).Where(IsReceiverNode).ToList();
            foreach (var d in nodes)
            {
                try
                {
                    if (d.GetMaxOutputReportLength() >= 20)
                        return d;
                }
                catch
                {
                }
            }
            return nodes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechReceiver: HID device scan failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>False for a node whose sysfs HID_ID names another product (a paired device's own node).</summary>
    private bool IsReceiverNode(HidDevice d)
    {
        try
        {
            string uevent = File.ReadAllText($"/sys/class/hidraw/{Path.GetFileName(d.DevicePath)}/device/uevent");
            return HidPPDecode.HidIdProduct(uevent) is not { } pid || pid == _info.ProductId;
        }
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A Logitech mouse discovered through a wireless receiver. Uses a
/// <see cref="HidPPDevice"/> created from the receiver's HID device
/// with the paired device's index for message routing.
/// </summary>
public class LogitechReceiverMouse : LogitechMouse
{
    private readonly HidDevice _receiverHidDevice;
    private readonly byte _deviceIndex;
    private string _discoveredName;
    private readonly bool _hasSpec;

    public LogitechReceiverMouse(HidDevice receiverHidDevice, byte deviceIndex, string name, ushort wpid)
        : base(wpid, "", wireless: true)
    {
        _receiverHidDevice = receiverHidDevice;
        _deviceIndex = deviceIndex;
        var spec = ReceiverPairedDevices.SpecFor(wpid);
        _discoveredName = spec?.Name ?? name;
        _hasSpec = spec is not null;
        if (spec is not null)
        {
            _maxDpi = (int)spec.MaxDpi;
            _minDpi = (int)spec.MinDpi;
            _dpiStep = (int)spec.DpiStep;
        }
    }

    public override string GetDisplayName() => _discoveredName;

    /// <summary>Place on the receiver; unlike the name, it does not change on connect.</summary>
    public (byte index, ushort wpid) Slot => (_deviceIndex, _productId);

    /// <summary>
    /// The receiver-attached device is always "connected" if the receiver is present.
    /// Actual reachability is checked via Ping during Connect().
    /// </summary>
    public override bool IsDeviceConnected() => true;

    /// <summary>
    /// Opens a HidPPDevice using the receiver's HID device and this device's index,
    /// then pings, discovers features, and reads capabilities.
    /// </summary>
    public override void Connect()
    {
        try
        {
            _device = new HidPPDevice(_receiverHidDevice, _deviceIndex);
            _device.Open();

            float proto = _device.Ping();
            if (proto == 0)
            {
                Logger.WriteLine($"LogitechReceiverMouse({GetDisplayName()}): device unreachable (powered off?)");
                return;
            }

            if (proto >= 2.0f)
            {
                _device.DiscoverFeatures();
                // The receiver codename can be truncated; the device's own 0x0005 name is whole.
                if (!_hasSpec && HidPPProtocol.GetDeviceName(_device) is { Length: > 0 } fullName)
                    _discoveredName = fullName;
            }

            DiscoverCapabilities();

            Logger.WriteLine($"LogitechReceiverMouse({GetDisplayName()}): connected via receiver, protocol {proto:F1}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechReceiverMouse({GetDisplayName()}): connect failed - {ex.Message}");
            throw;
        }
    }
}
