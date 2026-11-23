using GHelper.Linux.Peripherals.Mouse;

namespace GHelper.Linux.Peripherals.Logitech.Models;

/// <summary>MX Master 3 (wireless only, detected via BT or receiver).</summary>
public class MXMaster3 : LogitechMouse
{
    public MXMaster3() : base(0x0000, "", wireless: true) { }
    protected MXMaster3(ushort pid, string path, bool wireless) : base(pid, path, wireless) { }

    public override string GetDisplayName() => "MX Master 3";
    public override int DPIProfileCount() => 1;
    public override uint MaxDPI() => 4000;
    public override uint MinDPI() => 200;
    public override uint DPIIncrement() => 50;

    public override LightingZone[] SupportedLightingZones() => [];
    public override LightingMode[] SupportedLightingModes() => [LightingMode.Off];
}

/// <summary>MX Master 3 (Bluetooth, PID 0xB023).</summary>
public class MXMaster3BT : MXMaster3
{
    public MXMaster3BT() : base(0xB023, "", true) { }
    public override string GetDisplayName() => "MX Master 3 (BT)";
}

/// <summary>MX Master 3S (USB wired/dongle mode, PID 0xC095).</summary>
public class MXMaster3S : LogitechMouse
{
    public MXMaster3S() : base(0xC095, "", wireless: true) { }
    protected MXMaster3S(ushort pid, string path, bool wireless) : base(pid, path, wireless) { }

    public override string GetDisplayName() => "MX Master 3S";
    public override int DPIProfileCount() => 1;
    public override uint MaxDPI() => 8000;
    public override uint MinDPI() => 200;
    public override uint DPIIncrement() => 50;

    public override LightingZone[] SupportedLightingZones() => [];
    public override LightingMode[] SupportedLightingModes() => [LightingMode.Off];
}

/// <summary>MX Master 3S (Bluetooth, PID 0xB034).</summary>
public class MXMaster3SBT : MXMaster3S
{
    public MXMaster3SBT() : base(0xB034, "", true) { }
    public override string GetDisplayName() => "MX Master 3S (BT)";
}

/// <summary>MX Master 3S second hardware revision (Bluetooth, PID 0xB043).</summary>
public class MXMaster3SBTv2 : MXMaster3S
{
    public MXMaster3SBTv2() : base(0xB043, "", true) { }
    public override string GetDisplayName() => "MX Master 3S (BT)";
}

/// <summary>MX Master 3S for Business (Bluetooth, PID 0xB035 or 0xB044).</summary>
public class MXMaster3SBusinessBT : MXMaster3S
{
    public MXMaster3SBusinessBT(ushort pid) : base(pid, "", true) { }
    public override string GetDisplayName() => "MX Master 3S for Business (BT)";
}
