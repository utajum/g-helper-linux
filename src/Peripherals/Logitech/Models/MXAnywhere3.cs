using GHelper.Linux.Peripherals.Mouse;

namespace GHelper.Linux.Peripherals.Logitech.Models;

/// <summary>MX Anywhere 3 (wireless only, detected via BT or receiver).</summary>
public class MXAnywhere3 : LogitechMouse
{
    public MXAnywhere3() : base(0x0000, "", wireless: true) { }
    protected MXAnywhere3(ushort pid, string path, bool wireless) : base(pid, path, wireless) { }

    public override string GetDisplayName() => "MX Anywhere 3";
    public override int DPIProfileCount() => 1;
    public override uint MaxDPI() => 4000;
    public override uint MinDPI() => 200;
    public override uint DPIIncrement() => 50;

    public override LightingZone[] SupportedLightingZones() => [];
    public override LightingMode[] SupportedLightingModes() => [LightingMode.Off];
}

/// <summary>MX Anywhere 3 (Bluetooth, PID 0xB025).</summary>
public class MXAnywhere3BT : MXAnywhere3
{
    public MXAnywhere3BT() : base(0xB025, "", true) { }
    public override string GetDisplayName() => "MX Anywhere 3 (BT)";
}

/// <summary>MX Anywhere 3S (USB wired/dongle mode, PID 0xC09A).</summary>
public class MXAnywhere3S : LogitechMouse
{
    public MXAnywhere3S() : base(0xC09A, "", wireless: true) { }
    protected MXAnywhere3S(ushort pid, string path, bool wireless) : base(pid, path, wireless) { }

    public override string GetDisplayName() => "MX Anywhere 3S";
    public override int DPIProfileCount() => 1;
    public override uint MaxDPI() => 8000;
    public override uint MinDPI() => 200;
    public override uint DPIIncrement() => 50;

    public override LightingZone[] SupportedLightingZones() => [];
    public override LightingMode[] SupportedLightingModes() => [LightingMode.Off];
}

/// <summary>MX Anywhere 3S (Bluetooth, PID 0xB036).</summary>
public class MXAnywhere3SBT : MXAnywhere3S
{
    public MXAnywhere3SBT() : base(0xB036, "", true) { }
    public override string GetDisplayName() => "MX Anywhere 3S (BT)";
}

/// <summary>Anywhere Mouse MX 2 (wireless, detected via receiver).</summary>
public class AnywhereMX2 : LogitechMouse
{
    public AnywhereMX2() : base(0x0000, "", wireless: true) { }

    public override string GetDisplayName() => "Anywhere Mouse MX 2";
    public override int DPIProfileCount() => 1;
    public override uint MaxDPI() => 1600;
    public override uint MinDPI() => 400;
    public override uint DPIIncrement() => 50;

    public override LightingZone[] SupportedLightingZones() => [];
    public override LightingMode[] SupportedLightingModes() => [LightingMode.Off];
}

/// <summary>MX Anywhere 2S (Bluetooth, PID 0xB01A).</summary>
public class MXAnywhere2SBT : LogitechMouse
{
    public MXAnywhere2SBT() : base(0xB01A, "", wireless: true) { }

    public override string GetDisplayName() => "MX Anywhere 2S (BT)";
    public override int DPIProfileCount() => 1;
    public override uint MaxDPI() => 4000;
    public override uint MinDPI() => 200;
    public override uint DPIIncrement() => 50;

    public override LightingZone[] SupportedLightingZones() => [];
    public override LightingMode[] SupportedLightingModes() => [LightingMode.Off];
}
