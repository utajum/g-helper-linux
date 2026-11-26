// Windows types that the ported Windows G-Helper peripheral code names.
// They let AsusMouse.cs and the mouse models stay copies of the Windows files.

namespace GHelper.Linux.Peripherals
{
    /// <summary>Windows virtual-key codes (System.Windows.Forms.Keys) used by the mouse combos.</summary>
    public enum Keys
    {
        Tab = 0x09,
        Escape = 0x1B,
        C = 0x43,
        D = 0x44,
        E = 0x45,
        L = 0x4C,
        M = 0x4D,
        V = 0x56,
        Z = 0x5A,
        LWin = 0x5B,
        F4 = 0x73,
        LShiftKey = 0xA0,
        LControlKey = 0xA2,
        LMenu = 0xA4,
    }

    /// <summary>Device icon. The Linux UI shows no icons.</summary>
    public sealed class Image
    {
    }
}

namespace GHelper.Linux.Peripherals.Properties
{
    internal static class Resources
    {
        public static Image icons8_maus_48 { get; } = new();
        public static Image dock_48 { get; } = new();
    }
}
