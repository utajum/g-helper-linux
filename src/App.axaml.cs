using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using GHelper.Linux.Helpers;
using GHelper.Linux.Mode;
using GHelper.Linux.Platform;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.UI.Views;

namespace GHelper.Linux;

public class App : Application
{
    // Global service instances (mirrors G-Helper's Program.acpi pattern)
    public static IAsusWmi? Wmi { get; private set; }
    public static IPowerManager? Power { get; private set; }
    public static ISystemIntegration? System { get; private set; }
    public static IInputHandler? Input { get; private set; }
    public static IAudioControl? Audio { get; private set; }
    public static IDisplayControl? Display { get; private set; }

    // Business logic orchestrator
    public static ModeControl? Mode { get; private set; }

    public static MainWindow? MainWindowInstance { get; private set; }
    public static TrayIcon? TrayIconInstance { get; set; }

    // G-Helper WMI event codes (from original source)
    private const int EventPerformanceCycle = 174; // Fn+F5
    private const int EventKbBrightnessUp = 196;   // Fn+F3
    private const int EventKbBrightnessDown = 197;  // Fn+F2
    private const int EventRogKey = 56;             // ROG button

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize Linux platform backends
        InitializePlatform();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep running when window is closed (tray icon keeps app alive)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            MainWindowInstance = new MainWindow();

            // Show main window on startup (like Windows G-Helper)
            desktop.MainWindow = MainWindowInstance;

            // Set up tray icon (secondary access method)
            SetupTrayIcon(desktop);

            // Start hotkey listener
            StartHotkeyListener();

            // Apply saved performance mode on startup
            Mode?.SetPerformanceMode();

            // Update tray icon to match current mode
            UpdateTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializePlatform()
    {
        Wmi = new LinuxAsusWmi();
        Power = new LinuxPowerManager();
        System = new LinuxSystemIntegration();
        Input = new LinuxInputHandler();
        Audio = new LinuxAudioControl();
        Display = new LinuxDisplayControl();

        // Create mode controller (uses App.Wmi, App.Power, etc.)
        Mode = new ModeControl();

        Logger.WriteLine($"G-Helper Linux initialized");
        Logger.WriteLine($"Model: {System.GetModelName()}");
        Logger.WriteLine($"BIOS: {System.GetBiosVersion()}");

        // Log detected features
        LogFeatureDetection();
    }

    private void LogFeatureDetection()
    {
        var features = new[]
        {
            ("throttle_thermal_policy", "Performance modes"),
            ("dgpu_disable", "GPU Eco mode"),
            ("gpu_mux_mode", "MUX switch"),
            ("panel_od", "Panel overdrive"),
            ("mini_led_mode", "Mini LED"),
            ("ppt_pl1_spl", "PL1 power limit"),
            ("ppt_pl2_sppt", "PL2 power limit"),
            ("nv_dynamic_boost", "NVIDIA dynamic boost"),
            ("nv_temp_target", "NVIDIA temp target"),
        };

        foreach (var (attr, name) in features)
        {
            bool supported = Wmi?.IsFeatureSupported(attr) ?? false;
            Logger.WriteLine($"  {name}: {(supported ? "YES" : "no")}");
        }
    }

    private void StartHotkeyListener()
    {
        if (Input == null) return;

        Input.HotkeyPressed += OnHotkeyPressed;
        Input.StartListening();
    }

    private void OnHotkeyPressed(int eventCode)
    {
        Logger.WriteLine($"Hotkey event: {eventCode}");

        switch (eventCode)
        {
            case EventPerformanceCycle:
                // Fn+F5 — cycle performance modes
                Mode?.CyclePerformanceMode();
                UpdateTrayIcon();
                break;

            case EventKbBrightnessUp:
                // Fn+F3 — keyboard brightness up
                CycleKeyboardBrightness(up: true);
                break;

            case EventKbBrightnessDown:
                // Fn+F2 — keyboard brightness down
                CycleKeyboardBrightness(up: false);
                break;

            case EventRogKey:
                // ROG button — toggle main window
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ToggleMainWindow());
                break;
        }
    }

    private void CycleKeyboardBrightness(bool up)
    {
        int current = Wmi?.GetKeyboardBrightness() ?? 0;
        int next = up ? Math.Min(current + 1, 3) : Math.Max(current - 1, 0);
        Wmi?.SetKeyboardBrightness(next);
        System?.ShowNotification("G-Helper", $"Keyboard brightness: {next}");
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var trayIcon = new TrayIcon
        {
            ToolTipText = $"G-Helper — {Modes.GetCurrentName()}",
            IsVisible = true,
            Menu = CreateTrayMenu(desktop)
        };

        // Load tray icon from embedded assets
        try
        {
            var uri = new Uri("avares://ghelper-linux/UI/Assets/standard.ico");
            trayIcon.Icon = new WindowIcon(AssetLoader.Open(uri));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Could not load tray icon", ex);
        }

        trayIcon.Clicked += (_, _) => ToggleMainWindow();

        TrayIconInstance = trayIcon;
    }

    /// <summary>Update tray icon and tooltip to reflect current performance mode.</summary>
    public static void UpdateTrayIcon()
    {
        if (TrayIconInstance == null) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            int mode = Modes.GetCurrent();
            int baseMode = Modes.GetBase(mode);
            string name = Modes.GetName(mode);

            TrayIconInstance.ToolTipText = $"G-Helper — {name}";

            // Select icon based on base mode
            string iconName = baseMode switch
            {
                0 => "standard.ico",   // Balanced
                1 => "ultimate.ico",   // Turbo
                2 => "eco.ico",        // Silent
                _ => "standard.ico"
            };

            try
            {
                var uri = new Uri($"avares://ghelper-linux/UI/Assets/{iconName}");
                TrayIconInstance.Icon = new WindowIcon(AssetLoader.Open(uri));
            }
            catch
            {
                // Ignore — icon may not exist
            }
        });
    }

    private NativeMenu CreateTrayMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        // Performance modes
        var silent = new NativeMenuItem("Silent");
        silent.Click += (_, _) => { Mode?.SetPerformanceMode(2, true); UpdateTrayIcon(); };
        menu.Add(silent);

        var balanced = new NativeMenuItem("Balanced");
        balanced.Click += (_, _) => { Mode?.SetPerformanceMode(0, true); UpdateTrayIcon(); };
        menu.Add(balanced);

        var turbo = new NativeMenuItem("Turbo");
        turbo.Click += (_, _) => { Mode?.SetPerformanceMode(1, true); UpdateTrayIcon(); };
        menu.Add(turbo);

        menu.Add(new NativeMenuItemSeparator());

        // GPU modes
        var eco = new NativeMenuItem("Eco (iGPU only)");
        eco.Click += (_, _) => SetGpuMode(ecoEnabled: true);
        menu.Add(eco);

        var standard = new NativeMenuItem("Standard (dGPU)");
        standard.Click += (_, _) => SetGpuMode(ecoEnabled: false);
        menu.Add(standard);

        menu.Add(new NativeMenuItemSeparator());

        // Settings
        var settings = new NativeMenuItem("Settings");
        settings.Click += (_, _) => ToggleMainWindow();
        menu.Add(settings);

        menu.Add(new NativeMenuItemSeparator());

        // Quit
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            Shutdown(desktop);
        };
        menu.Add(quit);

        return menu;
    }

    private void ToggleMainWindow()
    {
        if (MainWindowInstance == null) return;

        if (MainWindowInstance.IsVisible)
        {
            MainWindowInstance.Hide();
        }
        else
        {
            MainWindowInstance.Show();
            MainWindowInstance.Activate();
        }
    }

    private void SetGpuMode(bool ecoEnabled)
    {
        Wmi?.SetGpuEco(ecoEnabled);
        string status = ecoEnabled ? "Eco (iGPU only)" : "Standard (dGPU)";
        Logger.WriteLine($"GPU mode: {status}");
        System?.ShowNotification("G-Helper", $"GPU: {status}");
    }

    private void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Logger.WriteLine("Shutting down...");

        // Cleanup
        Input?.Dispose();
        Wmi?.Dispose();

        desktop.Shutdown();
    }
}
