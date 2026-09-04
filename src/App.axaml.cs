using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using GHelper.Linux.Display;
using GHelper.Linux.Gpu;
using GHelper.Linux.Gpu.AMD;
using GHelper.Linux.Gpu.NVidia;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Input;
using GHelper.Linux.Mode;
using GHelper.Linux.Platform;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.UI.Views;
using GHelper.Linux.USB;

namespace GHelper.Linux;

public class App : Application
{
    // Global service instances (mirrors G-Helper's Program.acpi pattern)
    public static IHardwareControl? Wmi { get; private set; }
    public static IPowerManager? Power { get; private set; }
    public static ISystemIntegration? System { get; private set; }
    public static IInputHandler? Input { get; private set; }
    public static IAudioControl? Audio { get; private set; }
    public static IDisplayControl? Display { get; private set; }
    public static RyzenSmu? Smu { get; private set; }
    public static IntelUndervolt? IntelUv { get; private set; }
    public static IGpuControl? GpuControl { get; set; }

    // GPU mode switching controller (safety checks, driver detection, reboot scheduling)
    public static GPUModeControl? GpuModeCtrl { get; private set; }

    // AnimeMatrix / Slash LED controller
    public static AnimeMatrix.AniMatrixControl? AnimeMatrix { get; private set; }

    // Business logic orchestrator
    public static ModeControl? Mode { get; private set; }

    // ROG Ally controller helper (HID bindings + auto-mode timer + TDP).
    // Initialised on every model - internally short-circuits when
    // AppConfig.IsAlly() is false, so it's safe to query unconditionally.
    public static Ally.AllyControl? Ally { get; private set; }

    public static MainWindow? MainWindowInstance { get; set; }
    public static TrayIcon? TrayIconInstance { get; set; }

    /// <summary>
    /// On-screen keyboard window. Created on first use, then hidden/shown by
    /// the tray toggle so its uinput device survives across uses.
    /// </summary>
    public static UI.Views.OnScreenKeyboardWindow? OskWindowInstance { get; private set; }

    /// <summary>
    /// Software fn-lock remapper. Singleton instance, lifetime bound to the app.
    /// Null until <see cref="StartFnLock"/> is called for the first time.
    /// </summary>
    public static FnLockRemapper? FnLock { get; private set; }

    /// <summary>
    /// Tray menu item for the fn-lock toggle. Held so external state changes
    /// (hotkey, MainWindow click) can refresh its header text + checkmark.
    /// Always present in the tray menu once <see cref="BuildTrayMenu"/> runs.
    /// </summary>
    private static NativeMenuItem? _trayFnLockItem;
    private static NativeMenuItem? _trayDgpuStatusItem;

    // Kernel runtime PM strings stay untranslated (technical labels).
    // null status with a known second GPU means Eco removed it from the bus.
    private static string BuildDgpuStatusHeader()
        => "dGPU: " + (Gpu.GPUModeControl.DgpuRuntimeStatus() ?? "off");

    /// <summary>Refresh the tray dGPU status row. Called from TraySystemMonitor's tick.</summary>
    public static void RefreshTrayDgpuStatus()
    {
        if (_trayDgpuStatusItem == null)
            return;
        string header = BuildDgpuStatusHeader();
        if ((_trayDgpuStatusItem.Header as string) != header)
            _trayDgpuStatusItem.Header = header;
    }

    /// <summary>
    /// Set to true when the app is on a shutdown path (tray Quit, MainWindow
    /// Quit button, KDE logout/reboot, or any caller of <see cref="Shutdown"/>).
    /// MainWindow's Closing handler reads this flag: when false, Closing is
    /// cancelled and the window is hidden instead of disposed, so the next
    /// tray-toggle re-open is instant. When true, Closing proceeds normally.
    /// </summary>
    public static bool IsShuttingDown { get; set; }

    /// <summary>
    /// Active icon set slug. Read from AppConfig at startup; may be hot-swapped
    /// at runtime via the Extra window dropdown. Setting this fires
    /// <see cref="IconSetChanged"/> so all live <c>Icon</c> controls rebuild.
    /// Values are normalized through <see cref="UI.Controls.IconSets.Normalize"/>
    /// so unknown slugs silently fall back to the default set.
    /// </summary>
    public static string IconSet
    {
        get => _iconSet;
        set
        {
            var normalized = UI.Controls.IconSets.Normalize(value);
            if (_iconSet == normalized)
                return;
            _iconSet = normalized;
            IconSetChanged?.Invoke(null, EventArgs.Empty);
        }
    }
    private static string _iconSet = UI.Controls.IconSets.Default;

    /// <summary>
    /// Raised on the UI thread whenever <see cref="IconSet"/> changes.
    /// <c>Icon</c> controls subscribe in their <c>AttachedToVisualTree</c>
    /// handler and unsubscribe in <c>DetachedFromVisualTree</c>.
    /// </summary>
    public static event EventHandler? IconSetChanged;

    // Single-instance lock that prevents duplicate tray icons
    private static FileStream? _lockFile;

    public override void Initialize()
    {
        // Read active icon-set slug once, before any view is loaded.
        // The setter normalizes unknown slugs (corrupted config, or sets that
        // have since been removed) to the default set. Assigning through the
        // public setter is safe here - no Icon controls exist yet to receive
        // the change event.
        IconSet = AppConfig.GetString("icon_set", UI.Controls.IconSets.Default)
                  ?? UI.Controls.IconSets.Default;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Exit if another ghelper is already running
        if (!TryAcquireSingleInstanceLock())
        {
            Console.Error.WriteLine("g-helper: another instance is already running - exiting");
            Environment.Exit(0);
            return;
        }

        // Initialize Linux platform backends
        InitializePlatform();

        // Initialize i18n before any UI
        Labels.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep running when window is closed (tray icon keeps app alive)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Catch-all signal that a shutdown is in progress, regardless of
            // initiator (programmatic Shutdown(), KDE logout/reboot, etc).
            // MainWindow's Closing handler relies on this flag to distinguish
            // a real shutdown from a user-initiated window close (Hide-only).
            desktop.ShutdownRequested += (_, _) =>
            {
                IsShuttingDown = true;
                Logger.WriteLine("Shutdown requested - allowing windows to dispose");
            };

            MainWindowInstance = new MainWindow();
            if (AppConfig.Is("topmost"))
                MainWindowInstance.Topmost = true;

            bool oskStart = desktop.Args?.Contains("--osk") == true;
            bool gameMode = Platform.Linux.SteamShortcuts.IsSteamDeckGameMode;

            if (gameMode)
            {
                // gamescope (SteamOS game mode): run fullscreen like Heroic
                // does - floating windows get broken input coordinate mapping
                // under gamescope, and the session shows one app anyway.
                MainWindowInstance.WindowState = WindowState.FullScreen;
                desktop.MainWindow = MainWindowInstance;
            }
            // Show main window on startup unless "Start minimized to tray" is
            // enabled. "--osk" also skips it: the user asked for the keyboard.
            else if (!AppConfig.Is("silent_start") && !oskStart)
            {
                WindowPositioner.BottomRight(MainWindowInstance);
                desktop.MainWindow = MainWindowInstance;
            }

            // Set up tray icon (secondary access method)
            SetupTrayIcon(desktop);

            // Start hotkey listener
            StartHotkeyListener();

            // Command socket for follow-up "ghelper --osk" invocations, and
            // the keyboard itself when this startup was osk-initiated.
            CommandIpc.StartServer(cmd =>
            {
                if (cmd == "toggle-osk")
                    Avalonia.Threading.Dispatcher.UIThread.Post(ToggleOskWindow);
            });
            if (oskStart)
                Avalonia.Threading.Dispatcher.UIThread.Post(ToggleOskWindow);

            // Controller-driven focus navigation (game mode / handhelds).
            GHelper.Linux.Input.GamepadNav.Start();

            // One-time "add to Steam library?" offer. Not in game mode: the
            // app was just launched from Steam there.
            if (!gameMode)
                OfferSteamShortcutOnce();

            // Apply saved performance mode on startup
            Mode?.SetPerformanceMode();

            // Re-apply saved battery charge limit on startup
            Battery.BatteryControl.AutoBattery(init: true);

            // Init fan sensor defaults for model-specific RPM formatting
            Fan.FanSensorControl.InitFanMax();

            // Warn if udev rules are not installed (sysfs writes will fail).
            // NixOS: the module provides udev rules via services.udev.packages.
            if (!Platform.Linux.NixOS.SkipUdevWarning
                && !File.Exists("/etc/udev/rules.d/90-ghelper.rules"))
            {
                Logger.WriteLine("WARNING: udev rules not installed - sysfs writes will fail. Run install.sh for full functionality.");
                System?.ShowNotification(Labels.Get("setup_required"),
                    Labels.Get("udev_not_installed"),
                    "dialog-warning");
            }

            // Initialize AURA hardware (RGB) on background thread regardless of window visibility.
            // When done, post RefreshKeyboard to UI thread so the Aura panel/colors update
            // (fixes race where Loaded event fires before HID handshake completes).
            Task.Run(() =>
            {
                MainWindow.InitAuraHardware();
                // XG Mobile docks only attach to ASUS laptops; skip the USB
                // probe elsewhere. AURA still probes on Generic so external
                // ASUS RGB keyboards keep working.
                if (AppConfig.IsAsusDevice())
                    USB.XGM.InitHardware();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => MainWindowInstance?.RefreshKeyboard());
            });

            // Detect connected ASUS peripherals (mice) and register for hot-plug events.
            Task.Run(() =>
            {
                Peripherals.PeripheralsProvider.DetectAllMice();
                Peripherals.PeripheralsProvider.RegisterForDeviceEvents();
            });

            // Poll for BT mouse reconnect even when the window is hidden.
            // HidSharp misses BT hidraw events, so periodic rescan is needed.
            // Backs off to 5 min after 3 empty scans (most machines never
            // have a supported mouse); hot-plug events still detect
            // immediately, the timer only drives battery refresh cadence.
            int emptyScans = 0;
            _peripheralPollTimer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref _peripheralPollRunning, 1, 0) != 0)
                    return;
                try
                {
                    Peripherals.PeripheralsProvider.RefreshBatteryForAllDevices();
                    Peripherals.PeripheralsProvider.DetectAllMice();
                    bool any = Peripherals.PeripheralsProvider.ConnectedMice.Count > 0;
                    emptyScans = any ? 0 : Math.Min(emptyScans + 1, 3);
                    var period = TimeSpan.FromSeconds(emptyScans >= 3 ? 300 : 20);
                    _peripheralPollTimer?.Change(period, period);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Peripheral poll failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _peripheralPollRunning, 0);
                }
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

            // Ensure autostart .desktop file matches config preference and current binary path
            bool autostart = AppConfig.IsNotFalse("autostart");
            System?.SetAutostart(autostart);

            if (!AppConfig.IsOptimizedGpuModeEnabled() && AppConfig.Is("gpu_auto"))
            {
                Logger.WriteLine("Startup: gpu_optimized_enabled=0 - clearing stale gpu_auto");
                AppConfig.Set("gpu_auto", 0);
            }
            AppConfig.AutoDetectGpuBackendIfUnset();

            // Update tray icon to match current mode
            UpdateTrayIcon();

            // Start power state monitoring for auto GPU mode and auto performance
            Power?.StartPowerMonitoring();
            if (Power != null)
            {
                Power.PowerStateChanged += OnPowerStateChanged;
                Power.SystemResumed += OnSystemResumed;
                Power.MonitorSlept += OnMonitorSlept;
                Power.MonitorWoke += OnMonitorWoke;
            }

            // Apply pending GPU mode from config (e.g., Eco scheduled for reboot)
            // Then apply auto GPU mode if Optimized is enabled
            // Run on background thread - SetGpuEco can block for 30-60 seconds
            Task.Run(() =>
            {

                GpuModeCtrl?.CacheDgpuSlotIfPresent();

                // Check for boot recovery marker (impossible state was fixed during boot)
                const string RecoveryMarkerPath = "/etc/ghelper/last-recovery";
                try
                {
                    if (File.Exists(RecoveryMarkerPath))
                    {
                        string reason = File.ReadAllText(RecoveryMarkerPath).Trim();
                        Logger.WriteLine($"Boot recovery detected: {reason}");
                        System?.ShowNotification(Labels.Get("gpu_mode"),
                            Labels.Get("gpu_reset_standard"),
                            "dialog-warning");
                        try
                        { File.Delete(RecoveryMarkerPath); }
                        catch (Exception delEx)
                        {
                            Logger.WriteLine($"Could not delete recovery marker: {delEx.Message}");
                            // Non-fatal - marker will be shown again next launch but that's acceptable
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Recovery marker check failed: {ex.Message}");
                }

                // First: try to apply any pending mode from config
                if (GpuModeCtrl != null)
                {
                    var pendingResult = GpuModeCtrl.ApplyPendingOnStartup();
                    if (pendingResult == GpuSwitchResult.Applied)
                    {
                        System?.ShowNotification(Labels.Get("gpu_mode"),
                            Labels.Get("gpu_eco_applied"), "video-display");
                    }
                    else if (pendingResult == GpuSwitchResult.DriverBlocking)
                    {
                        System?.ShowNotification(Labels.Get("gpu_mode"),
                            Labels.Get("gpu_eco_driver_pending"), "dialog-warning");
                    }
                }

                // Then: auto GPU mode (Optimized) based on current power state
                if (GpuModeCtrl != null && AppConfig.Is("gpu_auto"))
                {
                    var autoResult = GpuModeCtrl.AutoGpuSwitch();
                    if (autoResult == GpuSwitchResult.Applied)
                    {
                        bool onAc = Power?.IsOnAcPower() ?? true;
                        System?.ShowNotification(Labels.Get("gpu_mode"),
                            onAc ? Labels.Get("gpu_optimized_ac") : Labels.Get("gpu_optimized_battery"),
                            "video-display");
                    }
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    MainWindowInstance?.RefreshGpuModePublic());
            });

            // Restore clamshell mode if it was enabled
            if (AppConfig.Is("toggle_clamshell_mode"))
                UI.Views.ExtraWindow.StartClamshellInhibit();

            OptimalBrightness.Init();

            // Auto-start the audio helper if the user enabled it in a prior
            // session. The helper is cheap (~10 MB RSS, <1% CPU when idle),
            // and starting at app launch keeps the FN-Lock-style toggle
            // visually consistent with what's actually running.
            if (AppConfig.Is("audio_enabled") && !AppConfig.Is("disable_audio"))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (AudioHelper.Instance.Start())
                    {
                        AudioHelper.Instance.ReapplyAllState();
                        MainWindowInstance?.RefreshAudioToggle();
                    }
                });
            }

            // Lenovo internal-mic boost clamp (ALC287 distortion fix): applied
            // at startup and after every resume while the option is on.
            if (AppConfig.IsLenovoDevice() && AppConfig.Is("lenovo_mic_boost_fix"))
                Task.Run(() => Platform.Linux.Lenovo.LenovoFeatures.ApplyMicBoostFix());

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                Install.Installer.CheckAndPromptAtStartup(MainWindowInstance));

            if (MainWindowInstance != null)
                UI.Views.UpdatesWindow.CheckForUpdateAtStartup(MainWindowInstance);

            // Register Unix signal handlers for clean shutdown on SIGTERM/SIGINT
            // This prevents KDE/GNOME from hanging on logout/reboot
            RegisterSignalHandlers(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializePlatform()
    {
        if (Helpers.AppConfig.IsLenovoDevice())
        {
            Logger.WriteLine($"Vendor: Lenovo ({Helpers.AppConfig.GetDmiVendor()})");
            Wmi = new Platform.Linux.Lenovo.LinuxLenovoWmi();
        }
        else
        {
            // ASUS and Generic both use the ASUS backend: on Generic every
            // ASUS attribute resolves to nothing and universal paths
            // (platform_profile, charge threshold, backlight) still work.
            if (Helpers.AppConfig.IsGenericDevice())
                Logger.WriteLine($"Vendor: generic ({Helpers.AppConfig.GetDmiVendor()}) - universal features only");
            Wmi = new LinuxAsusWmi();
        }
        Power = new LinuxPowerManager();
        System = new LinuxSystemIntegration();
        Input = new LinuxInputHandler();
        Audio = new LinuxAudioControl();
        Display = new LinuxDisplayControl();

        Smu = new RyzenSmu();
        Logger.WriteLine(Smu.IsAvailable
            ? "Ryzen Curve Optimizer: available via ryzen_smu driver"
            : $"Ryzen Curve Optimizer: unavailable ({Smu.UnavailableReason})");

        // Re-apply saved ryzenadj values (SMU resets on reboot). Off the UI
        // thread; non-interactive inside, so no auth prompt can appear.
        if (Platform.Linux.RyzenPower.IsRyzenCpu)
            Task.Run(Platform.Linux.RyzenPower.ApplySavedOnStart);

        IntelUv = new IntelUndervolt();
        if (IntelUv.IsAvailable)
            Logger.WriteLine("Intel CPU undervolt: available (MSR 0x150 mailbox)");

        // Create mode controller (uses App.Wmi, App.Power, etc.)
        Mode = new ModeControl();
        Mode.RefreshReapplyTimer(); // arm timer if reapply_time is set

        // Create GPU mode switching controller
        GpuModeCtrl = new GPUModeControl(Wmi, Power);
        GPUModeControl.OnLivePciTransition =
            Platform.Linux.LinuxAsusWmi.InvalidateGpuPresenceCache;
        GPUModeControl.OnReapplyGpuTuning =
            () => Mode?.ReapplyGpuForCurrentMode();

        // Create Ally controller helper. Constructor sets up the 300ms auto-
        // mode timer when on RC71L/RC72L; on every other model the .ctor is
        // a no-op so this is cheap and safe to call unconditionally.
        Ally = new Ally.AllyControl();
        Ally.Init();

        // AnimeMatrix / Slash LED controller. No-op when no device is present.
        AnimeMatrix = new AnimeMatrix.AniMatrixControl();

        // NumberPad service (only starts when enabled in config).
        GHelper.Linux.Input.NumberPad.InitIfEnabled();

        // Status LED indicators on at start when auto_status_led is set.
        Task.Run(Platform.Linux.StatusLed.Init);

        // Initialize GPU control (nvidia-smi / amdgpu sysfs for temp/load)
        InitializeGpuControl();

        Logger.WriteLine($"G-Helper Linux initialized");
        Logger.WriteLine($"Model: {System.GetModelName()}");
        Logger.WriteLine($"BIOS: {System.GetBiosVersion()}");

        // Log which sysfs backend each attribute resolved to (legacy vs firmware-attributes)
        if (Helpers.AppConfig.IsAsusDevice())
            SysfsHelper.LogResolvedAttributes();

        // Log detected features
        LogFeatureDetection();
    }

    private void LogFeatureDetection()
    {
        var features = new (AttrDef attr, string name)[]
        {
            (AsusAttributes.ThrottleThermalPolicy, "Performance modes"),
            (AsusAttributes.DgpuDisable, "GPU Eco mode"),
            (AsusAttributes.GpuMuxMode, "MUX switch"),
            (AsusAttributes.PanelOd, "Panel overdrive"),
            (AsusAttributes.MiniLedMode, "Mini LED"),
            (AsusAttributes.PptPl1Spl, "PL1 power limit"),
            (AsusAttributes.PptPl2Sppt, "PL2 power limit"),
            (AsusAttributes.NvDynamicBoost, "NVIDIA dynamic boost"),
            (AsusAttributes.NvTempTarget, "NVIDIA temp target"),
            (AsusAttributes.ScreenAutoBrightness, "Optimal Display Brightness"),
        };

        foreach (var (attr, name) in features)
        {
            bool supported = Wmi?.IsFeatureSupported(attr) ?? false;
            Logger.WriteLine($"  {name}: {(supported ? "YES" : "no")}");
        }

        // Raw WMI: probe all GPU ACPI endpoints in a single pkexec call (only when enabled)
        if (Helpers.AppConfig.Is("raw_wmi"))
        {
            Logger.WriteLine("  Raw WMI mode: ENABLED (user opt-in)");
            if (AsusWmiDebugfs.IsAvailable())
            {
                AsusWmiDebugfs.ProbeAll();      // 1 pkexec call - probes + caches all device IDs
                AsusWmiDebugfs.LogProbeResults(); // reads from cache, no pkexec
            }
            else
            {
                Logger.WriteLine("  Raw WMI debugfs: not available");
            }
        }
    }

    private void InitializeGpuControl()
    {
        try
        {
            var nvidia = new LinuxNvidiaGpuControl();
            if (nvidia.IsAvailable())
            {
                GpuControl = nvidia;
                Logger.WriteLine($"GPU Control: NVIDIA - {nvidia.GetGpuName() ?? "Unknown"}");
                return;
            }

            var amd = new LinuxAmdGpuControl();
            if (amd.IsAvailable())
            {
                GpuControl = amd;
                Logger.WriteLine($"GPU Control: AMD - {amd.GetGpuName() ?? "Unknown"}");
                return;
            }

            Logger.WriteLine("GPU Control: No dGPU detected");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU Control initialization failed", ex);
        }
    }

    public static void RefreshGpuControlIfMissing()
    {
        if (GpuControl != null && GpuControl.IsAvailable())
            return;
        try
        {
            var nvidia = new LinuxNvidiaGpuControl();
            if (nvidia.IsAvailable())
            {
                GpuControl = nvidia;
                Logger.WriteLine($"GPU Control: re-detected NVIDIA - {nvidia.GetGpuName() ?? "Unknown"}");
                return;
            }
            var amd = new LinuxAmdGpuControl();
            if (amd.IsAvailable())
            {
                GpuControl = amd;
                Logger.WriteLine($"GPU Control: re-detected AMD - {amd.GetGpuName() ?? "Unknown"}");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU Control re-init failed", ex);
        }
    }

    private void StartHotkeyListener()
    {
        if (Input == null)
            return;

        Input.HotkeyPressed += GHelper.Linux.Input.InputDispatcher.DispatchHotkey;
        Input.KeyBindingPressed += GHelper.Linux.Input.InputDispatcher.DispatchKeyBinding;
        Input.StartListening();

        // Firmware-driven profile changes (Lenovo Fn+Q): adopt the new mode in
        // the app instead of fighting the firmware.
        if (Wmi != null)
            Wmi.PlatformProfileChanged += OnPlatformProfileChanged;
    }

    private static void OnPlatformProfileChanged(string profile)
    {
        int mode = profile switch
        {
            "performance" or "balanced-performance" or "max-power" => 1,
            "low-power" or "quiet" => 2,
            _ => 0
        };

        if (GHelper.Linux.Mode.Modes.GetBase(GHelper.Linux.Mode.Modes.GetCurrent()) == mode)
            return;

        GHelper.Linux.Mode.Modes.SetCurrent(mode);
        Logger.WriteLine($"Adopted external platform_profile '{profile}' as mode {mode}");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            MainWindowInstance?.RefreshPerformanceMode();
            System?.ShowNotification(Labels.Get("performance"),
                GHelper.Linux.Mode.Modes.GetName(mode), "preferences-system-performance");
        });
    }

    /// <summary>
    /// Auto-switch screen refresh rate based on AC/battery power.
    /// AC → max refresh + overdrive ON, Battery → 60Hz + overdrive OFF.
    /// Called on power state change and when user enables auto mode.
    /// </summary>
    public void AutoScreen()
    {
        if (!AppConfig.Is("screen_auto"))
        {
            // Restore saved overdrive state even when auto-screen is off.
            int saved = AppConfig.Get("panel_od", -1);
            if (saved >= 0)
                Wmi?.SetPanelOverdrive(saved == 1);
            return;
        }

        var display = Display;
        if (display == null)
            return;

        var rates = display.GetAvailableRefreshRates();
        bool onAc = Power?.IsOnAcPower() ?? true;

        if (onAc)
        {
            int maxHz = rates.Count > 0 ? rates[0] : 120;
            display.SetRefreshRate(maxHz);
            Wmi?.SetPanelOverdrive(true);
            AppConfig.Set("panel_od", 1);
            Logger.WriteLine($"AutoScreen: AC power -> {maxHz}Hz + overdrive ON");
            System?.ShowNotification(Labels.Get("display"), Labels.Format("auto_screen_ac", maxHz), "video-display");
        }
        else
        {
            display.SetRefreshRate(60);
            Wmi?.SetPanelOverdrive(false);
            AppConfig.Set("panel_od", 0);
            Logger.WriteLine("AutoScreen: Battery -> 60Hz + overdrive OFF");
            System?.ShowNotification(Labels.Get("display"), Labels.Get("auto_screen_battery"), "video-display");
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            MainWindowInstance?.RefreshScreenPublic());
    }


    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Tray icons on Linux use D-Bus StatusNotifierItem (SNI) protocol.
        // This requires a valid DBUS_SESSION_BUS_ADDRESS - running with plain
        // 'sudo' breaks this. Use udev rules for non-root access instead,
        // or run with: sudo -E ./ghelper
        var dbusAddr = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (string.IsNullOrEmpty(dbusAddr))
        {
            Logger.WriteLine("WARNING: DBUS_SESSION_BUS_ADDRESS not set - tray icon will not appear.");
            Logger.WriteLine("  Tip: Install udev rules to run without sudo, or use: sudo -E ./ghelper");
        }

        try
        {
            var trayIcon = new TrayIcon
            {
                IsVisible = true,
                Menu = CreateTrayMenu(desktop)
            };

            // Load tray icon from embedded assets
            try
            {
                string iconName = AppConfig.IsBWIcon() ? "dark-standard.ico" : "standard.ico";
                var uri = new Uri($"avares://ghelper/UI/Assets/{iconName}");
                trayIcon.Icon = new WindowIcon(AssetLoader.Open(uri));
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Could not load tray icon image", ex);
            }

            trayIcon.Clicked += (_, _) => ToggleMainWindow();
            TrayIconInstance = trayIcon;

            TraySystemMonitor.Start(trayIcon, () => ToggleMainWindow());

            Labels.LanguageChanged += () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (TrayIconInstance != null)
                    {
                        TrayIconInstance.Menu = CreateTrayMenu(desktop);
                        UpdateTrayIcon();
                    }
                });
            };

            Logger.WriteLine("Tray icon created successfully");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Tray icon setup failed (D-Bus/SNI unavailable)", ex);
        }
    }

    /// <summary>Update tray icon and tooltip to reflect current performance mode.</summary>
    public static void UpdateTrayIcon()
    {
        if (TrayIconInstance == null)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            int mode = Modes.GetCurrent();
            int baseMode = Modes.GetBase(mode);

            TraySystemMonitor.Refresh();

            bool bw = AppConfig.IsBWIcon();

            // Select icon based on base mode and B&W preference
            string iconName = baseMode switch
            {
                0 => bw ? "dark-standard.ico" : "standard.ico",   // Balanced
                1 => bw ? "dark-standard.ico" : "ultimate.ico",   // Turbo (no dark-ultimate, use dark-standard)
                2 => bw ? "dark-eco.ico" : "eco.ico",        // Silent
                _ => bw ? "dark-standard.ico" : "standard.ico"
            };

            try
            {
                var uri = new Uri($"avares://ghelper/UI/Assets/{iconName}");
                TrayIconInstance.Icon = new WindowIcon(AssetLoader.Open(uri));
            }
            catch
            {
                // Ignore - icon may not exist
            }
        });
    }

    /// <summary>Apply Topmost setting to all currently open windows.</summary>
    public static void SetTopmostAll(bool topmost)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    window.Topmost = topmost;
                }
            }
        });
    }

    private NativeMenu CreateTrayMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        // Performance modes
        var silent = new NativeMenuItem(Labels.Get("mode_silent"));
        silent.Click += (_, _) => { Mode?.SetPerformanceMode(2, true); UpdateTrayIcon(); MainWindowInstance?.RefreshPerformanceMode(); };
        menu.Add(silent);

        var balanced = new NativeMenuItem(Labels.Get("mode_balanced"));
        balanced.Click += (_, _) => { Mode?.SetPerformanceMode(0, true); UpdateTrayIcon(); MainWindowInstance?.RefreshPerformanceMode(); };
        menu.Add(balanced);

        var turbo = new NativeMenuItem(Labels.Get("mode_turbo"));
        turbo.Click += (_, _) => { Mode?.SetPerformanceMode(1, true); UpdateTrayIcon(); MainWindowInstance?.RefreshPerformanceMode(); };
        menu.Add(turbo);

        menu.Add(new NativeMenuItemSeparator());

        // GPU modes - show if GPU Eco is available (sysfs or raw WMI debugfs).
        // All writes run in Task.Run via GPUModeControl
        // (dgpu_disable writes can block in the kernel for 30-60 seconds)
        if (Wmi?.IsGpuEcoAvailable() == true)
        {
            var eco = new NativeMenuItem(Labels.Get("tray_gpu_eco"));
            eco.Click += (_, _) => TrayGpuModeSwitch(GpuMode.Eco);
            menu.Add(eco);

            var standard = new NativeMenuItem(Labels.Get("tray_gpu_standard"));
            standard.Click += (_, _) => TrayGpuModeSwitch(GpuMode.Standard);
            menu.Add(standard);

            if (AppConfig.IsOptimizedGpuModeEnabled())
            {
                var optimized = new NativeMenuItem(Labels.Get("tray_gpu_optimized"));
                optimized.Click += (_, _) => TrayGpuModeSwitch(GpuMode.Optimized);
                menu.Add(optimized);
            }

            // Ultimate (MUX switch) - only on models with gpu_mux_mode support
            if (Wmi?.IsFeatureSupported(AsusAttributes.GpuMuxMode) == true)
            {
                var ultimate = new NativeMenuItem(Labels.Get("tray_gpu_ultimate"));
                ultimate.Click += (_, _) => TrayGpuModeSwitch(GpuMode.Ultimate);
                menu.Add(ultimate);
            }

            menu.Add(new NativeMenuItemSeparator());
        }

        // dGPU runtime PM status row (issue #150). Non-clickable text; the
        // sysfs read never wakes the card. TraySystemMonitor refreshes it.
        if (Gpu.GPUModeControl.HasSecondGpu())
        {
            _trayDgpuStatusItem = new NativeMenuItem(BuildDgpuStatusHeader()) { IsEnabled = false };
            menu.Add(_trayDgpuStatusItem);
            menu.Add(new NativeMenuItemSeparator());
        }

        // Settings
        var settings = new NativeMenuItem(Labels.Get("settings"));
        settings.Click += (_, _) => ToggleMainWindow();
        menu.Add(settings);

        // ROG Ally - surface the controller-mode toggle directly in the tray
        // menu so handheld users don't have to open the main window for it.
        // Only visible on RC71L/RC72L.
        if (AppConfig.IsAlly())
        {
            var allyToggle = new NativeMenuItem(Labels.Get("action_ally_toggle_mode"));
            allyToggle.Click += (_, _) =>
            {
                Ally?.ToggleModeHotkey();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    MainWindowInstance?.RefreshAllyPanel());
            };
            menu.Add(allyToggle);
        }

        menu.Add(new NativeMenuItemSeparator());

        string BuildBwIconHeader() =>
            (Helpers.AppConfig.Is("bw_icon") ? "✓ " : "   ") + Labels.Get("tray_bw_icon");
        var bwIcon = new NativeMenuItem(BuildBwIconHeader());
        bwIcon.Click += (_, _) =>
        {
            bool next = !Helpers.AppConfig.Is("bw_icon");
            Helpers.AppConfig.Set("bw_icon", next ? 1 : 0);
            bwIcon.Header = BuildBwIconHeader();
            UpdateTrayIcon();
        };
        menu.Add(bwIcon);

        // FN-Lock toggle. Always present in the menu; click routes through
        // the authoritative App.SetFnLockEnabled which handles enable/disable
        // + remapper start/stop + UI refresh in one place. Header carries a
        // checkmark when fn-lock is currently active.
        string BuildFnLockHeader() =>
            ((FnLock?.IsActive ?? false) && (FnLock?.FnLockOn ?? false) ? "✓ " : "   ")
            + Labels.Get("fnlock_tray_label");
        var fnItem = new NativeMenuItem(BuildFnLockHeader());
        _trayFnLockItem = fnItem;
        fnItem.Click += (_, _) =>
        {
            // Click flips the master enable+state via the same path as the
            // MainWindow button. The header is updated by RefreshTrayFnLockHeader
            // on the resulting state change.
            bool nowOn = (FnLock?.IsActive ?? false) && (FnLock?.FnLockOn ?? false);
            SetFnLockEnabled(!nowOn);
        };
        menu.Add(fnItem);

        // On-screen keyboard, for touch-only use (SteamOS desktop mode on
        // handhelds). Types into the focused window via uinput.
        var oskItem = new NativeMenuItem(Labels.Get("osk_tray_label"));
        oskItem.Click += (_, _) => ToggleOskWindow();
        menu.Add(oskItem);

        menu.Add(new NativeMenuItemSeparator());

        // Quit
        var quit = new NativeMenuItem(Labels.Get("quit"));
        quit.Click += (_, _) =>
        {
            IsShuttingDown = true;
            Shutdown(desktop);
        };
        menu.Add(quit);

        return menu;
    }

    public static void ToggleMainWindow()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        // Snapshot: Close() modifies the Windows collection during iteration.
        // The on-screen keyboard lives outside this toggle; it has its own
        // tray item and must not swallow a "show settings" click.
        var visibleWindows = desktop.Windows
            .Where(w => w.IsVisible && w is not UI.Views.OnScreenKeyboardWindow)
            .ToList();

        // Any window visible → close them all (child windows get recreated on demand)
        if (visibleWindows.Count > 0)
        {
            foreach (var w in visibleWindows)
            {
                try
                { w.Close(); }
                catch { }
            }
            return;
        }

        // Nothing visible → show main window only
        if (MainWindowInstance == null || MainWindowInstance.PlatformImpl == null)
        {
            MainWindowInstance = new MainWindow();
            if (AppConfig.Is("topmost"))
                MainWindowInstance.Topmost = true;
            desktop.MainWindow = MainWindowInstance;
        }

        WindowPositioner.BottomRight(MainWindowInstance);
        MainWindowInstance.Show();
        MainWindowInstance.Activate();
    }

    /// <summary>
    /// One-time offer to add G-Helper to the Steam library. Shown when Steam
    /// is installed and the shortcut is absent; remembered either way so it
    /// never nags. The actual add runs through the same SteamShortcuts path
    /// as the Updates-window toggle.
    /// </summary>
    private static void OfferSteamShortcutOnce()
    {
        if (AppConfig.Is("steam_offer_shown"))
            return;
        // Handheld-oriented feature: desktops add the shortcut from the
        // Updates window instead of getting a first-launch dialog.
        if (!AppConfig.IsHandheldDevice() && !Platform.Linux.ImmutableOs.IsSteamOs)
            return;
        Task.Run(() =>
        {
            try
            {
                if (!Platform.Linux.SteamShortcuts.IsSteamAvailable()
                    || Platform.Linux.SteamShortcuts.IsAdded())
                    return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    AppConfig.Set("steam_offer_shown", 1);
                    new UI.Views.SteamOfferWindow().Show();
                });
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Steam offer check failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Show/hide the on-screen keyboard. The window is created once and then
    /// hidden on close so the virtual keyboard device stays registered.
    /// Never activated: focus must stay on the window being typed into.
    /// </summary>
    public static void ToggleOskWindow()
    {
        if (OskWindowInstance == null || OskWindowInstance.PlatformImpl == null)
        {
            OskWindowInstance = new UI.Views.OnScreenKeyboardWindow();
            OskWindowInstance.Show();
            return;
        }
        if (OskWindowInstance.IsVisible)
            OskWindowInstance.Hide();
        else
            OskWindowInstance.Show();
    }

    /// <summary>
    /// Tray menu GPU mode switch - runs GPUModeControl on background thread.
    /// Tray menu cannot show dialogs, so DriverBlocking → auto-schedule for reboot.
    /// </summary>
    private static void TrayGpuModeSwitch(GpuMode target)
    {
        Task.Run(() =>
        {
            if (GpuModeCtrl == null)
                return;

            var result = GpuModeCtrl.RequestModeSwitch(target);

            switch (result)
            {
                case GpuSwitchResult.Applied:
                    if (target != GpuMode.Eco)
                        RefreshGpuControlIfMissing();
                    string text = target switch
                    {
                        GpuMode.Eco => Labels.Get("gpu_notify_eco"),
                        GpuMode.Standard => Labels.Get("gpu_notify_standard"),
                        GpuMode.Optimized => Labels.Get("gpu_notify_optimized"),
                        GpuMode.Ultimate => Labels.Get("gpu_notify_ultimate"),
                        _ => Labels.Get("gpu_notify_changed")
                    };
                    System?.ShowNotification(Labels.Get("gpu_mode"), text, "video-display");
                    break;

                case GpuSwitchResult.RebootRequired:
                    string rebootText = target switch
                    {
                        GpuMode.Ultimate => Labels.Get("gpu_reboot_ultimate"),
                        GpuMode.Standard => Labels.Get("gpu_reboot_standard"),
                        GpuMode.Optimized => Labels.Get("gpu_reboot_optimized"),
                        GpuMode.Eco => Labels.Get("gpu_reboot_eco"),
                        _ => Labels.Format("gpu_mode_reboot_format", target)
                    };
                    System?.ShowNotification(Labels.Get("gpu_mode"), rebootText, "system-reboot");
                    break;

                case GpuSwitchResult.EcoBlocked:
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_eco_blocked_mux"),
                        "dialog-warning");
                    break;

                case GpuSwitchResult.DriverBlocking:
                    // Tray menu can't show a dialog - auto-schedule for reboot
                    GpuModeCtrl.ScheduleModeForReboot(target);
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_driver_scheduled"), "system-reboot");
                    break;

                case GpuSwitchResult.Deferred:
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_eco_after_reboot"), "system-reboot");
                    break;

                case GpuSwitchResult.Failed:
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_switch_failed"), "dialog-error");
                    break;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                MainWindowInstance?.RefreshGpuModePublic());
        });
    }

    /// <summary>Debounce guard, skip power events within 3 seconds of the last one.</summary>
    private long _lastPowerChangeMs;

    /// <summary>
    /// Handle power state change (AC plugged/unplugged).
    /// Triggers auto GPU mode switch and auto performance mode.
    /// </summary>
    private void OnPowerStateChanged(bool onAc)
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (Math.Abs(now - _lastPowerChangeMs) < 3000)
            return;
        _lastPowerChangeMs = now;

        Logger.WriteLine($"Power state changed: AC={onAc}");

        // Auto GPU mode (Optimized = auto Eco/Standard based on AC power)
        // Run on background thread - SetGpuEco can block for 30-60 seconds
        Task.Run(() =>
        {
            if (GpuModeCtrl != null)
            {
                var result = GpuModeCtrl.AutoGpuSwitch();
                if (result == GpuSwitchResult.Applied)
                {
                    string msg = onAc
                        ? Labels.Get("gpu_optimized_ac")
                        : Labels.Get("gpu_optimized_battery");
                    System?.ShowNotification(Labels.Get("gpu_mode"), msg, "video-display");
                }
                else if (result == GpuSwitchResult.DriverBlocking)
                {
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_driver_staying"), "dialog-warning");
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                MainWindowInstance?.RefreshGpuModePublic();
                MainWindowInstance?.RefreshBatteryPublic();
            });
        });

        // Auto performance mode (if configured)
        Mode?.AutoPerformance(powerChanged: true);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            MainWindowInstance?.RefreshPerformanceMode());

        // Auto screen refresh rate (if configured)
        AutoScreen();

        // Apply AC- vs battery-specific keyboard backlight level (if configured).
        try
        {
            USB.Aura.ApplyConfiguredBrightness("PowerChange");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                MainWindowInstance?.RefreshExtraKeyboardBrightness());
        }
        catch (Exception ex)
        {
            Logger.WriteLine("ApplyConfiguredBrightness failed", ex);
        }

        try
        { USB.XGM.InitLight(); }
        catch (Exception ex) { Logger.WriteLine($"XGM.InitLight on power change failed: {ex.Message}"); }

        // Blocking systemctl call - keep it off the caller's thread.
        Task.Run(() =>
        {
            try
            { Gpu.GPUModeControl.ApplyNvidiaPowerdPolicy(onAc); }
            catch (Exception ex) { Logger.WriteLine($"ApplyNvidiaPowerdPolicy failed: {ex.Message}"); }
        });

        OptimalBrightness.OnPowerStateChanged();

        AnimeMatrix?.SetBatteryAuto();
    }

    /// <summary>
    /// Re-apply settings after system suspend resume. Some firmware resets
    /// the battery charge limit and some HID device modes drop across suspend.
    /// </summary>
    private void OnSystemResumed()
    {
        try
        {
            Battery.BatteryControl.AutoBattery();
            AnimeMatrix?.SetDevice(true);

            // Restore panel overdrive from saved config.
            int savedOd = AppConfig.Get("panel_od", -1);
            if (savedOd >= 0)
                Wmi?.SetPanelOverdrive(savedOd == 1);

            // Restore keyboard brightness (firmware may reset on suspend).
            try
            { USB.Aura.ApplyConfiguredBrightness("Resume"); }
            catch { }

            // Firmware may drop M-key EC bindings across suspend.
            try
            { Task.Run(GHelper.Linux.Input.MKeyControl.ApplyAll); }
            catch { }

            if (AppConfig.IsLenovoDevice())
            {
                if (AppConfig.Is("lenovo_mic_boost_fix"))
                    Task.Run(() => Platform.Linux.Lenovo.LenovoFeatures.ApplyMicBoostFix());

                if (USB.LenovoRgb.IsAvailable())
                    Task.Run(() => USB.LenovoRgb.Apply());
            }

            ReapplyMousePeripheralState();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Settings re-apply on resume failed", ex);
        }
    }

    /// <summary>
    /// <summary>
    /// When the monitor turns off (DPMS Off), the DE typically kills the
    /// keyboard backlight. If "keep backlight on" is enabled, re-light it.
    /// </summary>
    private void OnMonitorSlept()
    {
        if (!Helpers.AppConfig.Is("kb_keep_on"))
            return;
        try
        {
            bool onAc = Power?.IsOnAcPower() ?? true;
            int level = onAc
                ? Helpers.AppConfig.Get("keyboard_brightness_ac", -1)
                : Helpers.AppConfig.Get("keyboard_brightness", -1);
            if (level < 0)
                level = Helpers.AppConfig.Get("keyboard_brightness", -1);
            if (level < 0)
                level = Wmi?.KbdMaxBrightness ?? 3;
            if (level > 0)
            {
                Wmi?.SetKeyboardBrightness(level);
                Logger.WriteLine($"KeepOn: re-lit keyboard to {level} after DPMS Off");
            }
        }
        catch (Exception ex) { Logger.WriteLine("KeepOn: re-light on DPMS Off failed", ex); }
    }

    /// Re-apply mouse Hi-Res Scroll state when the display returns from DPMS Off.
    /// The HID++ flags survive normal idle, but firmware on some Logitech
    /// receivers drops the bits when the host stops polling for several minutes.
    /// </summary>
    private void OnMonitorWoke()
    {
        try
        { ReapplyMousePeripheralState(); }
        catch (Exception ex) { Logger.WriteLine("Mouse re-apply on monitor wake failed", ex); }

        // Re-light keyboard on wake too, in case the DE didn't restore it.
        if (Helpers.AppConfig.Is("kb_keep_on"))
        {
            try
            {
                bool onAc = Power?.IsOnAcPower() ?? true;
                int level = onAc
                    ? Helpers.AppConfig.Get("keyboard_brightness_ac", -1)
                    : Helpers.AppConfig.Get("keyboard_brightness", -1);
                if (level < 0)
                    level = Helpers.AppConfig.Get("keyboard_brightness", -1);
                if (level < 0)
                    level = Wmi?.KbdMaxBrightness ?? 3;
                if (level > 0)
                {
                    Wmi?.SetKeyboardBrightness(level);
                    Logger.WriteLine($"KeepOn: re-lit keyboard to {level} after DPMS On");
                }
            }
            catch (Exception ex) { Logger.WriteLine("KeepOn: re-light on DPMS On failed", ex); }
        }
    }

    /// <summary>
    /// Re-apply saved settings to every connected mouse. For Logitech devices
    /// this restores all persisted settings (DPI, scroll, lighting, etc.)
    private System.Threading.Timer? _peripheralPollTimer;
    private int _peripheralPollRunning;

    /// from config. For other mice, re-applies Hi-Res Scroll flags only.
    /// Idempotent - safe to call on resume, monitor wake, or reconnect.
    /// </summary>
    private static void ReapplyMousePeripheralState()
    {
        foreach (var mouse in Peripherals.PeripheralsProvider.ConnectedMice.ToArray())
        {
            try
            {
                if (mouse is Peripherals.Logitech.LogitechMouse logi)
                    logi.ApplySavedSettings();
                else if (mouse.HasHiResScroll())
                    mouse.WriteHiResScroll();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Mouse re-apply failed for {mouse.GetDisplayName()}: {ex.Message}");
            }
        }
    }

    // Unix signal handlers for clean shutdown on SIGTERM/SIGINT (logout/reboot)
    private static List<PosixSignalRegistration>? _signalRegistrations;

    private void RegisterSignalHandlers(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        try
        {
            _signalRegistrations = new();

            // SIGTERM: sent by KDE/GNOME during logout/reboot
            _signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
            {
                Logger.WriteLine("Received SIGTERM - initiating shutdown");
                ShutdownFromSignal(desktop);
            }));

            // SIGINT: Ctrl+C in terminal
            _signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, _ =>
            {
                Logger.WriteLine("Received SIGINT - initiating shutdown");
                ShutdownFromSignal(desktop);
            }));

            Logger.WriteLine("Unix signal handlers registered (SIGTERM, SIGINT)");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Failed to register signal handlers: {ex.Message}");
        }
    }

    private static void DisposeTrayIcons()
    {
        try
        { TraySystemMonitor.Stop(); }
        catch { }
        try
        {
            if (TrayIconInstance != null)
            {
                TrayIconInstance.IsVisible = false;
                TrayIconInstance.Dispose();
                TrayIconInstance = null;
            }
        }
        catch { }
    }

    private void ShutdownFromSignal(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Signal handler runs on a threadpool thread.
        // Don't rely on UI thread - it may already be blocked during session shutdown.
        Logger.WriteLine("Signal shutdown: cleaning up...");

        DisposeTrayIcons();

        // Best-effort: apply pending Eco mode before shutdown
        // (system is going down - display stack is closing, driver may be releasing)
        try
        { GpuModeCtrl?.ApplyPendingOnShutdown(); }
        catch { }

        try
        { USB.AsusLampArray.Release(); }
        catch { }

        try
        { Power?.StopPowerMonitoring(); }
        catch { }
        try
        { _peripheralPollTimer?.Dispose(); }
        catch { }
        try
        { UI.Views.ExtraWindow.StopClamshellInhibit(); }
        catch { }
        try
        { FnLock?.Stop(); }
        catch { }
        try
        { GHelper.Linux.Input.NumberPad.Stop(); }
        catch { }
        try
        { GHelper.Linux.Fan.ManualFanService.Stop(); }
        catch { }
        try
        { Input?.Dispose(); }
        catch { }
        try
        { Wmi?.Dispose(); }
        catch { }

        Logger.WriteLine("Signal shutdown: exiting process");
        Environment.Exit(0);
    }

    private void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Defensive: ensure Closing handlers see the shutdown flag before
        // desktop.Shutdown() walks open windows. ShutdownRequested also sets
        // this, but setting it here covers any path that calls Shutdown()
        // directly without going through Avalonia's request flow.
        IsShuttingDown = true;
        Logger.WriteLine("Shutting down...");

        DisposeTrayIcons();

        // Cleanup
        try
        { USB.AsusLampArray.Release(); }
        catch { }
        Power?.StopPowerMonitoring();
        _peripheralPollTimer?.Dispose();
        UI.Views.ExtraWindow.StopClamshellInhibit();
        FnLock?.Stop();
        OskWindowInstance?.ShutdownDevice();
        CommandIpc.Stop();
        GHelper.Linux.Input.GamepadNav.Stop();
        AnimeMatrix?.Dispose();
        GHelper.Linux.Input.NumberPad.Stop();
        GHelper.Linux.Fan.ManualFanService.Stop();
        Platform.Linux.StatusLed.Shutdown();
        Input?.Dispose();
        Wmi?.Dispose();

        desktop.Shutdown();
    }

    /// <summary>
    /// Starts or stops the remapper and refreshes MainWindow button + tray menu header.
    /// State is held entirely in <see cref="FnLock"/>.IsActive - there is no
    /// persisted enable flag, matching Windows g-helper's "off by default"
    /// </summary>
    public static void SetFnLockEnabled(bool enabled)
    {
        if (enabled)
            StartFnLock();
        else
            StopFnLock();
        RefreshTrayFnLockHeader();
    }

    /// <summary>
    /// Re-render the tray menu's fn-lock header (checkmark + label) so it
    /// stays in sync with the current state. Safe to call from any thread;
    /// marshals to UI thread internally. No-op if the menu has not been built.
    /// </summary>
    public static void RefreshTrayFnLockHeader()
    {
        var item = _trayFnLockItem;
        if (item == null)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            item.Header = ((FnLock?.IsActive ?? false) && (FnLock?.FnLockOn ?? false) ? "✓ " : "   ")
                          + Labels.Get("fnlock_tray_label");
        });
    }

    /// <summary>
    /// Start the software fn-lock remapper. Reads saved hotkey from config
    /// and wires OSD/tray refresh callbacks. Idempotent; returns early if
    /// already running.
    /// </summary>
    public static void StartFnLock()
    {
        if (FnLock != null && FnLock.IsActive)
            return;

        if (FnLock == null)
        {
            FnLock = new FnLockRemapper();
            FnLock.FnLockChanged += isOn =>
            {
                string title = Labels.Get("fnlock_tray_label");
                string body = isOn ? Labels.Get("fnlock_osd_on") : Labels.Get("fnlock_osd_off");
                System?.ShowNotification(title, body, "preferences-desktop-keyboard");
            };
            // Update the MainWindow quick-toggle button + tray menu header on
            // every state change (covers hotkey toggles + external triggers).
            FnLock.FnLockChanged += _ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    MainWindowInstance?.RefreshFnLockButton());
            FnLock.FnLockChanged += _ => RefreshTrayFnLockHeader();
        }

        // Pre-flight capability check FIRST so a failure path doesn't fire
        // FnLockChanged (which would briefly show "FN-Lock: ON" toast + blue
        // button before being undone by the failure path below).
        var (avail, reason) = FnLockRemapper.CheckCapability();
        if (!avail)
        {
            Helpers.Logger.WriteLine($"FnLockRemapper: capability check failed - {reason}");
            System?.ShowNotification(
                Labels.Get("fnlock_tray_label"),
                reason,
                "dialog-warning");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                MainWindowInstance?.RefreshFnLockButton());
            RefreshTrayFnLockHeader();
            return;
        }

        // User clicked to turn fn-lock ON, so start in media-keys mode. The
        // remapper boolean defaults to false at construction; we set it
        // explicitly here so the OSD/tray reflect ON immediately on first start.
        FnLock.FnLockOn = true;
        FnLock.SetToggleHotkey(
            (ushort)AppConfig.Get("fnlock_modifier", EvdevInterop.KEY_LEFTMETA),
            (ushort)AppConfig.Get("fnlock_key", EvdevInterop.KEY_F2));

        // Catch post-capability-check failures (UI_DEV_CREATE rejected, no
        // devices grabbed, pipe() failed). Without this guard the UI would
        // visually claim "ON" while no remapping actually happens.
        if (!FnLock.Start())
        {
            Helpers.Logger.WriteLine("FnLockRemapper: Start failed - check log for details");
            // Reset the in-memory flag so RefreshFnLockButton renders OFF.
            FnLock.FnLockOn = false;
            System?.ShowNotification(
                Labels.Get("fnlock_tray_label"),
                Labels.Get("fnlock_unavail_start_failed"),
                "dialog-warning");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                MainWindowInstance?.RefreshFnLockButton());
            RefreshTrayFnLockHeader();
            return;
        }

        // Make sure the main-window button + tray menu header show up immediately.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            MainWindowInstance?.RefreshFnLockButton());
        RefreshTrayFnLockHeader();
    }

    /// <summary>Stop and release the fn-lock remapper. Idempotent.</summary>
    public static void StopFnLock()
    {
        FnLock?.Stop();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            MainWindowInstance?.RefreshFnLockButton());
        RefreshTrayFnLockHeader();
    }

    /// <summary>Stop and re-Start to pick up keymap or hotkey changes.</summary>
    public static void RestartFnLock()
    {
        StopFnLock();
        StartFnLock();
    }

    /// <summary>
    /// Try to acquire a single-instance lock file. Returns false if another
    /// instance is already running. Uses XDG_RUNTIME_DIR (per-user) to avoid
    /// multi-user conflicts. The OS releases the lock automatically on exit/crash.
    /// </summary>
    private static bool TryAcquireSingleInstanceLock()
    {
        string lockDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp";
        string lockPath = Path.Combine(lockDir, "ghelper.lock");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                if (attempt == 0)
                    Thread.Sleep(500); // retry once - covers app restart race
            }
        }
        return false;
    }
}
