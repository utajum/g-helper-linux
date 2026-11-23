using GHelper.Linux.Helpers;
using GHelper.Linux.Peripherals.Logitech.HidPP;
using HidSharp;

namespace GHelper.Linux.Peripherals.Logitech;

/// <summary>
/// Represents a plugged-in Logitech wireless receiver. Opens the receiver's
/// HID device, queries paired-device registers via HID++ 1.0, and creates
/// <see cref="LogitechReceiverMouse"/> instances for each paired mouse.
/// </summary>
public class LogitechReceiver : IDisposable
{
    private const byte REG_PAIRING_INFO = 0xB5;

    private readonly ReceiverEntry _info;
    private HidPPDevice? _device;

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

    /// <summary>
    /// Opens the receiver and enumerates all paired devices.
    /// For each paired mouse, creates a <see cref="LogitechReceiverMouse"/>.
    /// </summary>
    public List<LogitechReceiverMouse> DiscoverDevices()
    {
        var mice = new List<LogitechReceiverMouse>();

        var hidDevice = FindReceiverHidDevice();
        if (hidDevice is null)
        {
            Logger.WriteLine($"LogitechReceiver({_info.Name}): no HID device found");
            return mice;
        }

        _device?.Dispose();
        _device = new HidPPDevice(hidDevice, 0xFF);
        try
        {
            _device.Open();
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechReceiver({_info.Name}): open failed - {ex.Message}");
            _device.Dispose();
            _device = null;
            return mice;
        }

        for (byte devIdx = 1; devIdx <= _info.MaxDevices; devIdx++)
        {
            try
            {
                var paired = GetPairedDevice(devIdx);
                if (paired is null)
                    continue;

                var (name, kind, wpid) = paired.Value;

                // Receiver pairing kind: 1 keyboard, 2 mouse, 8 trackball, 9 touchpad.
                if (kind is not (1 or 2 or 8 or 9))
                {
                    Logger.WriteLine($"LogitechReceiver: skipping device '{name}' (kind={kind})");
                    continue;
                }

                Logger.WriteLine($"LogitechReceiver: found paired device '{name}' (kind={kind}, wpid=0x{wpid:X4}) at index {devIdx}");

                var mouse = new LogitechReceiverMouse(hidDevice, devIdx, name, wpid);
                mice.Add(mouse);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechReceiver: error reading device index {devIdx}: {ex.Message}");
            }
        }

        return mice;
    }

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

    /// <summary>Finds the HidSharp HidDevice for this receiver.</summary>
    private HidDevice? FindReceiverHidDevice()
    {
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(HidPPDevice.LOGITECH_VID, _info.ProductId))
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
            foreach (var d in DeviceList.Local.GetHidDevices(HidPPDevice.LOGITECH_VID, _info.ProductId))
                return d;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechReceiver: HID device scan failed: {ex.Message}");
        }
        return null;
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
