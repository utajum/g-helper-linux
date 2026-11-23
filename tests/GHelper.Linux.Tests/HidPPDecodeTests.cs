// Logitech HID++ payload decoder tests. Pure functions, no sandbox state.

using GHelper.Linux.Peripherals.Logitech.HidPP;

namespace GHelper.Linux.Tests;

public static class HidPPDecodeTests
{
    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- HidPPDecode ---");

        Harness.Scenario("Dpi_SteppedRange", _ =>
        {
            // 100, 0xE000|50, 25600, 0
            var r = HidPPDecode.ParseDpiWords([0x00, 0x64, 0xE0, 0x32, 0x64, 0x00, 0x00, 0x00]);
            Harness.AssertEqual((100, 25600, 50), r!.Value, "range");
        });

        Harness.Scenario("Dpi_DiscreteList_NoTerminator", _ =>
        {
            var r = HidPPDecode.ParseDpiWords([0x01, 0x90, 0x03, 0x20, 0x06, 0x40, 0x0C, 0x80]);
            Harness.AssertEqual((400, 3200, 0), r!.Value, "list");
        });

        Harness.Scenario("Dpi_LeadingMarkerOrEmpty_IsNull", _ =>
        {
            Harness.Assert(HidPPDecode.ParseDpiWords([0xE0, 0x32, 0x06, 0x40]) is null, "leading marker");
            Harness.Assert(HidPPDecode.ParseDpiWords([0x00, 0x00]) is null, "empty list");
            Harness.Assert(HidPPDecode.ParseDpiWords([0x01, 0x90, 0xE0, 0x00, 0x06, 0x40]) is null, "zero step");
        });

        Harness.Scenario("UnifiedBattery_NoGauge_UsesLevelBits", _ =>
        {
            Harness.AssertEqual((50, false), HidPPDecode.DecodeUnifiedBattery(37, 4, 0, hasSoc: false), "good, no gauge");
            Harness.AssertEqual((37, false), HidPPDecode.DecodeUnifiedBattery(37, 4, 0, hasSoc: true), "gauge");
            Harness.AssertEqual((90, true), HidPPDecode.DecodeUnifiedBattery(0, 8, 1, hasSoc: true), "gauge 0 falls back");
        });

        Harness.Scenario("BatteryStatus_OffBatteryLevels", _ =>
        {
            Harness.AssertEqual((50, false), HidPPDecode.DecodeBatteryStatus(50, 0)!.Value, "discharging");
            Harness.Assert(HidPPDecode.DecodeBatteryStatus(0, 0) is null, "discharging, no level");
            Harness.AssertEqual((100, true), HidPPDecode.DecodeBatteryStatus(0, 3)!.Value, "complete");
            Harness.AssertEqual((-1, true), HidPPDecode.DecodeBatteryStatus(0, 1)!.Value, "charging, unknown");
            Harness.AssertEqual((-1, true), HidPPDecode.DecodeBatteryStatus(0, 4)!.Value, "slow recharge");
            Harness.AssertEqual((-1, false), HidPPDecode.DecodeBatteryStatus(0, 6)!.Value, "thermal error");
        });

        Harness.Scenario("BatteryVoltage_ChargeStatusBits", _ =>
        {
            Harness.Assert(!HidPPDecode.DecodeVoltageCharging(0x00), "on battery");
            Harness.Assert(HidPPDecode.DecodeVoltageCharging(0x80), "charging");
            Harness.Assert(HidPPDecode.DecodeVoltageCharging(0x81), "full");
            Harness.Assert(!HidPPDecode.DecodeVoltageCharging(0x82), "not charging");
            Harness.Assert(!HidPPDecode.DecodeVoltageCharging(0x83), "undefined");
            Harness.Assert(HidPPDecode.DecodeVoltageCharging(0x88), "fast charging bit");
        });

        Harness.Scenario("RgbParams_Layouts", _ =>
        {
            Harness.AssertEqual("0A141E00000000000000",
                Convert.ToHexString(HidPPDecode.BuildRgbEffectParams(0x01, 10, 20, 30, 0)!), "static");
            Harness.AssertEqual("0A141E1B580064000000",
                Convert.ToHexString(HidPPDecode.BuildRgbEffectParams(0x0A, 10, 20, 30, 7000)!), "breathe");
            Harness.AssertEqual("00000000001388640000",
                Convert.ToHexString(HidPPDecode.BuildRgbEffectParams(0x03, 1, 2, 3, 5000)!), "cycle");
            Harness.AssertEqual("00FF0000000013886400",
                Convert.ToHexString(HidPPDecode.BuildRgbEffectParams(0x15, 1, 2, 3, 5000)!), "cycle saturation");
            Harness.AssertEqual("0A141E46000000000000",
                Convert.ToHexString(HidPPDecode.BuildRgbEffectParams(0x02, 10, 20, 30, 7000)!), "pulse");
            Harness.AssertEqual("00FF0000000013886400",
                Convert.ToHexString(HidPPDecode.BuildRgbEffectParams(0x16, 1, 2, 3, 5000)!), "wave saturation");
            Harness.Assert(HidPPDecode.BuildRgbEffectParams(0x0B, 1, 2, 3, 5000) is null, "unknown layout");
        });

        Harness.Scenario("Brightness_RangeMapping", _ =>
        {
            Harness.AssertEqual(0, HidPPDecode.ScaleToPercent(10, 10, 255), "min");
            Harness.AssertEqual(100, HidPPDecode.ScaleToPercent(255, 10, 255), "max");
            Harness.AssertEqual(50, HidPPDecode.ScaleToPercent(128, 1, 255), "mid");
            Harness.AssertEqual(255, HidPPDecode.PercentToRange(100, 10, 255), "100%");
            Harness.AssertEqual(10, HidPPDecode.PercentToRange(-5, 10, 255), "clamped low");
            Harness.AssertEqual(-12, HidPPDecode.PercentToRange(0, -12, 12), "signed min");
            Harness.AssertEqual(0, HidPPDecode.PercentToRange(50, -12, 12), "signed mid");
        });

        Harness.Scenario("Receiver_PairingInfo_UnifyingAndBolt", _ =>
        {
            // Unifying 0x20+(n-1): [sub, dest, interval, wpid_hi, wpid_lo, ?, ?, kind]
            var u = HidPPDecode.DecodePairingInfo([0x20, 0x07, 0x08, 0x40, 0x82, 0x00, 0x00, 0x42], bolt: false);
            Harness.AssertEqual(((ushort)0x4082, 2), u!.Value, "unifying mouse");
            // Bolt 0x50+n: [sub, flags|kind, wpid_lo, wpid_hi, serial...]
            var b = HidPPDecode.DecodePairingInfo([0x51, 0x01, 0x34, 0xB0, 0x11, 0x22, 0x33, 0x44], bolt: true);
            Harness.AssertEqual(((ushort)0xB034, 1), b!.Value, "bolt keyboard");
            Harness.Assert(HidPPDecode.DecodePairingInfo([0x20, 0, 0, 0, 0, 0, 0, 2], bolt: false) is null, "empty slot");
        });

        Harness.Scenario("Receiver_Codename", _ =>
        {
            Harness.AssertEqual("MX Master 3",
                HidPPDecode.DecodeCodename([0x40, 0x0B, .. "MX Master 3"u8, 0x00, 0x00], bolt: false), "unifying");
            Harness.AssertEqual("MX Anywhere 3S",
                HidPPDecode.DecodeCodename([0x61, 0x01, 0x0E, .. "MX Anywhere 3S"u8], bolt: true), "bolt");
            Harness.Assert(HidPPDecode.DecodeCodename([0x40, 0x00], bolt: false) is null, "empty");
        });

        Harness.Scenario("Receiver_RenamedDevice_IsConnected", _ =>
        {
            // Bolt lists "MX MCHNCL M"; connect renames it to the 0x0005 name "MX Mechanical Mini".
            (string, (byte, ushort)?)[] connected = [("G502 HERO", null), ("MX Mechanical Mini", (1, 0xB367))];
            Harness.Assert(HidPPDecode.IsPairedDeviceConnected(connected, "MX MCHNCL M", (1, 0xB367)), "renamed, same slot");
            Harness.Assert(!HidPPDecode.IsPairedDeviceConnected(connected, "MX Master 3S", (2, 0xB034)), "new slot");
            Harness.Assert(HidPPDecode.IsPairedDeviceConnected([("MX Master 3S", null)], "MX Master 3S", (2, 0xB034)), "same name");
        });

        Harness.Scenario("DisabledKeys_Summary", _ =>
        {
            Harness.AssertEqual("-", HidPPDecode.DisabledKeysSummary(0), "none");
            Harness.AssertEqual("Caps Lock, Win", HidPPDecode.DisabledKeysSummary(0x11), "caps + win");
        });

        Harness.Scenario("Backlight_SecondsToWire", _ =>
        {
            Harness.AssertEqual((ushort)1, HidPPDecode.BacklightSecondsToWire(0), "min 1");
            Harness.AssertEqual((ushort)1, HidPPDecode.BacklightSecondsToWire(5), "5 s");
            Harness.AssertEqual((ushort)2, HidPPDecode.BacklightSecondsToWire(6), "round up");
            Harness.AssertEqual((ushort)120, HidPPDecode.BacklightSecondsToWire(600), "10 min");
        });
    }
}
