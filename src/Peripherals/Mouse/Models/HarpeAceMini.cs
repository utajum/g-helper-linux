namespace GHelper.Linux.Peripherals.Mouse.Models;

public class HarpeAceMiniWired : AsusMouse
{
    public HarpeAceMiniWired() : this(0x1B63, "mi_00", false) { }
    protected HarpeAceMiniWired(ushort pid, string path, bool wireless, byte reportId = 0x00)
        : base(0x0B05, pid, path, wireless, reportId) { }

    public override string GetDisplayName() => "ROG Harpe Ace Mini (Wired)";
    public override int ProfileCount() => 5;
    public override int DPIProfileCount() => 4;
    public override uint MaxDPI() => 42000;
    public override PollingRate[] SupportedPollingrates() =>
        [PollingRate.PR125Hz, PollingRate.PR250Hz, PollingRate.PR500Hz, PollingRate.PR1000Hz];
    public override bool HasXYDPI() => true;
    public override bool HasAngleTuning() => true;
    public override int AngleTuningStep() => 5;
    public override bool HasDPIColors() => true;
    public override bool CanChangeDPICount() => true;
    public override LightingZone[] SupportedLightingZones() => [LightingZone.Scrollwheel];
}

public class HarpeAceMiniOmni : HarpeAceMiniWired
{
    public HarpeAceMiniOmni() : base(0x1ACE, "mi_02&col03", true, 0x03) { }
    public override string GetDisplayName() => "ROG Harpe Ace Mini";
}

public class HarpeMiniCore : HarpeAceMiniWired
{
    public HarpeMiniCore() : base(0x1C8F, "mi_00", false) { }
    public override string GetDisplayName() => "ROG Harpe Mini Core";
    public override uint MaxDPI() => 12000;
    public override bool HasXYDPI() => false;
}
