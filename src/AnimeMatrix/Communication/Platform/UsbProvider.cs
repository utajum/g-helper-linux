namespace GHelper.Linux.AnimeMatrix.Communication.Platform;

/// <summary>
/// Abstract USB HID transport for AnimeMatrix / Slash devices.
/// Concrete implementations (e.g. LinuxUsbProvider) provide actual HID I/O.
/// </summary>
public abstract class UsbProvider : IDisposable
{
    /// <summary>USB vendor ID (e.g. 0x0B05 for ASUS).</summary>
    protected ushort VendorID;

    /// <summary>USB product ID for the target device.</summary>
    protected ushort ProductID;

    protected UsbProvider(ushort vendorId, ushort productId)
    {
        VendorID = vendorId;
        ProductID = productId;
    }

    /// <summary>Sends a HID SetFeature report.</summary>
    public abstract void Set(byte[] data);

    /// <summary>Sends a HID GetFeature report and returns the response.</summary>
    public abstract byte[] Get(byte[] data);

    /// <summary>Reads an HID input report.</summary>
    public abstract void Read(byte[] data);

    /// <summary>Writes an HID output report.</summary>
    public abstract void Write(byte[] data);

    /// <summary>Drops input reports that are still queued.</summary>
    public virtual void Drain(int packetSize) { }

    /// <inheritdoc />
    public abstract void Dispose();
}
