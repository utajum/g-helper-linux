// Minimal in-memory implementations of IHardwareControl and IPowerManager for
// the GPUModeControl scenario tests. Only methods the controller
// actually calls are wired up; the rest throw NotImplementedException
// so accidental usage in a test surfaces loudly.

using GHelper.Linux.Platform;

namespace GHelper.Linux.Tests;

public sealed class FakeAsusWmi : IHardwareControl
{
    // Mutable state the scenario can read and assert on.
    public bool EcoEnabled;            // true ⇒ dgpu_disable=1
    public int MuxMode = 1;            // 1 = hybrid, 0 = Ultimate (dGPU direct)
    public bool DgpuDisableSupported = true;
    public bool MuxModeSupported = true;
    public bool CanToggleBackendValue = true;
    public bool ThrowOnSetGpuEco;      // simulate firmware rejection
    public bool ThrowOnSetGpuMuxMode;
    public Exception? NextSetGpuEcoException;

    // Call recording - useful for asserting side effects.
    public readonly List<(string Method, object? Arg)> Calls = new();

    public bool GetGpuEco()
    {
        Calls.Add(("GetGpuEco", null));
        return EcoEnabled;
    }

    public void SetGpuEco(bool enabled)
    {
        Calls.Add(("SetGpuEco", enabled));
        if (NextSetGpuEcoException != null)
        {
            var ex = NextSetGpuEcoException;
            NextSetGpuEcoException = null;
            throw ex;
        }
        if (ThrowOnSetGpuEco) throw new IOException("simulated firmware rejection");
        EcoEnabled = enabled;
    }

    public int GetGpuMuxMode()
    {
        Calls.Add(("GetGpuMuxMode", null));
        return MuxModeSupported ? MuxMode : 1;
    }

    public void SetGpuMuxMode(int mode)
    {
        Calls.Add(("SetGpuMuxMode", mode));
        if (ThrowOnSetGpuMuxMode) throw new IOException("simulated MUX rejection");
        if (EcoEnabled) throw new InvalidOperationException("MUX write rejected while dgpu_disable=1");
        MuxMode = mode;
    }

    public bool IsFeatureSupported(string feature)
    {
        Calls.Add(("IsFeatureSupported", feature));
        // The two feature checks that matter to GPUModeControl paths
        if (feature == "dgpu_disable") return DgpuDisableSupported;
        if (feature == "gpu_mux_mode") return MuxModeSupported;
        return false;
    }

    public bool IsGpuEcoAvailable() => DgpuDisableSupported;
    public bool CanToggleGpuBackend() => CanToggleBackendValue;
    public int FanCount => 2;

    public event Action<int>? WmiEvent;
    public event Action<string>? KeyBindingEvent;
    public event Action<string>? PlatformProfileChanged { add { } remove { } }

    public bool HasKbdBrightnessHwChanged => false;

    // Everything below is irrelevant for these tests - left as throwing
    // stubs so a regression that suddenly calls them is visible.
    public int DeviceGet(int deviceId) => -1;
    public int DeviceSet(int deviceId, int value) => 0;
    public byte[]? DeviceGetBuffer(int deviceId, int args = 0) => null;
    public int GetThrottleThermalPolicy() => 0;
    public void SetThrottleThermalPolicy(int mode) { }
    public int GetFanRpm(int fanIndex) => 0;
    public byte[]? GetFanCurve(int fanIndex) => null;
    public void SetFanCurve(int fanIndex, byte[] curve) { }
    public void DisableFanCurve(int fanIndex) { }
    public byte[]? ResetFanCurveToDefaults(int fanIndex) => null;
    public bool IsFanCurveEnabled(int fanIndex) => false;
    public int GetBatteryChargeLimit() => 100;
    public bool SetBatteryChargeLimit(int percent) => true;
    public bool GetPanelOverdrive() => false;
    public bool SetPanelOverdrive(bool enabled) => true;
    public int GetMiniLedMode() => 0;
    public void SetMiniLedMode(int mode) { }
    public int GetMiniLedModeCount() => 0;
    public int GetScreenAutoBrightness() => -1;
    public void SetScreenAutoBrightness(bool enabled) { }
    public bool SetPptLimit(string attribute, int watts) => false;
    public int GetPptLimit(string attribute) => -1;
    public Platform.Linux.AttrRange? GetAttributeRange(Platform.Linux.AttrDef attr) => null;
    public int GetKeyboardBrightness() => 0;
    public void SetKeyboardBrightness(int level) { }
    public int KbdMaxBrightness => 3;
    public void EnsureManualFanMode() { }
    public void SetKeyboardRgb(byte r, byte g, byte b) { }
    public void SubscribeEvents() { }
    public void Dispose() { }

    public void TriggerWmiEvent(int code) => WmiEvent?.Invoke(code);
    public void TriggerKeyBindingEvent(string name) => KeyBindingEvent?.Invoke(name);
}

public sealed class FakePowerManager : IPowerManager
{
    public bool OnAc = true;
    public bool IsOnAcPower() => OnAc;

    public void SetCpuBoost(bool enabled) { }
    public bool GetCpuBoost() => true;
    public void SetPlatformProfile(string profile) { }
    public string GetPlatformProfile() => "balanced";
    public string[] GetPlatformProfileChoices() => Array.Empty<string>();
    public Task SetAspmPolicy(string policy) => Task.CompletedTask;
    public string GetAspmPolicy() => "default";
    public int GetBatteryPercentage() => 100;
    public int GetBatteryDrainRate() => 0;
    public int GetBatteryHealth() => 100;
    public event Action<bool>? PowerStateChanged;
    public event Action? SystemResumed;
    public event Action? MonitorSlept;
    public event Action? MonitorWoke;
    public void StartPowerMonitoring() { }
    public void StopPowerMonitoring() { }
    public void TriggerPowerStateChanged(bool onAc) => PowerStateChanged?.Invoke(onAc);
    public void TriggerMonitorSlept() => MonitorSlept?.Invoke();
    public void TriggerMonitorWoke() => MonitorWoke?.Invoke();
}
