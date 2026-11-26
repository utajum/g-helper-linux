// Stand-ins for the Windows G-Helper types that the mouse source names.
// AppConfig keeps values in memory like the real one keeps them in config.json.

using GHelper.AnimeMatrix.Communication.Platform;

public static class AppConfig
{
    private static readonly Dictionary<string, object> Values = new();

    public static void Reset() => Values.Clear();
    public static int Get(string name, int empty = -1) => Values.TryGetValue(name, out var v) && v is int i ? i : empty;
    public static string? GetString(string name, string? empty = null) => Values.TryGetValue(name, out var v) && v is string s ? s : empty;
    public static void Set(string name, int value) => Values[name] = value;
    public static void Set(string name, string value) => Values[name] = value;
}

public static class Logger
{
    public static void WriteLine(string logMessage) { }
}

namespace System.Windows.Forms
{
    public enum Keys
    {
        Tab = 0x09, Escape = 0x1B, C = 0x43, D = 0x44, E = 0x45, L = 0x4C, M = 0x4D, V = 0x56, Z = 0x5A,
        LWin = 0x5B, F4 = 0x73, LShiftKey = 0xA0, LControlKey = 0xA2, LMenu = 0xA4,
    }
}

namespace GHelper
{
    public sealed class Image { }
}

namespace GHelper.Properties
{
    internal static class Resources
    {
        public static Image icons8_maus_48 { get; } = new();
        public static Image dock_48 { get; } = new();
    }
}

namespace GHelper.USB
{
    public enum AuraMode
    {
        AuraStatic = 0, AuraBreathe = 1, AuraColorCycle = 2, AuraRainbow = 3, Comet = 11,
    }

    public enum AuraSpeed
    {
        Slow = 0, Normal = 1, Fast = 2,
    }
}

namespace GHelper.AnimeMatrix.Communication.Platform
{
    internal class WindowsUsbProvider : UsbProvider
    {
        public WindowsUsbProvider(ushort vendorId, ushort productId, string path, int timeout = 500) : base(vendorId, productId)
            => throw new IOException("no HID in the golden generator");
        public WindowsUsbProvider(ushort vendorId, ushort productId, int maxFeatureReportLength, string name = "Matrix") : base(vendorId, productId)
            => throw new IOException("no HID in the golden generator");
        public override void Set(byte[] data) { }
        public override byte[] Get(byte[] data) => data;
        public override void Read(byte[] data) { }
        public override void Write(byte[] data) { }
        public override void Dispose() { }
    }
}
