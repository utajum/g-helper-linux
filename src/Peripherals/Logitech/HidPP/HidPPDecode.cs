namespace GHelper.Linux.Peripherals.Logitech.HidPP;

/// <summary>
/// Pure HID++ payload decoders and encoders. No device I/O, so they are unit-tested.
/// Offsets are into the feature payload (reply byte 2 onward); receiver notification decoders take the whole report.
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
                p[0] = r;
                p[1] = g;
                p[2] = b;
                break;
            case 0x02: // pulse: color @0, speed @3 (one byte; unit unverified, period / 100 used)
                p[0] = r;
                p[1] = g;
                p[2] = b;
                p[3] = (byte)(period / 100);
                break;
            case 0x03: // cycle: period @5, intensity @7
                p[5] = (byte)(period >> 8);
                p[6] = (byte)period;
                p[7] = 100;
                break;
            case 0x0A: // breathe: color @0, period @3, form @5, intensity @6
                p[0] = r;
                p[1] = g;
                p[2] = b;
                p[3] = (byte)(period >> 8);
                p[4] = (byte)period;
                p[6] = 100;
                break;
            case 0x15: // cycle with saturation: saturation @1, period @6, intensity @8
                p[1] = 255;
                p[6] = (byte)(period >> 8);
                p[7] = (byte)period;
                p[8] = 100;
                break;
            case 0x16: // wave with saturation: saturation @1, period @6, intensity @8, direction @9
                p[1] = 255;
                p[6] = (byte)(period >> 8);
                p[7] = (byte)period;
                p[8] = 100;
                break;
            case 0x04: // wave: period @6, direction @9 (layout assumed from 0x16, unverified)
                p[6] = (byte)(period >> 8);
                p[7] = (byte)period;
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

    /// <summary>Product id from a hidraw sysfs uevent ("HID_ID=0003:0000046D:0000C52B"), or null.</summary>
    public static ushort? HidIdProduct(string uevent)
    {
        foreach (var line in uevent.Split('\n'))
        {
            if (line.StartsWith("HID_ID=") && line.Split(':') is [_, _, var pid] && pid.Trim().Length >= 4
                && ushort.TryParse(pid.Trim()[^4..], System.Globalization.NumberStyles.HexNumber, null, out ushort p))
                return p;
        }
        return null;
    }

    // Receiver notifications: [report id, device index, sub id, address, data...].

    /// <summary>
    /// HID++ 0x41 device connection: slot, wpid (LE @5), kind (low nibble @4), link up (bit 6 @4 clear).
    /// DJ reports (0x20) use the same sub id with another layout, so they are not taken.
    /// </summary>
    public static (byte index, ushort wpid, int kind, bool linkUp)? DecodeDeviceConnection(ReadOnlySpan<byte> r) =>
        r.Length >= 7 && r[0] is 0x10 or 0x11 && r[2] == 0x41 && r[1] is >= 1 and <= 6
            ? (r[1], (ushort)(r[5] | (r[6] << 8)), r[4] & 0x0F, (r[4] & 0x40) == 0)
            : null;

    /// <summary>0x4A pairing lock: open (bit 0 @3) and error @4 (1 timeout, 2 not supported, 3 too many, 6 sequence timeout).</summary>
    public static (bool open, byte error)? DecodeLockChange(ReadOnlySpan<byte> r) =>
        r.Length >= 5 && r[2] == 0x4A ? ((r[3] & 0x01) != 0, r[4]) : null;

    /// <summary>
    /// Unifying pairing session, fed with receiver reports. The lock is open only after a 0x4A open notice;
    /// the write reply alone does not mean it. A new slot seen while open is the paired device. A known slot
    /// counts only when the lock then closes without error, as a re-paired device keeps its slot.
    /// </summary>
    public sealed class UnifyingPairing(IReadOnlySet<byte> known)
    {
        private byte _newSlot, _knownSlot;

        public bool Open { get; private set; }
        public bool Closed { get; private set; }
        public byte Error { get; private set; }

        /// <summary>The paired slot, or 0.</summary>
        public byte Slot => _newSlot != 0 ? _newSlot : Closed && Error == 0 ? _knownSlot : (byte)0;

        /// <summary>True when nothing more can change the result: paired, failed, or closed before it opened.</summary>
        public bool Done => _newSlot != 0 || Error != 0 || (Closed && !Open);

        public void Feed(ReadOnlySpan<byte> r)
        {
            if (DecodeLockChange(r) is { } lockState)
            {
                if (lockState.error != 0)
                    Error = lockState.error;
                else if (lockState.open)
                    Open = true;
                else
                    Closed = true;
            }
            else if (Open && DecodeDeviceConnection(r) is { } c)
            {
                if (!known.Contains(c.index))
                    _newSlot = c.index;
                else if (c.linkUp)
                    _knownSlot = c.index;
            }
        }
    }

    /// <summary>Bolt 0x53 discovery status: discovering when @3 is 0, error @4 (1 timeout, 2 failed).</summary>
    public static (bool discovering, byte error)? DecodeDiscoveryStatus(ReadOnlySpan<byte> r) =>
        r.Length >= 5 && r[2] == 0x53 ? (r[3] == 0, r[4]) : null;

    /// <summary>One part of a Bolt 0x4F discovery: part 0 has kind, address and auth flags, part 1 the name.</summary>
    public record BoltDiscovery(int Counter, int Kind, byte[]? Address, byte Auth, string? Name);

    /// <summary>
    /// Bolt 0x4F: counter @3 (low) @4 (high), part @5. Part 0: kind @7, address @10..15, auth @18.
    /// Part 1: name length @6, name @7.
    /// </summary>
    public static BoltDiscovery? DecodeBoltDiscovery(ReadOnlySpan<byte> r)
    {
        if (r.Length < 19 || r[2] != 0x4F)
            return null;
        int counter = r[3] | (r[4] << 8);
        if (r[5] == 0)
            return new(counter, r[7], r.Slice(10, 6).ToArray(), r[18], null);
        if (r[5] != 1)
            return null;
        int len = Math.Min(r[6], r.Length - 7);
        return new(counter, 0, null, 0, System.Text.Encoding.ASCII.GetString(r.Slice(7, Math.Max(len, 0))));
    }

    /// <summary>Bolt 0x4D passkey request: up to six ASCII digits @4, NUL-padded.</summary>
    public static string? DecodePasskey(ReadOnlySpan<byte> r) =>
        r.Length >= 10 && r[2] == 0x4D ? System.Text.Encoding.ASCII.GetString(r.Slice(4, 6)).TrimEnd('\0') : null;

    /// <summary>Bolt 0x54 pairing status: state @3 (0 lock open, 2 done), error @4, new slot @11.</summary>
    public static (byte state, byte error, byte slot)? DecodePairingStatus(ReadOnlySpan<byte> r) =>
        r.Length >= 12 && r[2] == 0x54 ? (r[3], r[4], r[11]) : null;

    /// <summary>Mouse passkey entry: the passkey in binary, at least 10 digits; true is the right button.</summary>
    public static bool[] PasskeyClicks(string passkey) =>
        int.TryParse(passkey, out int value) && value >= 0
            ? Convert.ToString(value, 2).PadLeft(10, '0').Select(c => c == '1').ToArray()
            : [];

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

    /// <summary>RGB_EFFECTS (0x8071) fn 0x70 payload: [getOrSet, flags(2), power-save s(2), off s(2)], big-endian.</summary>
    public static (ushort flags, int powerSave, int off)? DecodeRgbPowerConfig(ReadOnlySpan<byte> p) =>
        p.Length < 7 ? null : ((ushort)(p[1] << 8 | p[2]), p[3] << 8 | p[4], p[5] << 8 | p[6]);

    /// <summary>Builds the fn 0x70 set payload; timeouts are clamped to 0..0xFFFF seconds.</summary>
    public static byte[] BuildRgbPowerConfig(ushort flags, int powerSave, int off)
    {
        int ps = Math.Clamp(powerSave, 0, 0xFFFF), o = Math.Clamp(off, 0, 0xFFFF);
        return [0x01, (byte)(flags >> 8), (byte)flags, (byte)(ps >> 8), (byte)ps, (byte)(o >> 8), (byte)o];
    }
}
