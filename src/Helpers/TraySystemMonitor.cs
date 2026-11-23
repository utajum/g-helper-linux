using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GHelper.Linux.I18n;
using SkiaSharp;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Tray-icon orchestrator: drives three surfaces from a single 3-second timer.
///
/// Surfaces (in priority order):
/// <list type="number">
///   <item><b>Main tray icon tooltip</b> - hover text on the existing
///         performance-mode icon, via <c>TrayIcon.ToolTipText</c> (the SNI
///         <c>Title</c> and, since Avalonia 12.2, the SNI <c>ToolTip</c>
///         that KDE Plasma shows on hover).</item>
///   <item><b>CPU temp tray icon</b> - optional second SNI service that
///         renders the live CPU temp as text on a colored background
///         (default blue), or transparent with auto-contrast outline.</item>
///   <item><b>GPU temp tray icon</b> - optional third SNI service, same
///         shape as CPU; only shown when a discrete GPU is detected.</item>
/// </list>
///
/// Public API:
/// <list type="bullet">
///   <item><see cref="Start"/> - call once after the main TrayIcon is created.
///         Pushes the initial tooltip, creates optional icons per config,
///         starts the refresh timer.</item>
///   <item><see cref="Refresh"/> - force-refresh tooltip + icons; called on
///         perf-mode change so the swap is visually atomic.</item>
///   <item><see cref="Stop"/> - halt the timer and release optional icons;
///         call before disposing the WMI handle during shutdown.</item>
///   <item><see cref="SetCpuIconEnabled"/> / <see cref="SetGpuIconEnabled"/> -
///         create or release the optional icons in response to
///         ExtraWindow toggles.</item>
///   <item><see cref="RefreshIconAppearance"/> - recompute icon images
///         after color/transparency settings change. Cheap (cache aware).</item>
/// </list>
///
/// Native AOT note: icon rendering uses SkiaSharp public API which is
/// trim-safe.
/// </summary>
public static class TraySystemMonitor
{
    // Timer + cached refs 

    private static DispatcherTimer? _timer;
    private static TrayIcon? _mainTray;       // tooltip target (created by App)
    private static TrayIcon? _cpuTray;        // optional, created on demand
    private static TrayIcon? _gpuTray;        // optional, created on demand
    private static TrayIcon? _dgpuTray;       // optional, dGPU status dot (#150)

    // Callback invoked when the user clicks any of the optional temp icons.
    // Mirrors the main tray icon's click behavior - all three icons toggle
    // the main window visibility consistently. Captured at Start() time so
    // EnsureCpuTray / EnsureGpuTray can subscribe to TrayIcon.Clicked
    // without re-resolving App.Current on every create/release cycle.
    private static Action? _toggleMainWindow;

    // 3-second cadence: a battery-friendly mid-ground that catches
    // CPU/GPU transients within ~1-2 ticks. Matches MainWindow's perceived
    // freshness without doubling the sysfs read load when both are active.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    // Plasma sorts tray icons by title: shared prefix keeps ours adjacent, "Linux" puts main last (#227).
    private const string TitlePrefix = "G-Helper ";

    // Render cache
    // Per-icon "last rendered key" - we skip the SKBitmap encode when the
    // tuple matches the previous tick. PNG encoding is cheap but
    // re-assigning TrayIcon.Icon triggers a D-Bus EmitNewIcon round-trip
    // that some SNI hosts (older Plasma versions) re-fetch synchronously.

    private record struct IconKey(int Temp, string BgHex, bool Transparent, string TextHex);

    private static IconKey _cpuLastKey;
    private static bool _cpuKeyValid;

    private static IconKey _gpuLastKey;
    private static bool _gpuKeyValid;

    // dGPU dot cache: keyed on the runtime-PM status string ("active" etc).
    private static string _dgpuLastStatus = "";
    private static bool _dgpuKeyValid;

    // 
    //  PUBLIC API
    // 

    /// <summary>
    /// Initialize the tooltip + optional temp icons. Cache the main tray
    /// reference, push the initial tooltip text, create CPU/GPU icons per
    /// saved toggles, start the periodic refresh timer.
    /// Idempotent: re-calling resets the timer rather than duplicating it.
    /// </summary>
    /// <param name="mainTray">The primary perf-mode tray icon owned by App.</param>
    /// <param name="toggleMainWindow">Callback that toggles MainWindow visibility.
    /// Wired to the optional CPU/GPU temp icons so all three icons behave
    /// identically on click.</param>
    public static void Start(TrayIcon mainTray, Action toggleMainWindow)
    {
        _mainTray = mainTray;
        _toggleMainWindow = toggleMainWindow;

        // Optional icons - create on init if their config toggle is on.
        // Failures are non-fatal (D-Bus / SNI host issues should never
        // prevent the main tray icon from starting).
        try
        {
            if (AppConfig.Is("cpu_tray_enabled"))
                EnsureCpuTray();
            if (AppConfig.Is("gpu_tray_enabled") && App.GpuControl?.IsAvailable() == true)
                EnsureGpuTray();
            if (AppConfig.Is("dgpu_tray_enabled") && Gpu.GPUModeControl.HasSecondGpu())
                EnsureDgpuTray();
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TraySystemMonitor: optional icon init failed: {ex.Message}");
        }

        // Push immediately so the user doesn't see "G-Helper - Balanced"
        // (Avalonia's leftover Title) for the first 3 seconds.
        Tick();

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>
    /// Force an immediate refresh of all three surfaces. Used by
    /// <c>UpdateTrayIcon</c> on perf-mode changes so the swap is visually
    /// atomic with the icon change.
    /// </summary>
    public static void Refresh()
    {
        if (_mainTray == null)
            return;
        Tick();
    }

    /// <summary>
    /// Halt the refresh timer and release optional icons. Call this
    /// <em>before</em> disposing the WMI handle during shutdown so an
    /// in-flight tick can't dereference a freed sysfs descriptor.
    /// </summary>
    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;

        // Release SNI services for the optional icons. The main tray is
        // owned by App.axaml.cs and is disposed via Avalonia's lifetime.
        try
        { if (_cpuTray != null) { _cpuTray.IsVisible = false; _cpuTray = null; } }
        catch { }
        try
        { if (_gpuTray != null) { _gpuTray.IsVisible = false; _gpuTray = null; } }
        catch { }
        try
        { if (_dgpuTray != null) { _dgpuTray.IsVisible = false; _dgpuTray = null; } }
        catch { }
        _cpuKeyValid = false;
        _gpuKeyValid = false;
        _dgpuKeyValid = false;
    }

    /// <summary>
    /// Toggle the CPU temp icon on/off. Creates or releases the SNI service
    /// to match, then forces a refresh tick so the user sees the change
    /// instantly. Persists nothing - caller writes the config key.
    /// </summary>
    public static void SetCpuIconEnabled(bool enabled)
    {
        if (enabled)
            EnsureCpuTray();
        else
            ReleaseCpuTray();
        Tick();
    }

    /// <summary>
    /// Toggle the GPU temp icon on/off. See <see cref="SetCpuIconEnabled"/>
    /// for behavior. The caller is responsible for not enabling the GPU
    /// icon on iGPU-only systems (gate with <c>App.GpuControl?.IsAvailable()</c>).
    /// </summary>
    public static void SetGpuIconEnabled(bool enabled)
    {
        if (enabled)
            EnsureGpuTray();
        else
            ReleaseGpuTray();
        Tick();
    }

    /// <summary>Toggle the dGPU status dot on/off (#150). dGPU-only.</summary>
    public static void SetDgpuIconEnabled(bool enabled)
    {
        if (enabled)
            EnsureDgpuTray();
        else
            ReleaseDgpuTray();
        Tick();
    }

    /// <summary>
    /// Invalidate the per-icon render cache and force a re-render. Call
    /// this after the user changes background / text colors or toggles
    /// transparency in ExtraWindow. The next tick re-encodes the PNG even
    /// if the temperature hasn't changed.
    /// </summary>
    public static void RefreshIconAppearance()
    {
        _cpuKeyValid = false;
        _gpuKeyValid = false;
        Tick();
    }

    // 
    //  TIMER TICK - drives all three surfaces from one sysfs read
    // 

    /// <summary>
    /// Single-tick driver. Reads CPU/GPU temps once, updates whichever
    /// surfaces are active. Robust to sysfs hiccups (EAGAIN/ENODEV during
    /// driver hotplug, suspend/resume) - falls back to "--" placeholder
    /// rather than letting the timer die.
    /// </summary>
    private static void Tick()
    {
        App.RefreshTrayDgpuStatus();

        int cpuTemp = -1;
        int gpuTemp = -1;
        try
        {
            cpuTemp = App.Wmi?.DeviceGet(0x00120094) ?? -1; // Temp_CPU
            // Skip dGPU temp when idle (resets autosuspend timer, blocks RTD3).
            if (!Gpu.NVidia.LinuxNvidiaGpuControl.ShouldSkipDgpuTelemetry()
                && AppConfig.IsNotFalse("dgpu_monitor"))
                gpuTemp = App.Wmi?.DeviceGet(0x00120097) ?? -1; // Temp_GPU
        }
        catch
        {
            // Swallow - we'd rather show stale data than crash the timer.
        }

        // AMD iGPU fallback: ASUS WMI Temp_GPU (0x00120097) only reports the
        // discrete GPU. On AMD-iGPU-only hardware (ROG Ally, GZ302E, GA403,
        // all-AMD Z13) it returns -1 / 0 and the GPU surface stays empty.
        // Fall back to amdgpu sysfs (read once, ~60 µs) so users on those
        // machines get a populated GPU readout in the tray.
        if (gpuTemp <= 0 && Gpu.AMD.LinuxAmdGpuMetrics.IsAvailable)
        {
            int? igpu = Gpu.AMD.LinuxAmdGpuMetrics.GetIgpuTempCelsius();
            if (igpu != null)
                gpuTemp = igpu.Value;
        }

        // Surface 1: tooltip on main icon.
        PushTooltip(BuildTooltipBody(cpuTemp, gpuTemp));

        // Surface 2: CPU temp icon (if active).
        if (_cpuTray != null)
        {
            UpdateCpuIcon(cpuTemp);
            // Hover tooltip - the visible digits already convey the value,
            // but Plasma users hovering the icon expect the unit ("°C") and
            // a confirmation that this is the CPU (not GPU). Reuses the
            // same i18n key ("tray_tooltip_cpu" = "CPU: {0}") that the main
            // icon uses, so we get free localization across all 29 langs.
            _cpuTray.ToolTipText = TitlePrefix +
                Labels.Format("tray_tooltip_cpu", cpuTemp > 0 ? TempHelper.FormatTemp(cpuTemp) : "--");
        }

        // Surface 3: GPU temp icon (if active).
        if (_gpuTray != null)
        {
            UpdateGpuIcon(gpuTemp);
            // Mirror of CPU tooltip - see above.
            _gpuTray.ToolTipText = TitlePrefix +
                Labels.Format("tray_tooltip_gpu", gpuTemp > 0 ? TempHelper.FormatTemp(gpuTemp) : "--");
        }

        // Surface 4: dGPU status dot (if active). Reading runtime_status never
        // wakes the card. null => Eco/off.
        if (_dgpuTray != null)
        {
            string? status = Gpu.GPUModeControl.DgpuRuntimeStatus();
            UpdateDgpuIcon(status);
            _dgpuTray.ToolTipText = TitlePrefix + "dGPU: " + (status ?? "off");
        }
    }

    // 
    //  TOOLTIP (existing logic, unchanged from the old TrayTooltip.cs)
    // 

    /// <summary>
    /// Build the multi-line tooltip body. Always shows CPU temp (or "--"
    /// when the read fails). Shows GPU temp on a second line only when
    /// it reports a positive value, so dGPU eco / GPU-off states render
    /// cleanly as a single line. Mode information is intentionally not
    /// duplicated - the tray icon's color/shape already conveys that.
    /// </summary>
    private static string BuildTooltipBody(int cpuTemp, int gpuTemp)
    {
        string line1 = Labels.Format("tray_tooltip_cpu", cpuTemp > 0 ? TempHelper.FormatTemp(cpuTemp) : "--");
        if (gpuTemp > 0)
        {
            string line2 = Labels.Format("tray_tooltip_gpu", TempHelper.FormatTemp(gpuTemp));
            return line1 + "\n" + line2;
        }
        return line1;
    }

    /// <summary>
    /// Push <paramref name="body"/> to the main icon's SNI <c>Title</c> via
    /// <c>TrayIcon.ToolTipText</c>. Hosts render it on hover (GNOME, Plasma);
    /// accessibility layers read it as the item name.
    /// </summary>
    private static void PushTooltip(string body)
    {
        if (_mainTray == null)
            return;

        _mainTray.ToolTipText = TitlePrefix + "Linux\n" + body;
    }

    // 
    //  CPU / GPU TEMP ICONS
    // 

    /// <summary>
    /// Lazily create the CPU temp tray icon. Idempotent. Ignores SNI
    /// failures so a missing D-Bus session never breaks app startup.
    /// </summary>
    private static void EnsureCpuTray()
    {
        if (_cpuTray != null)
            return;
        try
        {
            _cpuTray = new TrayIcon { IsVisible = true };
            // Match the main tray icon's click behavior - all three icons
            // (main, CPU, GPU) toggle the MainWindow consistently. Without
            // this the temp icons feel inert and users wonder if the app
            // crashed.
            _cpuTray.Clicked += (_, _) => _toggleMainWindow?.Invoke();
            TrayIcon.GetIcons(Application.Current!)?.Add(_cpuTray);
            _cpuKeyValid = false; // force first-tick render
            Logger.WriteLine("TraySystemMonitor: CPU temp icon created");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TraySystemMonitor: CPU icon create failed: {ex.Message}");
            _cpuTray = null;
        }
    }

    /// <summary>Tear down the CPU temp tray icon. Safe to call when not active.</summary>
    private static void ReleaseCpuTray()
    {
        if (_cpuTray == null)
            return;
        try
        { TrayIcon.GetIcons(Application.Current!)?.Remove(_cpuTray); }
        catch { }
        _cpuTray = null;
        _cpuKeyValid = false;
    }

    /// <summary>Lazily create the GPU temp tray icon. See <see cref="EnsureCpuTray"/>.</summary>
    private static void EnsureGpuTray()
    {
        if (_gpuTray != null)
            return;
        try
        {
            _gpuTray = new TrayIcon { IsVisible = true };
            // Same click behavior as the CPU icon - see EnsureCpuTray.
            _gpuTray.Clicked += (_, _) => _toggleMainWindow?.Invoke();
            TrayIcon.GetIcons(Application.Current!)?.Add(_gpuTray);
            _gpuKeyValid = false;
            Logger.WriteLine("TraySystemMonitor: GPU temp icon created");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TraySystemMonitor: GPU icon create failed: {ex.Message}");
            _gpuTray = null;
        }
    }

    /// <summary>Tear down the GPU temp tray icon.</summary>
    private static void ReleaseGpuTray()
    {
        if (_gpuTray == null)
            return;
        try
        { TrayIcon.GetIcons(Application.Current!)?.Remove(_gpuTray); }
        catch { }
        _gpuTray = null;
        _gpuKeyValid = false;
    }

    /// <summary>Lazily create the dGPU status dot icon. See <see cref="EnsureCpuTray"/>.</summary>
    private static void EnsureDgpuTray()
    {
        if (_dgpuTray != null)
            return;
        try
        {
            _dgpuTray = new TrayIcon { IsVisible = true };
            _dgpuTray.Clicked += (_, _) => _toggleMainWindow?.Invoke();
            TrayIcon.GetIcons(Application.Current!)?.Add(_dgpuTray);
            _dgpuKeyValid = false;
            Logger.WriteLine("TraySystemMonitor: dGPU status icon created");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TraySystemMonitor: dGPU icon create failed: {ex.Message}");
            _dgpuTray = null;
        }
    }

    /// <summary>Tear down the dGPU status dot icon.</summary>
    private static void ReleaseDgpuTray()
    {
        if (_dgpuTray == null)
            return;
        try
        { TrayIcon.GetIcons(Application.Current!)?.Remove(_dgpuTray); }
        catch { }
        _dgpuTray = null;
        _dgpuKeyValid = false;
    }

    /// <summary>
    /// Update the dGPU dot if the status changed since last tick. Color
    /// encodes state: green=active, gray=suspended, hollow=off.
    /// </summary>
    private static void UpdateDgpuIcon(string? status)
    {
        if (_dgpuTray == null)
            return;
        string key = status ?? "off";
        if (_dgpuKeyValid && key == _dgpuLastStatus)
            return;
        try
        {
            _dgpuTray.Icon = RenderDotIcon(key);
            _dgpuLastStatus = key;
            _dgpuKeyValid = true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TraySystemMonitor: dGPU icon render failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Update the CPU temp icon if the rendered tuple has changed since
    /// last tick. Defaults: blue (#3AAEEF) bg, white text, opaque.
    /// </summary>
    private static void UpdateCpuIcon(int temp)
    {
        if (_cpuTray == null)
            return;

        string bg = AppConfig.GetString("cpu_tray_bg") ?? "#3AAEEF";
        bool transparent = AppConfig.Is("cpu_tray_bg_transparent");
        string text = AppConfig.GetString("cpu_tray_text") ?? "#FFFFFF";

        var key = new IconKey(temp, bg, transparent, text);
        if (_cpuKeyValid && key == _cpuLastKey)
            return;

        try
        {
            _cpuTray.Icon = RenderTempIcon(temp, bg, transparent, text);
            _cpuLastKey = key;
            _cpuKeyValid = true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TraySystemMonitor: CPU icon render failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Update the GPU temp icon. Defaults: green (#06B48A) bg, white text.
    /// </summary>
    private static void UpdateGpuIcon(int temp)
    {
        if (_gpuTray == null)
            return;

        string bg = AppConfig.GetString("gpu_tray_bg") ?? "#06B48A";
        bool transparent = AppConfig.Is("gpu_tray_bg_transparent");
        string text = AppConfig.GetString("gpu_tray_text") ?? "#FFFFFF";

        var key = new IconKey(temp, bg, transparent, text);
        if (_gpuKeyValid && key == _gpuLastKey)
            return;

        try
        {
            _gpuTray.Icon = RenderTempIcon(temp, bg, transparent, text);
            _gpuLastKey = key;
            _gpuKeyValid = true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TraySystemMonitor: GPU icon render failed: {ex.Message}");
        }
    }

    // 
    //  ICON RENDERING (SkiaSharp)
    // 

    private const int IconSize = 44; // 44x44 source - SNI host scales for HiDPI

    /// <summary>
    /// Render a temperature icon as a 44×44 PNG and return it wrapped in
    /// a <see cref="WindowIcon"/>. The 44px source ensures crisp rendering
    /// on HiDPI panels (KDE Plasma fetches at panel-size and downscales).
    ///
    /// Layout:
    /// <list type="bullet">
    ///   <item>If <paramref name="transparent"/> is false: rounded-rect
    ///         background fill (8px corners) of <paramref name="bgHex"/>.</item>
    ///   <item>If transparent: skip the BG. To stay legible against any
    ///         panel color the DE might use, draw the text twice -
    ///         once with a 2px stroke in the inverse-luminance color
    ///         (white text → black outline; dark text → white outline),
    ///         then the fill. The luminance threshold uses ITU-R BT.601
    ///         coefficients (0.299/0.587/0.114).</item>
    ///   <item>Text: bold sans-serif. Default 28pt; auto-shrinks to 22pt
    ///         when the temp is 3 digits (rare - sensor errors only).</item>
    ///   <item>Text content: <c>temp.ToString()</c> when temp > 0,
    ///         else "--" placeholder.</item>
    /// </list>
    ///
    /// Allocates: 1 SKBitmap + 1 SKCanvas + 1-3 SKPaint + 1 SKImage +
    /// 1 SKData + 1 byte[] + 1 MemoryStream per call. The MemoryStream
    /// is owned by the returned WindowIcon and disposed when Avalonia
    /// drops the icon ref - the rest are <c>using</c>-scoped.
    /// </summary>
    private static WindowIcon RenderTempIcon(int temp, string bgHex, bool transparent, string textHex)
    {
        using var bitmap = new SKBitmap(IconSize, IconSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // Background (only when not transparent).
        if (!transparent)
        {
            SKColor bgColor;
            try
            { bgColor = SKColor.Parse(bgHex); }
            catch { bgColor = SKColor.Parse("#3AAEEF"); } // safe fallback to blue

            using var bgPaint = new SKPaint
            {
                Color = bgColor,
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };
            canvas.DrawRoundRect(SKRect.Create(0, 0, IconSize, IconSize), 8, 8, bgPaint);
        }

        // Text body. Show "--" placeholder for invalid sensor reads so
        // the icon never goes blank on transient EAGAIN/ENODEV.
        string s = temp > 0 ? temp.ToString() : "--";

        // Auto-shrink if 3+ digits (very rare; sensor glitch territory).
        // 28pt @ 44px fits 2 chars comfortably; 22pt fits 3.
        float textSize = s.Length >= 3 ? 22f : 28f;

        SKColor textColor;
        try
        { textColor = SKColor.Parse(textHex); }
        catch { textColor = SKColors.White; }

        // Vertical centering: SkiaSharp baseline math. For a 28pt cap-height
        // bold sans, baseline at y=33 puts the digits visually centered in
        // a 44px square. For 22pt, shift to y=30.
        float baseline = s.Length >= 3 ? 30f : 33f;

        // Modern SkiaSharp API: font properties live on SKFont, paint
        // carries only color/style. The legacy SKPaint.TextSize / Typeface
        // / TextAlign properties are obsolete in 3.x and removed in 4.x.
        using var typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
        using var font = new SKFont(typeface, textSize);

        using var textPaint = new SKPaint
        {
            Color = textColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        // Auto-contrast outline only when no background fill is hiding
        // the panel color. ITU-R BT.601 luma coefficients.
        if (transparent)
        {
            double lum = 0.299 * textColor.Red + 0.587 * textColor.Green + 0.114 * textColor.Blue;
            SKColor outline = lum > 128 ? SKColors.Black : SKColors.White;

            using var strokePaint = new SKPaint
            {
                Color = outline,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.5f,
                StrokeJoin = SKStrokeJoin.Round,
            };
            canvas.DrawText(s, IconSize / 2f, baseline, SKTextAlign.Center, font, strokePaint);
        }

        canvas.DrawText(s, IconSize / 2f, baseline, SKTextAlign.Center, font, textPaint);

        // Encode to PNG. Quality is ignored for PNG (lossless) but the API
        // requires a number; 100 is the conventional placeholder.
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        // Hand the bytes to a MemoryStream that WindowIcon takes ownership
        // of. Avalonia disposes it when the icon ref is dropped.
        var stream = new MemoryStream(data.ToArray());
        return new WindowIcon(stream);
    }

    /// <summary>
    /// Render the dGPU status dot (#150): a filled circle for active (green)
    /// / suspended (gray), or a hollow ring for off. 44x44 PNG.
    /// </summary>
    private static WindowIcon RenderDotIcon(string status)
    {
        using var bitmap = new SKBitmap(IconSize, IconSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        bool filled = status is "active" or "suspended";
        SKColor color = status switch
        {
            "active" => SKColor.Parse("#2ECC71"),    // green
            "suspended" => SKColor.Parse("#8A94A6"), // gray
            _ => SKColor.Parse("#6E7B8B"),           // off (hollow, dim)
        };

        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
            StrokeWidth = 4f,
        };
        canvas.DrawCircle(IconSize / 2f, IconSize / 2f, 13f, paint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var stream = new MemoryStream(data.ToArray());
        return new WindowIcon(stream);
    }
}
