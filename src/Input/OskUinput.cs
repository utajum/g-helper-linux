using System.Runtime.InteropServices;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.Input;

/// <summary>
/// Virtual keyboard device for the on-screen keyboard. Emits key events
/// through /dev/uinput, so it works on X11 and every Wayland compositor
/// alike (the same injection path Steam Input and keyd use), with the
/// user's active XKB layout applied by the compositor.
///
/// Same device pattern as FnLockRemapper/NumberPad: legacy uinput_user_dev
/// creation, EV_KEY + EV_SYN + EV_REP, keycodes 1..255 announced.
/// </summary>
internal sealed class OskUinput : IDisposable
{
    private const string DeviceName = "G-Helper OSK";

    private int _fd = -1;
    private readonly object _lock = new();

    public bool Started => _fd >= 0;

    /// <summary>Localized reason /dev/uinput cannot be used, or null when
    /// it is accessible. Mirrors FnLockRemapper.CheckCapability wording.</summary>
    public static string? ProbeError()
    {
        if (EvdevInterop.access("/dev/uinput", EvdevInterop.W_OK) == 0)
            return null;
        int err = Marshal.GetLastWin32Error();
        Logger.WriteLine($"OskUinput: /dev/uinput access failed errno={err}");
        return err switch
        {
            2 /* ENOENT */ => Labels.Get("fnlock_unavail_no_uinput"),
            13 /* EACCES */ => Labels.Get("fnlock_unavail_denied"),
            _ => Labels.Get("fnlock_unavail_errno"),
        };
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_fd >= 0)
                return true;

            int fd = EvdevInterop.open("/dev/uinput",
                EvdevInterop.O_WRONLY | EvdevInterop.O_NONBLOCK | EvdevInterop.O_CLOEXEC);
            if (fd < 0)
            {
                Logger.WriteLine($"OskUinput: open /dev/uinput failed (errno={Marshal.GetLastWin32Error()})");
                return false;
            }

            if (EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_KEY) < 0
                || EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_SYN) < 0
                || EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_EVBIT, EvdevInterop.EV_REP) < 0)
            {
                Logger.WriteLine("OskUinput: UI_SET_EVBIT failed");
                EvdevInterop.close(fd);
                return false;
            }

            // Every code the layout can emit lives below 256.
            for (uint k = 1; k < 256; k++)
                EvdevInterop.ioctl_int(fd, EvdevInterop.UI_SET_KEYBIT, (int)k);

            var udev = new EvdevInterop.UinputUserDev
            {
                name = DeviceName,
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
                    Logger.WriteLine($"OskUinput: write(uinput_user_dev) returned {n} (errno={Marshal.GetLastWin32Error()})");
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
                Logger.WriteLine("OskUinput: UI_DEV_CREATE failed");
                EvdevInterop.close(fd);
                return false;
            }

            _fd = fd;
            Logger.WriteLine("OskUinput: virtual keyboard created");
            return true;
        }
    }

    /// <summary>Tap a key with the given modifiers held around it.</summary>
    public void Tap(ushort code, IReadOnlyList<ushort> mods)
    {
        lock (_lock)
        {
            if (_fd < 0)
                return;
            foreach (var m in mods)
                Emit(m, 1);
            Emit(code, 1);
            Emit(code, 0);
            for (int i = mods.Count - 1; i >= 0; i--)
                Emit(mods[i], 0);
        }
    }

    /// <summary>Single press or release, for keys used as real holds.</summary>
    public void Key(ushort code, bool down)
    {
        lock (_lock)
        {
            if (_fd < 0)
                return;
            Emit(code, down ? 1 : 0);
        }
    }

    private void Emit(ushort code, int value)
    {
        WriteEvent(EvdevInterop.EV_KEY, code, value);
        WriteEvent(EvdevInterop.EV_SYN, EvdevInterop.SYN_REPORT, 0);
    }

    private void WriteEvent(ushort type, ushort code, int value)
    {
        var ev = new EvdevInterop.InputEvent { type = type, code = code, value = value };
        IntPtr buf = Marshal.AllocHGlobal(EvdevInterop.InputEventSize);
        try
        {
            Marshal.StructureToPtr(ev, buf, false);
            EvdevInterop.write(_fd, buf, (ulong)EvdevInterop.InputEventSize);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_fd < 0)
                return;
            EvdevInterop.ioctl_uint(_fd, EvdevInterop.UI_DEV_DESTROY, 0);
            EvdevInterop.close(_fd);
            _fd = -1;
        }
    }
}
