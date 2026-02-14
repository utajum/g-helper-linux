using System.Runtime.InteropServices;
using System.Threading;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Software FnLock implementation using evdev grab and uinput injection.
/// For laptops without hardware FnLock support (like ROG Strix G614).
/// 
/// How it works:
/// 1. Grabs the keyboard input device exclusively (EVIOCGRAB)
/// 2. Reads raw key events in a background thread
/// 3. Tracks Fn key state separately
/// 4. When FnLock is ON and F1-F12 pressed without Fn: translates to media keys
/// 5. Injects translated keys via uinput
/// 6. Passes through all other keys unchanged
/// </summary>
public class SoftwareFnLock : IDisposable
{
    // Input event types
    private const int EV_KEY = 1;
    private const int EV_SYN = 0;
    
    // Key states
    private const int KEY_PRESSED = 1;
    private const int KEY_RELEASED = 0;
    private const int KEY_REPEAT = 2;
    
    // Fn key code (ASUS specific - may vary by model)
    // Common values: 464 (KEY_FN), 185, etc.
    private const int KEY_FN = 464;
    
    // F1-F12 key codes (Linux input event codes)
    private const int KEY_F1 = 59;
    private const int KEY_F11 = 69;  // 59 + 10
    private const int KEY_F12 = 68;
    
    // Media key codes for translation
    private static readonly Dictionary<int, int> FnLockMap = new()
    {
        [KEY_F1] = 113,   // MUTE
        [KEY_F1 + 1] = 114,  // F2 -> VOLUMEDOWN
        [KEY_F1 + 2] = 115,  // F3 -> VOLUMEUP
        [KEY_F1 + 3] = 190,  // F4 -> MUTE (mic) - will handle specially
        [KEY_F1 + 4] = 232,  // F5 -> SYSRQ (Performance mode - handle specially)
        [KEY_F1 + 5] = 210,  // F6 -> PRINT (PrintScreen)
        [KEY_F1 + 6] = 224,  // F7 -> BRIGHTNESSDOWN
        [KEY_F1 + 7] = 225,  // F8 -> BRIGHTNESSUP
        [KEY_F1 + 8] = 148,  // F9 -> PROG1 (Win+P simulation)
        [KEY_F1 + 9] = 191,  // F10 -> F21 (Touchpad toggle)
        [KEY_F1 + 10] = 142, // F11 -> SLEEP
    };

    private int _inputFd = -1;
    private int _uinputFd = -1;
    private Thread? _eventThread;
    private volatile bool _running;
    private volatile bool _fnPressed;
    private string? _devicePath;

    public bool IsEnabled { get; set; }

    public SoftwareFnLock()
    {
        // Don't auto-start - wait for Init()
    }

    public bool Init()
    {
        try
        {
            // Find the keyboard device
            _devicePath = FindKeyboardDevice();
            if (_devicePath == null)
            {
                Helpers.Logger.WriteLine("SoftwareFnLock: Could not find keyboard device");
                return false;
            }

            // Open input device
            _inputFd = open(_devicePath, 0x0000 | 0x0004); // O_RDONLY | O_NONBLOCK
            if (_inputFd < 0)
            {
                Helpers.Logger.WriteLine($"SoftwareFnLock: Failed to open {_devicePath}");
                return false;
            }

            // Grab the device exclusively (EVIOCGRAB)
            if (ioctl(_inputFd, 1074021776, 1) < 0) // EVIOCGRAB
            {
                Helpers.Logger.WriteLine("SoftwareFnLock: Failed to grab device");
                close(_inputFd);
                _inputFd = -1;
                return false;
            }

            // Create uinput device for output
            _uinputFd = open("/dev/uinput", 0x0001 | 0x0004); // O_WRONLY | O_NONBLOCK
            if (_uinputFd < 0)
            {
                _uinputFd = open("/dev/input/uinput", 0x0001 | 0x0004);
            }
            
            if (_uinputFd < 0)
            {
                Helpers.Logger.WriteLine("SoftwareFnLock: Failed to open uinput");
                ioctl(_inputFd, 1074021776, 0); // Release grab
                close(_inputFd);
                _inputFd = -1;
                return false;
            }

            // Setup uinput device
            SetupUinputDevice();

            _running = true;
            _eventThread = new Thread(EventLoop)
            {
                Name = "SoftwareFnLock",
                IsBackground = true
            };
            _eventThread.Start();

            Helpers.Logger.WriteLine($"SoftwareFnLock: Initialized on {_devicePath}");
            return true;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("SoftwareFnLock init failed", ex);
            Cleanup();
            return false;
        }
    }

    private string? FindKeyboardDevice()
    {
        try
        {
            // Look for the main keyboard - try common patterns
            // Priority: ASUS keyboard, then AT keyboard, then first USB keyboard
            string[] candidates = {
                "/dev/input/by-path/platform-asus-keyboard-event",
                "/dev/input/by-path/platform-asus-nb-wmi-event",
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }

            // Search in /dev/input/by-path for keyboard
            if (Directory.Exists("/dev/input/by-path"))
            {
                foreach (var link in Directory.GetFiles("/dev/input/by-path", "*keyboard*"))
                {
                    var realPath = GetRealPath(link);
                    if (realPath != null)
                        return realPath;
                }

                // Try event-kbd links
                foreach (var link in Directory.GetFiles("/dev/input/by-path", "*event-kbd"))
                {
                    var realPath = GetRealPath(link);
                    if (realPath != null)
                        return realPath;
                }
            }

            // Fallback: look at all event devices and find one with keys
            if (Directory.Exists("/dev/input"))
            {
                foreach (var eventDev in Directory.GetFiles("/dev/input", "event*"))
                {
                    if (IsKeyboardDevice(eventDev))
                        return eventDev;
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("FindKeyboardDevice failed", ex);
        }

        return null;
    }

    private string? GetRealPath(string link)
    {
        try
        {
            var target = File.ReadAllText(link); // For symlinks
            if (target.StartsWith("/"))
                return target;
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, target));
        }
        catch
        {
            return link;
        }
    }

    private bool IsKeyboardDevice(string devicePath)
    {
        var fd = open(devicePath, 0x0000);
        if (fd < 0) return false;

        try
        {
            // Check if device has KEY capability
            long[] bits = new long[4];
            int EVIOCGBIT_0 = unchecked((int)2148025601U); // _IOC(_IOC_READ, 'E', 0x20 + 0, sizeof(bits))
            if (ioctl(fd, EVIOCGBIT_0, bits) >= 0)
            {
                // Check EV_KEY bit
                if ((bits[0] & (1 << EV_KEY)) != 0)
                {
                    // Check for common keys (A, SPACE, ENTER)
                    long[] keyBits = new long[96];
                    int EVIOCGBIT_KEY = unchecked((int)2150143762U); // _IOC(_IOC_READ, 'E', 0x20 + EV_KEY, sizeof(keyBits))
                    if (ioctl(fd, EVIOCGBIT_KEY, keyBits) >= 0)
                    {
                        // Check if KEY_A (30), KEY_SPACE (57), KEY_ENTER (28) exist
                        return HasKey(keyBits, 30) && HasKey(keyBits, 57);
                    }
                }
            }
        }
        finally
        {
            close(fd);
        }
        return false;
    }

    private bool HasKey(long[] bits, int keyCode)
    {
        int index = keyCode / 64;
        int bit = keyCode % 64;
        if (index < bits.Length)
            return (bits[index] & (1L << bit)) != 0;
        return false;
    }

    private void SetupUinputDevice()
    {
        // Set device name
        var name = "G-Helper FnLock Virtual Keyboard";
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        Array.Resize(ref nameBytes, 80);
        ioctl(_uinputFd, 1074029598, nameBytes); // UI_SET_PHYS

        // Enable KEY events
        ioctl(_uinputFd, 1074025827, 1); // UI_SET_EVBIT(EV_KEY)

        // Enable all keys we might need
        for (int i = 1; i < 256; i++)
        {
            ioctl(_uinputFd, 1074025828, i); // UI_SET_KEYBIT
        }

        // Create device
        ioctl(_uinputFd, 1074025835, 0); // UI_DEV_CREATE
    }

    private void EventLoop()
    {
        byte[] buffer = new byte[24]; // sizeof(input_event)

        while (_running)
        {
            try
            {
                int n = read(_inputFd, buffer, 24);
                if (n == 24)
                {
                    ProcessEvent(buffer);
                }
                else if (n < 0)
                {
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                if (_running)
                    Helpers.Logger.WriteLine("SoftwareFnLock event error", ex);
            }
        }
    }

    private void ProcessEvent(byte[] buffer)
    {
        // input_event structure:
        // struct timeval { long tv_sec; long tv_usec; }  // 16 bytes
        // ushort type;  // 2 bytes (offset 16)
        // ushort code;  // 2 bytes (offset 18)
        // int value;    // 4 bytes (offset 20)

        ushort type = BitConverter.ToUInt16(buffer, 16);
        ushort code = BitConverter.ToUInt16(buffer, 18);
        int value = BitConverter.ToInt32(buffer, 20);

        if (type == EV_KEY)
        {
            // Track Fn key state
            if (code == KEY_FN)
            {
                _fnPressed = value == KEY_PRESSED;
                // Don't pass Fn key through - it's a meta key
                return;
            }

            // Check if this is a key we should translate
            int? translatedCode = null;
            
            if (IsEnabled && !_fnPressed && code >= KEY_F1 && code <= KEY_F11)
            {
                // FnLock is ON, Fn is NOT pressed, and it's F1-F11
                translatedCode = TranslateKey(code);
            }

            if (translatedCode.HasValue)
            {
                // Inject translated key
                SendKeyEvent(translatedCode.Value, value);
                
                // Also send sync
                SendSynEvent();
            }
            else
            {
                // Pass through unchanged
                SendKeyEvent(code, value);
                SendSynEvent();
            }
        }
        else if (type == EV_SYN)
        {
            // Pass through sync events
            SendSynEvent();
        }
    }

    private int? TranslateKey(int code)
    {
        if (FnLockMap.TryGetValue(code, out var translated))
            return translated;
        return null;
    }

    private void SendKeyEvent(int code, int value)
    {
        var ev = new InputEvent
        {
            Time = new Timeval { Sec = 0, Usec = 0 },
            Type = EV_KEY,
            Code = (ushort)code,
            Value = value
        };
        
        WriteEvent(ev);
    }

    private void SendSynEvent()
    {
        var ev = new InputEvent
        {
            Time = new Timeval { Sec = 0, Usec = 0 },
            Type = EV_SYN,
            Code = 0,
            Value = 0
        };
        
        WriteEvent(ev);
    }

    private void WriteEvent(InputEvent ev)
    {
        if (_uinputFd < 0) return;
        
        var bytes = StructToBytes(ev);
        write(_uinputFd, bytes, bytes.Length);
    }

    private byte[] StructToBytes<T>(T structure) where T : struct
    {
        int size = Marshal.SizeOf(structure);
        byte[] bytes = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(structure, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return bytes;
    }

    public void Dispose()
    {
        _running = false;
        _eventThread?.Join(1000);
        Cleanup();
    }

    private void Cleanup()
    {
        if (_inputFd >= 0)
        {
            ioctl(_inputFd, 1074021776, 0); // Release grab
            close(_inputFd);
            _inputFd = -1;
        }

        if (_uinputFd >= 0)
        {
            ioctl(_uinputFd, 1074025836, 0); // UI_DEV_DESTROY
            close(_uinputFd);
            _uinputFd = -1;
        }
    }

    // P/Invoke declarations
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, int request, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, int request, [In, Out] long[] arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, int request, [In, Out] byte[] arg);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timeval
    {
        public long Sec;
        public long Usec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public Timeval Time;
        public ushort Type;
        public ushort Code;
        public int Value;
    }
}
