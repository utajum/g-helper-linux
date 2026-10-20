using HidSharp;
using HidSharp.Reports;

namespace GHelper.Linux.USB;

/// <summary>
/// HID LampArray driver for 2024+ Strix/Scar keyboards (G614/G615/G634/G635/
/// G814/G815/G834/G835) and Slash models. On these the lightbar (and on some
/// firmware the whole keyboard) ignores the legacy 0xBC direct protocol; the
/// standard HID LampArray reports must be used for software-driven modes
/// (Heatmap/Battery/Gradient/ZoneTest).
///
/// Report IDs are ridBase-relative (ridBase 0x00 or 0x40 by firmware):
///   +1 LampArrayAttributes (lamp count)
///   +2 LampAttributesRequest (select lamp)
///   +3 LampAttributesResponse (position x, purposes)
///   +4 LampMultiUpdate (stream colors, 8 lamps per report)
///   +6 LampArrayControl (1 = autonomous, 0 = host controlled)
/// </summary>
public static class AsusLampArray
{
    const byte FLAG_COMPLETE = 0x01;
    const int MULTI_MAX = 8;
    const uint PURPOSE_CONTROL = 0x01;

    struct Lamp
    {
        public int Zone;   // blend base: 0 = keyboard zones 0-3, 4 = lightbar zones 4-7
        public double T;   // normalized x position 0..1 within its group
    }

    static HidDevice? _device;
    static HidStream? _stream;
    static byte _ridBase;
    static int _featLen;
    static volatile bool _probed;
    static volatile bool _probing;
    static bool _failLogged;
    static bool _controlled;

    static Lamp[] _lamps = Array.Empty<Lamp>();

    static byte RidAttr => (byte)(_ridBase + 0x01);
    static byte RidRequest => (byte)(_ridBase + 0x02);
    static byte RidResponse => (byte)(_ridBase + 0x03);
    static byte RidMulti => (byte)(_ridBase + 0x04);
    static byte RidControl => (byte)(_ridBase + 0x06);

    /// <summary>True once probe found a usable LampArray device. First call
    /// kicks off an async probe and returns false; Aura re-applies after.</summary>
    public static bool Available
    {
        get
        {
            if (_probed)
                return _device != null;
            if (!Helpers.AppConfig.IsLampArray())
            {
                _probed = true;
                return false;
            }
            if (!_probing)
            {
                _probing = true;
                Task.Run(Probe);
            }
            return false;
        }
    }

    public static bool Probing => _probing && !_probed;

    static void Probe()
    {
        _device = FindDevice();
        if (_device != null && Reopen())
        {
            _featLen = _device.GetMaxFeatureReportLength();
            ReadLamps(ReadLampCount());
        }

        if (_lamps.Length == 0)
        {
            _stream?.Dispose();
            _stream = null;
            _device = null;
            Helpers.Logger.WriteLine("LampArray: not available");
        }
        else
        {
            Helpers.Logger.WriteLine($"LampArray: rid=0x{_ridBase:X2} feat={_featLen} lamps={_lamps.Length}");
        }

        _probed = true;
        Aura.ApplyAura();
    }

    static bool Reopen()
    {
        if (_stream != null)
            return true;
        try
        {
            _stream = _device!.Open();
            _failLogged = false;
            return true;
        }
        catch (Exception ex)
        {
            if (!_failLogged)
                Helpers.Logger.WriteLine($"LampArray: open failed {ex.Message}");
            _failLogged = true;
            return false;
        }
    }

    static HidDevice? FindDevice()
    {
        foreach (byte b in new byte[] { 0x00, 0x40 })
        {
            foreach (var device in AsusHid.FindDevices((byte)(b + 0x04), AsusHid.MAIN_AURA_PIDS))
            {
                try
                {
                    if (device.GetReportDescriptor().TryGetReport(ReportType.Feature, (byte)(b + 0x06), out _))
                    {
                        _ridBase = b;
                        return device;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    static int ReadLampCount()
    {
        try
        {
            byte[] attr = new byte[_featLen];
            attr[0] = RidAttr;
            lock (AsusHid.HidLock)
                _stream!.GetFeature(attr);
            int count = attr[1] | (attr[2] << 8);
            if (count > 0 && count <= 512)
                return count;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"LampArray: attr read error {ex.Message}");
        }
        return 0;
    }

    static void ReadLamps(int count)
    {
        var xs = new int[count];
        var keyboard = new bool[count];
        int keyMin = int.MaxValue, keyMax = int.MinValue;
        int barMin = int.MaxValue, barMax = int.MinValue;

        for (int i = 0; i < count; i++)
        {
            try
            {
                byte[] req = new byte[_featLen];
                req[0] = RidRequest;
                req[1] = (byte)i;
                req[2] = (byte)(i >> 8);
                byte[] resp = new byte[_featLen];
                resp[0] = RidResponse;
                lock (AsusHid.HidLock)
                {
                    _stream!.SetFeature(req);
                    _stream.GetFeature(resp);
                }
                xs[i] = BitConverter.ToInt32(resp, 3);
                keyboard[i] = (BitConverter.ToUInt32(resp, 19) & PURPOSE_CONTROL) != 0;
            }
            catch { }

            if (keyboard[i])
            {
                keyMin = Math.Min(keyMin, xs[i]);
                keyMax = Math.Max(keyMax, xs[i]);
            }
            else
            {
                barMin = Math.Min(barMin, xs[i]);
                barMax = Math.Max(barMax, xs[i]);
            }
        }

        _lamps = new Lamp[count];
        for (int i = 0; i < count; i++)
        {
            int min = keyboard[i] ? keyMin : barMin;
            int span = Math.Max(1, (keyboard[i] ? keyMax : barMax) - min);
            _lamps[i] = new Lamp
            {
                Zone = keyboard[i] ? 0 : 4,
                T = (xs[i] - min) / (double)span
            };
        }
    }

    static void Send(byte[] data)
    {
        var s = _stream;
        if (s == null)
            return;
        byte[] buf = new byte[_featLen];
        Array.Copy(data, buf, Math.Min(data.Length, _featLen));
        try
        {
            lock (AsusHid.HidLock)
                s.SetFeature(buf);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"LampArray: write error {ex.Message}");
            _stream = null;
            _controlled = false;
            s.Dispose();
        }
    }

    /// <summary>Takes host control: legacy aura off + control(1) then (0).</summary>
    static void Control()
    {
        lock (AsusHid.HidLock)
        {
            AsusHid.SetFeatureAura(new byte[] { AsusHid.AURA_ID, 0xC0, 0x03, 0x01 });
            Send(new byte[] { RidControl, 0x01 });
            Send(new byte[] { RidControl, 0x00 });
        }
        _controlled = _stream != null;
    }

    /// <summary>Called on every mode apply. Software-painted modes stream via
    /// multi-update; firmware modes hand the array back to autonomous.</summary>
    public static void SetMode(AuraMode mode)
    {
        if (!Available)
            return;
        bool streaming = mode is AuraMode.Heatmap or AuraMode.Battery
            or AuraMode.Ambient or AuraMode.Gradient or AuraMode.ZoneTest;
        if (streaming)
            _controlled = false;
        else
            Reset();
    }

    /// <summary>Returns the array to firmware (autonomous) control.</summary>
    static void Reset()
    {
        lock (AsusHid.HidLock)
        {
            if (Reopen())
                Send(new byte[] { RidControl, 0x01 });
            AsusHid.SetFeatureAura(new byte[] { AsusHid.AURA_ID, 0xC0, 0x04, 0x01, 0x01 });
        }
        _controlled = false;
    }

    /// <summary>Call on app quit so lighting does not stay frozen on the last frame.</summary>
    public static void Release()
    {
        if (_controlled)
            Reset();
    }

    static (byte R, byte G, byte B) Blend(byte[] zones, int off, double t)
    {
        // zones is 8*3 RGB bytes; blend between the 4 zone colors of a group
        double f = Math.Clamp(t, 0, 1) * 3;
        int a = (int)f;
        int b = Math.Min(3, a + 1);
        double w = f - a;
        int ia = (off + a) * 3;
        int ib = (off + b) * 3;
        return (
            (byte)(zones[ia] + (zones[ib] - zones[ia]) * w),
            (byte)(zones[ia + 1] + (zones[ib + 1] - zones[ia + 1]) * w),
            (byte)(zones[ia + 2] + (zones[ib + 2] - zones[ia + 2]) * w));
    }

    /// <summary>Single color across all lamps.</summary>
    public static void SetColor(byte r, byte g, byte b)
    {
        byte[] zones = new byte[8 * 3];
        for (int z = 0; z < 8; z++)
        {
            zones[z * 3] = r;
            zones[z * 3 + 1] = g;
            zones[z * 3 + 2] = b;
        }
        SetColors(zones);
    }

    /// <summary>zones: 8-zone RGB bytes (0-3 keyboard L-R, 4-7 lightbar L-R).</summary>
    public static void SetColors(byte[] zones)
    {
        if (!Available || !Reopen() || zones.Length < 8 * 3)
            return;
        if (!_controlled)
            Control();

        var arr = new (byte R, byte G, byte B)[_lamps.Length];
        for (int i = 0; i < _lamps.Length; i++)
            arr[i] = Blend(zones, _lamps[i].Zone, _lamps[i].T);
        SendMulti(arr);
    }

    static void SendMulti((byte R, byte G, byte B)[] arr)
    {
        for (int start = 0; start < arr.Length; start += MULTI_MAX)
        {
            int n = Math.Min(MULTI_MAX, arr.Length - start);

            byte[] report = new byte[3 + MULTI_MAX * 2 + MULTI_MAX * 4];
            report[0] = RidMulti;
            report[1] = (byte)n;
            report[2] = start + n >= arr.Length ? FLAG_COMPLETE : (byte)0;
            int idOff = 3;
            int colOff = 3 + MULTI_MAX * 2;
            for (int i = 0; i < n; i++)
            {
                int lamp = start + i;
                report[idOff + i * 2] = (byte)(lamp & 0xFF);
                report[idOff + i * 2 + 1] = (byte)(lamp >> 8);
                var c = arr[lamp];
                report[colOff + i * 4] = c.R;
                report[colOff + i * 4 + 1] = c.G;
                report[colOff + i * 4 + 2] = c.B;
                report[colOff + i * 4 + 3] = 0xFF;
            }
            Send(report);
        }
    }
}
