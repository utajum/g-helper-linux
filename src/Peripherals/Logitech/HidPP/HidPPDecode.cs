namespace GHelper.Linux.Peripherals.Logitech.HidPP;

/// <summary>
/// Pure HID++ payload decoders and encoders. No device I/O, so they are unit-tested.
/// All offsets are into the feature payload (reply byte 2 onward).
/// </summary>
public static class HidPPDecode
{
    /// <summary>
    /// Parses a DPI word list (0x2201 fn1, 0x2202 fn2): big-endian words, 0x0000 ends it,
    /// 0xE000|step between two values marks a stepped range. Returns (min, max, step), step 0 = discrete list.
    /// </summary>
    public static (int min, int max, int step)? ParseDpiWords(ReadOnlySpan<byte> bytes)
    {
        int min = 0, max = 0, step = 0;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            int word = (bytes[i] << 8) | bytes[i + 1];
            if (word == 0)
                break;
            if (word >= 0xE000)
            {
                if (min == 0 || (word & 0x1FFF) == 0)
                    return null;
                step = word & 0x1FFF;
                continue;
            }
            min = min == 0 ? word : Math.Min(min, word);
            max = Math.Max(max, word);
        }
        return min == 0 ? null : (min, max, step);
    }

    /// <summary>
    /// UNIFIED_BATTERY (0x1004) GetStatus: soc, level bitfield (8 full, 4 good, 2 low, 1 critical), status.
    /// Without the state-of-charge capability the soc byte is not a reading, so the level bits are used.
    /// </summary>
    public static (int level, bool charging) DecodeUnifiedBattery(byte soc, byte levelBits, byte status, bool hasSoc)
    {
        int level = hasSoc && soc > 0 ? soc : levelBits switch
        {
            8 => 90,
            4 => 50,
            2 => 20,
            1 => 5,
            _ => 0,
        };
        return (level, status is 1 or 2 or 3 or 4);
    }

    /// <summary>
    /// BATTERY_STATUS (0x1000) GetBatteryLevelStatus: level is a reading only while discharging (status 0).
    /// Status 1 recharging, 2 almost full, 3 full, 4 slow recharge, 5+ errors. Level -1 = unknown.
    /// </summary>
    public static (int level, bool charging)? DecodeBatteryStatus(byte level, byte status)
    {
        if (status == 3)
            return (100, true);
        if (status == 0)
            return level == 0 ? null : (level, false);
        return (level > 0 ? level : -1, status is 1 or 2 or 4);
    }

    /// <summary>
    /// BATTERY_VOLTAGE (0x1001) flags: bit 7 = external power, then bits 0-2:
    /// 0 charging, 1 full, 2 not charging, other unknown.
    /// </summary>
    public static bool DecodeVoltageCharging(byte flags) =>
        (flags & 0x80) != 0 && (flags & 0x07) is 0 or 1;

    /// <summary>
    /// Builds the 10 effect parameter bytes for 0x8070 setZoneEffect / 0x8071 setRgbClusterEffect.
    /// Returns null for an effect ID whose layout is not known.
    /// </summary>
    public static byte[]? BuildRgbEffectParams(ushort effectId, byte r, byte g, byte b, int periodMs)
    {
        var p = new byte[10];
        ushort period = (ushort)Math.Clamp(periodMs, 1000, 20000);
        switch (effectId)
        {
            case 0x00: // off
                break;
            case 0x01: // static: color @0, ramp @3
                p[0] = r; p[1] = g; p[2] = b;
                break;
            case 0x02: // pulse: color @0, speed @3 (one byte; unit unverified, period / 100 used)
                p[0] = r; p[1] = g; p[2] = b;
                p[3] = (byte)(period / 100);
                break;
            case 0x03: // cycle: period @5, intensity @7
                p[5] = (byte)(period >> 8); p[6] = (byte)period; p[7] = 100;
                break;
            case 0x0A: // breathe: color @0, period @3, form @5, intensity @6
                p[0] = r; p[1] = g; p[2] = b;
                p[3] = (byte)(period >> 8); p[4] = (byte)period; p[6] = 100;
                break;
            case 0x15: // cycle with saturation: saturation @1, period @6, intensity @8
                p[1] = 255; p[6] = (byte)(period >> 8); p[7] = (byte)period; p[8] = 100;
                break;
            case 0x16: // wave with saturation: saturation @1, period @6, intensity @8, direction @9
                p[1] = 255; p[6] = (byte)(period >> 8); p[7] = (byte)period; p[8] = 100;
                break;
            case 0x04: // wave: period @6, direction @9 (layout assumed from 0x16, unverified)
                p[6] = (byte)(period >> 8); p[7] = (byte)period;
                break;
            default:
                return null;
        }
        return p;
    }

    /// <summary>
    /// Decodes receiver register 0xB5 pairing info; data starts at the sub-register echo.
    /// Unifying-style 0x2N: wpid BE @3, kind @7 low nibble. Bolt 0x5N: kind @1 low nibble, wpid LE @2.
    /// Kind uses the receiver vocabulary (1 keyboard, 2 mouse), not the 0x0005 one.
    /// </summary>
    public static (ushort wpid, int kind)? DecodePairingInfo(ReadOnlySpan<byte> info, bool bolt)
    {
        if (info.Length < 8)
            return null;
        ushort wpid = bolt ? (ushort)(info[2] | (info[3] << 8)) : (ushort)((info[3] << 8) | info[4]);
        int kind = (bolt ? info[1] : info[7]) & 0x0F;
        return wpid == 0 ? null : (wpid, kind);
    }

    /// <summary>
    /// Decodes a receiver codename (0xB5 sub 0x4N, or Bolt 0x6N chunk 1); data starts at the
    /// sub-register echo. Unifying: [sub, len, name]. Bolt: [sub, chunk, len, name], 14 bytes max per chunk.
    /// </summary>
    public static string? DecodeCodename(ReadOnlySpan<byte> reply, bool bolt)
    {
        int lenAt = bolt ? 2 : 1;
        if (reply.Length <= lenAt)
            return null;
        int len = Math.Min(reply[lenAt], reply.Length - lenAt - 1);
        if (bolt)
            len = Math.Min(len, 14);
        return len <= 0 ? null : System.Text.Encoding.ASCII.GetString(reply.Slice(lenAt + 1, len));
    }

    /// <summary>
    /// True if a device the receiver lists is already connected: same name, or same slot (index, wpid).
    /// The slot is needed because connect renames the receiver codename to the full 0x0005 name.
    /// </summary>
    public static bool IsPairedDeviceConnected(IEnumerable<(string name, (byte index, ushort wpid)? slot)> connected,
        string name, (byte index, ushort wpid) slot) =>
        connected.Any(c => c.name == name || c.slot == slot);

    /// <summary>KEYBOARD_DISABLE_KEYS (0x4521) bitmask: Caps Lock, Num Lock, Scroll Lock, Insert, Win.</summary>
    public static string DisabledKeysSummary(byte mask)
    {
        string[] names = ["Caps Lock", "Num Lock", "Scroll Lock", "Insert", "Win"];
        var on = names.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
        return on.Length == 0 ? "-" : string.Join(", ", on);
    }

    /// <summary>Maps a raw value in [min, max] to 0-100.</summary>
    public static int ScaleToPercent(int raw, int min, int max) =>
        max <= min ? 0 : (int)Math.Round(100.0 * (Math.Clamp(raw, min, max) - min) / (max - min));

    /// <summary>Maps 0-100 to a raw value in [min, max].</summary>
    public static int PercentToRange(int percent, int min, int max) =>
        min + (int)Math.Round((max - min) * Math.Clamp(percent, 0, 100) / 100.0);

    /// <summary>BACKLIGHT2 (0x1982) durations are in 5 s units on the wire; rounds up, min 1.</summary>
    public static ushort BacklightSecondsToWire(int seconds) => (ushort)Math.Clamp((seconds + 4) / 5, 1, 0xFFFF);
}
