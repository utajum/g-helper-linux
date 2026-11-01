// Minimal stubs for types the SUT references but the tests do not exercise.
// These let GPUModeControl compile in isolation without dragging Avalonia,
// libusb, SkiaSharp etc. into the test binary.

namespace GHelper.Linux.USB;

/// <summary>
/// Stub AURA mode enum. The real enum lives in src/USB/Aura.cs and is
/// referenced by GPUModeControl.RequestModeSwitch when deciding whether
/// to refresh the keyboard's GPU-color effect after a successful mode
/// switch. Only tests that set <c>aura_mode</c> to <c>GpuMode</c> reach
/// <c>ApplyGpuColor()</c>.
/// </summary>
public enum AuraMode
{
    Static = 0,
    Breathe = 1,
    Strobe = 2,
    GpuMode = 99,
}

/// <summary>
/// Stub for the AURA keyboard colour helper. Counts calls; production
/// code talks to the HID device which is absent in the test environment.
/// </summary>
public static class CustomRgb
{
    public static int Calls;

    public static void ApplyGpuColor() => Calls++;
}
