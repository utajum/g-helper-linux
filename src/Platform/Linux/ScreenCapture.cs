using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GHelper.Linux.Helpers;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Screen colors for the Ambient AURA mode: a 4x2 grid (RGB, row-major) of the
/// average colors below the top third of the screen, same area as upstream.
/// X11 reads the root window. Wayland streams the screen through the
/// xdg-desktop-portal ScreenCast and PipeWire; the restore token keeps the
/// share dialog to the first start.
/// </summary>
internal static class ScreenCapture
{
    public const int GridSize = 4 * 2 * 3;

    private const int RowsPerCell = 8;
    private const int StepX = 4;
    private const string TokenKey = "ambient_restore_token";

    private static readonly object _lock = new();
    private static readonly object _gridLock = new();
    private static readonly byte[] _grid = new byte[GridSize];
    private static bool _hasGrid;
    private static bool _running;
    private static int _gen;
    private static long _retryAt;

    // X11
    private static nint _dpy, _root;

    // Wayland
    private static DBusConnection? _bus;
    private static bool _pwInit;
    private static nint _loop, _context, _core, _stream, _mem;
    private static int _width, _height, _ro, _go, _bo;
    private static long _lastFrame;
    private static volatile int _streamEnd; // 1 = error, retry; 2 = closed
    private static volatile bool _stopping;
    private static int _token;

    public static void Start()
    {
        lock (_lock)
        {
            if (_running)
                return;
            _running = true;
            int gen = ++_gen;
            if (Display.DisplayBackendFactory.IsWaylandSession())
            {
                Logger.WriteLine("Ambient: starting portal screencast");
                _ = Task.Run(() => StartPortalAsync(gen));
            }
            else
            {
                _dpy = XOpenDisplay(null);
                if (_dpy == 0)
                {
                    Logger.WriteLine("Ambient: XOpenDisplay failed");
                    _running = false;
                    return;
                }
                _root = XDefaultRootWindow(_dpy);
                Logger.WriteLine("Ambient: X11 capture");
            }
        }
    }

    public static void Stop()
    {
        lock (_lock)
        {
            _gen++;
            _retryAt = 0;
            if (!_running)
                return;
            _running = false;
            StopPipeWire();
            _bus?.Dispose(); // portal closes the session with the connection
            _bus = null;
            if (_dpy != 0)
            {
                XCloseDisplay(_dpy);
                _dpy = 0;
            }
            lock (_gridLock)
                _hasGrid = false;
            Logger.WriteLine("Ambient: capture stopped");
        }
    }

    /// <summary>Copy the latest grid to <paramref name="dst"/>. False until the first frame.</summary>
    public static bool TryGetGrid(byte[] dst)
    {
        // Under _lock so a tick racing Stop() can't restart the capture
        lock (_lock)
        {
            int end = _streamEnd;
            if (end != 0)
            {
                Stop();
                if (end == 1)
                    _retryAt = Environment.TickCount64 + 10000;
            }
            if (!_running && _retryAt != 0 && Environment.TickCount64 >= _retryAt)
            {
                _retryAt = 0;
                Start();
            }
            if (_dpy != 0)
                return SampleX11(dst);
        }
        lock (_gridLock)
        {
            if (!_hasGrid)
                return false;
            Array.Copy(_grid, dst, GridSize);
            return true;
        }
    }

    // Grid sampling

    private static int SampleRow(int h, int gridRow, int k)
    {
        int top = h / 3;
        int height = h - (int)Math.Round(h * (0.33 + 0.022)); // cut top third + panel
        return Math.Min(h - 1, top + (int)((gridRow + (k + 0.5) / RowsPerCell) * height / 2));
    }

    private static unsafe void AddRow(byte* row, int w, int gridRow, int ro, int go, int bo, Span<long> sum, Span<int> count)
    {
        for (int x = 0; x < w; x += StepX)
        {
            int cell = gridRow * 4 + x * 4 / w;
            byte* p = row + x * 4;
            sum[cell * 3] += p[ro];
            sum[cell * 3 + 1] += p[go];
            sum[cell * 3 + 2] += p[bo];
            count[cell]++;
        }
    }

    private static bool Finish(Span<long> sum, Span<int> count, byte[] dst)
    {
        for (int cell = 0; cell < 8; cell++)
        {
            if (count[cell] == 0)
                return false;
            for (int c = 0; c < 3; c++)
                dst[cell * 3 + c] = (byte)(sum[cell * 3 + c] / count[cell]);
        }
        return true;
    }

    // X11

    private static unsafe bool SampleX11(byte[] dst)
    {
        byte* attrs = stackalloc byte[136]; // XWindowAttributes
        if (XGetWindowAttributes(_dpy, _root, attrs) == 0)
            return false;
        int w = *(int*)(attrs + 8), h = *(int*)(attrs + 12);
        if (w <= 0 || h <= 0)
            return false;

        Span<long> sum = stackalloc long[GridSize];
        Span<int> count = stackalloc int[8];
        sum.Clear();
        count.Clear();
        for (int r = 0; r < 2; r++)
            for (int k = 0; k < RowsPerCell; k++)
            {
                nint img = XGetImage(_dpy, _root, 0, SampleRow(h, r, k), (uint)w, 1, nuint.MaxValue, 2 /* ZPixmap */);
                if (img == 0)
                    return false;
                if (*(int*)(img + 48) == 32) // bits_per_pixel
                {
                    bool rgb = *(nuint*)(img + 56) == 0xFF; // red_mask
                    AddRow(*(byte**)(img + 16), w, r, rgb ? 0 : 2, 1, rgb ? 2 : 0, sum, count);
                }
                XDestroyImage(img);
            }
        return Finish(sum, count, dst);
    }

    // Wayland: portal

    private static async Task StartPortalAsync(int gen)
    {
        try
        {
            var bus = new DBusConnection(DBusAddress.Session!);
            lock (_lock)
            {
                if (gen != _gen)
                {
                    bus.Dispose();
                    return;
                }
                _bus = bus; // Stop() disposes it, which also ends a pending share dialog
            }
            await bus.ConnectAsync();
            string sender = bus.UniqueName!.TrimStart(':').Replace('.', '_');

            var created = await RequestAsync(bus, sender, "CreateSession", null, new()
            {
                ["session_handle_token"] = "ghelper" + gen,
            });
            var handle = created["session_handle"];
            string session = handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString();

            var select = new Dictionary<string, VariantValue>
            {
                ["types"] = 1u,          // monitor
                ["multiple"] = false,
                ["persist_mode"] = 2u,   // until revoked
            };
            string? token = AppConfig.GetString(TokenKey);
            if (!string.IsNullOrEmpty(token))
                select["restore_token"] = token;
            await RequestAsync(bus, sender, "SelectSources", session, select);

            var started = await RequestAsync(bus, sender, "Start", session, new());
            uint node = started["streams"].GetItem(0).GetItem(0).GetUInt32();
            if (started.TryGetValue("restore_token", out var newToken))
                AppConfig.Set(TokenKey, newToken.GetString());

            int fd = -1;
            try
            {
                using var handleFd = await bus.CallMethodAsync(Call(bus, "OpenPipeWireRemote", session, new()),
                    static (Message m, object? _) => m.GetBodyReader().ReadHandle<SafeFileHandle>(), null);
                fd = (int)handleFd.DangerousGetHandle();
                handleFd.SetHandleAsInvalid(); // PipeWire owns it now
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Ambient: OpenPipeWireRemote failed, using default PipeWire: {ex.Message}");
            }

            lock (_lock)
            {
                if (gen != _gen)
                {
                    if (fd >= 0)
                        close(fd);
                    return;
                }
                Logger.WriteLine($"Ambient: portal stream node {node}");
                StartPipeWire(fd, node);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (gen != _gen)
                    return; // stopped meanwhile, Stop() cleaned up
                Logger.WriteLine(ex is OperationCanceledException
                    ? "Ambient: screen share was cancelled"
                    : $"Ambient: portal screencast failed: {ex.Message}");
                StopPipeWire();
                _bus?.Dispose();
                _bus = null;
                _running = false;
            }
        }
    }

    private static async Task<Dictionary<string, VariantValue>> RequestAsync(DBusConnection bus, string sender,
        string member, string? session, Dictionary<string, VariantValue> options)
    {
        string token = "ghelper_req" + Interlocked.Increment(ref _token);
        options["handle_token"] = token;
        var tcs = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await bus.WatchSignalAsync(null, $"/org/freedesktop/portal/desktop/request/{sender}/{token}",
            "org.freedesktop.portal.Request", "Response",
            static (Message m, object? _) =>
            {
                var r = m.GetBodyReader();
                return (r.ReadUInt32(), r.ReadDictionaryOfStringToVariantValue());
            },
            n =>
            {
                if (n.HasValue)
                    tcs.TrySetResult(n.Value);
                else
                    tcs.TrySetException(n.IsCompletion ? new ObjectDisposedException(nameof(DBusConnection)) : n.Exception);
            },
            ObserverFlags.EmitOnConnectionClosed, emitOnCapturedContext: false);

        await bus.CallMethodAsync(Call(bus, member, session, options));
        var (code, results) = await tcs.Task;
        if (code == 1)
            throw new OperationCanceledException(member);
        if (code != 0)
            throw new InvalidOperationException($"{member} response {code}");
        return results;
    }

    private static MessageBuffer Call(DBusConnection bus, string member, string? session, Dictionary<string, VariantValue> options)
    {
        var w = bus.GetMessageWriter();
        try
        {
            string sig = (session != null ? "o" : "") + (member == "Start" ? "s" : "") + "a{sv}";
            w.WriteMethodCallHeader("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                "org.freedesktop.portal.ScreenCast", member, sig);
            if (session != null)
                w.WriteObjectPath(session);
            if (member == "Start")
                w.WriteString(""); // parent window
            w.WriteDictionary(options);
            return w.CreateMessage();
        }
        finally
        {
            w.Dispose();
        }
    }

    // Wayland: PipeWire stream

    private static unsafe void StartPipeWire(int fd, uint node)
    {
        if (!_pwInit)
        {
            pw_init(0, 0);
            _pwInit = true;
        }
        _streamEnd = 0;
        _width = _height = 0;
        _loop = pw_thread_loop_new("ghelper-ambient", 0);
        _context = pw_context_new(pw_thread_loop_get_loop(_loop), 0, 0);
        if (_context == 0 || pw_thread_loop_start(_loop) < 0)
            throw new InvalidOperationException("PipeWire loop start failed");

        pw_thread_loop_lock(_loop);
        try
        {
            _core = fd >= 0 ? pw_context_connect_fd(_context, fd, 0, 0) : pw_context_connect(_context, 0, 0);
            if (_core == 0)
                throw new InvalidOperationException("PipeWire connect failed");
            _stream = pw_stream_new(_core, "ghelper-ambient",
                pw_properties_new_string("media.type=Video media.category=Capture media.role=Screen"));
            if (_stream == 0)
                throw new InvalidOperationException("pw_stream_new failed");

            // spa_hook (48) + pw_stream_events (96) + params array (8) + EnumFormat pod
            byte[] pod = EnumFormat();
            _mem = (nint)NativeMemory.AllocZeroed((nuint)(152 + pod.Length));
            nint hook = _mem, events = _mem + 48, parms = _mem + 144, podPtr = _mem + 152;
            *(uint*)events = 2; // PW_VERSION_STREAM_EVENTS
            *(nint*)(events + 16) = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, nint, void>)&OnStateChanged;
            *(nint*)(events + 40) = (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, void>)&OnParamChanged;
            *(nint*)(events + 64) = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnProcess;
            Marshal.Copy(pod, 0, podPtr, pod.Length);
            *(nint*)parms = podPtr;

            pw_stream_add_listener(_stream, hook, events, 0);
            int rc = pw_stream_connect(_stream, 0 /* input */, node, 1 | 4 /* AUTOCONNECT | MAP_BUFFERS */, parms, 1);
            if (rc < 0)
                throw new InvalidOperationException($"pw_stream_connect failed: {rc}");
        }
        finally
        {
            pw_thread_loop_unlock(_loop);
        }
    }

    private static unsafe void StopPipeWire()
    {
        if (_loop == 0)
            return;
        _stopping = true;
        pw_thread_loop_lock(_loop);
        if (_stream != 0)
        {
            pw_stream_disconnect(_stream);
            pw_stream_destroy(_stream);
            _stream = 0;
        }
        if (_core != 0)
        {
            pw_core_disconnect(_core);
            _core = 0;
        }
        pw_thread_loop_unlock(_loop);
        pw_thread_loop_stop(_loop);
        if (_context != 0)
        {
            pw_context_destroy(_context);
            _context = 0;
        }
        pw_thread_loop_destroy(_loop);
        _loop = 0;
        if (_mem != 0)
        {
            NativeMemory.Free((void*)_mem);
            _mem = 0;
        }
        _streamEnd = 0;
        _stopping = false;
    }

    /// <summary>EnumFormat pod: raw video, any 32-bit RGB order, any size, max 10 fps.</summary>
    private static byte[] EnumFormat()
    {
        var w = new List<uint> { 0, 15 /* Object */, 0x40003 /* Format */, 3 /* EnumFormat */ };
        void Id(uint key, uint v) => w.AddRange(new uint[] { key, 0, 4, 3, v, 0 });
        void Range(uint key, uint type, uint d0, uint d1, uint min0, uint min1, uint max0, uint max1) =>
            w.AddRange(new uint[] { key, 0, 40, 19 /* Choice */, 1 /* Range */, 0, 8, type, d0, d1, min0, min1, max0, max1 });

        Id(1, 2); // mediaType video
        Id(2, 1); // mediaSubtype raw
        uint[] formats = { 8, 8, 7, 12, 11, 9, 10, 13, 14 }; // BGRx default, then RGBx BGRA RGBA xRGB xBGR ARGB ABGR
        w.AddRange(new uint[] { 0x20001, 0, (uint)(16 + 4 * formats.Length), 19, 3 /* Enum */, 0, 4, 3 });
        w.AddRange(formats);
        if (formats.Length % 2 == 1)
            w.Add(0);
        Range(0x20003, 10 /* Rectangle */, 1920, 1080, 1, 1, 16384, 16384);
        Range(0x20004, 11 /* Fraction */, 0, 1, 0, 1, 1000, 1);
        Range(0x20005, 11, 10, 1, 1, 1, 10, 1); // maxFramerate
        w[0] = (uint)((w.Count - 2) * 4);
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(w)).ToArray();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnStateChanged(nint data, int old, int state, nint error)
    {
        if (_stopping)
            return;
        try
        {
            if (state == -1)
            {
                Logger.WriteLine($"Ambient: stream error: {Marshal.PtrToStringUTF8(error)}");
                _streamEnd = 1;
            }
            else if (state == 0 && old != 0)
            {
                Logger.WriteLine("Ambient: stream closed");
                _streamEnd = 2;
            }
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnParamChanged(nint data, uint id, nint param)
    {
        if (id != 4 /* Format */ || param == 0 || *(uint*)(param + 4) != 15)
            return;
        try
        {
            uint format = 0;
            int w = 0, h = 0;
            byte* p = (byte*)param + 16;
            byte* end = (byte*)param + 8 + *(uint*)param;
            while (p + 16 <= end)
            {
                uint key = *(uint*)p, size = *(uint*)(p + 8), type = *(uint*)(p + 12);
                byte* v = p + 16;
                if (type == 19) // Choice: first value
                {
                    type = *(uint*)(v + 12);
                    v += 16;
                }
                if (key == 0x20001 && type == 3)
                    format = *(uint*)v;
                else if (key == 0x20003 && type == 10)
                {
                    w = *(int*)v;
                    h = *(int*)(v + 4);
                }
                p += 16 + ((size + 7) & ~7u);
            }

            (int r, int g, int b) = format switch
            {
                7 or 11 => (0, 1, 2),  // RGBx RGBA
                8 or 12 => (2, 1, 0),  // BGRx BGRA
                9 or 13 => (1, 2, 3),  // xRGB ARGB
                10 or 14 => (3, 2, 1), // xBGR ABGR
                _ => (-1, -1, -1),
            };
            Logger.WriteLine($"Ambient: stream {w}x{h} format {format}");
            if (r < 0)
                return;
            _ro = r;
            _go = g;
            _bo = b;
            _width = w;
            _height = h;
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnProcess(nint data)
    {
        try
        {
            nint buf = 0, next;
            while ((next = pw_stream_dequeue_buffer(_stream)) != 0)
            {
                if (buf != 0)
                    pw_stream_queue_buffer(_stream, buf);
                buf = next;
            }
            if (buf == 0)
                return;
            long now = Environment.TickCount64;
            if (now - _lastFrame >= 80 && ReadFrame(*(nint*)buf))
                _lastFrame = now;
            pw_stream_queue_buffer(_stream, buf);
        }
        catch
        {
        }
    }

    private static unsafe bool ReadFrame(nint spaBuffer)
    {
        if (*(uint*)(spaBuffer + 4) == 0) // n_datas
            return false;
        nint d = *(nint*)(spaBuffer + 16);
        byte* data = *(byte**)(d + 24);
        uint maxsize = *(uint*)(d + 20);
        nint chunk = *(nint*)(d + 32);
        int w = _width, h = _height;
        if (data == null || chunk == 0 || maxsize == 0 || w <= 0 || h <= 0)
            return false;
        uint offset = *(uint*)chunk % maxsize, size = *(uint*)(chunk + 4);
        int stride = *(int*)(chunk + 8), flags = *(int*)(chunk + 12);
        if (size == 0 || (flags & 1) != 0) // empty or corrupted
            return false;
        if (stride <= 0)
            stride = w * 4;
        if (offset + (long)stride * (h - 1) + w * 4L > maxsize)
            return false;

        Span<long> sum = stackalloc long[GridSize];
        Span<int> count = stackalloc int[8];
        sum.Clear();
        count.Clear();
        for (int r = 0; r < 2; r++)
            for (int k = 0; k < RowsPerCell; k++)
                AddRow(data + offset + (long)SampleRow(h, r, k) * stride, w, r, _ro, _go, _bo, sum, count);

        lock (_gridLock)
        {
            _hasGrid = Finish(sum, count, _grid);
            return _hasGrid;
        }
    }

    // Native

    [DllImport("libc")]
    private static extern int close(int fd);

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern nint XDefaultRootWindow(nint display);

    [DllImport("libX11.so.6")]
    private static unsafe extern int XGetWindowAttributes(nint display, nint window, byte* attributes);

    [DllImport("libX11.so.6")]
    private static extern nint XGetImage(nint display, nint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);

    [DllImport("libX11.so.6")]
    private static extern int XDestroyImage(nint image);

    private const string PipeWire = "libpipewire-0.3.so.0";

    [DllImport(PipeWire)]
    private static extern void pw_init(nint argc, nint argv);

    [DllImport(PipeWire)]
    private static extern nint pw_thread_loop_new(string name, nint props);

    [DllImport(PipeWire)]
    private static extern nint pw_thread_loop_get_loop(nint loop);

    [DllImport(PipeWire)]
    private static extern int pw_thread_loop_start(nint loop);

    [DllImport(PipeWire)]
    private static extern void pw_thread_loop_stop(nint loop);

    [DllImport(PipeWire)]
    private static extern void pw_thread_loop_lock(nint loop);

    [DllImport(PipeWire)]
    private static extern void pw_thread_loop_unlock(nint loop);

    [DllImport(PipeWire)]
    private static extern void pw_thread_loop_destroy(nint loop);

    [DllImport(PipeWire)]
    private static extern nint pw_context_new(nint loop, nint props, nuint userDataSize);

    [DllImport(PipeWire)]
    private static extern nint pw_context_connect(nint context, nint props, nuint userDataSize);

    [DllImport(PipeWire)]
    private static extern nint pw_context_connect_fd(nint context, int fd, nint props, nuint userDataSize);

    [DllImport(PipeWire)]
    private static extern void pw_context_destroy(nint context);

    [DllImport(PipeWire)]
    private static extern int pw_core_disconnect(nint core);

    [DllImport(PipeWire)]
    private static extern nint pw_properties_new_string(string args);

    [DllImport(PipeWire)]
    private static extern nint pw_stream_new(nint core, string name, nint props);

    [DllImport(PipeWire)]
    private static extern void pw_stream_add_listener(nint stream, nint listener, nint events, nint data);

    [DllImport(PipeWire)]
    private static extern int pw_stream_connect(nint stream, int direction, uint targetId, int flags, nint parameters, uint count);

    [DllImport(PipeWire)]
    private static extern int pw_stream_disconnect(nint stream);

    [DllImport(PipeWire)]
    private static extern void pw_stream_destroy(nint stream);

    [DllImport(PipeWire)]
    private static extern nint pw_stream_dequeue_buffer(nint stream);

    [DllImport(PipeWire)]
    private static extern int pw_stream_queue_buffer(nint stream, nint buffer);
}
