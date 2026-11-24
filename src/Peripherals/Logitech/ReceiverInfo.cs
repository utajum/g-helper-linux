namespace GHelper.Linux.Peripherals.Logitech;

/// <summary>Logitech wireless receiver technology family.</summary>
public enum ReceiverKind { Bolt, Unifying, Lightspeed, Nano }

/// <summary>Descriptor for a known Logitech USB receiver.</summary>
/// <param name="ProductId">USB product ID.</param>
/// <param name="Kind">Receiver technology family.</param>
/// <param name="MaxDevices">Maximum number of paired devices.</param>
/// <param name="UsbInterface">HID interface index for HID++ communication.</param>
/// <param name="Name">Human-readable name.</param>
public record ReceiverEntry(ushort ProductId, ReceiverKind Kind, int MaxDevices, int UsbInterface, string Name);

/// <summary>
/// Static table of every known Logitech receiver, keyed by USB product ID.
/// Used by <see cref="LogitechReceiver"/> to identify plugged-in receivers
/// and by <see cref="PeripheralsProvider"/> to trigger device enumeration.
/// </summary>
public static class ReceiverInfo
{
    public static readonly ReceiverEntry[] KnownReceivers =
    [
        new(0xC548, ReceiverKind.Bolt,       6, 2, "Bolt Receiver"),

        new(0xC52B, ReceiverKind.Unifying,   6, 2, "Unifying Receiver"),
        new(0xC532, ReceiverKind.Unifying,   6, 2, "Unifying Receiver"),

        new(0xC518, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC51A, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC51B, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC521, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC525, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC526, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC52E, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC52F, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC531, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC534, ReceiverKind.Nano,       2, 1, "Nano Receiver"),
        new(0xC535, ReceiverKind.Nano,       1, 1, "Nano Receiver (Dell)"),
        new(0xC537, ReceiverKind.Nano,       1, 1, "Nano Receiver"),
        new(0xC53E, ReceiverKind.Nano,       1, 1, "Spotlight Receiver"),

        new(0xC539, ReceiverKind.Lightspeed, 1, 2, "Lightspeed Receiver"),
        new(0xC53A, ReceiverKind.Lightspeed, 1, 2, "Lightspeed Receiver"),
        new(0xC53D, ReceiverKind.Lightspeed, 1, 2, "Lightspeed Receiver"),
        new(0xC53F, ReceiverKind.Lightspeed, 1, 2, "Lightspeed Receiver"),
        new(0xC541, ReceiverKind.Lightspeed, 1, 2, "Lightspeed Receiver"),
        new(0xC545, ReceiverKind.Lightspeed, 1, 2, "Lightspeed Receiver"),
        new(0xC547, ReceiverKind.Lightspeed, 1, 2, "Lightspeed Receiver"),
        new(0xC54D, ReceiverKind.Lightspeed, 1, 2, "Lightspeed Receiver"),
    ];

    /// <summary>Finds a receiver entry by USB product ID, or null if unknown.</summary>
    public static ReceiverEntry? Find(ushort pid) =>
        Array.Find(KnownReceivers, r => r.ProductId == pid);
}
