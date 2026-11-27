using GHelper.Linux.Helpers;

namespace GHelper.Linux.Peripherals.Logitech.HidPP;

/// <summary>
/// Provides typed methods for reading and writing Logitech HID++ 2.0
/// device settings: battery, DPI, polling rate, RGB, backlight, etc.
/// Each method is a static helper that operates on an <see cref="HidPPDevice"/>.
/// </summary>
public static class HidPPProtocol
{
    /// <summary>
    /// Reads battery status from the device.
    /// Tries UNIFIED_BATTERY first, then BATTERY_STATUS, then BATTERY_VOLTAGE.
    /// </summary>
    /// <returns>Battery level (0-100) and charging flag, or null if unsupported.</returns>
    public static (int level, bool charging)? GetBattery(HidPPDevice device)
    {
        var unified = GetBatteryUnified(device);
        if (unified is not null)
            return unified;

        var status = GetBatteryStatus(device);
        if (status is not null)
            return status;

        return GetBatteryVoltage(device);
    }

    /// <summary>
    /// Reads UNIFIED_BATTERY (0x1004) feature.
    /// Function 0x10 = GetStatus: [0]=stateOfCharge%, [1]=batteryLevel, [2]=chargingStatus, [3]=externalPowerStatus.
    /// </summary>
    private static (int level, bool charging)? GetBatteryUnified(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.UNIFIED_BATTERY) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.UNIFIED_BATTERY, 0x10);
        if (reply is null || reply.Length < 5)
            return null;

        // GetCapabilities fn0 payload[1] bit 1 = state-of-charge gauge; assume present if unreadable.
        byte[]? caps = device.FeatureRequest(Feature.UNIFIED_BATTERY, 0x00);
        bool hasSoc = caps is null || caps.Length < 4 || (caps[3] & 0x02) != 0;

        return HidPPDecode.DecodeUnifiedBattery(reply[2], reply[3], reply[4], hasSoc);
    }

    /// <summary>
    /// Reads BATTERY_STATUS (0x1000) feature.
    /// Function 0x00 = GetStatus: [0]=level%, [1]=nextLevel%, [2]=status.
    /// Status: 0=discharging, 1=recharging, 2=almost full, 3=complete, 4=slow recharge.
    /// </summary>
    private static (int level, bool charging)? GetBatteryStatus(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.BATTERY_STATUS) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.BATTERY_STATUS, 0x00);
        if (reply is null || reply.Length < 5)
            return null;

        return HidPPDecode.DecodeBatteryStatus(reply[2], reply[4]);
    }

    /// <summary>
    /// Reads BATTERY_VOLTAGE (0x1001) feature.
    /// Function 0x00 = GetBatteryVoltage: [0-1]=voltage (big-endian mV), [2]=flags.
    /// Estimates percentage from voltage using linear interpolation.
    /// </summary>
    private static (int level, bool charging)? GetBatteryVoltage(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.BATTERY_VOLTAGE) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.BATTERY_VOLTAGE, 0x00);
        if (reply is null || reply.Length < 5)
            return null;

        int voltage = (reply[2] << 8) | reply[3];
        int level = EstimateBatteryPercentage(voltage);
        return (level, HidPPDecode.DecodeVoltageCharging(reply[4]));
    }

    /// <summary>
    /// Estimates battery percentage from voltage in mV.
    /// </summary>
    private static int EstimateBatteryPercentage(int millivolts)
    {
        ReadOnlySpan<(int voltage, int percent)> table =
        [
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60),
            (3811, 50),  (3778, 40), (3751, 30), (3717, 20), (3671, 10),
            (3646, 5),   (3579, 2),  (3500, 0),
        ];

        if (millivolts >= table[0].voltage)
            return 100;
        if (millivolts <= table[^1].voltage)
            return 0;

        for (int i = 0; i < table.Length - 1; i++)
        {
            var (vHigh, pHigh) = table[i];
            var (vLow, pLow) = table[i + 1];
            if (millivolts >= vLow && millivolts <= vHigh)
            {
                float pct = pLow + (pHigh - pLow) * (float)(millivolts - vLow) / (vHigh - vLow);
                return (int)Math.Round(pct);
            }
        }

        return 0;
    }

    /// <summary>
    /// Reads the device name via DEVICE_NAME (0x0005) feature.
    /// Function 0x00 = GetNameLength, function 0x10 = GetName (chunked).
    /// </summary>
    public static string? GetDeviceName(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.DEVICE_NAME) < 0)
            return null;

        byte[]? lenReply = device.FeatureRequest(Feature.DEVICE_NAME, 0x00);
        if (lenReply is null || lenReply.Length < 3)
            return null;

        int nameLen = lenReply[2];
        if (nameLen == 0)
            return null;

        var nameBytes = new byte[nameLen];
        int offset = 0;
        while (offset < nameLen)
        {
            byte[]? chunk = device.FeatureRequest(Feature.DEVICE_NAME, 0x10, (byte)offset);
            if (chunk is null || chunk.Length < 3)
                break;

            // Payload starts at index 2 (after subId, addr).
            int available = Math.Min(chunk.Length - 2, nameLen - offset);
            Buffer.BlockCopy(chunk, 2, nameBytes, offset, available);
            offset += available;
        }

        return System.Text.Encoding.UTF8.GetString(nameBytes, 0, Math.Min(offset, nameLen));
    }

    /// <summary>
    /// Reads the device unit ID via DEVICE_UNIT_ID (0x0004).
    /// Returns a hex string (e.g. "A1B2C3D4") or null if unsupported.
    /// </summary>
    public static string? GetUnitId(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.DEVICE_UNIT_ID) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.DEVICE_UNIT_ID, 0x00);
        if (reply is null || reply.Length < 6)
            return null;

        // Payload starts at byte 2. Unit ID is 4 bytes (bytes 2-5).
        return $"{reply[2]:X2}{reply[3]:X2}{reply[4]:X2}{reply[5]:X2}";
    }

    /// <summary>
    /// Reads the firmware version via DEVICE_FW_VERSION (0x0003) feature.
    /// Function 0x00 = GetEntityCount, function 0x10 = GetFwInfo.
    /// Returns the main firmware version string (e.g. "09.02.B0004").
    /// </summary>
    public static string? GetFirmwareVersion(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.DEVICE_FW_VERSION) < 0)
            return null;

        byte[]? countReply = device.FeatureRequest(Feature.DEVICE_FW_VERSION, 0x00);
        if (countReply is null || countReply.Length < 3)
            return null;

        int count = countReply[2];

        for (int i = 0; i < count; i++)
        {
            byte[]? info = device.FeatureRequest(Feature.DEVICE_FW_VERSION, 0x10, (byte)i);
            if (info is null || info.Length < 10)
                continue;

            // info[2] = entity type (low nibble): 0=firmware, 1=bootloader, 2=hardware
            int entityType = info[2] & 0x0F;
            if (entityType != 0)
                continue;

            // info[3..5] = name (3 ASCII chars)
            // info[6] = version major (hex)
            // info[7] = version minor (hex)
            // info[8..9] = build number (big-endian)
            byte major = info[6];
            byte minor = info[7];
            int build = (info[8] << 8) | info[9];

            string version = $"{major:X2}.{minor:X2}";
            if (build != 0)
                version += $".B{build:X4}";

            return version;
        }

        return null;
    }

    /// <summary>
    /// Reads the supported DPI range. ADJUSTABLE_DPI (0x2201) fn 0x10 getSensorDpiList(sensor),
    /// or EXTENDED_ADJUSTABLE_DPI (0x2202) fn 0x20 getSensorDpiRanges(sensor, X, page), paged.
    /// </summary>
    public static (int min, int max, int step)? GetDPIRange(HidPPDevice device, byte sensor = 0)
    {
        if (device.GetFeatureIndex(Feature.ADJUSTABLE_DPI) >= 0)
        {
            byte[]? reply = device.FeatureRequest(Feature.ADJUSTABLE_DPI, 0x10, sensor);
            if (reply is null || reply.Length < 5 || reply[2] != sensor)
                return null;
            return HidPPDecode.ParseDpiWords(reply.AsSpan(3));
        }

        if (device.GetFeatureIndex(Feature.EXTENDED_ADJUSTABLE_DPI) < 0)
            return null;

        var words = new List<byte>();
        for (byte page = 0; page < 16; page++)
        {
            byte[]? reply = device.FeatureRequest(Feature.EXTENDED_ADJUSTABLE_DPI, 0x20, sensor, 0x00, page);
            if (reply is null || reply.Length < 7 || reply[2] != sensor || reply[3] != 0 || reply[4] != page)
                return null;
            words.AddRange(reply.AsSpan(5).ToArray());
            for (int i = 0; i + 1 < words.Count; i += 2)
            {
                if (words[i] == 0 && words[i + 1] == 0)
                    return HidPPDecode.ParseDpiWords(words.ToArray());
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the current DPI. 0x2201 fn 0x20 getSensorDpi: [sensor, dpi(2), default(2)].
    /// 0x2202 fn 0x50 getSensorDpiParameters: [sensor, x(2), defaultX(2), y(2), defaultY(2), lod].
    /// </summary>
    public static int? GetDPI(HidPPDevice device, byte sensor = 0)
    {
        if (device.GetFeatureIndex(Feature.ADJUSTABLE_DPI) >= 0)
        {
            byte[]? reply = device.FeatureRequest(Feature.ADJUSTABLE_DPI, 0x20, sensor);
            if (reply is null || reply.Length < 5)
                return null;
            return (reply[3] << 8) | reply[4];
        }

        if (device.GetFeatureIndex(Feature.EXTENDED_ADJUSTABLE_DPI) < 0)
            return null;

        byte[]? ext = device.FeatureRequest(Feature.EXTENDED_ADJUSTABLE_DPI, 0x50, sensor);
        if (ext is null || ext.Length < 12 || ext[2] != sensor)
            return null;
        int x = (ext[3] << 8) | ext[4];
        return x != 0 ? x : (ext[5] << 8) | ext[6];
    }

    /// <summary>
    /// Sets the DPI. 0x2201 fn 0x30 setSensorDpi [sensor, dpi(2)].
    /// 0x2202 fn 0x60 setSensorDpiParameters [sensor, x(2), y(2) or 0, lod or 0];
    /// Y and LOD only when the sensor capabilities (fn 0x10) report them, LOD kept as read.
    /// </summary>
    public static bool SetDPI(HidPPDevice device, int dpi, byte sensor = 0)
    {
        byte hi = (byte)(dpi >> 8), lo = (byte)(dpi & 0xFF);

        if (device.GetFeatureIndex(Feature.ADJUSTABLE_DPI) >= 0)
            return device.FeatureRequest(Feature.ADJUSTABLE_DPI, 0x30, sensor, hi, lo) is not null;

        if (device.GetFeatureIndex(Feature.EXTENDED_ADJUSTABLE_DPI) < 0)
            return false;

        byte[]? caps = device.FeatureRequest(Feature.EXTENDED_ADJUSTABLE_DPI, 0x10, sensor);
        if (caps is null || caps.Length < 5 || caps[2] != sensor)
            return false;
        bool hasY = (caps[4] & 0x01) != 0;
        byte lod = 0;
        if ((caps[4] & 0x02) != 0)
        {
            byte[]? cur = device.FeatureRequest(Feature.EXTENDED_ADJUSTABLE_DPI, 0x50, sensor);
            if (cur is null || cur.Length < 12 || cur[2] != sensor)
                return false;
            lod = cur[11];
        }

        return device.FeatureRequest(Feature.EXTENDED_ADJUSTABLE_DPI, 0x60, sensor,
            hi, lo, hasY ? hi : (byte)0, hasY ? lo : (byte)0, lod) is not null;
    }

    /// <summary>
    /// Reads the polling (report) rate via REPORT_RATE (0x8060).
    /// Function 0x00 = GetReportRateList: returns a bitmask of supported rates.
    /// Function 0x10 = GetReportRate: returns current rate in ms.
    /// Bitmask: bit0=1ms, bit1=2ms, bit2=4ms, bit3=8ms, bit4=500us, bit5=250us, bit6=125us.
    /// </summary>
    public static (int currentMs, int[] supportedMs)? GetPollingRate(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.REPORT_RATE) < 0)
            return null;

        byte[]? listReply = device.FeatureRequest(Feature.REPORT_RATE, 0x00);
        if (listReply is null || listReply.Length < 3)
            return null;

        byte rateBits = listReply[2];
        var supported = new List<int>();
        // REPORT_RATE bitmask: bit N = (N+1) ms
        for (int i = 0; i < 8; i++)
        {
            if ((rateBits & (1 << i)) != 0)
                supported.Add(i + 1);
        }

        byte[]? curReply = device.FeatureRequest(Feature.REPORT_RATE, 0x10);
        if (curReply is null || curReply.Length < 3)
            return null;

        int currentMs = curReply[2];

        return (currentMs, supported.ToArray());
    }

    /// <summary>
    /// Sets the polling rate in ms via REPORT_RATE (0x8060), function 0x20.
    /// </summary>
    public static bool SetPollingRate(HidPPDevice device, int rateMs)
    {
        if (device.GetFeatureIndex(Feature.REPORT_RATE) < 0)
            return false;

        byte[]? reply = device.FeatureRequest(Feature.REPORT_RATE, 0x20, (byte)rateMs);
        return reply is not null;
    }

    /// <summary>
    /// Reads the rate via EXTENDED_ADJUSTABLE_REPORT_RATE (0x8061). fn 0x10 getActualReportRateList:
    /// payload[1] bit N = rate index N (0=125Hz .. 6=8000Hz). fn 0x20 getReportRate [connection]:
    /// payload[0] = index; connection 0 = wired (direct index 0xFF), 1 = gaming wireless.
    /// </summary>
    public static (int currentIndex, int[] supportedIndices)? GetExtendedPollingRate(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.EXTENDED_ADJUSTABLE_REPORT_RATE) < 0)
            return null;

        byte[]? listReply = device.FeatureRequest(Feature.EXTENDED_ADJUSTABLE_REPORT_RATE, 0x10);
        if (listReply is null || listReply.Length < 4)
            return null;

        var supported = new List<int>();
        for (int i = 0; i < 7; i++)
        {
            if ((listReply[3] & (1 << i)) != 0)
                supported.Add(i);
        }

        byte connection = (byte)(device.DeviceIndex == 0xFF ? 0x00 : 0x01);
        byte[]? curReply = device.FeatureRequest(Feature.EXTENDED_ADJUSTABLE_REPORT_RATE, 0x20, connection);
        if (curReply is null || curReply.Length < 3 || curReply[2] > 6)
            return null;

        return (curReply[2], supported.ToArray());
    }

    /// <summary>
    /// Sets extended polling rate via EXTENDED_ADJUSTABLE_REPORT_RATE (0x8061), function 0x30.
    /// </summary>
    public static bool SetExtendedPollingRate(HidPPDevice device, byte rateIndex)
    {
        if (device.GetFeatureIndex(Feature.EXTENDED_ADJUSTABLE_REPORT_RATE) < 0 || rateIndex > 6)
            return false;

        byte[]? reply = device.FeatureRequest(Feature.EXTENDED_ADJUSTABLE_REPORT_RATE, 0x30, rateIndex);
        return reply is not null;
    }

    /// <summary>
    /// Reads smart shift state via SMART_SHIFT (0x2110).
    /// Function 0x00 = GetRatchetControlMode:
    ///   [0]=mode (1=freewheel, 2=ratchet), [1]=autoDisengage threshold.
    /// </summary>
    public static (bool ratchet, int autoThreshold)? GetSmartShift(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.SMART_SHIFT) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.SMART_SHIFT, 0x00);
        if (reply is null || reply.Length < 4)
            return null;

        byte mode = reply[2]; // 1=freewheel, 2=ratchet
        byte threshold = reply[3];
        return (mode == 2, threshold);
    }

    /// <summary>
    /// Sets smart shift mode via SMART_SHIFT (0x2110), function 0x10.
    /// </summary>
    public static bool SetSmartShift(HidPPDevice device, bool ratchet, int autoThreshold = 0)
    {
        if (device.GetFeatureIndex(Feature.SMART_SHIFT) < 0)
            return false;

        byte mode = (byte)(ratchet ? 2 : 1);
        byte threshold = (byte)Math.Clamp(autoThreshold, 0, 255);

        byte[]? reply = device.FeatureRequest(Feature.SMART_SHIFT, 0x10, mode, threshold);
        return reply is not null;
    }

    /// <summary>
    /// Reads hi-res wheel mode via HIRES_WHEEL (0x2121).
    /// Function 0x10 = GetWheelMode: byte[0] bitfield:
    ///   bit 0 = target (HID++ vs HID), bit 1 = hi-res, bit 2 = invert.
    /// </summary>
    public static (bool hiRes, bool invert, bool divert)? GetHiResScroll(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.HIRES_WHEEL) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.HIRES_WHEEL, 0x10);
        if (reply is null || reply.Length < 3)
            return null;

        byte wheelMode = reply[2];
        bool divert = (wheelMode & 0x01) != 0;
        bool hiRes = (wheelMode & 0x02) != 0;
        bool invert = (wheelMode & 0x04) != 0;
        return (hiRes, invert, divert);
    }

    /// <summary>
    /// Sets hi-res wheel mode via HIRES_WHEEL (0x2121), function 0x20.
    /// </summary>
    public static bool SetHiResScroll(HidPPDevice device, bool hiRes, bool invert, bool divert = false)
    {
        if (device.GetFeatureIndex(Feature.HIRES_WHEEL) < 0)
            return false;

        // Read-modify-write: bits 3-7 and bytes 1-2 are not ours and go back as read.
        byte[]? cur = device.FeatureRequest(Feature.HIRES_WHEEL, 0x10);
        if (cur is null || cur.Length < 5)
            return false;

        byte mode = (byte)(cur[2] & ~0x07);
        if (divert)
            mode |= 0x01;
        if (hiRes)
            mode |= 0x02;
        if (invert)
            mode |= 0x04;

        byte[]? reply = device.FeatureRequest(Feature.HIRES_WHEEL, 0x20, mode, cur[3], cur[4]);
        return reply is not null;
    }

    /// <summary>
    /// Gets the number of RGB zones. RGB_EFFECTS (0x8071) fn 0x00 [0xFF, 0xFF, 0]: payload[2].
    /// COLOR_LED_EFFECTS (0x8070) fn 0x00 getInfo: payload[0].
    /// </summary>
    public static int GetRGBZoneCount(HidPPDevice device, ushort feature)
    {
        if (device.GetFeatureIndex(feature) < 0)
            return 0;

        if (feature == Feature.COLOR_LED_EFFECTS)
        {
            byte[]? info = device.FeatureRequest(feature, 0x00);
            return info is null || info.Length < 3 ? 0 : info[2];
        }

        byte[]? reply = device.FeatureRequest(feature, 0x00, 0xFF, 0xFF, 0x00);
        if (reply is null || reply.Length < 5)
            return 0;
        return reply[4];
    }

    /// <summary>
    /// Lists the effect IDs a zone offers, by effect index. 0xFFFF = unreadable.
    /// 0x8071 fn 0x00 [zone, 0xFF, 0]: payload [zone, ?, location(2), count]; [zone, i, 0]: [zone, i, id(2)].
    /// 0x8070 fn 0x10 [zone]: payload [zone, location(2), count]; fn 0x20 [zone, i]: [zone, i, id(2)].
    /// </summary>
    public static ushort[] GetRGBZoneEffectIds(HidPPDevice device, ushort feature, byte zone)
    {
        if (device.GetFeatureIndex(feature) < 0)
            return [];

        bool led = feature == Feature.COLOR_LED_EFFECTS;
        byte[]? info = led
            ? device.FeatureRequest(feature, 0x10, zone)
            : device.FeatureRequest(feature, 0x00, zone, 0xFF, 0x00);
        if (info is null || info.Length < 7 || info[2] != zone)
            return [];

        var ids = new ushort[led ? info[5] : info[6]];
        for (int i = 0; i < ids.Length; i++)
        {
            byte[]? e = led
                ? device.FeatureRequest(feature, 0x20, zone, (byte)i)
                : device.FeatureRequest(feature, 0x00, zone, (byte)i, 0x00);
            ids[i] = e is not null && e.Length >= 6 && e[2] == zone && e[3] == i
                ? (ushort)((e[4] << 8) | e[5])
                : (ushort)0xFFFF;
        }
        return ids;
    }

    /// <summary>
    /// Reads a zone's active effect via COLOR_LED_EFFECTS (0x8070) fn 0xE0 getZoneEffect [zone, RAM]:
    /// payload [zone, effectIndex, params(10)]. Only when getInfo ext caps (payload[3..4]) bit 0 is set.
    /// </summary>
    public static (byte index, byte[] effectParams)? GetRGBZoneEffect(HidPPDevice device, byte zone)
    {
        if (device.GetFeatureIndex(Feature.COLOR_LED_EFFECTS) < 0)
            return null;

        byte[]? info = device.FeatureRequest(Feature.COLOR_LED_EFFECTS, 0x00);
        if (info is null || info.Length < 7 || (info[6] & 0x01) == 0)
            return null;

        byte[]? r = device.FeatureRequest(Feature.COLOR_LED_EFFECTS, 0xE0, zone, 0x00);
        if (r is null || r.Length < 14 || r[2] != zone)
            return null;
        return (r[3], r[4..14]);
    }

    /// <summary>
    /// Takes software control of all RGB clusters via RGB_EFFECTS (0x8071) fn 0x50 manageSwControl,
    /// keeping the other control and event flags. Returns the flags read before the claim.
    /// </summary>
    public static (byte control, byte events)? ClaimRGBControl(HidPPDevice device)
    {
        byte[]? cur = device.FeatureRequest(Feature.RGB_EFFECTS, 0x50, 0x00, 0x00, 0x00);
        if (cur is null || cur.Length < 5)
            return null;
        byte control = cur[3], events = cur[4];
        if (device.FeatureRequest(Feature.RGB_EFFECTS, 0x50, 0x01, (byte)(control | 0x01), events) is null)
            return null;
        return (control, events);
    }

    /// <summary>Reads RGB_EFFECTS (0x8071) fn 0x70 getRgbPowerModeConfig: no-activity seconds to power save and to off.</summary>
    public static (ushort flags, int powerSave, int off)? GetRGBPowerConfig(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.RGB_EFFECTS) < 0)
            return null;
        byte[]? r = device.FeatureRequest(Feature.RGB_EFFECTS, 0x70, 0x00, 0x00, 0x00);
        return r is null || r.Length < 9 ? null : HidPPDecode.DecodeRgbPowerConfig(r.AsSpan(2));
    }

    /// <summary>Writes RGB_EFFECTS (0x8071) fn 0x70 setRgbPowerModeConfig, keeping the flags as read.</summary>
    public static bool SetRGBPowerConfig(HidPPDevice device, ushort flags, int powerSave, int off) =>
        device.FeatureRequest(Feature.RGB_EFFECTS, 0x70, HidPPDecode.BuildRgbPowerConfig(flags, powerSave, off)) is not null;

    /// <summary>Returns cluster control to the firmware, restoring the flags read before the claim.</summary>
    public static void ReleaseRGBControl(HidPPDevice device, byte control, byte events)
    {
        device.FeatureRequest(Feature.RGB_EFFECTS, 0x50, 0x01, (byte)(control & ~0x01), events);
    }

    /// <summary>
    /// Applies an effect to a zone, volatile: [zone, effectIndex, params(10), persistence].
    /// 0x8071 fn 0x10 setRgbClusterEffect (persistence 0x01 = RAM, full power; needs software control).
    /// 0x8070 fn 0x30 setZoneEffect (persistence 0x00 = RAM).
    /// </summary>
    public static bool SetRGBZoneEffect(HidPPDevice device, ushort feature, byte zone, byte effectIndex, byte[] effectParams)
    {
        if (device.GetFeatureIndex(feature) < 0 || effectParams.Length != 10)
            return false;

        bool led = feature == Feature.COLOR_LED_EFFECTS;
        var args = new byte[13];
        args[0] = zone;
        args[1] = effectIndex;
        effectParams.CopyTo(args, 2);
        args[12] = (byte)(led ? 0x00 : 0x01);
        return device.FeatureRequest(feature, (byte)(led ? 0x30 : 0x10), args) is not null;
    }

    /// <summary>
    /// Reads the backlight configuration via BACKLIGHT2 (0x1982), function 0x00.
    /// Returns the current brightness level (0-based index).
    /// </summary>
    public static int? GetBacklightLevel(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.BACKLIGHT2) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.BACKLIGHT2, 0x00);
        if (reply is null || reply.Length < 8)
            return null;

        // [subId, addr, enabled, options, supported, effectsHi, effectsLo, level, ...]
        return reply[7];
    }

    /// <summary>
    /// Sets the backlight brightness via BACKLIGHT2 (0x1982), function 0x10.
    /// </summary>
    public static bool SetBacklightLevel(HidPPDevice device, int level)
    {
        if (device.GetFeatureIndex(Feature.BACKLIGHT2) < 0)
            return false;

        byte[]? current = device.FeatureRequest(Feature.BACKLIGHT2, 0x00);
        if (current is null || current.Length < 14)
            return false;

        // SetBacklightConfig: [enabled, options, 0xFF, level, dho, dhi, dpow] (durations u16 LE)
        byte enabled = current[2];
        byte options = current[3];
        // Force manual mode (mode=3 in bits 3-4) to allow direct level control; keep other bits.
        options = (byte)((options & ~0x18) | (0x03 << 3));

        byte[]? reply = device.FeatureRequest(Feature.BACKLIGHT2, 0x10,
            enabled, options, 0xFF, (byte)level,
            current[8], current[9],
            current[10], current[11],
            current[12], current[13]);

        return reply is not null;
    }

    /// <summary>
    /// Reads BACKLIGHT2 (0x1982) durations in seconds: hands-out, hands-in, powered.
    /// getBacklightConfig payload [6..11], u16 LE in 5 s units.
    /// </summary>
    public static (int handsOut, int handsIn, int powered)? GetBacklightDelays(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.BACKLIGHT2) < 0)
            return null;

        byte[]? cur = device.FeatureRequest(Feature.BACKLIGHT2, 0x00);
        if (cur is null || cur.Length < 14)
            return null;

        return ((cur[8] | (cur[9] << 8)) * 5, (cur[10] | (cur[11] << 8)) * 5, (cur[12] | (cur[13] << 8)) * 5);
    }

    /// <summary>
    /// Writes BACKLIGHT2 (0x1982) durations in seconds, keeping enabled, options and level as read.
    /// Level is sent only in manual mode (3), as the feature ignores it otherwise.
    /// </summary>
    public static bool SetBacklightDelays(HidPPDevice device, int handsOut, int handsIn, int powered)
    {
        if (device.GetFeatureIndex(Feature.BACKLIGHT2) < 0)
            return false;

        byte[]? cur = device.FeatureRequest(Feature.BACKLIGHT2, 0x00);
        if (cur is null || cur.Length < 14)
            return false;

        ushort dho = HidPPDecode.BacklightSecondsToWire(handsOut);
        ushort dhi = HidPPDecode.BacklightSecondsToWire(handsIn);
        ushort dpow = HidPPDecode.BacklightSecondsToWire(powered);
        byte level = ((cur[3] >> 3) & 0x03) == 0x03 ? cur[7] : (byte)0;

        return device.FeatureRequest(Feature.BACKLIGHT2, 0x10,
            cur[2], cur[3], 0xFF, level,
            (byte)dho, (byte)(dho >> 8),
            (byte)dhi, (byte)(dhi >> 8),
            (byte)dpow, (byte)(dpow >> 8)) is not null;
    }

    /// <summary>
    /// Gets the active onboard profile via ONBOARD_PROFILES (0x8100) fn 0x40 getCurrentProfile:
    /// payload [profile(2)] BE.
    /// </summary>
    public static int? GetActiveProfile(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.ONBOARD_PROFILES) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.ONBOARD_PROFILES, 0x40);
        if (reply is null || reply.Length < 4)
            return null;

        return (reply[2] << 8) | reply[3];
    }

    /// <summary>
    /// Sets the active onboard profile via ONBOARD_PROFILES (0x8100) fn 0x30 setCurrentProfile [profile(2)].
    /// </summary>
    public static bool SetActiveProfile(HidPPDevice device, int profile)
    {
        if (device.GetFeatureIndex(Feature.ONBOARD_PROFILES) < 0)
            return false;

        byte[]? reply = device.FeatureRequest(Feature.ONBOARD_PROFILES, 0x30, (byte)(profile >> 8), (byte)profile);
        return reply is not null;
    }

    /// <summary>
    /// Reads the onboard mode via ONBOARD_PROFILES (0x8100), function 0x20.
    /// Returns true for onboard mode (0x01), false for host mode (0x02).
    /// </summary>
    public static bool? GetOnboardMode(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.ONBOARD_PROFILES) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.ONBOARD_PROFILES, 0x20);
        if (reply is null || reply.Length < 3)
            return null;

        return reply[2] == 0x01;
    }

    /// <summary>
    /// Sets the onboard mode via ONBOARD_PROFILES (0x8100), function 0x10.
    /// When onboard=true, the device uses stored profiles (0x01).
    /// When onboard=false, the device is controlled by host software (0x02).
    /// </summary>
    public static bool SetOnboardMode(HidPPDevice device, bool onboard)
    {
        if (device.GetFeatureIndex(Feature.ONBOARD_PROFILES) < 0)
            return false;

        byte mode = (byte)(onboard ? 0x01 : 0x02);
        byte[]? reply = device.FeatureRequest(Feature.ONBOARD_PROFILES, 0x10, mode);
        return reply is not null;
    }

    /// <summary>
    /// BRIGHTNESS_CONTROL (0x8040) fn 0x00 getInfo: payload [max(2), steps, caps, min(2)].
    /// Caps bit 2 = illumination on/off via fn 0x30 get / fn 0x40 set.
    /// </summary>
    private static (int min, int max, bool onOff)? GetBrightnessInfo(HidPPDevice device)
    {
        byte[]? info = device.FeatureRequest(Feature.BRIGHTNESS_CONTROL, 0x00);
        if (info is null || info.Length < 8)
            return null;
        int max = (info[2] << 8) | info[3];
        int min = (info[6] << 8) | info[7];
        return max > min ? (min, max, (info[5] & 0x04) != 0) : null;
    }

    /// <summary>
    /// Reads LED brightness via BRIGHTNESS_CONTROL (0x8040) fn 0x10 as 0-100 of the device range.
    /// </summary>
    public static int? GetBrightnessControl(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.BRIGHTNESS_CONTROL) < 0)
            return null;

        var info = GetBrightnessInfo(device);
        if (info is null)
            return null;
        if (info.Value.onOff)
        {
            byte[]? on = device.FeatureRequest(Feature.BRIGHTNESS_CONTROL, 0x30);
            if (on is not null && on.Length >= 3 && (on[2] & 0x01) == 0)
                return 0;
        }

        byte[]? reply = device.FeatureRequest(Feature.BRIGHTNESS_CONTROL, 0x10);
        if (reply is null || reply.Length < 4)
            return null;

        return HidPPDecode.ScaleToPercent((reply[2] << 8) | reply[3], info.Value.min, info.Value.max);
    }

    /// <summary>
    /// Sets LED brightness via BRIGHTNESS_CONTROL (0x8040) fn 0x20, level 0-100 scaled to the device range.
    /// On devices with illumination on/off, 0 switches it off and anything else switches it on.
    /// </summary>
    public static bool SetBrightnessControl(HidPPDevice device, int level)
    {
        if (device.GetFeatureIndex(Feature.BRIGHTNESS_CONTROL) < 0)
            return false;

        var info = GetBrightnessInfo(device);
        if (info is null)
            return false;
        if (info.Value.onOff)
        {
            if (device.FeatureRequest(Feature.BRIGHTNESS_CONTROL, 0x40, (byte)(level > 0 ? 0x01 : 0x00)) is null)
                return false;
            if (level <= 0)
                return true;
        }

        int raw = HidPPDecode.PercentToRange(level, info.Value.min, info.Value.max);
        byte[]? reply = device.FeatureRequest(Feature.BRIGHTNESS_CONTROL, 0x20,
            (byte)(raw >> 8), (byte)(raw & 0xFF));
        return reply is not null;
    }

    /// <summary>
    /// Reads smart shift state via SMART_SHIFT_ENHANCED (0x2111), function 0x10.
    /// Returns mode, auto-disengage threshold, and ratchet torque.
    /// </summary>
    public static (bool ratchet, int autoThreshold, int torque)? GetSmartShiftEnhanced(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.SMART_SHIFT_ENHANCED) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.SMART_SHIFT_ENHANCED, 0x10);
        if (reply is null || reply.Length < 5)
            return null;

        byte mode = reply[2]; // 1=freewheel, 2=ratchet
        byte threshold = reply[3];
        byte torque = reply[4];
        return (mode == 2, threshold, torque);
    }

    /// <summary>
    /// Sets smart shift mode via SMART_SHIFT_ENHANCED (0x2111), function 0x20.
    /// </summary>
    public static bool SetSmartShiftEnhanced(HidPPDevice device, bool ratchet,
        int autoThreshold = 0, int torque = 0)
    {
        if (device.GetFeatureIndex(Feature.SMART_SHIFT_ENHANCED) < 0)
            return false;

        byte mode = (byte)(ratchet ? 2 : 1);
        byte thresholdVal = (byte)Math.Clamp(autoThreshold, 0, 255);
        byte torqueVal = (byte)Math.Clamp(torque, 0, 100);

        byte[]? reply = device.FeatureRequest(Feature.SMART_SHIFT_ENHANCED, 0x20,
            mode, thresholdVal, torqueVal);
        return reply is not null;
    }

    /// <summary>
    /// Reads thumb wheel status via THUMB_WHEEL (0x2150), function 0x10.
    /// Response bytes: [0]=divert flags, [1]=invert flags.
    /// Divert bit 0: HID++ notification mode. Invert bit 0: scroll direction inverted.
    /// </summary>
    public static (bool divert, bool invert)? GetThumbWheel(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.THUMB_WHEEL) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.THUMB_WHEEL, 0x10);
        if (reply is null || reply.Length < 4)
            return null;

        bool divert = (reply[2] & 0x01) != 0;
        bool invert = (reply[3] & 0x01) != 0;
        return (divert, invert);
    }

    /// <summary>
    /// Sets thumb wheel mode via THUMB_WHEEL (0x2150), function 0x20.
    /// </summary>
    public static bool SetThumbWheel(HidPPDevice device, bool divert, bool invert)
    {
        if (device.GetFeatureIndex(Feature.THUMB_WHEEL) < 0)
            return false;

        byte divertByte = (byte)(divert ? 0x01 : 0x00);
        byte invertByte = (byte)(invert ? 0x01 : 0x00);

        byte[]? reply = device.FeatureRequest(Feature.THUMB_WHEEL, 0x20, divertByte, invertByte);
        return reply is not null;
    }

    /// <summary>
    /// Reads the number of reprogrammable controls via REPROG_CONTROLS_V4 (0x1B04),
    /// function 0x00. Falls back to REPROG_CONTROLS_V2 (0x1B01) if V4 is absent.
    /// </summary>
    public static int GetReprogControlsCount(HidPPDevice device)
    {
        ushort feature = device.GetFeatureIndex(Feature.REPROG_CONTROLS_V4) >= 0
            ? Feature.REPROG_CONTROLS_V4
            : Feature.REPROG_CONTROLS_V2;

        if (device.GetFeatureIndex(feature) < 0)
            return 0;

        byte[]? reply = device.FeatureRequest(feature, 0x00);
        if (reply is null || reply.Length < 3)
            return 0;

        return reply[2];
    }

    /// <summary>
    /// Reads info for a single reprogrammable control via REPROG_CONTROLS_V4 (0x1B04),
    /// function 0x10. Returns control ID (CID), native task ID, and capability flags.
    /// </summary>
    public static (ushort cid, ushort taskId, ushort flags)? GetReprogControlInfo(
        HidPPDevice device, byte index)
    {
        ushort feature = device.GetFeatureIndex(Feature.REPROG_CONTROLS_V4) >= 0
            ? Feature.REPROG_CONTROLS_V4
            : Feature.REPROG_CONTROLS_V2;

        if (device.GetFeatureIndex(feature) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(feature, 0x10, index);
        if (reply is null || reply.Length < 7)
            return null;

        ushort cid = (ushort)((reply[2] << 8) | reply[3]);
        ushort taskId = (ushort)((reply[4] << 8) | reply[5]);
        byte flags1 = reply[6];
        byte flags2 = reply.Length > 10 ? reply[10] : (byte)0;
        ushort flags = (ushort)(flags1 | (flags2 << 8));
        return (cid, taskId, flags);
    }

    /// <summary>
    /// Reads low-resolution wheel reporting mode via LOWRES_WHEEL (0x2130), function 0x00.
    /// Returns true if the wheel reports via HID++ (diverted), false for standard HID.
    /// </summary>
    public static bool? GetLowResWheelDiverted(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.LOWRES_WHEEL) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.LOWRES_WHEEL, 0x00);
        if (reply is null || reply.Length < 3)
            return null;

        return (reply[2] & 0x01) != 0;
    }

    /// <summary>
    /// Reads hi-res scrolling mode via HI_RES_SCROLLING (0x2120), function 0x00.
    /// This is the older feature (distinct from HIRES_WHEEL 0x2121).
    /// Returns (enabled, resolution multiplier).
    /// </summary>
    public static (bool enabled, int resolution)? GetHiResScrollingLegacy(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.HI_RES_SCROLLING) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.HI_RES_SCROLLING, 0x00);
        if (reply is null || reply.Length < 4)
            return null;

        byte mode = reply[2];
        byte resolution = reply[3];
        return (mode != 0, resolution);
    }

    /// <summary>
    /// Sets hi-res scrolling mode via HI_RES_SCROLLING (0x2120), function 0x10.
    /// </summary>
    public static bool SetHiResScrollingLegacy(HidPPDevice device, bool enabled)
    {
        if (device.GetFeatureIndex(Feature.HI_RES_SCROLLING) < 0)
            return false;

        byte[]? reply = device.FeatureRequest(Feature.HI_RES_SCROLLING, 0x10,
            (byte)(enabled ? 0x01 : 0x00));
        return reply is not null;
    }

    /// <summary>
    /// Reads crown state via CROWN (0x4600), function 0x10.
    /// Payload byte 0 = divert (0x01=normal, 0x02=diverted),
    /// byte 1 = scroll mode (0x01=smooth, 0x02=ratchet).
    /// </summary>
    public static (bool smoothScroll, bool diverted)? GetCrown(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.CROWN) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.CROWN, 0x10);
        if (reply is null || reply.Length < 4)
            return null;

        bool diverted = reply[2] == 0x02;
        bool smooth = reply[3] == 0x01;
        return (smooth, diverted);
    }

    /// <summary>
    /// Sets crown state via CROWN (0x4600), function 0x20.
    /// </summary>
    public static bool SetCrown(HidPPDevice device, bool smoothScroll, bool diverted)
    {
        if (device.GetFeatureIndex(Feature.CROWN) < 0)
            return false;

        byte[]? reply = device.FeatureRequest(Feature.CROWN, 0x20,
            (byte)(diverted ? 0x02 : 0x01),
            (byte)(smoothScroll ? 0x01 : 0x02));
        return reply is not null;
    }

    /// <summary>
    /// Reads the pointer speed via POINTER_SPEED (0x2205), function 0x00.
    /// Returns the raw 16-bit speed value. 256 = 1.0x, range 0x002E to 0x01FF.
    /// </summary>
    public static int? GetPointerSpeed(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.POINTER_SPEED) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.POINTER_SPEED, 0x00);
        if (reply is null || reply.Length < 4)
            return null;

        int speed = (reply[2] << 8) | reply[3];
        return speed;
    }

    /// <summary>
    /// Sets the pointer speed via POINTER_SPEED (0x2205), function 0x10.
    /// Value 256 = 1.0x, valid range 0x002E (46) to 0x01FF (511).
    /// </summary>
    public static bool SetPointerSpeed(HidPPDevice device, int speed)
    {
        if (device.GetFeatureIndex(Feature.POINTER_SPEED) < 0)
            return false;

        speed = Math.Clamp(speed, 0x002E, 0x01FF);
        byte[]? reply = device.FeatureRequest(Feature.POINTER_SPEED, 0x10,
            (byte)(speed >> 8),
            (byte)(speed & 0xFF));
        return reply is not null;
    }

    /// <summary>
    /// Reads host info via CHANGE_HOST (0x1814), function 0x00.
    /// Returns the number of pairing slots and the current host index (0-based).
    /// </summary>
    public static (int hostCount, int currentHost)? GetHostInfo(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.CHANGE_HOST) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.CHANGE_HOST, 0x00);
        if (reply is null || reply.Length < 4)
            return null;

        int hostCount = reply[2];
        int currentHost = reply[3];

        if (hostCount <= 0 || hostCount > 3)
            hostCount = 3;

        return (hostCount, currentHost);
    }

    /// <summary>
    /// Switches the active host via CHANGE_HOST (0x1814), function 0x10.
    /// The device disconnects after switching, so no reply is expected.
    /// </summary>
    public static void SetChangeHost(HidPPDevice device, int hostIndex)
    {
        if (device.GetFeatureIndex(Feature.CHANGE_HOST) < 0)
            return;

        device.FeatureRequest(Feature.CHANGE_HOST, 0x10, (byte)hostIndex);
    }

    /// <summary>
    /// 0x40A3 host byte: current host from HOSTS_INFO (0x1815) payload[3], as some firmware
    /// ignores host 0xFF; 0xFF when HOSTS_INFO is missing.
    /// </summary>
    private static byte FnInversionHost(HidPPDevice device)
    {
        byte[]? hosts = device.GetFeatureIndex(Feature.HOSTS_INFO) >= 0
            ? device.FeatureRequest(Feature.HOSTS_INFO, 0x00)
            : null;
        return hosts is not null && hosts.Length >= 6 ? hosts[5] : (byte)0xFF;
    }

    /// <summary>
    /// Reads Fn inversion. 0x40A3 fn 0x00 [host]: payload [host, state, default, caps].
    /// 0x40A2 fn 0x00: payload [state, default]. 0x40A0 fn 0x00: payload[0] low nibble.
    /// </summary>
    public static bool? GetFnInversion(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.K375S_FN_INVERSION) >= 0)
        {
            byte[]? r = device.FeatureRequest(Feature.K375S_FN_INVERSION, 0x00, FnInversionHost(device));
            return r is null || r.Length < 4 ? null : r[3] == 0x01;
        }
        if (device.GetFeatureIndex(Feature.NEW_FN_INVERSION) >= 0)
        {
            byte[]? r = device.FeatureRequest(Feature.NEW_FN_INVERSION, 0x00);
            return r is null || r.Length < 3 ? null : r[2] == 0x01;
        }
        if (device.GetFeatureIndex(Feature.FN_INVERSION) >= 0)
        {
            byte[]? r = device.FeatureRequest(Feature.FN_INVERSION, 0x00);
            return r is null || r.Length < 3 ? null : (r[2] & 0x0F) == 0x01;
        }
        return null;
    }

    /// <summary>
    /// Writes Fn inversion via fn 0x10. 0x40A3 takes [host, state].
    /// </summary>
    public static bool SetFnInversion(HidPPDevice device, bool inverted)
    {
        byte state = (byte)(inverted ? 0x01 : 0x00);
        if (device.GetFeatureIndex(Feature.K375S_FN_INVERSION) >= 0)
            return device.FeatureRequest(Feature.K375S_FN_INVERSION, 0x10, FnInversionHost(device), state) is not null;
        ushort feat = device.GetFeatureIndex(Feature.NEW_FN_INVERSION) >= 0 ? Feature.NEW_FN_INVERSION : Feature.FN_INVERSION;
        if (device.GetFeatureIndex(feat) < 0)
            return false;
        return device.FeatureRequest(feat, 0x10, state) is not null;
    }

    /// <summary>
    /// Reads the OS platform and platform count. MULTIPLATFORM (0x4531) fn 0x00 getFeatureInfos:
    /// payload [caps(2), platformCount, descriptorCount, hostCount, currentHost, currentPlatform];
    /// null when caps lacks set-host-platform (0x0200). DUALPLATFORM (0x4530) fn 0x00: payload[0], 2 platforms.
    /// </summary>
    public static (int platform, int count)? GetOsPlatform(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.MULTIPLATFORM) >= 0)
        {
            byte[]? r = device.FeatureRequest(Feature.MULTIPLATFORM, 0x00);
            if (r is null || r.Length < 9 || (r[2] & 0x02) == 0)
                return null;
            return (r[8], r[4]);
        }
        if (device.GetFeatureIndex(Feature.DUALPLATFORM) >= 0)
        {
            byte[]? r = device.FeatureRequest(Feature.DUALPLATFORM, 0x00);
            return r is null || r.Length < 3 ? null : (r[2], 2);
        }
        return null;
    }

    /// <summary>
    /// Writes the OS platform. 0x4531 fn 0x30 setHostPlatform [host, platform] with the current
    /// host slot from getFeatureInfos (0xFF if out of range). 0x4530 fn 0x20 setPlatform [platform].
    /// </summary>
    public static bool SetOsPlatform(HidPPDevice device, int platform)
    {
        if (device.GetFeatureIndex(Feature.MULTIPLATFORM) >= 0)
        {
            byte[]? r = device.FeatureRequest(Feature.MULTIPLATFORM, 0x00);
            if (r is null || r.Length < 9 || (r[2] & 0x02) == 0 || platform >= r[4])
                return false;
            byte host = r[7] < r[6] ? r[7] : (byte)0xFF;
            return device.FeatureRequest(Feature.MULTIPLATFORM, 0x30, host, (byte)platform) is not null;
        }
        if (device.GetFeatureIndex(Feature.DUALPLATFORM) >= 0)
            return device.FeatureRequest(Feature.DUALPLATFORM, 0x20, (byte)platform) is not null;
        return false;
    }

    // -- REPROG_CONTROLS_V4 (0x1B04) button enumeration --

    private static readonly Dictionary<ushort, string> CidNames = new()
    {
        [0x0050] = "Left Click",
        [0x0051] = "Right Click",
        [0x0052] = "Middle Click",
        [0x0053] = "Back",
        [0x0056] = "Forward",
        [0x005B] = "Tilt Left",
        [0x005D] = "Tilt Right",
        [0x00BD] = "Thumb Wheel",
        [0x00C3] = "Gesture Button",
        [0x00C4] = "SmartShift",
        [0x00D7] = "Top Button",
        [0x00ED] = "DPI Change",
        [0x00FD] = "DPI Switch",
        [0x01A0] = "Haptic Thumb Pad",
    };

    /// <summary>
    /// Enumerates all reprogrammable controls, returning CID, task ID, capability flags,
    /// and a human-readable name for each button.
    /// </summary>
    public static List<(ushort cid, ushort taskId, ushort flags, string name)> GetReprogControlsList(HidPPDevice device)
    {
        int count = GetReprogControlsCount(device);
        var result = new List<(ushort cid, ushort taskId, ushort flags, string name)>();
        for (int i = 0; i < count; i++)
        {
            var info = GetReprogControlInfo(device, (byte)i);
            if (info is null)
                continue;
            string name = CidNames.GetValueOrDefault(info.Value.cid, $"Button 0x{info.Value.cid:X4}");
            result.Add((info.Value.cid, info.Value.taskId, info.Value.flags, name));
        }
        return result;
    }

    /// <summary>
    /// Reads current CID reporting state (diversion etc.) via REPROG_CONTROLS_V4 (0x1B04), fn 0x20.
    /// Returns the low byte of mapping flags (DIVERTED = 0x01).
    /// </summary>
    public static byte? GetCidReporting(HidPPDevice device, ushort cid)
    {
        if (device.GetFeatureIndex(Feature.REPROG_CONTROLS_V4) < 0)
            return null;
        byte[]? reply = device.FeatureRequest(Feature.REPROG_CONTROLS_V4, 0x20,
            (byte)(cid >> 8), (byte)(cid & 0xFF));
        if (reply is null || reply.Length < 5)
            return null;
        return reply[4];
    }

    /// <summary>
    /// Sets the divert flag for a CID via REPROG_CONTROLS_V4 (0x1B04), fn 0x30.
    /// Packet: cid_hi, cid_lo, bfield, remap_hi, remap_lo. bfield bit0 = DIVERTED, bit1 = Xvalid.
    /// </summary>
    public static bool SetCidReporting(HidPPDevice device, ushort cid, bool diverted, bool persisted = false)
    {
        if (device.GetFeatureIndex(Feature.REPROG_CONTROLS_V4) < 0)
            return false;

        byte bfield = 0x02; // Xvalid for DIVERTED bit
        if (diverted)
            bfield |= 0x01;
        if (persisted)
            bfield |= 0x0C; // PERSISTENTLY_DIVERTED (0x04) + its Xvalid (0x08)

        byte[]? reply = device.FeatureRequest(Feature.REPROG_CONTROLS_V4, 0x30,
            (byte)(cid >> 8), (byte)(cid & 0xFF), bfield, 0x00, 0x00);
        return reply is not null;
    }

    // -- GESTURE_2 (0x6501) gesture enable/disable --

    private static readonly Dictionary<byte, string> GestureNames = new()
    {
        [1] = "Tap 1 Finger",
        [2] = "Tap 2 Fingers",
        [3] = "Tap 3 Fingers",
        [4] = "Click 1 Finger",
        [5] = "Click 2 Fingers",
        [6] = "Click 3 Fingers",
        [10] = "Double Tap 1 Finger",
        [11] = "Double Tap 2 Fingers",
        [20] = "Track 1 Finger",
        [21] = "Tracking Acceleration",
        [30] = "Tap Drag 1 Finger",
        [31] = "Tap Drag 2 Fingers",
        [33] = "Tap Gestures",
        [40] = "Scroll 1 Finger",
        [41] = "Scroll 2 Fingers",
        [42] = "Scroll 2F Horizontal",
        [43] = "Scroll 2F Vertical",
        [45] = "Natural Scrolling",
        [46] = "Thumb Wheel",
        [48] = "V-Scroll Inertia",
        [49] = "V-Scroll Ballistics",
        [50] = "Swipe 2F Horizontal",
        [51] = "Swipe 3F Horizontal",
        [52] = "Swipe 4F Horizontal",
        [80] = "Zoom 2 Fingers",
        [87] = "Rotate 2 Fingers",
    };

    /// <summary>
    /// Parses the GESTURE_2 field table and returns all toggleable gestures with
    /// their current enabled state and bitmap enable index.
    /// Protocol: fn 0x00 returns 8 field pairs per call. Gesture fields have
    /// high byte &amp; 0x80 set. Each gesture that can be enabled or is default-enabled
    /// gets a sequential enable index used for bitmap read/write via fn 0x10/0x20.
    /// </summary>
    public static List<(ushort gestureId, string name, bool enabled, int enableIndex)> GetGestureList(
        HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.GESTURE_2) < 0)
            return [];

        var gestures = new List<(ushort gestureId, string name, bool enabled, int enableIndex)>();
        int fieldIndex = 0;
        int nextEnableIndex = 0;

        while (fieldIndex < 256)
        {
            byte[]? reply = device.FeatureRequest(Feature.GESTURE_2, 0x00,
                (byte)(fieldIndex >> 8), (byte)(fieldIndex & 0xFF));
            if (reply is null || reply.Length < 18)
                break;

            bool done = false;
            for (int i = 0; i < 8; i++)
            {
                byte high = reply[2 + i * 2];
                byte low = reply[2 + i * 2 + 1];

                if (high == 0x01)
                {
                    done = true;
                    break;
                }

                if ((high & 0x80) != 0)
                {
                    bool canEnable = (high & 0x01) != 0;
                    bool defaultEnabled = (high & 0x20) != 0;

                    if (canEnable || defaultEnabled)
                    {
                        int enableIndex = nextEnableIndex++;
                        if (canEnable)
                        {
                            bool enabled = defaultEnabled;
                            int offset = enableIndex >> 3;
                            byte mask = (byte)(1 << (enableIndex % 8));
                            byte[]? state = device.FeatureRequest(Feature.GESTURE_2, 0x10,
                                (byte)offset, 0x01, mask);
                            if (state is not null && state.Length >= 3)
                                enabled = (state[2] & mask) != 0;

                            string name = GestureNames.GetValueOrDefault(low, $"Gesture {low}");
                            gestures.Add((low, name, enabled, enableIndex));
                        }
                    }
                }

                fieldIndex++;
            }

            if (done)
                break;
        }

        return gestures;
    }

    /// <summary>
    /// Enables or disables a gesture by its bitmap enable index.
    /// fn 0x20: args = [offset, 0x01, mask, value].
    /// </summary>
    public static bool SetGestureEnabled(HidPPDevice device, int enableIndex, bool enabled)
    {
        if (device.GetFeatureIndex(Feature.GESTURE_2) < 0)
            return false;

        int offset = enableIndex >> 3;
        byte mask = (byte)(1 << (enableIndex % 8));
        byte value = enabled ? mask : (byte)0;

        byte[]? reply = device.FeatureRequest(Feature.GESTURE_2, 0x20,
            (byte)offset, 0x01, mask, value);
        return reply is not null;
    }

    /// <summary>
    /// Parses the GESTURE_2 field table for divertable gestures and returns each
    /// with its current diverted state and bitmap divert index.
    /// fn 0x30 reads diverted bitmap, fn 0x40 writes it. High byte bit 0x02 marks divertable.
    /// </summary>
    public static List<(ushort gestureId, string name, bool diverted, int divertIndex)> GetGestureDivertList(
        HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.GESTURE_2) < 0)
            return [];

        var result = new List<(ushort, string, bool, int)>();
        int fieldIndex = 0;
        int nextDivertIndex = 0;

        while (fieldIndex < 256)
        {
            byte[]? reply = device.FeatureRequest(Feature.GESTURE_2, 0x00,
                (byte)(fieldIndex >> 8), (byte)(fieldIndex & 0xFF));
            if (reply is null || reply.Length < 18)
                break;

            bool done = false;
            for (int i = 0; i < 8; i++)
            {
                byte high = reply[2 + i * 2];
                byte low = reply[2 + i * 2 + 1];

                if (high == 0x01)
                {
                    done = true;
                    break;
                }

                if ((high & 0x80) != 0)
                {
                    bool canDivert = (high & 0x02) != 0;
                    if (canDivert)
                    {
                        int divertIndex = nextDivertIndex++;
                        int offset = divertIndex >> 3;
                        byte mask = (byte)(1 << (divertIndex % 8));
                        bool diverted = false;
                        byte[]? state = device.FeatureRequest(Feature.GESTURE_2, 0x30,
                            (byte)offset, 0x01, mask);
                        if (state is not null && state.Length >= 3)
                            diverted = (state[2] & mask) != 0;

                        string name = GestureNames.GetValueOrDefault(low, $"Gesture {low}");
                        result.Add((low, name, diverted, divertIndex));
                    }
                }

                fieldIndex++;
            }

            if (done)
                break;
        }

        return result;
    }

    /// <summary>
    /// Sets gesture diverted state. fn 0x40: args = [offset, 0x01, mask, value].
    /// </summary>
    public static bool SetGestureDiverted(HidPPDevice device, int divertIndex, bool diverted)
    {
        if (device.GetFeatureIndex(Feature.GESTURE_2) < 0)
            return false;

        int offset = divertIndex >> 3;
        byte mask = (byte)(1 << (divertIndex % 8));
        byte value = diverted ? mask : (byte)0;

        byte[]? reply = device.FeatureRequest(Feature.GESTURE_2, 0x40,
            (byte)offset, 0x01, mask, value);
        return reply is not null;
    }

    private static readonly Dictionary<byte, string> GestureParamNames = new()
    {
        [0x01] = "Extra Capabilities",
        [0x02] = "Pixel Zone",
        [0x03] = "Ratio Zone",
        [0x04] = "Scale Factor",
    };

    /// <summary>
    /// Enumerates gesture params via fn 0x00 walk (param fields have high byte 0x20-0x3F),
    /// reads current value via fn 0x70 and default/max via fn 0x60.
    /// Returns (paramIndex, name, currentValue, maxValue, size in bytes).
    /// </summary>
    public static List<(int index, string name, int currentValue, int maxValue, int size)> GetGestureParams(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.GESTURE_2) < 0)
            return [];

        var result = new List<(int, string, int, int, int)>();
        int fieldIndex = 0;
        int nextParamIndex = 0;

        while (fieldIndex < 256)
        {
            byte[]? reply = device.FeatureRequest(Feature.GESTURE_2, 0x00,
                (byte)(fieldIndex >> 8), (byte)(fieldIndex & 0xFF));
            if (reply is null || reply.Length < 18)
                break;

            bool done = false;
            for (int i = 0; i < 8; i++)
            {
                byte high = reply[2 + i * 2];
                byte low = reply[2 + i * 2 + 1];

                if (high == 0x01)
                {
                    done = true;
                    break;
                }

                // Param fields use high nibble 0x20 or 0x30
                if ((high & 0xF0) == 0x20 || (high & 0xF0) == 0x30)
                {
                    int size = high & 0x0F;
                    int paramIndex = nextParamIndex++;

                    int current = 0;
                    byte[]? cur = device.FeatureRequest(Feature.GESTURE_2, 0x70,
                        (byte)paramIndex, 0xFF);
                    if (cur is not null && cur.Length >= 2 + size)
                    {
                        for (int b = 0; b < size; b++)
                            current = (current << 8) | cur[2 + b];
                    }

                    int max = 0;
                    byte[]? def = device.FeatureRequest(Feature.GESTURE_2, 0x60,
                        (byte)paramIndex, 0xFF);
                    if (def is not null && def.Length >= 2 + size)
                    {
                        for (int b = 0; b < size; b++)
                            max = (max << 8) | def[2 + b];
                    }
                    // Default value is the "normal" value; double it as a sensible slider max.
                    if (max > 0)
                        max = max * 2;
                    else
                        max = 1024;

                    string name = GestureParamNames.GetValueOrDefault(low, $"Param {low}");
                    result.Add((paramIndex, name, current, max, size));
                }

                fieldIndex++;
            }

            if (done)
                break;
        }

        return result;
    }

    /// <summary>
    /// Sets a gesture param value via fn 0x80: [index, value (size bytes, BE), 0xFF].
    /// </summary>
    public static bool SetGestureParam(HidPPDevice device, int paramIndex, int value, int size)
    {
        if (device.GetFeatureIndex(Feature.GESTURE_2) < 0 || size is < 1 or > 4)
            return false;

        var args = new byte[size + 2];
        args[0] = (byte)paramIndex;
        for (int b = 0; b < size; b++)
            args[1 + b] = (byte)(value >> (8 * (size - 1 - b)));
        args[^1] = 0xFF;
        byte[]? reply = device.FeatureRequest(Feature.GESTURE_2, 0x80, args);
        return reply is not null;
    }

    // ── HAPTIC (0x19B0) ──

    /// <summary>Reads haptic feedback state. fn 0x10: [enabled, level, levelType].</summary>
    public static (bool enabled, int level)? GetHapticLevel(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.HAPTIC) < 0)
            return null;

        byte[]? reply = device.FeatureRequest(Feature.HAPTIC, 0x10);
        if (reply is null || reply.Length < 4)
            return null;

        bool enabled = (reply[2] & 0x01) != 0;
        int level = enabled ? reply[3] : 0;
        return (enabled, level);
    }

    /// <summary>Sets haptic feedback. fn 0x20: [flags, level]; flag bits other than enable are kept as read.</summary>
    public static bool SetHapticLevel(HidPPDevice device, bool enabled, int level)
    {
        if (device.GetFeatureIndex(Feature.HAPTIC) < 0)
            return false;

        byte[]? cur = device.FeatureRequest(Feature.HAPTIC, 0x10);
        if (cur is null || cur.Length < 4)
            return false;

        byte flags = (byte)(enabled ? cur[2] | 0x01 : cur[2] & ~0x01);
        byte lv = (byte)Math.Clamp(enabled ? level : cur[3], 0, 100);
        byte[]? reply = device.FeatureRequest(Feature.HAPTIC, 0x20, flags, lv);
        return reply is not null;
    }

    /// <summary>
    /// Plays a haptic waveform via fn 0x40, only if capabilities (fn 0x00, payload[4..7] BE mask,
    /// bit N = waveform N) advertise it and the status (fn 0x30, payload[0]) is 0.
    /// </summary>
    public static bool PlayHapticWaveform(HidPPDevice device, byte waveform)
    {
        if (device.GetFeatureIndex(Feature.HAPTIC) < 0 || waveform > 31)
            return false;

        byte[]? caps = device.FeatureRequest(Feature.HAPTIC, 0x00);
        if (caps is null || caps.Length < 10)
            return false;
        uint mask = (uint)((caps[6] << 24) | (caps[7] << 16) | (caps[8] << 8) | caps[9]);
        if ((mask & (1u << waveform)) == 0)
            return false;

        byte[]? status = device.FeatureRequest(Feature.HAPTIC, 0x30);
        if (status is null || status.Length < 3 || status[2] != 0)
            return false;

        return device.FeatureRequest(Feature.HAPTIC, 0x40, waveform) is not null;
    }

    // ── FORCE_SENSING_BUTTON (0x19C0) ──

    /// <summary>Reads force-sensing button configs. Returns list of (index, current, min, max).</summary>
    public static List<(byte index, int current, int min, int max)> GetForceSensingButtons(HidPPDevice device)
    {
        var result = new List<(byte, int, int, int)>();
        if (device.GetFeatureIndex(Feature.FORCE_SENSING_BUTTON) < 0)
            return result;

        byte[]? countReply = device.FeatureRequest(Feature.FORCE_SENSING_BUTTON, 0x00);
        if (countReply is null || countReply.Length < 3)
            return result;

        int count = countReply[2];
        for (byte i = 0; i < count && i < 8; i++)
        {
            // getButtonInfo payload [flags(2), default(2), max(2), min(2)]; getButtonConfig payload [current(2)].
            byte[]? info = device.FeatureRequest(Feature.FORCE_SENSING_BUTTON, 0x10, i);
            if (info is null || info.Length < 10)
                continue;

            int maxForce = (info[6] << 8) | info[7];
            int minForce = (info[8] << 8) | info[9];

            byte[]? cur = device.FeatureRequest(Feature.FORCE_SENSING_BUTTON, 0x20, i);
            int current = (cur is not null && cur.Length >= 4)
                ? (cur[2] << 8) | cur[3]
                : minForce;

            result.Add((i, current, minForce, maxForce));
        }
        return result;
    }

    /// <summary>Sets force for a button. fn 0x30: [index, forceHi, forceLo].</summary>
    public static bool SetForceSensingButton(HidPPDevice device, byte index, int force)
    {
        if (device.GetFeatureIndex(Feature.FORCE_SENSING_BUTTON) < 0)
            return false;

        byte[]? reply = device.FeatureRequest(Feature.FORCE_SENSING_BUTTON, 0x30,
            index, (byte)(force >> 8), (byte)(force & 0xFF));
        return reply is not null;
    }

    // ── ANALOG_BUTTONS (0x1B0C) ──

    /// <summary>Reads analog button capabilities and per-button config.</summary>
    public static (int buttonCount, int maxActuation, int maxRapidTrigger, int maxHaptics,
        List<(byte index, int actuation, int rapidTrigger, int haptics)> buttons)?
        GetAnalogButtons(HidPPDevice device)
    {
        if (device.GetFeatureIndex(Feature.ANALOG_BUTTONS) < 0)
            return null;

        byte[]? caps = device.FeatureRequest(Feature.ANALOG_BUTTONS, 0x00);
        if (caps is null || caps.Length < 7)
            return null;

        int buttonCount = Math.Min((int)caps[3], 2); // only L/R user-accessible
        int maxAct = caps[4] > 0 ? caps[4] >> 2 : 10;
        int maxRt = caps[5] > 0 ? caps[5] >> 2 : 5;
        int maxHap = caps[6] > 0 ? caps[6] >> 2 : 5;

        var buttons = new List<(byte, int, int, int)>();
        for (byte i = 0; i < buttonCount; i++)
        {
            byte[]? cfg = device.FeatureRequest(Feature.ANALOG_BUTTONS, 0x20, i);
            if (cfg is null || cfg.Length < 6)
                continue;
            buttons.Add((i, cfg[3] >> 2, cfg[4] >> 2, cfg[5] >> 2));
        }

        return (buttonCount, maxAct, maxRt, maxHap, buttons);
    }

    /// <summary>Sets analog button config. Preserves sensitivityFlag in rapidTrigger byte.</summary>
    public static bool SetAnalogButton(HidPPDevice device, byte index,
        int actuation, int rapidTrigger, int haptics)
    {
        if (device.GetFeatureIndex(Feature.ANALOG_BUTTONS) < 0)
            return false;

        // Read current to preserve sensitivityFlag (byte 4 bit 0).
        byte[]? cur = device.FeatureRequest(Feature.ANALOG_BUTTONS, 0x20, index);
        byte sensFlag = (cur is not null && cur.Length >= 5) ? (byte)(cur[4] & 0x01) : (byte)0;

        byte wireAct = (byte)((actuation & 0x3F) << 2);
        byte wireRt = (byte)(((rapidTrigger & 0x3F) << 2) | sensFlag);
        byte wireHap = (byte)((haptics & 0x3F) << 2);

        byte[]? reply = device.FeatureRequest(Feature.ANALOG_BUTTONS, 0x10,
            index, wireAct, wireRt, wireHap);
        return reply is not null;
    }
}
