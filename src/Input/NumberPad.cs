using System.Runtime.InteropServices;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Input;

/// <summary>
/// ASUS illuminated touchpad NumberPad driver. Two responsibilities:
///
/// 1. LED control - the NumberPad LEDs are toggled by writing a 13-byte
///    magic packet to the touchpad's I2C slave (0x15, or 0x38 on a few ASUF
///    models). The bus number comes from parsing /proc/bus/input/devices.
/// 2. Touch interception - while active the touchpad is EVIOCGRAB-ed, touches
///    on keys are mapped to the layout's key grid, and KEY_KP* keycodes are
///    emitted through a /dev/uinput virtual keyboard (keycodes, not characters,
///    so the compositor applies the user's layout natively). Pointer touches go
///    to a virtual copy of the touchpad (Windows mixed mode).
///
/// Holding the layout's NumLock zone (see <see cref="NumberPadGesture"/>) presses
/// NumLock, and the NumberPad follows the NumLock LED between idle (LEDs off, no
/// grab) and active. The service itself is enabled via the "numberpad" config
/// flag; call <see cref="InitIfEnabled"/> at startup.
/// </summary>
public static class NumberPad
{
    public enum ProbeStatus
    {
        Ok,
        NoHardware,
        I2cUnavailable,
        PermissionDenied,
    }

    public sealed class ProbeResult
    {
        public ProbeStatus Status { get; init; }
        public string Detail { get; init; } = "";
        public string TouchpadName { get; init; } = "";
    }

    private const string VirtualDeviceName = "g-helper numberpad";
    private const string DevUinput = "/dev/uinput";
    private const string ProcBusInputDevices = "/proc/bus/input/devices";

    // I2C_SLAVE_FORCE: set the slave address even though i2c_hid is bound to
    // the device. The plain I2C_SLAVE (0x0703) fails with EBUSY; the vendor
    // LED register lives outside the HID conversation so forcing is safe.
    private const ulong I2C_SLAVE_FORCE = 0x0706;

    // 13-byte LED-control packet: constant header, state byte, terminator.
    private static readonly byte[] PacketHeader =
    {
        0x05, 0x00, 0x3d, 0x03, 0x06, 0x00, 0x07, 0x00, 0x0d, 0x14, 0x03,
    };
    private const byte PacketTerminator = 0xad;
    private const byte StateUnlock = 0x60;
    private const byte StateEnable = 0x01;
    private const byte StateDisable = 0x00;

    private const int PollTimeoutMs = 500;

    internal sealed class Target
    {
        public string I2cPath = "";
        public int I2cAddr;
        public string EventPath = "";
        public string Name = "";
    }

    private static readonly object _lock = new();
    private static Thread? _thread;
    private static volatile bool _running;
    private static int[] _stopPipe = { -1, -1 };
    private static int _touchpadFd = -1;
    private static int _uinputFd = -1;
    private static int _touchFd = -1;
    private static int _ledVersion = -1;
    private static bool _numLock;
    private static HashSet<string> _ledDevices = new();
    private static volatile bool _touchpadOff;
    private static TouchpadRange _range;
    private static Target? _target;
    private static NumberPadLayout _layout = NumberPadLayouts.Universal4x4;

    /// <summary>True while the event loop is running (idle or active).</summary>
    public static bool IsRunning => _running;

    /// <summary>Start the service if the user enabled it. Call once at app startup.</summary>
    public static void InitIfEnabled()
    {
        if (AppConfig.Is("numberpad"))
            Start();
    }

    /// <summary>
    /// Probe hardware and permissions without side effects. Cheap enough to
    /// call on every settings-window open.
    /// </summary>
    public static ProbeResult Probe()
    {
        var target = Detect();
        if (target == null)
            return new ProbeResult { Status = ProbeStatus.NoHardware };

        if (!File.Exists(target.I2cPath))
            return new ProbeResult { Status = ProbeStatus.I2cUnavailable, Detail = target.I2cPath, TouchpadName = target.Name };
        if (EvdevInterop.access(target.I2cPath, EvdevInterop.W_OK) != 0)
            return new ProbeResult { Status = ProbeStatus.PermissionDenied, Detail = target.I2cPath, TouchpadName = target.Name };

        if (!File.Exists(DevUinput))
            return new ProbeResult { Status = ProbeStatus.I2cUnavailable, Detail = DevUinput, TouchpadName = target.Name };
        if (EvdevInterop.access(DevUinput, EvdevInterop.W_OK) != 0)
            return new ProbeResult { Status = ProbeStatus.PermissionDenied, Detail = DevUinput, TouchpadName = target.Name };

        if (!File.Exists(target.EventPath))
            return new ProbeResult { Status = ProbeStatus.I2cUnavailable, Detail = target.EventPath, TouchpadName = target.Name };
        if (EvdevInterop.access(target.EventPath, EvdevInterop.R_OK | EvdevInterop.W_OK) != 0)
            return new ProbeResult { Status = ProbeStatus.PermissionDenied, Detail = target.EventPath, TouchpadName = target.Name };

        return new ProbeResult { Status = ProbeStatus.Ok, TouchpadName = target.Name };
    }

    /// <summary>Touchpad range for the layout preview; a 130x82 mm pad when it cannot be read.</summary>
    public static TouchpadRange ReadRange()
    {
        lock (_lock)
            if (_running)
                return _range;
        var target = Detect();
        int fd = target == null ? -1 : EvdevInterop.open(target.EventPath, EvdevInterop.O_RDONLY | EvdevInterop.O_CLOEXEC);
        if (fd >= 0)
        {
            bool ok = ReadAbsBounds(fd, out var range);
            EvdevInterop.close(fd);
            if (ok)
                return range;
        }
        return new TouchpadRange(130 * 31, 82 * 31, 31, 31);
    }

    /// <summary>
    /// Open the touchpad and virtual keyboard, then run the event loop on a
    /// background thread. Starts in idle (LEDs off, no grab), even when NumLock
    /// is already on; holding the NumLock zone activates. Idempotent.
    /// </summary>
    public static bool Start()
    {
        lock (_lock)
        {
            if (_running)
                return true;

            var probe = Probe();
            if (probe.Status != ProbeStatus.Ok)
            {
                Logger.WriteLine($"NumberPad: cannot start - {probe.Status} {probe.Detail}");
                return false;
            }

            var target = Detect();
            if (target == null)
                return false;

            int fd = EvdevInterop.open(target.EventPath,
                EvdevInterop.O_RDWR | EvdevInterop.O_NONBLOCK | EvdevInterop.O_CLOEXEC);
            if (fd < 0)
            {
                Logger.WriteLine($"NumberPad: open {target.EventPath} failed (errno={Marshal.GetLastWin32Error()})");
                return false;
            }

            if (!ReadAbsBounds(fd, out var range))
            {
                Logger.WriteLine("NumberPad: touchpad reported invalid absolute range");
                EvdevInterop.close(fd);
                return false;
            }

            _numLock = IsNumLockOn();
            if (!CreateVirtualKeyboard())
            {
                EvdevInterop.close(fd);
                return false;
            }
            // A new keyboard with LEDs makes the kernel reset every keyboard LED: restore NumLock, drop the reset.
            var devices = NumLockDevices();
            WriteNumLockLeds(devices.Values, _numLock);
            _ledDevices = devices.Keys.ToHashSet();
            DrainVirtualKeyboard();

            if (EvdevInterop.pipe(_stopPipe) != 0)
            {
                Logger.WriteLine("NumberPad: pipe() failed");
                DestroyVirtualKeyboard();
                EvdevInterop.close(fd);
                return false;
            }

            _target = target;
            _touchpadFd = fd;
            _range = range;
            _layout = NumberPadLayouts.Resolve(AppConfig.GetModel(), target.Name, AppConfig.GetString("numberpad_layout", "")!);
            if (!CreateVirtualTouchpad(fd, target.Name))
                Logger.WriteLine("NumberPad: no virtual touchpad, the pointer stops while active");
            _ledVersion = _layout.Geometry?.CalcWidth > 0 ? ReadLedVersion() : -1;

            _running = true;
            _thread = new Thread(EventLoop) { IsBackground = true, Name = "numberpad" };
            _thread.Start();

            Logger.WriteLine($"NumberPad: started on {target.EventPath} '{target.Name}' i2c={target.I2cPath} addr=0x{target.I2cAddr:x2} layout={_layout.Name} {_layout.Cols}x{_layout.Rows} range={range} led={_ledVersion}");
            return true;
        }
    }

    /// <summary>
    /// G-Helper's touchpad toggle. Off turns the NumberPad and NumLock off and
    /// blocks the hold until the touchpad is on again, as on Windows.
    /// </summary>
    public static void SetTouchpadEnabled(bool enabled)
    {
        _touchpadOff = !enabled;
        lock (_lock)
        {
            if (!enabled && _running)
                WakeLoop((byte)'t');
        }
    }

    private static void WakeLoop(byte cmd)
    {
        if (_stopPipe[1] < 0)
            return;
        try
        {
            var b = Marshal.AllocHGlobal(1);
            Marshal.WriteByte(b, cmd);
            EvdevInterop.write(_stopPipe[1], b, 1);
            Marshal.FreeHGlobal(b);
        }
        catch { }
    }

    /// <summary>Stop the loop, ungrab, LEDs off, release everything. Safe to repeat.</summary>
    public static void Stop()
    {
        Thread? t;
        lock (_lock)
        {
            if (!_running)
                return;
            _running = false;
            t = _thread;
            WakeLoop((byte)'q');
        }

        try
        { t?.Join(2000); }
        catch { }

        lock (_lock)
        {
            ReleaseAll();
        }
        Logger.WriteLine("NumberPad: stopped");
    }

    // DETECTION

    /// <summary>
    /// Parse /proc/bus/input/devices for the ASUS-family touchpad. The same
    /// block yields the I2C bus (from the Sysfs path), the slave address
    /// (from the controller family in the name) and the evdev node (from
    /// the Handlers line).
    /// </summary>
    private static Target? Detect()
    {
        string contents;
        try
        { contents = File.ReadAllText(ProcBusInputDevices); }
        catch
        { return null; }
        return FindTarget(contents, Helpers.AppConfig.IsAsusDevice());
    }

    /// <summary>Pick the NumberPad touchpad from /proc/bus/input/devices text.</summary>
    internal static Target? FindTarget(string contents, bool isAsus)
    {
        foreach (string block in contents.Split("\n\n"))
        {
            string? name = null, sysfs = null, handlers = null;
            foreach (string line in block.Split('\n'))
            {
                if (line.StartsWith("N: Name="))
                    name = line["N: Name=".Length..].Trim('"');
                else if (line.StartsWith("S: Sysfs="))
                    sysfs = line["S: Sysfs=".Length..];
                else if (line.StartsWith("H: Handlers="))
                    handlers = line["H: Handlers=".Length..];
            }

            if (name == null || sysfs == null || handlers == null)
                continue;
            if (!name.Contains("Touchpad"))
                continue;
            // ASUE/ASUP/ASUF/ASCF/ASCP are ASUS-proprietary ACPI ids. ELAN touchpads
            // exist on every vendor's laptops, so the generic ELAN name only
            // counts on ASUS hardware; sending the NumberPad I2C magic to a
            // Lenovo/HP ELAN controller is undefined behavior.
            bool familyMatch = name.Contains("ASUE")
                || name.Contains("ASUP") || name.Contains("ASUF")
                || name.Contains("ASCF") || name.Contains("ASCP")
                || (name.Contains("ELAN") && isAsus);
            if (!familyMatch)
                continue;
            // Known false positives, excluded by the upstream driver too.
            if (name.Contains("9009") || name.Contains("9008"))
                continue;

            int addr = name.Contains("ASUF1416") || name.Contains("ASUF1205") || name.Contains("ASUF1204")
                ? 0x38
                : 0x15;

            // Last i2c-N segment in the sysfs path is the bus number.
            int bus = -1;
            foreach (string seg in sysfs.Split('/'))
                if (seg.StartsWith("i2c-") && int.TryParse(seg["i2c-".Length..], out int b))
                    bus = b;
            if (bus < 0)
                continue;

            string eventPath = "";
            foreach (string tok in handlers.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (tok.StartsWith("event"))
                    eventPath = "/dev/input/" + tok;
            if (eventPath.Length == 0)
                continue;

            return new Target
            {
                I2cPath = $"/dev/i2c-{bus}",
                I2cAddr = addr,
                EventPath = eventPath,
                Name = name,
            };
        }
        return null;
    }

    // ioctl helpers not covered by EvdevInterop (read-direction, type 'E').
    private static uint Eviocgabs(ushort axis)
        => 0x80000000u | (24u << 16) | ((uint)'E' << 8) | (0x40u + axis);

    private static uint Eviocgled(uint len)
        => 0x80000000u | (len << 16) | ((uint)'E' << 8) | 0x19u;

    private static uint Eviocgmtslots(uint len)
        => 0x80000000u | (len << 16) | ((uint)'E' << 8) | 0x0au;

    /// <summary>Read the X/Y maxima and resolution from EVIOCGABS, preferring single-touch axes.</summary>
    private static bool ReadAbsBounds(int fd, out TouchpadRange range)
    {
        var (xMax, xRes) = ReadAbs(fd, EvdevInterop.ABS_X);
        if (xMax <= 0)
            (xMax, xRes) = ReadAbs(fd, EvdevInterop.ABS_MT_POSITION_X);
        var (yMax, yRes) = ReadAbs(fd, EvdevInterop.ABS_Y);
        if (yMax <= 0)
            (yMax, yRes) = ReadAbs(fd, EvdevInterop.ABS_MT_POSITION_Y);
        range = new TouchpadRange(xMax, yMax, xRes, yRes);
        return xMax > 0 && yMax > 0;
    }

    private static (int Max, int Res) ReadAbs(int fd, ushort axis)
    {
        // input_absinfo: value, minimum, maximum, fuzz, flat, resolution (6 ints).
        IntPtr buf = Marshal.AllocHGlobal(24);
        try
        {
            if (EvdevInterop.ioctl_buf(fd, Eviocgabs(axis), buf) < 0)
                return (0, 0);
            return (Marshal.ReadInt32(buf, 8), Marshal.ReadInt32(buf, 20));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Feed the current slot and positions: evdev does not send an X/Y equal to the last one again.</summary>
    private static void SeedSlots(NumberPadGesture gesture)
    {
        IntPtr abs = Marshal.AllocHGlobal(24);
        IntPtr buf = Marshal.AllocHGlobal(4 * 65);
        try
        {
            if (EvdevInterop.ioctl_buf(_touchpadFd, Eviocgabs(EvdevInterop.ABS_MT_SLOT), abs) < 0)
                return;
            int slots = Math.Clamp(Marshal.ReadInt32(abs, 8) + 1, 1, 64);
            foreach (ushort code in new[] { EvdevInterop.ABS_MT_POSITION_X, EvdevInterop.ABS_MT_POSITION_Y })
            {
                Marshal.WriteInt32(buf, code);
                if (EvdevInterop.ioctl_buf(_touchpadFd, Eviocgmtslots((uint)(4 * (slots + 1))), buf) < 0)
                    continue;
                for (int s = 0; s < slots; s++)
                {
                    gesture.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_SLOT, s, 0);
                    gesture.Feed(EvdevInterop.EV_ABS, code, Marshal.ReadInt32(buf, 4 * (s + 1)), 0);
                }
            }
            gesture.Feed(EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_SLOT, Marshal.ReadInt32(abs, 0), 0);
        }
        finally
        {
            Marshal.FreeHGlobal(abs);
            Marshal.FreeHGlobal(buf);
        }
    }

    // LED CONTROL

    /// <summary>Write one 13-byte LED-control packet to the touchpad's I2C slave.</summary>
    private static void I2cSend(byte state)
    {
        var target = _target;
        if (target == null)
            return;

        int fd = EvdevInterop.open(target.I2cPath, EvdevInterop.O_WRONLY);
        if (fd < 0)
        {
            Logger.WriteLine($"NumberPad: open {target.I2cPath} failed (errno={Marshal.GetLastWin32Error()})");
            return;
        }
        try
        {
            if (EvdevInterop.ioctl_int(fd, I2C_SLAVE_FORCE, target.I2cAddr) < 0)
            {
                Logger.WriteLine($"NumberPad: I2C_SLAVE_FORCE failed (errno={Marshal.GetLastWin32Error()})");
                return;
            }

            byte[] packet = new byte[13];
            PacketHeader.CopyTo(packet, 0);
            packet[11] = state;
            packet[12] = PacketTerminator;

            IntPtr buf = Marshal.AllocHGlobal(packet.Length);
            try
            {
                Marshal.Copy(packet, 0, buf, packet.Length);
                if (EvdevInterop.write(fd, buf, (ulong)packet.Length) != packet.Length)
                    Logger.WriteLine($"NumberPad: i2c write failed (errno={Marshal.GetLastWin32Error()})");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { EvdevInterop.close(fd); }
    }

    private const ulong I2C_RDWR = 0x0707;
    private const ushort I2C_M_RD = 0x0001;

    /// <summary>
    /// LED protocol version, read as the Windows driver does: set feature report
    /// 0x0d [05 03 71 03], wait 50 ms, get it back as [71 03 lo hi]; the version is
    /// the top nibble of hi. PixArt (093A) is always 1. Returns -1 when the read fails.
    /// </summary>
    private static int ReadLedVersion()
    {
        var target = _target;
        if (target == null)
            return -1;
        if (target.Name.Contains(" 093A:"))
            return 1;

        int fd = EvdevInterop.open(target.I2cPath, EvdevInterop.O_RDWR);
        if (fd < 0)
            return -1;
        // i2c-hid: command register 0x0005, data register 0x0006, feature report 0x0d.
        byte[] set = { 0x05, 0x00, 0x3d, 0x03, 0x06, 0x00, 0x07, 0x00, 0x0d, 0x05, 0x03, 0x71, 0x03 };
        byte[] get = { 0x05, 0x00, 0x3d, 0x02, 0x06, 0x00 };
        IntPtr tx = Marshal.AllocHGlobal(set.Length), rx = Marshal.AllocHGlobal(7), msgs = Marshal.AllocHGlobal(32), data = Marshal.AllocHGlobal(16);
        try
        {
            if (EvdevInterop.ioctl_int(fd, I2C_SLAVE_FORCE, target.I2cAddr) < 0)
                return -1;
            Marshal.Copy(set, 0, tx, set.Length);
            if (EvdevInterop.write(fd, tx, (ulong)set.Length) != set.Length)
                return -1;
            Thread.Sleep(50);

            // struct i2c_msg { u16 addr, flags, len; u8 *buf } x2, then i2c_rdwr_ioctl_data { msgs, nmsgs }.
            Marshal.Copy(get, 0, tx, get.Length);
            WriteMsg(msgs, 0, target.I2cAddr, 0, get.Length, tx);
            WriteMsg(msgs, 16, target.I2cAddr, I2C_M_RD, 7, rx);
            Marshal.WriteIntPtr(data, msgs);
            Marshal.WriteInt32(data, 8, 2);
            if (EvdevInterop.ioctl_buf(fd, I2C_RDWR, data) < 0)
                return -1;
            byte[] r = new byte[7];
            Marshal.Copy(rx, r, 0, 7);
            return r[2] == 0x0d && r[3] == 0x71 && r[4] == 0x03 ? r[6] >> 4 : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(tx);
            Marshal.FreeHGlobal(rx);
            Marshal.FreeHGlobal(msgs);
            Marshal.FreeHGlobal(data);
            EvdevInterop.close(fd);
        }
    }

    private static void WriteMsg(IntPtr msgs, int offset, int addr, ushort flags, int len, IntPtr buf)
    {
        Marshal.WriteInt16(msgs, offset, (short)addr);
        Marshal.WriteInt16(msgs, offset + 2, (short)flags);
        Marshal.WriteInt16(msgs, offset + 4, (short)len);
        Marshal.WriteIntPtr(msgs, offset + 8, buf);
    }

    /// <summary>
    /// Send the saved LED level (1 bright, 2 dim) as the Windows driver does:
    /// version 1 sends 0x40|n; other versions put the level in the on value
    /// (0x01, 0x17). Unknown (-1) uses 0x40|n, the form asus-numberpad-driver uses.
    /// </summary>
    private static void ApplyLedLevel()
    {
        bool dim = AppConfig.Get("numberpad_led", 1) == 2;
        if (_ledVersion is 0 or >= 2)
            I2cSend(dim ? (byte)0x17 : StateEnable);
        else
            I2cSend(dim ? (byte)0x41 : (byte)0x40);
    }

    // CALCULATOR

    private static readonly string[] Calculators =
    {
        "gnome-calculator", "kcalc", "qalculate-qt", "qalculate-gtk", "galculator", "mate-calc", "speedcrunch", "xcalc",
    };

    /// <summary>Start the first calculator found on PATH, like the Windows LaunchCalculatorApp.</summary>
    private static void LaunchCalculator()
    {
        string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (string name in Calculators)
            foreach (string dir in dirs)
            {
                string path = Path.Combine(dir, name);
                if (!File.Exists(path))
                    continue;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false })?.Dispose();
                    Logger.WriteLine($"NumberPad: started {path}");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"NumberPad: start {path} failed: {ex.Message}");
                }
                return;
            }
        Logger.WriteLine("NumberPad: no calculator found");
    }

    // UINPUT VIRTUAL KEYBOARD

    private static bool CreateVirtualKeyboard()
    {
        // Read-write: the desktop sets the NumLock LED on every keyboard, and
        // uinput hands those LED events back to us.
        int fd = EvdevInterop.open(DevUinput,
            EvdevInterop.O_RDWR | EvdevInterop.O_NONBLOCK | EvdevInterop.O_CLOEXEC);
        if (fd < 0)
        {
            Logger.WriteLine($"NumberPad: open {DevUinput} failed (errno={Marshal.GetLastWin32Error()})");
            return false;
        }

        if (EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_KEY) < 0
            || EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_SYN) < 0)
        {
            Logger.WriteLine("NumberPad: UI_SET_EVBIT failed");
            EvdevInterop.close(fd);
            return false;
        }

        foreach (ushort k in NumberPadLayouts.AllKeys)
            EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_KEYBIT, k);
        EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_LED);
        EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_LEDBIT, EvdevInterop.LED_NUML);

        var udev = new EvdevInterop.UinputUserDev
        {
            name = VirtualDeviceName,
            id = new EvdevInterop.InputId
            {
                bustype = EvdevInterop.BUS_VIRTUAL,
                vendor = 0x0FAC,
                product = 0x6771,
                version = 1,
            },
            ff_effects_max = 0,
            absmax = new int[64],
            absmin = new int[64],
            absfuzz = new int[64],
            absflat = new int[64],
        };

        int size = Marshal.SizeOf<EvdevInterop.UinputUserDev>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(udev, buf, false);
            long n = EvdevInterop.write(fd, buf, (ulong)size);
            if (n != size)
            {
                Logger.WriteLine($"NumberPad: write(uinput_user_dev) returned {n}, expected {size} (errno={Marshal.GetLastWin32Error()})");
                EvdevInterop.close(fd);
                return false;
            }
        }
        finally
        {
            Marshal.DestroyStructure<EvdevInterop.UinputUserDev>(buf);
            Marshal.FreeHGlobal(buf);
        }

        if (EvdevInterop.ioctl_uint(fd, EvdevInterop.UI_DEV_CREATE, 0) < 0)
        {
            Logger.WriteLine("NumberPad: UI_DEV_CREATE failed");
            EvdevInterop.close(fd);
            return false;
        }

        _uinputFd = fd;
        return true;
    }

    private static void DrainVirtualKeyboard()
    {
        IntPtr buf = Marshal.AllocHGlobal(EvdevInterop.InputEventSize);
        try
        {
            while (EvdevInterop.read(_uinputFd, buf, EvdevInterop.InputEventSize) == EvdevInterop.InputEventSize)
            {
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void DestroyVirtualKeyboard()
    {
        if (_uinputFd < 0)
            return;
        EvdevInterop.ioctl_uint(_uinputFd, EvdevInterop.UI_DEV_DESTROY, 0);
        EvdevInterop.close(_uinputFd);
        _uinputFd = -1;
    }

    // UINPUT VIRTUAL TOUCHPAD

    private static readonly ushort[] TouchpadKeys =
    {
        EvdevInterop.BTN_LEFT, 0x111, 0x112, EvdevInterop.BTN_TOOL_FINGER, EvdevInterop.BTN_TOOL_QUINTTAP,
        EvdevInterop.BTN_TOUCH, EvdevInterop.BTN_TOOL_DOUBLETAP, EvdevInterop.BTN_TOOL_TRIPLETAP, EvdevInterop.BTN_TOOL_QUADTAP,
    };

    private static readonly ushort[] TouchpadAxes =
    {
        EvdevInterop.ABS_X, EvdevInterop.ABS_Y, EvdevInterop.ABS_MT_SLOT,
        EvdevInterop.ABS_MT_POSITION_X, EvdevInterop.ABS_MT_POSITION_Y, EvdevInterop.ABS_MT_TRACKING_ID,
    };

    /// <summary>
    /// Copy of the real touchpad (name, ids, axes, buttons, properties) that
    /// gets the pointer touches while the real one is grabbed. Same name and
    /// ids so the desktop applies the same touchpad settings.
    /// </summary>
    private static bool CreateVirtualTouchpad(int realFd, string name)
    {
        int fd = EvdevInterop.open(DevUinput,
            EvdevInterop.O_WRONLY | EvdevInterop.O_NONBLOCK | EvdevInterop.O_CLOEXEC);
        if (fd < 0)
            return false;

        IntPtr buf = Marshal.AllocHGlobal(96);
        try
        {
            EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_SYN);
            EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_KEY);
            EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_ABS);

            byte[] keys = ReadBits(realFd, EvdevInterop.EVIOCGBIT(EvdevInterop.EV_KEY, 96), buf, 96);
            foreach (ushort k in TouchpadKeys)
                if ((keys[k / 8] & (1 << (k % 8))) != 0 || k is EvdevInterop.BTN_LEFT or EvdevInterop.BTN_TOUCH or EvdevInterop.BTN_TOOL_FINGER)
                    EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_KEYBIT, k);

            byte[] props = ReadBits(realFd, EvdevInterop.EVIOCGPROP(4), buf, 4);
            for (int p = 0; p < 32; p++)
                if ((props[p / 8] & (1 << (p % 8))) != 0)
                    EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_PROPBIT, p);

            // struct uinput_abs_setup: u16 code, pad, input_absinfo.
            foreach (ushort axis in TouchpadAxes)
            {
                if (EvdevInterop.ioctl_buf(realFd, EvdevInterop.EVIOCGABS(axis), buf + 4) < 0)
                    continue;
                Marshal.WriteInt32(buf, axis);
                Marshal.WriteInt32(buf, 4, 0);
                EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_ABSBIT, axis);
                if (EvdevInterop.ioctl_buf(fd, EvdevInterop.UI_ABS_SETUP, buf) < 0)
                    throw new IOException($"UI_ABS_SETUP {axis} errno={Marshal.GetLastWin32Error()}");
            }

            // struct uinput_setup: input_id, char name[80], u32 ff_effects_max.
            for (int i = 0; i < 92; i++)
                Marshal.WriteByte(buf, i, 0);
            EvdevInterop.ioctl_buf(realFd, EvdevInterop.EVIOCGID, buf);
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            Marshal.Copy(nameBytes, 0, buf + 8, Math.Min(nameBytes.Length, 79));
            if (EvdevInterop.ioctl_buf(fd, EvdevInterop.UI_DEV_SETUP, buf) < 0
                || EvdevInterop.ioctl_uint(fd, EvdevInterop.UI_DEV_CREATE, 0) < 0)
                throw new IOException($"UI_DEV_SETUP/CREATE errno={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"NumberPad: virtual touchpad failed: {ex.Message}");
            EvdevInterop.close(fd);
            return false;
        }
        finally { Marshal.FreeHGlobal(buf); }

        _touchFd = fd;
        return true;
    }

    private static byte[] ReadBits(int fd, uint request, IntPtr buf, int len)
    {
        for (int i = 0; i < len; i++)
            Marshal.WriteByte(buf, i, 0);
        EvdevInterop.ioctl_buf(fd, request, buf);
        byte[] bits = new byte[len];
        Marshal.Copy(buf, bits, 0, len);
        return bits;
    }

    private static void DestroyVirtualTouchpad()
    {
        if (_touchFd < 0)
            return;
        EvdevInterop.ioctl_uint(_touchFd, EvdevInterop.UI_DEV_DESTROY, 0);
        EvdevInterop.close(_touchFd);
        _touchFd = -1;
    }

    private static void EmitKey(ushort code, int value)
    {
        WriteEvent(_uinputFd, EvdevInterop.EV_KEY, code, value);
        WriteEvent(_uinputFd, EvdevInterop.EV_SYN, EvdevInterop.SYN_REPORT, 0);
    }

    private static void WriteEvent(int fd, ushort type, ushort code, int value)
    {
        if (fd < 0)
            return;
        var ev = new EvdevInterop.InputEvent { type = type, code = code, value = value };
        IntPtr buf = Marshal.AllocHGlobal(EvdevInterop.InputEventSize);
        try
        {
            Marshal.StructureToPtr(ev, buf, false);
            EvdevInterop.write(fd, buf, EvdevInterop.InputEventSize);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Press keys in order, release in reverse (modifier ordering).</summary>
    private static void EmitTap(ushort[] keys)
    {
        foreach (ushort key in keys)
            EmitKey(key, 1);
        for (int i = keys.Length - 1; i >= 0; i--)
            EmitKey(keys[i], 0);
    }

    // NUMLOCK

    /// <summary>
    /// True if any connected evdev device reports the NumLock LED on. Some
    /// laptops expose the LED on only one of several input devices, so all
    /// are checked before concluding it is off.
    /// </summary>
    private static bool IsNumLockOn()
    {
        if (!Directory.Exists("/dev/input"))
            return false;

        foreach (string path in Directory.EnumerateFiles("/dev/input", "event*"))
        {
            int fd = EvdevInterop.open(path,
                EvdevInterop.O_RDONLY | EvdevInterop.O_NONBLOCK | EvdevInterop.O_CLOEXEC);
            if (fd < 0)
                continue;
            try
            {
                const uint len = 8;
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    for (int i = 0; i < len; i++)
                        Marshal.WriteByte(buf, i, 0);
                    // LED_NUML is bit 0 of the EVIOCGLED bitmask.
                    if (EvdevInterop.ioctl_buf(fd, Eviocgled(len), buf) >= 0
                        && (Marshal.ReadByte(buf, 0) & 1) != 0)
                        return true;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { EvdevInterop.close(fd); }
        }
        return false;
    }

    private static Dictionary<string, string> NumLockDevices()
    {
        try
        { return NumLockDevices(File.ReadAllText(ProcBusInputDevices)); }
        catch
        { return new(); }
    }

    /// <summary>Sysfs path to event node of every input device with a NumLock LED.</summary>
    internal static Dictionary<string, string> NumLockDevices(string contents)
    {
        var devices = new Dictionary<string, string>();
        foreach (string block in contents.Split("\n\n"))
        {
            string? sysfs = null, node = null;
            bool numLed = false;
            foreach (string line in block.Split('\n'))
            {
                if (line.StartsWith("S: Sysfs="))
                    sysfs = line["S: Sysfs=".Length..];
                else if (line.StartsWith("H: Handlers="))
                    node = line["H: Handlers=".Length..].Split(' ').FirstOrDefault(t => t.StartsWith("event"));
                else if (line.StartsWith("B: LED="))
                    numLed = ulong.TryParse(line["B: LED=".Length..].Trim(), System.Globalization.NumberStyles.HexNumber, null, out ulong bits) && (bits & 1) != 0;
            }
            if (sysfs != null && node != null && numLed)
                devices[sysfs] = "/dev/input/" + node;
        }
        return devices;
    }

    private static void WriteNumLockLeds(IEnumerable<string> nodes, bool on)
    {
        foreach (string node in nodes)
        {
            int fd = EvdevInterop.open(node, EvdevInterop.O_WRONLY | EvdevInterop.O_NONBLOCK | EvdevInterop.O_CLOEXEC);
            if (fd < 0)
                continue;
            WriteEvent(fd, EvdevInterop.EV_LED, EvdevInterop.LED_NUML, on ? 1 : 0);
            WriteEvent(fd, EvdevInterop.EV_SYN, EvdevInterop.SYN_REPORT, 0);
            EvdevInterop.close(fd);
        }
    }

    // EVENT LOOP

    /// <summary>LEDs and evdev grab toggled in tandem; failures log and continue.</summary>
    private static void ApplyActiveState(bool active)
    {
        if (active)
        {
            I2cSend(StateUnlock);
            I2cSend(StateEnable);
            if (_layout.Geometry?.CalcWidth > 0 && AppConfig.Get("numberpad_led", 1) == 2)
                ApplyLedLevel();
            if (EvdevInterop.ioctl_int(_touchpadFd, EvdevInterop.EVIOCGRAB, 1) < 0)
                Logger.WriteLine($"NumberPad: EVIOCGRAB failed (errno={Marshal.GetLastWin32Error()})");
        }
        else
        {
            if (EvdevInterop.ioctl_int(_touchpadFd, EvdevInterop.EVIOCGRAB, 0) < 0)
                Logger.WriteLine($"NumberPad: ungrab failed (errno={Marshal.GetLastWin32Error()})");
            I2cSend(StateDisable);
        }
    }

    private static void EventLoop()
    {
        var poll = new EvdevInterop.Pollfd[3];
        poll[0].fd = _touchpadFd;
        poll[0].events = EvdevInterop.POLLIN;
        poll[1].fd = _stopPipe[0];
        poll[1].events = EvdevInterop.POLLIN;
        poll[2].fd = _uinputFd;
        poll[2].events = EvdevInterop.POLLIN;

        var gesture = new NumberPadGesture(new NumberPadSurface(_layout, _range));
        var pointer = new PointerWriter();
        SeedSlots(gesture);

        IntPtr buf = Marshal.AllocHGlobal(EvdevInterop.InputEventSize);
        try
        {
            while (_running)
            {
                int timeout = PollTimeoutMs;
                long wait = gesture.MsUntilDue(Environment.TickCount64);
                if (wait >= 0)
                    timeout = (int)Math.Min(wait, PollTimeoutMs);

                int rc = EvdevInterop.poll(poll, 3, timeout);
                if (rc < 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 4 /* EINTR */)
                        continue;
                    Logger.WriteLine($"NumberPad: poll() failed errno={err}");
                    break;
                }

                Apply(gesture, pointer, gesture.Tick(Environment.TickCount64));

                if (rc <= 0)
                    continue;
                if ((poll[1].revents & EvdevInterop.POLLIN) != 0)
                {
                    if (EvdevInterop.read(_stopPipe[0], buf, 1) != 1 || Marshal.ReadByte(buf) != 't' || !_running)
                        break;
                    // Touchpad switched off: NumberPad and NumLock off.
                    if (gesture.Active)
                    {
                        if (_numLock)
                            EmitTap(new[] { NumberPadLayouts.KEY_NUMLOCK });
                        _numLock = false;
                        SetActive(gesture, pointer, false);
                        Logger.WriteLine("NumberPad: touchpad off -> idle");
                    }
                }

                // NumLock LED from the desktop: follow the last state only, resume replays a lit LED as off then on.
                bool numLock = _numLock, ledEvent = false;
                while ((poll[2].revents & EvdevInterop.POLLIN) != 0
                    && EvdevInterop.read(_uinputFd, buf, EvdevInterop.InputEventSize) == EvdevInterop.InputEventSize)
                {
                    var led = Marshal.PtrToStructure<EvdevInterop.InputEvent>(buf);
                    if (led.type == EvdevInterop.EV_LED && led.code == EvdevInterop.LED_NUML)
                    {
                        numLock = led.value != 0;
                        ledEvent = true;
                    }
                }
                if (ledEvent)
                {
                    var devices = NumLockDevices();
                    bool added = devices.Keys.Any(d => !_ledDevices.Contains(d));
                    _ledDevices = devices.Keys.ToHashSet();
                    if (added)
                    {
                        // The kernel reset every LED for the new keyboard; NumLock did not change.
                        numLock = _numLock;
                        WriteNumLockLeds(devices.Values, _numLock);
                        Logger.WriteLine($"NumberPad: LED reset by a new keyboard, NumLock LED {(_numLock ? "on" : "off")} restored");
                    }
                }
                if (numLock != _numLock)
                {
                    _numLock = numLock;
                    if (gesture.Active != _numLock && !(_numLock && _touchpadOff))
                    {
                        SetActive(gesture, pointer, _numLock);
                        Logger.WriteLine($"NumberPad: NumLock {(_numLock ? "on" : "off")} -> {(_numLock ? "active" : "idle")}");
                    }
                }

                if ((poll[0].revents & (EvdevInterop.POLLERR | EvdevInterop.POLLHUP | EvdevInterop.POLLNVAL)) != 0)
                {
                    Logger.WriteLine("NumberPad: touchpad device lost");
                    break;
                }
                if ((poll[0].revents & EvdevInterop.POLLIN) == 0)
                    continue;

                while (true)
                {
                    long n = EvdevInterop.read(_touchpadFd, buf, EvdevInterop.InputEventSize);
                    if (n != EvdevInterop.InputEventSize)
                        break;

                    var ev = Marshal.PtrToStructure<EvdevInterop.InputEvent>(buf);
                    Apply(gesture, pointer, gesture.Feed(ev.type, ev.code, ev.value, Environment.TickCount64));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        if (gesture.Active)
            SetActive(gesture, pointer, false);
    }

    private static void Apply(NumberPadGesture gesture, PointerWriter pointer, IReadOnlyList<NumberPadOutput> outputs)
    {
        if (outputs.Count == 0)
            return;
        foreach (var o in outputs.ToArray())
        {
            switch (o.Kind)
            {
                case NumberPadEvent.KeyDown:
                    foreach (ushort key in o.Keys!)
                        EmitKey(key, 1);
                    break;
                case NumberPadEvent.KeyUp:
                    for (int i = o.Keys!.Length - 1; i >= 0; i--)
                        EmitKey(o.Keys[i], 0);
                    break;
                case NumberPadEvent.Pointer:
                    foreach (var (type, code, value) in pointer.Encode(o.Frame!))
                        WriteEvent(_touchFd, type, code, value);
                    break;
                case NumberPadEvent.Toggle:
                    // The hold presses NumLock and the NumberPad follows it, as on Windows.
                    bool on = !gesture.Active;
                    if (on && _touchpadOff)
                        break;
                    if (_numLock != on)
                        EmitTap(new[] { NumberPadLayouts.KEY_NUMLOCK });
                    _numLock = on;
                    SetActive(gesture, pointer, on);
                    Logger.WriteLine($"NumberPad: NumLock hold -> {(on ? "active" : "idle")}");
                    break;
                case NumberPadEvent.Brightness:
                    AppConfig.Set("numberpad_led", AppConfig.Get("numberpad_led", 1) == 2 ? 1 : 2);
                    ApplyLedLevel();
                    Logger.WriteLine($"NumberPad: LED level {AppConfig.Get("numberpad_led", 1)}");
                    break;
                case NumberPadEvent.Calculator:
                    LaunchCalculator();
                    break;
            }
        }
    }

    private static void SetActive(NumberPadGesture gesture, PointerWriter pointer, bool on)
    {
        Apply(gesture, pointer, gesture.SetActive(on));
        ApplyActiveState(on);
    }

    private static void ReleaseAll()
    {
        if (_touchpadFd >= 0)
        {
            try
            { EvdevInterop.close(_touchpadFd); }
            catch { }
            _touchpadFd = -1;
        }

        DestroyVirtualKeyboard();
        DestroyVirtualTouchpad();

        if (_stopPipe[0] >= 0)
        { try { EvdevInterop.close(_stopPipe[0]); } catch { } _stopPipe[0] = -1; }
        if (_stopPipe[1] >= 0)
        { try { EvdevInterop.close(_stopPipe[1]); } catch { } _stopPipe[1] = -1; }

        _target = null;
        _thread = null;
    }
}
