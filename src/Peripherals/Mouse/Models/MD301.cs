namespace GHelper.Linux.Peripherals.Mouse.Models;

public class MD301 : AsusMouse
{
    public MD301() : this(0x1CD3, true) { }
    protected MD301(ushort pid, bool wireless)
        : base(0x0B05, pid, "mi_03", wireless) { }

    public override string GetDisplayName() => "ProArt Mouse MD301 (Wireless)";
    public override int ProfileCount() => 3;
    public override int DPIProfileCount() => 3;
    public override uint MaxDPI() => 8000;
    public override bool HasAngleSnapping() => false;
    public override bool HasAutoPowerOff() => true;
    public override PollingRate[] SupportedPollingrates() =>
        [PollingRate.PR125Hz, PollingRate.PR250Hz, PollingRate.PR500Hz, PollingRate.PR1000Hz];
    public override LightingZone[] SupportedLightingZones() => [];
}

public class MD301Wired : MD301
{
    public MD301Wired() : base(0x1CD5, false) { }
    public override string GetDisplayName() => "ProArt Mouse MD301 (Wired)";
}
