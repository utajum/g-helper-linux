using System.Runtime.InteropServices;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Input;

/// <summary>
/// ASUS illuminated touchpad NumberPad driver. Two responsibilities:
///
/// 1. LED control - the NumberPad LEDs are toggled by writing a 13-byte
///    magic packet to the touchpad's I2C slave (0x15, or 0x38 on a few ASUF
///    models). The bus number comes from parsing /proc/bus/input/devices.
/// 2. Touch interception - while active the touchpad is EVIOCGRAB-ed so the
///    pointer freezes, taps are mapped to a proportional grid, and KEY_KP*
///    keycodes are emitted through a /dev/uinput virtual keyboard (keycodes,
///    not characters, so the compositor applies the user's layout natively).
///
/// Holding a finger in the top-right corner for one second toggles between
/// idle (LEDs off, no grab) and active. The service itself is enabled via
/// the "numberpad" config flag; call <see cref="InitIfEnabled"/> at startup.
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

    private const ushort BTN_TOUCH = 0x14a;
    private const ushort ABS_MT_POSITION_X = 0x35;
    private const ushort ABS_MT_POSITION_Y = 0x36;

    private const long HoldDurationMs = 1000;
    private const int PollTimeoutMs = 500;

    private sealed class Target
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
    private static int _xMax;
    private static int _yMax;
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

    /// <summary>
    /// Open the touchpad and virtual keyboard, then run the event loop on a
    /// background thread. Starts in idle (LEDs off, no grab); the corner
    /// hold gesture activates. Idempotent.
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

            if (!ReadAbsBounds(fd, out int xMax, out int yMax))
            {
                Logger.WriteLine("NumberPad: touchpad reported invalid absolute range");
                EvdevInterop.close(fd);
                return false;
            }

            if (!CreateVirtualKeyboard())
            {
                EvdevInterop.close(fd);
                return false;
            }

            if (EvdevInterop.pipe(_stopPipe) != 0)
            {
                Logger.WriteLine("NumberPad: pipe() failed");
                DestroyVirtualKeyboard();
                EvdevInterop.close(fd);
                return false;
            }

            _target = target;
            _touchpadFd = fd;
            _xMax = xMax;
            _yMax = yMax;
            _layout = NumberPadLayouts.ForProduct(AppConfig.GetModel());

            _running = true;
            _thread = new Thread(EventLoop) { IsBackground = true, Name = "numberpad" };
            _thread.Start();

            Logger.WriteLine($"NumberPad: started on {target.EventPath} '{target.Name}' i2c={target.I2cPath} addr=0x{target.I2cAddr:x2} layout={_layout.Cols}x{_layout.Rows}");
            return true;
        }
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
            if (_stopPipe[1] >= 0)
            {
                try
                {
                    var b = Marshal.AllocHGlobal(1);
                    Marshal.WriteByte(b, (byte)'q');
                    EvdevInterop.write(_stopPipe[1], b, 1);
                    Marshal.FreeHGlobal(b);
                }
                catch { }
            }
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
            // ASUE/ASUP/ASUF are ASUS-proprietary ACPI ids. ELAN touchpads
            // exist on every vendor's laptops, so the generic ELAN name only
            // counts on ASUS hardware; sending the NumberPad I2C magic to a
            // Lenovo/HP ELAN controller is undefined behavior.
            bool familyMatch = name.Contains("ASUE")
                || name.Contains("ASUP") || name.Contains("ASUF")
                || (name.Contains("ELAN") && Helpers.AppConfig.IsAsusDevice());
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

    /// <summary>Read the X/Y maxima from EVIOCGABS, preferring single-touch axes.</summary>
    private static bool ReadAbsBounds(int fd, out int xMax, out int yMax)
    {
        xMax = ReadAbsMax(fd, EvdevInterop.ABS_X);
        if (xMax <= 0)
            xMax = ReadAbsMax(fd, ABS_MT_POSITION_X);
        yMax = ReadAbsMax(fd, EvdevInterop.ABS_Y);
        if (yMax <= 0)
            yMax = ReadAbsMax(fd, ABS_MT_POSITION_Y);
        return xMax > 0 && yMax > 0;
    }

    private static int ReadAbsMax(int fd, ushort axis)
    {
        // input_absinfo: value, minimum, maximum, fuzz, flat, resolution (6 ints).
        IntPtr buf = Marshal.AllocHGlobal(24);
        try
        {
            if (EvdevInterop.ioctl_buf(fd, Eviocgabs(axis), buf) < 0)
                return 0;
            return Marshal.ReadInt32(buf, 8);
        }
        finally { Marshal.FreeHGlobal(buf); }
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

    // UINPUT VIRTUAL KEYBOARD

    private static bool CreateVirtualKeyboard()
    {
        int fd = EvdevInterop.open(DevUinput,
            EvdevInterop.O_WRONLY | EvdevInterop.O_NONBLOCK | EvdevInterop.O_CLOEXEC);
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

    private static void DestroyVirtualKeyboard()
    {
        if (_uinputFd < 0)
            return;
        EvdevInterop.ioctl_uint(_uinputFd, EvdevInterop.UI_DEV_DESTROY, 0);
        EvdevInterop.close(_uinputFd);
        _uinputFd = -1;
    }

    private static void EmitKey(ushort code, int value)
    {
        WriteEvent(EvdevInterop.EV_KEY, code, value);
        WriteEvent(EvdevInterop.EV_SYN, EvdevInterop.SYN_REPORT, 0);
    }

    private static void WriteEvent(ushort type, ushort code, int value)
    {
        if (_uinputFd < 0)
            return;
        var ev = new EvdevInterop.InputEvent { type = type, code = code, value = value };
        IntPtr buf = Marshal.AllocHGlobal(EvdevInterop.InputEventSize);
        try
        {
            Marshal.StructureToPtr(ev, buf, false);
            EvdevInterop.write(_uinputFd, buf, EvdevInterop.InputEventSize);
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

    /// <summary>
    /// Ensure NumLock is on so KEY_KP1..9 produce digits, not navigation.
    /// Called only on idle-to-active transitions; deactivation leaves
    /// NumLock alone since the user's original state is unknowable.
    /// </summary>
    private static void EnsureNumLockOn()
    {
        if (!IsNumLockOn())
            EmitTap(new[] { NumberPadLayouts.KEY_NUMLOCK });
    }

    // EVENT LOOP

    private static bool InCorner(int x, int y)
        => x > _xMax * _layout.CornerXMinFrac && y < _yMax * _layout.CornerYMaxFrac;

    /// <summary>Cell index for a touch point, or -1 if out of range or inert.</summary>
    private static int CellFor(int x, int y)
    {
        var layout = _layout;
        if (_xMax <= 0 || _yMax <= 0)
            return -1;
        int col = (int)Math.Clamp((long)x * layout.Cols / _xMax, 0, layout.Cols - 1);
        int row = (int)Math.Clamp((long)y * layout.Rows / _yMax, 0, layout.Rows - 1);
        int idx = row * layout.Cols + col;
        return layout.Cells[idx] == null ? -1 : idx;
    }

    /// <summary>LEDs and evdev grab toggled in tandem; failures log and continue.</summary>
    private static void ApplyActiveState(bool active)
    {
        if (active)
        {
            I2cSend(StateUnlock);
            I2cSend(StateEnable);
            if (EvdevInterop.ioctl_int(_touchpadFd, EvdevInterop.EVIOCGRAB, 1) < 0)
                Logger.WriteLine($"NumberPad: EVIOCGRAB failed (errno={Marshal.GetLastWin32Error()})");
            EnsureNumLockOn();
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
        var poll = new EvdevInterop.Pollfd[2];
        poll[0].fd = _touchpadFd;
        poll[0].events = EvdevInterop.POLLIN;
        poll[1].fd = _stopPipe[0];
        poll[1].events = EvdevInterop.POLLIN;

        bool active = false;
        int curX = 0, curY = 0;
        int pressCell = -1;
        long holdDeadline = -1;

        IntPtr buf = Marshal.AllocHGlobal(EvdevInterop.InputEventSize);
        try
        {
            while (_running)
            {
                int timeout = PollTimeoutMs;
                if (holdDeadline >= 0)
                    timeout = (int)Math.Clamp(holdDeadline - Environment.TickCount64, 0, PollTimeoutMs);

                int rc = EvdevInterop.poll(poll, 2, timeout);
                if (rc < 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 4 /* EINTR */)
                        continue;
                    Logger.WriteLine($"NumberPad: poll() failed errno={err}");
                    break;
                }

                // Corner-hold timer fired: flip active/idle and consume this
                // touch so the upcoming BTN_TOUCH=0 does not also emit a key.
                if (holdDeadline >= 0 && Environment.TickCount64 >= holdDeadline)
                {
                    holdDeadline = -1;
                    pressCell = -1;
                    active = !active;
                    ApplyActiveState(active);
                    Logger.WriteLine($"NumberPad: corner hold -> {(active ? "active" : "idle")}");
                }

                if (rc <= 0)
                    continue;
                if ((poll[1].revents & EvdevInterop.POLLIN) != 0)
                    break;
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

                    if (ev.type == EvdevInterop.EV_KEY && ev.code == BTN_TOUCH)
                    {
                        if (ev.value == 1)
                        {
                            pressCell = active ? CellFor(curX, curY) : -1;
                            if (InCorner(curX, curY))
                                holdDeadline = Environment.TickCount64 + HoldDurationMs;
                        }
                        else if (ev.value == 0)
                        {
                            holdDeadline = -1;
                            if (active && pressCell >= 0 && pressCell == CellFor(curX, curY))
                            {
                                var keys = _layout.Cells[pressCell];
                                if (keys != null)
                                    EmitTap(keys);
                            }
                            pressCell = -1;
                        }
                    }
                    else if (ev.type == EvdevInterop.EV_ABS
                        && (ev.code == EvdevInterop.ABS_X || ev.code == ABS_MT_POSITION_X))
                    {
                        curX = ev.value;
                        if (holdDeadline >= 0 && !InCorner(curX, curY))
                            holdDeadline = -1;
                    }
                    else if (ev.type == EvdevInterop.EV_ABS
                        && (ev.code == EvdevInterop.ABS_Y || ev.code == ABS_MT_POSITION_Y))
                    {
                        curY = ev.value;
                        if (holdDeadline >= 0 && !InCorner(curX, curY))
                            holdDeadline = -1;
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        if (active)
            ApplyActiveState(false);
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

        if (_stopPipe[0] >= 0)
        { try { EvdevInterop.close(_stopPipe[0]); } catch { } _stopPipe[0] = -1; }
        if (_stopPipe[1] >= 0)
        { try { EvdevInterop.close(_stopPipe[1]); } catch { } _stopPipe[1] = -1; }

        _target = null;
        _thread = null;
    }
}
