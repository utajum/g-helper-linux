// One script of calls on an ASUS mouse model, with a fake HID transport. The same file
// builds against the Windows G-Helper source (scripts/mouse-golden, WINDOWS_SOURCE) to
// write mouse-golden.txt, and against the Linux port in the tests, which compare the two.

using System.Reflection;
using System.Text;
#if WINDOWS_SOURCE
using GHelper.AnimeMatrix.Communication;
using GHelper.AnimeMatrix.Communication.Platform;
using GHelper.Peripherals.Mouse;
#else
using GHelper.Linux.AnimeMatrix.Communication;
using GHelper.Linux.AnimeMatrix.Communication.Platform;
using GHelper.Linux.Peripherals.Mouse;
#endif

namespace GHelper.Linux.Tests;

public static class MouseScenario
{
    /// <summary>Every model class: concrete subclasses of AsusMouse, by class name.</summary>
    public static List<Type> Models(Assembly assembly) => assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(AsusMouse)) && !t.IsAbstract)
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToList();

    /// <summary>The golden text of one model: capabilities, then packets and state per step.</summary>
    public static string Run(Type model)
    {
        var sb = new StringBuilder();
        sb.Append("[").Append(model.Name).Append("]\n");
        if (model.GetConstructor(Type.EmptyTypes) is null)
        {
            sb.Append("no default constructor\n");
            return sb.ToString();
        }

        var mouse = (AsusMouse)Activator.CreateInstance(model)!;
        var usb = new FakeUsb(sb);
        typeof(Device).GetField("_usbProvider", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(mouse, usb);

        Step(sb, "caps", () => Caps(sb, mouse));
        Step(sb, "sync", () => mouse.SynchronizeDevice());
        Step(sb, "state", () => State(sb, mouse));

        Step(sb, "profile", () => mouse.SetProfile(1));
        Step(sb, "polling", () => mouse.SetPollingRate(mouse.SupportedPollingrates().Last()));
        Step(sb, "angle snapping", () => mouse.SetAngleSnapping(true));
        Step(sb, "angle", () => mouse.SetAngleAdjustment(5));
        Step(sb, "debounce", () => mouse.SetDebounce(mouse.MaxDebounce()));
        Step(sb, "lift-off", () => mouse.SetLiftOffDistance(LiftOffDistance.High));
        Step(sb, "acceleration", () => mouse.SetAcceleration(1));
        Step(sb, "deceleration", () => mouse.SetDeceleration(1));
        Step(sb, "motion sync", () => mouse.SetMotionSync(true));
        Step(sb, "zone mode", () =>
        {
            mouse.SetZoneMode(true);
            mouse.UpdateZoneModeDPI(1600);
            mouse.UpdateZoneModePollingRate(PollingRate.PR4000Hz);
        });
        Step(sb, "energy", () => mouse.SetEnergySettings(20, PowerOffSetting.Minutes5));
        Step(sb, "dpi", () =>
        {
            mouse.SetDPIForProfile(new AsusMouseDPI { DPI = 1600, Color = System.Drawing.Color.FromArgb(0, 0, 255) }, 1);
            mouse.SetDPIProfile(1);
            if (mouse.CanChangeDPICount())
                mouse.SetDPIProfileCount(3);
        });
        Step(sb, "lighting", () =>
        {
            var ls = new LightingSetting
            {
                LightingMode = LightingMode.Breathing,
                Brightness = 50,
                RGBColor = System.Drawing.Color.FromArgb(10, 20, 30),
                AnimationSpeed = AnimationSpeed.Fast,
                AnimationDirection = AnimationDirection.CounterClockwise,
            };
            foreach (var zone in mouse.SupportedLightingZones())
                mouse.SetLightingSetting(ls, zone);
            mouse.SetLightingSetting(ls, LightingZone.All);
        });
        Step(sb, "bindings", () =>
        {
            if (mouse.ButtonSlots.Count > 0)
                mouse.SetButtonBinding(mouse.ButtonSlots.Keys.First(), 0x01F1);
            mouse.ResetButtonBindings();
        });
        Step(sb, "export", () => sb.Append("export ").Append(Hex(mouse.Export())).Append('\n'));
        Step(sb, "import", () =>
        {
            bool ok = mouse.Import(mouse.Export());
            sb.Append("import ").Append(ok).Append('\n');
        });
        Step(sb, "state", () => State(sb, mouse));
        return sb.ToString();
    }

    private static void Step(StringBuilder sb, string name, Action action)
    {
        sb.Append("# ").Append(name).Append('\n');
        try
        {
            action();
        }
        catch (Exception ex)
        {
            sb.Append("! ").Append(ex.GetType().Name).Append('\n');
        }
    }

    private static void Caps(StringBuilder sb, AsusMouse m)
    {
        void Cap(string name, object? value) => sb.Append(name).Append('=').Append(Text(value)).Append('\n');
        Cap("name", m.GetDisplayName());
        Cap("ids", $"{m.VendorID():X4}:{m.ProductID():X4}");
        Cap("wireless", m.Wireless);
        Cap("packet", m.USBPacketSize());
        Cap("profiles", m.HasProfiles() + " " + m.ProfileCount());
        Cap("dpi", $"{m.DPIProfileCount()} {m.MinDPI()}-{m.MaxDPI()} step {m.DPIIncrements()} xy {m.HasXYDPI()} colors {m.HasDPIColors()} count {m.CanChangeDPICount()}");
        Cap("polling", string.Join(",", m.SupportedPollingrates()) + " set " + m.CanSetPollingRate());
        Cap("angle", $"{m.HasAngleSnapping()} tuning {m.HasAngleTuning()} {m.AngleTuningMin()}..{m.AngleTuningMax()} step {m.AngleTuningStep()}");
        Cap("debounce", $"{m.HasDebounceSetting()} {m.MinDebounce()}..{m.MaxDebounce()}");
        Cap("liftoff", m.HasLiftOffSetting());
        Cap("acceleration", $"{m.HasAcceleration()} {m.MaxAcceleration()} deceleration {m.HasDeceleration()} {m.MaxDeceleration()}");
        Cap("motion sync", m.HasMotionSync());
        Cap("zone mode", m.HasZoneMode());
        Cap("battery", $"{m.HasBattery()} power off {m.HasAutoPowerOff()} low {m.HasLowBatteryWarning()} {m.LowBatteryWarningStep()}/{m.LowBatteryWarningMax()}");
        Cap("rgb", $"{m.HasRGB()} zones {string.Join(",", m.SupportedLightingZones())} max {m.MaxBrightness()}");
        Cap("modes", string.Join(",", Enum.GetValues<LightingMode>().Where(m.IsLightingModeSupported)));
        Cap("bindings", m.HasButtonBindings() + " shift " + m.HasSpeedShift + " slots " + string.Join(",", m.ButtonSlots.Select(s => $"{s.Key}:{s.Value.SourceCode:X4}")));
    }

    private static void State(StringBuilder sb, AsusMouse m)
    {
        sb.Append($"ready={m.IsDeviceReady} battery={m.Battery} charging={m.Charging} profile={m.Profile} dpiProfile={m.DpiProfile} dpiCount={m.CurrentDPIProfileCount}\n");
        sb.Append("dpis=").Append(string.Join(",", m.DpiSettings.Select(d => d is null ? "null" : $"{d.DPI}/{Text(d.Color)}"))).Append('\n');
        sb.Append($"polling={m.PollingRate} snapping={m.AngleSnapping} angle={m.AngleAdjustmentDegrees} debounce={m.Debounce} liftoff={m.LiftOffDistance}\n");
        sb.Append($"acceleration={m.Acceleration} deceleration={m.Deceleration} motionSync={m.MotionSync} zoneMode={m.ZoneMode}\n");
        sb.Append($"lowBattery={m.LowBatteryWarning} powerOff={m.PowerOffSetting}\n");
        sb.Append("lighting=").Append(string.Join(" | ", m.LightingSetting.Select(Text))).Append('\n');
        sb.Append("bindings=").Append(string.Join(",", m.ButtonBindings.Select(b => b.ToString("X4")))).Append(" ready=").Append(m.ButtonBindingsReady).Append('\n');
    }

    private static string Text(object? value) => value switch
    {
        null => "null",
        System.Drawing.Color c => $"{c.R:X2}{c.G:X2}{c.B:X2}",
        LightingSetting ls => $"{ls.LightingMode} {ls.Brightness} {Text(ls.RGBColor)} {ls.RandomColor} {ls.AnimationSpeed} {ls.AnimationDirection}",
        _ => value.ToString() ?? "",
    };

    private static string Hex(byte[] data) => Convert.ToHexString(data);

    /// <summary>Records each write. A read returns the header of the last write, then bytes 1..3.</summary>
    private sealed class FakeUsb : UsbProvider
    {
        private readonly StringBuilder _log;
        private byte[] _last = [];

        public FakeUsb(StringBuilder log) : base(0x0B05, 0) => _log = log;

        public override void Write(byte[] data)
        {
            _last = (byte[])data.Clone();
            string hex = Hex(data);
            while (hex.EndsWith("00", StringComparison.Ordinal))
                hex = hex[..^2];
            _log.Append("w ").Append(hex).Append('\n');
        }

        public override void Read(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
                data[i] = i < 4 && i < _last.Length ? _last[i] : (byte)(1 + i % 3);
        }

        public override void Set(byte[] data) => _log.Append("set ").Append(Hex(data)).Append('\n');
        public override byte[] Get(byte[] data) => data;
        public override void Dispose() { }
    }
}
