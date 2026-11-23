using GHelper.Linux.Peripherals.Mouse;

namespace GHelper.Linux.Peripherals.Logitech.Models;

/// <summary>LIFT vertical mouse (Bluetooth, PID 0xB031; for Business 0xB033).</summary>
public class LiftBT : LogitechMouse
{
    private readonly string _name;

    public LiftBT(ushort pid, string name) : base(pid, "", wireless: true) => _name = name;

    public override string GetDisplayName() => _name;
    public override int DPIProfileCount() => 1;
    public override uint MaxDPI() => 4000;
    public override uint MinDPI() => 200;
    public override uint DPIIncrement() => 50;

    public override LightingZone[] SupportedLightingZones() => [];
    public override LightingMode[] SupportedLightingModes() => [LightingMode.Off];
}
