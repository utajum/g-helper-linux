using System.Diagnostics.CodeAnalysis;
using GHelper.Linux.AnimeMatrix.Communication.Platform;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.AnimeMatrix.Communication;

/// <summary>
/// Base class for ASUS USB-HID devices (AnimeMatrix, Slash, etc.).
/// Subclasses set vendor/product IDs and call <see cref="SetProvider"/> to open the HID transport.
/// </summary>
public abstract class Device : IDisposable
{
    /// <summary>Active HID transport, null until SetProvider succeeds.</summary>
    protected UsbProvider? _usbProvider;

    /// <summary>USB vendor ID for the target device.</summary>
    protected ushort _vendorId;

    /// <summary>USB product ID for the target device.</summary>
    protected ushort _productId;

    /// <summary>Minimum feature report length for HID interface matching.</summary>
    protected int _maxFeatureReportLength;

    public ushort VendorID()
    {
        return _vendorId;
    }

    public ushort ProductID()
    {
        return _productId;
    }

    protected virtual string LogName => "Device";

    protected Device(ushort vendorId, ushort productId)
    {
        _vendorId = vendorId;
        _productId = productId;
    }

    /// <summary>Stores IDs and immediately opens the HID transport.</summary>
    protected Device(ushort vendorId, ushort productId, int maxFeatureReportLength)
    {
        _vendorId = vendorId;
        _productId = productId;
        _maxFeatureReportLength = maxFeatureReportLength;

        SetProvider();
    }

    /// <summary>Creates the LinuxUsbProvider if not already set. Override for custom matching.</summary>
    public virtual void SetProvider()
    {
        if (_usbProvider is not null)
            return;

        try
        {
            _usbProvider = new LinuxUsbProvider(_vendorId, _productId, _maxFeatureReportLength, LogName);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"{LogName}: failed to open HID device - {ex.Message}");
        }
    }

    /// <summary>Creates a packet of type T with the given command bytes.</summary>
    protected T Packet<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params byte[] command) where T : Packet
    {
        return (T)Activator.CreateInstance(typeof(T), command)!;
    }

    /// <summary>Sends a HID SetFeature report.</summary>
    public void Set(Packet packet)
    {
        _usbProvider?.Set(packet.Data);
    }

    /// <summary>Sends a HID GetFeature report and returns the response.</summary>
    public byte[] Get(Packet packet)
    {
        return _usbProvider?.Get(packet.Data) ?? [];
    }

    /// <summary>Reads an HID input report into the provided buffer.</summary>
    public void Read(byte[] data)
    {
        _usbProvider?.Read(data);
    }

    /// <summary>Writes raw HID output data.</summary>
    public void Write(byte[] data)
    {
        _usbProvider?.Write(data);
    }

    /// <summary>Drops input reports that are still queued.</summary>
    public void Drain(int packetSize)
    {
        _usbProvider?.Drain(packetSize);
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
        _usbProvider?.Dispose();
        _usbProvider = null;
    }
}
