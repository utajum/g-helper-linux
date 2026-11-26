using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GHelper.Linux.Helpers;
using GHelper.Linux.Peripherals;
using GHelper.Linux.Peripherals.Mouse;

namespace GHelper.Linux.Input;

/// <summary>
/// ASUS mouse combos, as in Windows InputDispatcher. A mouse button bound to a combo
/// sends F13..F24; the app reads that key from the mouse's event node and presses the
/// combo keys through uinput. Unlike a Windows hotkey, the F13..F24 key also reaches the desktop.
/// </summary>
internal static class MouseCombos
{
    private const ushort KEY_F13 = 183;
    private const ushort KEY_F24 = 194;

    private static readonly object _lock = new();
    private static readonly Dictionary<string, CancellationTokenSource> _watched = new();
    private static OskUinput? _keyboard;

    /// <summary>Windows RegisterKeys: watch the mice that have a combo bound, stop the others.</summary>
    public static void RegisterKeys()
    {
        var pids = PeripheralsProvider.SnapshotMice().OfType<AsusMouse>()
            .Where(m => m.HasButtonBindings() && m.ButtonBindings.Any(AsusMouse.CombosByCode.ContainsKey))
            .Select(m => m.ProductID())
            .ToHashSet();
        var nodes = pids.Count == 0 ? [] : EventNodes(pids);

        lock (_lock)
        {
            foreach (var node in _watched.Keys.Except(nodes).ToList())
            {
                _watched[node].Cancel();
                _watched.Remove(node);
            }
            foreach (var node in nodes.Except(_watched.Keys).ToList())
            {
                var stop = new CancellationTokenSource();
                _watched[node] = stop;
                new Thread(() => Listen(node, stop.Token)) { IsBackground = true, Name = "MouseCombos" }.Start();
            }
            if (_watched.Count > 0)
            {
                _keyboard ??= new OskUinput();
                _keyboard.Start();
            }
            Logger.WriteLine($"MouseCombos: watching {_watched.Count} event nodes");
        }
    }

    // /dev/input/eventN of the given ASUS product IDs.
    private static HashSet<string> EventNodes(HashSet<ushort> pids)
    {
        var nodes = new HashSet<string>();
        foreach (var dir in Directory.GetDirectories("/sys/class/input", "event*"))
        {
            try
            {
                string vendor = File.ReadAllText(Path.Combine(dir, "device/id/vendor")).Trim();
                ushort product = Convert.ToUInt16(File.ReadAllText(Path.Combine(dir, "device/id/product")).Trim(), 16);
                if (vendor == "0b05" && pids.Contains(product))
                    nodes.Add("/dev/input/" + Path.GetFileName(dir));
            }
            catch { }
        }
        return nodes;
    }

    // Reads key presses until RegisterKeys stops the node or the device goes away.
    private static void Listen(string node, CancellationToken stop)
    {
        int fd = EvdevInterop.open(node, EvdevInterop.O_RDONLY | EvdevInterop.O_NONBLOCK | EvdevInterop.O_CLOEXEC);
        if (fd < 0)
        {
            Logger.WriteLine($"MouseCombos: open {node} failed (errno={Marshal.GetLastWin32Error()})");
            return;
        }
        IntPtr buf = Marshal.AllocHGlobal(EvdevInterop.InputEventSize);
        var poll = new EvdevInterop.Pollfd[] { new() { fd = fd, events = EvdevInterop.POLLIN } };
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (EvdevInterop.poll(poll, 1, 500) <= 0)
                    continue;
                if ((poll[0].revents & (EvdevInterop.POLLERR | EvdevInterop.POLLHUP | EvdevInterop.POLLNVAL)) != 0)
                    break;
                while (EvdevInterop.read(fd, buf, (ulong)EvdevInterop.InputEventSize) == EvdevInterop.InputEventSize)
                {
                    var ev = Marshal.PtrToStructure<EvdevInterop.InputEvent>(buf);
                    if (ev.type != EvdevInterop.EV_KEY || ev.value != 1 || ev.code < KEY_F13 || ev.code > KEY_F24)
                        continue;
                    ushort code = (ushort)(0x68 + (ev.code - KEY_F13));
                    if (AsusMouse.CombosByCode.TryGetValue(code, out var combo))
                        RunKeyCommand(combo.ResolveCommand());
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("MouseCombos: " + ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            EvdevInterop.close(fd);
            lock (_lock)
            {
                if (_watched.TryGetValue(node, out var current) && current.Token == stop)
                    _watched.Remove(node);
            }
        }
    }

    /// <summary>Windows RunKeyCommand without launch: press 1 to 4 keys in order, release them in reverse.</summary>
    internal static void RunKeyCommand(string command)
    {
        var keys = new List<ushort>();
        foreach (int vk in ParseHexValues(command))
        {
            if (!VirtualKeyToEvdev.TryGetValue(vk, out ushort key))
            {
                Logger.WriteLine($"MouseCombos: no Linux key for 0x{vk:X2}");
                return;
            }
            keys.Add(key);
        }
        if (keys.Count is < 1 or > 4)
            return;
        lock (_lock)
            _keyboard?.Tap(keys[^1], keys[..^1]);
    }

    public static int[] ParseHexValues(string input)
    {
        string pattern = @"\b(0x[0-9A-Fa-f]{1,2}|[0-9A-Fa-f]{1,2})\b";

        if (!Regex.IsMatch(input, $"^{pattern}(\\s+{pattern})*$"))
            return new int[0];

        MatchCollection matches = Regex.Matches(input, pattern);

        int[] hexValues = new int[matches.Count];

        for (int i = 0; i < matches.Count; i++)
        {
            string hexValueStr = matches[i].Value;
            int hexValue = int.Parse(hexValueStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? hexValueStr.Substring(2)
                : hexValueStr, System.Globalization.NumberStyles.HexNumber);

            hexValues[i] = hexValue;
        }

        return hexValues;
    }

    // Windows virtual-key code -> Linux KEY_* code (US layout positions).
    internal static readonly Dictionary<int, ushort> VirtualKeyToEvdev = new()
    {
        [0x08] = 14,
        [0x09] = 15,
        [0x0D] = 28,
        [0x10] = 42,
        [0x11] = 29,
        [0x12] = 56,
        [0x13] = 119,
        [0x14] = 58,
        [0x1B] = 1,
        [0x20] = 57,
        [0x21] = 104,
        [0x22] = 109,
        [0x23] = 107,
        [0x24] = 102,
        [0x25] = 105,
        [0x26] = 103,
        [0x27] = 106,
        [0x28] = 108,
        [0x2C] = 99,
        [0x2D] = 110,
        [0x2E] = 111,
        [0x30] = 11,
        [0x31] = 2,
        [0x32] = 3,
        [0x33] = 4,
        [0x34] = 5,
        [0x35] = 6,
        [0x36] = 7,
        [0x37] = 8,
        [0x38] = 9,
        [0x39] = 10,
        [0x41] = 30,
        [0x42] = 48,
        [0x43] = 46,
        [0x44] = 32,
        [0x45] = 18,
        [0x46] = 33,
        [0x47] = 34,
        [0x48] = 35,
        [0x49] = 23,
        [0x4A] = 36,
        [0x4B] = 37,
        [0x4C] = 38,
        [0x4D] = 50,
        [0x4E] = 49,
        [0x4F] = 24,
        [0x50] = 25,
        [0x51] = 16,
        [0x52] = 19,
        [0x53] = 31,
        [0x54] = 20,
        [0x55] = 22,
        [0x56] = 47,
        [0x57] = 17,
        [0x58] = 45,
        [0x59] = 21,
        [0x5A] = 44,
        [0x5B] = 125,
        [0x5C] = 126,
        [0x5D] = 127,
        [0x60] = 82,
        [0x61] = 79,
        [0x62] = 80,
        [0x63] = 81,
        [0x64] = 75,
        [0x65] = 76,
        [0x66] = 77,
        [0x67] = 71,
        [0x68] = 72,
        [0x69] = 73,
        [0x6A] = 55,
        [0x6B] = 78,
        [0x6D] = 74,
        [0x6E] = 83,
        [0x6F] = 98,
        [0x70] = 59,
        [0x71] = 60,
        [0x72] = 61,
        [0x73] = 62,
        [0x74] = 63,
        [0x75] = 64,
        [0x76] = 65,
        [0x77] = 66,
        [0x78] = 67,
        [0x79] = 68,
        [0x7A] = 87,
        [0x7B] = 88,
        [0x7C] = 183,
        [0x7D] = 184,
        [0x7E] = 185,
        [0x7F] = 186,
        [0x80] = 187,
        [0x81] = 188,
        [0x82] = 189,
        [0x83] = 190,
        [0x84] = 191,
        [0x85] = 192,
        [0x86] = 193,
        [0x87] = 194,
        [0x90] = 69,
        [0x91] = 70,
        [0xA0] = 42,
        [0xA1] = 54,
        [0xA2] = 29,
        [0xA3] = 97,
        [0xA4] = 56,
        [0xA5] = 100,
        [0xAD] = 113,
        [0xAE] = 114,
        [0xAF] = 115,
        [0xB0] = 163,
        [0xB1] = 165,
        [0xB2] = 166,
        [0xB3] = 164,
        [0xBA] = 39,
        [0xBB] = 13,
        [0xBC] = 51,
        [0xBD] = 12,
        [0xBE] = 52,
        [0xBF] = 53,
        [0xC0] = 41,
        [0xDB] = 26,
        [0xDC] = 43,
        [0xDD] = 27,
        [0xDE] = 40,
    };
}
