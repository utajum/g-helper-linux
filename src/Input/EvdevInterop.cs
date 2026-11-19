using System.Runtime.InteropServices;

namespace GHelper.Linux.Input;

/// <summary>
/// libc + Linux input subsystem P/Invoke layer. Just enough surface to
/// enumerate /dev/input/event*, query capabilities, EVIOCGRAB exclusively,
/// and write back through a /dev/uinput virtual device.
///
/// Reference: linux/input.h, linux/input-event-codes.h, linux/uinput.h.
/// All struct layouts mirror the kernel ABI on x86_64; ioctl request numbers
/// are computed from the canonical _IOR/_IOW/_IOWR macros.
/// </summary>
internal static class EvdevInterop
{
    private const string Libc = "libc";

    // open() flags
    public const int O_RDONLY = 0x00;
    public const int O_WRONLY = 0x01;
    public const int O_RDWR = 0x02;
    public const int O_NONBLOCK = 0x800;
    public const int O_CLOEXEC = 0x80000;

    // poll() events
    public const short POLLIN = 0x0001;
    public const short POLLERR = 0x0008;
    public const short POLLHUP = 0x0010;
    public const short POLLNVAL = 0x0020;

    // EV_* event types
    public const ushort EV_SYN = 0x00;
    public const ushort EV_KEY = 0x01;
    public const ushort EV_ABS = 0x03;
    public const ushort EV_MSC = 0x04;
    public const ushort EV_LED = 0x11;
    public const ushort EV_REP = 0x14;

    // ABS_* axis codes (gamepad subset). LX/LY = left stick, RX/RY = right
    // stick, Z/RZ = analog triggers (LT/RT). Used by AllyControl's stick
    // auto-capture.
    public const ushort ABS_X = 0x00;   // left stick X
    public const ushort ABS_Y = 0x01;   // left stick Y
    public const ushort ABS_Z = 0x02;   // left analog trigger (LT)
    public const ushort ABS_RX = 0x03;  // right stick X
    public const ushort ABS_RY = 0x04;  // right stick Y
    public const ushort ABS_RZ = 0x05;  // right analog trigger (RT)

    // D-pad hat axes (gamepads report the d-pad as ABS_HAT0X/Y of -1/0/1).
    public const ushort ABS_HAT0X = 0x10;
    public const ushort ABS_HAT0Y = 0x11;

    // BTN_* gamepad button codes (linux/input-event-codes.h).
    public const ushort BTN_SOUTH = 0x130;  // A
    public const ushort BTN_EAST = 0x131;   // B
    public const ushort BTN_NORTH = 0x133;  // X (historical naming, Y on xbox layout)
    public const ushort BTN_WEST = 0x134;   // Y (historical naming, X on xbox layout)
    public const ushort BTN_TL = 0x136;     // LB
    public const ushort BTN_TR = 0x137;     // RB
    public const ushort BTN_SELECT = 0x13a; // View
    public const ushort BTN_START = 0x13b;  // Menu
    public const ushort BTN_MODE = 0x13c;   // Guide

    // Touchpad codes used by NumberPad.
    public const ushort BTN_LEFT = 0x110;
    public const ushort BTN_TOOL_FINGER = 0x145;
    public const ushort BTN_TOOL_QUINTTAP = 0x148;
    public const ushort BTN_TOUCH = 0x14a;
    public const ushort BTN_TOOL_DOUBLETAP = 0x14d;
    public const ushort BTN_TOOL_TRIPLETAP = 0x14e;
    public const ushort BTN_TOOL_QUADTAP = 0x14f;
    public const ushort ABS_MT_SLOT = 0x2f;
    public const ushort ABS_MT_POSITION_X = 0x35;
    public const ushort ABS_MT_POSITION_Y = 0x36;
    public const ushort ABS_MT_TRACKING_ID = 0x39;
    public const ushort LED_NUML = 0x00;

    // MSC_* misc-event subcodes (used inside EV_MSC events).
    public const ushort MSC_SCAN = 0x04;

    // SYN_* sync codes
    public const ushort SYN_REPORT = 0x00;

    // BUS_* values from input.h
    public const ushort BUS_PCI = 0x01;
    public const ushort BUS_USB = 0x03;
    public const ushort BUS_HIL = 0x04;
    public const ushort BUS_BLUETOOTH = 0x05;
    public const ushort BUS_VIRTUAL = 0x06;
    public const ushort BUS_I8042 = 0x11;
    public const ushort BUS_I2C = 0x18;
    public const ushort BUS_HOST = 0x19;

    // KEY_* codes (subset we care about for fn-lock)
    public const ushort KEY_RESERVED = 0;
    public const ushort KEY_ESC = 1;
    public const ushort KEY_F1 = 59;
    public const ushort KEY_F2 = 60;
    public const ushort KEY_F3 = 61;
    public const ushort KEY_F4 = 62;
    public const ushort KEY_F5 = 63;
    public const ushort KEY_F6 = 64;
    public const ushort KEY_F7 = 65;
    public const ushort KEY_F8 = 66;
    public const ushort KEY_F9 = 67;
    public const ushort KEY_F10 = 68;
    public const ushort KEY_F11 = 87;
    public const ushort KEY_F12 = 88;
    public const ushort KEY_LEFTMETA = 125;
    public const ushort KEY_RIGHTMETA = 126;
    public const ushort KEY_MUTE = 113;
    public const ushort KEY_VOLUMEDOWN = 114;
    public const ushort KEY_VOLUMEUP = 115;
    public const ushort KEY_BRIGHTNESSDOWN = 224;
    public const ushort KEY_BRIGHTNESSUP = 225;
    public const ushort KEY_SEARCH = 217;
    public const ushort KEY_PROG1 = 148;
    public const ushort KEY_PROG2 = 149;
    public const ushort KEY_PROG3 = 202;
    public const ushort KEY_PROG4 = 203;
    public const ushort KEY_KBDILLUMTOGGLE = 228;
    public const ushort KEY_KBDILLUMDOWN = 229;
    public const ushort KEY_KBDILLUMUP = 230;
    public const ushort KEY_RFKILL = 247;
    public const ushort KEY_CAMERA = 212;
    public const ushort KEY_SYSRQ = 99;            // PrintScreen / SysRq
    public const ushort KEY_SWITCHVIDEOMODE = 227; // Display switch / Project (Win+P equivalent)
    public const ushort KEY_TOUCHPAD_TOGGLE = 530;
    public const ushort KEY_PLAYPAUSE = 164;
    public const ushort KEY_PREVIOUSSONG = 165;
    public const ushort KEY_NEXTSONG = 163;
    public const ushort KEY_SLEEP = 142;
    public const ushort KEY_WAKEUP = 143;
    public const ushort KEY_FN = 0x1d0;
    public const ushort KEY_FN_ESC = 0x1d1;
    public const ushort KEY_F13 = 183;
    public const ushort KEY_F14 = 184;
    public const ushort KEY_F15 = 185;
    public const ushort KEY_F16 = 186;
    public const ushort KEY_F17 = 187;
    public const ushort KEY_F18 = 188;
    public const ushort KEY_F19 = 189;
    public const ushort KEY_F20 = 190;
    public const ushort KEY_F21 = 191;
    public const ushort KEY_F22 = 192;
    public const ushort KEY_F23 = 193;
    public const ushort KEY_F24 = 194;
    public const ushort KEY_MAX = 0x2ff;

    /// <summary>The 12 default-mapped function keys, in F1..F12 order.</summary>
    public static readonly ushort[] FunctionKeys =
    {
        KEY_F1, KEY_F2, KEY_F3, KEY_F4, KEY_F5, KEY_F6,
        KEY_F7, KEY_F8, KEY_F9, KEY_F10, KEY_F11, KEY_F12,
    };

    // ioctl request encoding (Linux x86_64 _IOC scheme)
    // _IOC(dir, type, nr, size)
    private const uint _IOC_NRBITS = 8;
    private const uint _IOC_TYPEBITS = 8;
    private const uint _IOC_SIZEBITS = 14;
    private const uint _IOC_NRSHIFT = 0;
    private const uint _IOC_TYPESHIFT = _IOC_NRSHIFT + _IOC_NRBITS;
    private const uint _IOC_SIZESHIFT = _IOC_TYPESHIFT + _IOC_TYPEBITS;
    private const uint _IOC_DIRSHIFT = _IOC_SIZESHIFT + _IOC_SIZEBITS;
    private const uint _IOC_NONE = 0;
    private const uint _IOC_WRITE = 1;
    private const uint _IOC_READ = 2;

    private static uint IOC(uint dir, char type, uint nr, uint size)
        => (dir << (int)_IOC_DIRSHIFT)
         | ((uint)type << (int)_IOC_TYPESHIFT)
         | (nr << (int)_IOC_NRSHIFT)
         | (size << (int)_IOC_SIZESHIFT);

    private static uint IOR(char type, uint nr, uint size) => IOC(_IOC_READ, type, nr, size);
    private static uint IOW(char type, uint nr, uint size) => IOC(_IOC_WRITE, type, nr, size);
    private static uint IOWR(char type, uint nr, uint size) => IOC(_IOC_READ | _IOC_WRITE, type, nr, size);
    private static uint IO(char type, uint nr) => IOC(_IOC_NONE, type, nr, 0);

    // ioctl request numbers (computed once at static-init).
    public static readonly uint EVIOCGID = IOR('E', 0x02, (uint)Marshal.SizeOf<InputId>());
    public static readonly uint EVIOCGRAB = IOW('E', 0x90, sizeof(int));
    public static uint EVIOCGNAME(uint len) => IOC(_IOC_READ, 'E', 0x06, len);
    public static uint EVIOCGBIT(uint ev, uint len) => IOC(_IOC_READ, 'E', 0x20u + ev, len);
    public static uint EVIOCGABS(uint abs) => IOC(_IOC_READ, 'E', 0x40u + abs, (uint)Marshal.SizeOf<InputAbsInfo>());

    /// <summary>input_absinfo: axis value + range metadata (EVIOCGABS).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InputAbsInfo
    {
        public int value;
        public int minimum;
        public int maximum;
        public int fuzz;
        public int flat;
        public int resolution;
    }

    public static readonly uint UI_SET_EVBIT = IOW('U', 100, sizeof(int));
    public static readonly uint UI_SET_KEYBIT = IOW('U', 101, sizeof(int));
    public static readonly uint UI_SET_ABSBIT = IOW('U', 103, sizeof(int));
    public static readonly uint UI_SET_LEDBIT = IOW('U', 105, sizeof(int));
    public static readonly uint UI_SET_PROPBIT = IOW('U', 110, sizeof(int));
    public static readonly uint UI_DEV_CREATE = IO('U', 1);
    public static readonly uint UI_DEV_DESTROY = IO('U', 2);
    // struct uinput_setup is 92 bytes, struct uinput_abs_setup 28 bytes.
    public static readonly uint UI_DEV_SETUP = IOW('U', 3, 92);
    public static readonly uint UI_ABS_SETUP = IOW('U', 4, 28);
    public static uint EVIOCGPROP(uint len) => IOC(_IOC_READ, 'E', 0x09, len);
    public static uint HIDIOCSFEATURE(uint len) => IOC(_IOC_READ | _IOC_WRITE, 'H', 0x06, len);
    public static uint HIDIOCGFEATURE(uint len) => IOC(_IOC_READ | _IOC_WRITE, 'H', 0x07, len);

    [StructLayout(LayoutKind.Sequential)]
    public struct InputId
    {
        public ushort bustype;
        public ushort vendor;
        public ushort product;
        public ushort version;
    }

    /// <summary>
    /// Linux kernel input_event for x86_64 with default 64-bit time_t.
    /// Layout: long sec, long usec, ushort type, ushort code, int value.
    /// Total 24 bytes on x86_64.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InputEvent
    {
        public long tv_sec;
        public long tv_usec;
        public ushort type;
        public ushort code;
        public int value;
    }

    public const int InputEventSize = 24;

    /// <summary>
    /// uinput_user_dev for the legacy write()-based device creation path.
    /// 80-char name + input_id + 4 ints + 4 * 64 absinfo arrays.
    /// We don't use ABS, but the kernel still expects the full struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UinputUserDev
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string name;
        public InputId id;
        public int ff_effects_max;
        // ABS arrays: 64 entries each for absmax, absmin, absfuzz, absflat.
        // Defined inline as fixed-size arrays via padding bytes (reflection-free).
        // Kernel reads 4*64*int = 1024 bytes. We fill with zeros.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public int[] absmax;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public int[] absmin;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public int[] absfuzz;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public int[] absflat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Pollfd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport(Libc, SetLastError = true)]
    public static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern long read(int fd, IntPtr buf, ulong count);

    [DllImport(Libc, SetLastError = true)]
    public static extern long write(int fd, IntPtr buf, ulong count);

    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    public static extern int ioctl_int(int fd, ulong request, int arg);

    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    public static extern int ioctl_buf(int fd, ulong request, IntPtr buf);

    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    public static extern int ioctl_uint(int fd, ulong request, uint arg);

    [DllImport(Libc, SetLastError = true)]
    public static extern int poll([In, Out] Pollfd[] fds, ulong nfds, int timeout);

    [DllImport(Libc, SetLastError = true)]
    public static extern int pipe([Out] int[] pipefd);

    [DllImport(Libc, SetLastError = true)]
    public static extern int access([MarshalAs(UnmanagedType.LPStr)] string path, int mode);

    public const int W_OK = 2;
    public const int R_OK = 4;
}
