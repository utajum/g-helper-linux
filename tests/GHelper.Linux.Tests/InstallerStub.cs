// Minimal stubs for types that NvidiaProcessScanner.cs and GPUModeControl.cs
// reference but that are too heavy (Avalonia, I18n, etc.) for the test build.

using GHelper.Linux.Platform;

namespace GHelper.Linux.Install
{
    internal static class Installer
    {
        public static bool StartupWillPrompt => false;
        public static void WaitForStartupDecision() { }
    }
}

namespace GHelper.Linux.Platform.Linux
{
    partial class LinuxSystemIntegration
    {
        public sealed class PrivilegedSelf : IDisposable
        {
            public string Path { get; }
            internal PrivilegedSelf(string path, string? tempCopy) => Path = path;
            public void Dispose() { }
        }

        public static PrivilegedSelf ResolvePrivilegedSelf()
            => new(Environment.ProcessPath ?? "/proc/self/exe", null);
    }
}

namespace GHelper.Linux
{
    internal static class App
    {
        public static IHardwareControl? Wmi => null;

        // LinuxNvidiaGpuControl uses App.System?.ShowNotification(); null = no-op.
        public static ISystemIntegration? System => null;

        // Modes.SetCurrent reads App.Power?.IsOnAcPower(); null = AC.
        public static IPowerManager? Power => null;

        // GPUModeControl calls App.Mode?.WaitForApply(); null = no-op.
        internal sealed class ModeControlStub
        {
            public void WaitForApply() { }
        }

        public static ModeControlStub? Mode => null;
    }
}
