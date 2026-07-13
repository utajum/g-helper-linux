using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Fan curve editor and power limits window.
/// Linux port of G-Helper's Fans form.
/// </summary>
public partial class FansWindow : Window
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    private readonly DispatcherTimer _sensorTimer;
    private System.Timers.Timer? _plDebounce;
    private bool _updatingPLSliders;
    private bool _updatingUV;
    private bool _updatingAdvanced;
    private bool _updatingGpu;
    private bool _suppressModeCombo;
    private int _activeTab;

    public FansWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        // Wire up curve change events
        chartCPU.CurveChanged += (_, curve) => OnCurveChanged(0, curve);
        chartGPU.CurveChanged += (_, curve) => OnCurveChanged(1, curve);
        chartMid.CurveChanged += (_, curve) => OnCurveChanged(2, curve);
        chartXGM.CurveChanged += (_, curve) => OnCurveChanged(3, curve);

        _sensorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sensorTimer.Tick += (_, _) =>
        {
            RefreshSensors();
            if (++_dgpuProcessTick >= 4)
            {
                _dgpuProcessTick = 0;
                RefreshFansDgpuProcessCount();
                StartGpuBackgroundRefresh();
            }
        };

        Loaded += (_, _) =>
        {
            ApplyFanCurveCapability();
            InitModeCombo();
            LoadFanCurves();
            LoadPowerLimits();
            LoadRyzenPower();
            LoadUV();
            LoadAdvanced();
            LoadGpuTuning();
            RefreshBoostButton();
            RefreshSensors();
            RefreshGpuTabVisibility();
            RefreshFansDgpuProcessCount();
            ToggleNavigation(_activeTab);
            InitHysteresis();
            _sensorTimer.Start();
        };

        if (App.Mode != null)
            App.Mode.ModeApplied += OnModeApplied;

        Activated += (_, _) => RefreshGpuTabVisibility();

        Closing += (_, _) =>
        {
            _sensorTimer.Stop();
            if (App.Mode != null)
                App.Mode.ModeApplied -= OnModeApplied;
        };
    }

    /// <summary>Hide the fan-curve editor on hardware without a writable fan
    /// curve interface (Lenovo mainline kernels expose fan RPM but no curve
    /// control). Sensors, PPT sliders and the rest of the window stay.</summary>
    private void ApplyFanCurveCapability()
    {
        if (Helpers.AppConfig.IsAsusDevice())
            return;
        // Non-ASUS DMI with a writable ASUS curve hwmon (vendor override,
        // exotic kernels): keep the editor, capability beats vendor.
        if (Platform.Linux.SysfsHelper.FindHwmonByName("asus_custom_fan_curve") != null)
            return;

        chartGrid.IsVisible = false;
        buttonApplyFans.IsVisible = false;
        buttonReset.IsVisible = false;
        buttonDisable.IsVisible = false;
        checkApplyFans.IsVisible = false;
        headerFanCurves.Text = Labels.Get("fans_power");
        MinHeight = 0;
        Height = 480;

        InitLenovoFanTargets();
    }

    //  Lenovo manual fan target RPM (lenovo_wmi_other hwmon, kernel 7.0+) 

    private readonly List<(int Fan, Slider Slider, TextBlock Value)> _lenovoFanSliders = new();

    private void InitLenovoFanTargets()
    {
        if (!Platform.Linux.Lenovo.LenovoDetection.HasFanTarget())
            return;

        string[] names = { "CPU Fan", "GPU Fan", "Fan 3", "Fan 4" };
        bool anyFan = false;

        for (int fan = 1; fan <= 4; fan++)
        {
            if (Platform.Linux.Lenovo.LenovoSysfs.FanTargetPath(fan) == null)
                continue;
            var range = Platform.Linux.Lenovo.LenovoSysfs.FanTargetRange(fan);
            if (range == null)
                continue;

            var (min, max, div) = range.Value;
            // One slider step below min = "Auto" (writes 0).
            double sliderMin = Math.Max(0, min - div);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("110,*,90"),
                Margin = new Avalonia.Thickness(0, 2),
            };
            var label = new TextBlock
            {
                Text = names[fan - 1],
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            label.Classes.Add("label-dim");
            Grid.SetColumn(label, 0);

            var slider = new Slider
            {
                Minimum = sliderMin,
                Maximum = max,
                TickFrequency = div,
                IsSnapToTickEnabled = true,
            };
            Grid.SetColumn(slider, 1);

            var value = new TextBlock
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            value.Classes.Add("value");
            Grid.SetColumn(value, 2);

            int target = Platform.Linux.Lenovo.LenovoFeatures.GetFanTarget(fan);
            slider.Value = target <= 0 ? sliderMin : Math.Clamp(target, min, max);
            value.Text = target <= 0 ? "Auto" : $"{target} RPM";

            int fanNum = fan; // capture
            double lastApplied = slider.Value;
            slider.ValueChanged += (_, args) =>
            {
                if (Math.Abs(args.NewValue - lastApplied) < div / 2.0)
                    return;
                lastApplied = args.NewValue;
                bool auto = args.NewValue < min;
                int rpm = auto ? 0 : (int)args.NewValue;
                value.Text = auto ? "Auto" : $"{rpm} RPM";
                Task.Run(() => Platform.Linux.Lenovo.LenovoFeatures.SetFanTarget(fanNum, rpm));
            };

            grid.Children.Add(label);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            stackLenovoFanTarget.Children.Add(grid);
            _lenovoFanSliders.Add((fan, slider, value));
            anyFan = true;
        }

        panelLenovoFanTarget.IsVisible = anyFan;
        if (anyFan)
            Helpers.Logger.WriteLine("FansWindow: Lenovo manual fan-target sliders enabled");
    }

    private void RefreshGpuTabVisibility()
    {
        bool shouldShow = App.GpuModeCtrl?.GetCurrentMode() != Gpu.GpuMode.Eco;
        bool wasNull = App.GpuControl == null;

        if (shouldShow && wasNull)
            App.RefreshGpuControlIfMissing();

        bool gainedGpu = wasNull && App.GpuControl?.IsAvailable() == true;

        buttonTabGpu.IsVisible = shouldShow;
        if (!buttonTabGpu.IsVisible && _activeTab == 1)
            ToggleNavigation(0);

        if (gainedGpu)
            LoadGpuTuning();

        RefreshFansDgpuProcessCount();
    }

    private int _dgpuProcessTick;
    private NvidiaProcessesWindow? _nvidiaProcessesWindow;

    private bool _dgpuCountBusy;

    private async void RefreshFansDgpuProcessCount()
    {
        if (_activeTab != 1)
        {
            rowFansDgpuProcesses.IsVisible = false;
            return;
        }
        // Skip the privileged holder scan while a GPU switch is running
        // (driver teardown/bringup); the count is transient and the scan
        // would contend with the switch.
        if (App.GpuModeCtrl?.IsSwitchInProgress == true)
            return;
        if (App.GpuModeCtrl?.GetCurrentMode() == Gpu.GpuMode.Eco)
        {
            rowFansDgpuProcesses.IsVisible = false;
            return;
        }
        // CountHolders spawns the privileged helper (or walks /proc): keep
        // it off the UI thread so opening the GPU tab never stutters.
        if (_dgpuCountBusy)
            return;
        _dgpuCountBusy = true;
        try
        {
            int count = await Task.Run(Gpu.NVidia.NvidiaProcessScanner.CountHolders);
            labelFansDgpuProcessCount.Text = Labels.Format("gpu_dgpu_users_count", count);
            labelFansViewDgpuProcesses.Text = Labels.Get("gpu_dgpu_users_view");
            rowFansDgpuProcesses.IsVisible = true;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"FansWindow: dGPU holder count failed: {ex.Message}");
        }
        finally
        {
            _dgpuCountBusy = false;
        }
    }

    private void ButtonFansViewDgpuProcesses_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_nvidiaProcessesWindow == null || !_nvidiaProcessesWindow.IsVisible)
        {
            _nvidiaProcessesWindow = new NvidiaProcessesWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _nvidiaProcessesWindow.Topmost = true;
            Helpers.WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_nvidiaProcessesWindow);
            _nvidiaProcessesWindow.Show();
        }
        else
        {
            _nvidiaProcessesWindow.Activate();
        }
    }

    public void RefreshGpuPublic()
    {
        RefreshGpuTabVisibility();
        if (buttonTabGpu.IsVisible)
            LoadGpuTuning();
    }

    private void OnModeApplied(int mode)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                InitModeCombo();
                LoadFanCurves();
                LoadPowerLimits();
                LoadRyzenPower();
                LoadUV();
                LoadAdvanced();
                LoadGpuTuning();
                RefreshBoostButton();
                LoadHysteresisValues();
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("FansWindow OnModeApplied refresh failed", ex);
            }
        });
    }

    // Fan hysteresis (raw WMI only)

    private bool _updatingHysteresis;
    private System.Timers.Timer? _hysteresisDebounce;
    private (int up, int down) _hysteresisDefaults = (-1, -1);

    private static readonly string[] HysteresisLabels =
        { "Very Low", "Low", "Medium", "High", "Very High" };

    /// <summary>Probe the device once on window load (one privileged call,
    /// background thread). Panel stays hidden when raw_wmi is off or the
    /// device does not respond.</summary>
    private void InitHysteresis()
    {
        if (!Fan.FanHysteresis.IsChannelAvailable())
        {
            panelHysteresis.IsVisible = false;
            return;
        }

        Task.Run(() =>
        {
            var defaults = Fan.FanHysteresis.Get();
            Dispatcher.UIThread.Post(() =>
            {
                _hysteresisDefaults = defaults;
                if (defaults.up < 0 || defaults.down < 0)
                {
                    panelHysteresis.IsVisible = false;
                    return;
                }

                panelHysteresis.IsVisible = true;
                LoadHysteresisValues();
            });
        });
    }

    /// <summary>Sync sliders to the saved per-mode values (or hardware defaults).</summary>
    private void LoadHysteresisValues()
    {
        if (!panelHysteresis.IsVisible)
            return;

        int up = Helpers.AppConfig.GetMode("hysteresis_up");
        int down = Helpers.AppConfig.GetMode("hysteresis_down");

        if (up < 0)
            up = _hysteresisDefaults.up > 0 ? _hysteresisDefaults.up : 3;
        if (down < 0)
            down = _hysteresisDefaults.down > 0 ? _hysteresisDefaults.down : 3;

        _updatingHysteresis = true;
        sliderHysteresisUp.Value = Math.Clamp(up, Fan.FanHysteresis.Min, Fan.FanHysteresis.Max);
        sliderHysteresisDown.Value = Math.Clamp(down, Fan.FanHysteresis.Min, Fan.FanHysteresis.Max);
        VisualiseHysteresis();
        _updatingHysteresis = false;
    }

    private void VisualiseHysteresis()
    {
        labelHysteresisUpValue.Text = HysteresisLabels[(int)sliderHysteresisUp.Value - 1];
        labelHysteresisDownValue.Text = HysteresisLabels[(int)sliderHysteresisDown.Value - 1];
    }

    private void SliderHysteresis_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Setting Minimum coerces the value during XAML load, before named
        // controls are populated; ignore events until the window is ready.
        if (_updatingHysteresis || labelHysteresisUpValue is null || labelHysteresisDownValue is null)
            return;

        VisualiseHysteresis();
        Helpers.AppConfig.SetMode("hysteresis_up", (int)sliderHysteresisUp.Value);
        Helpers.AppConfig.SetMode("hysteresis_down", (int)sliderHysteresisDown.Value);

        // Debounce the privileged write until the user stops dragging.
        _hysteresisDebounce?.Stop();
        _hysteresisDebounce ??= new System.Timers.Timer(500) { AutoReset = false };
        _hysteresisDebounce.Elapsed -= HysteresisDebounce_Elapsed;
        _hysteresisDebounce.Elapsed += HysteresisDebounce_Elapsed;
        _hysteresisDebounce.Start();
    }

    private void HysteresisDebounce_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        int up = Helpers.AppConfig.GetMode("hysteresis_up");
        int down = Helpers.AppConfig.GetMode("hysteresis_down");
        if (up > 0 && down > 0)
            Fan.FanHysteresis.Set(up, down);
    }

    private void ToggleNavigation(int index)
    {
        _activeTab = Math.Clamp(index, 0, 2);
        panelCpu.IsVisible = _activeTab == 0;
        panelGpu.IsVisible = _activeTab == 1;
        panelAdvanced.IsVisible = _activeTab == 2;

        // Entering the GPU tab: refresh the holder count + live status now
        if (_activeTab == 1)
        {
            RefreshFansDgpuProcessCount();
            StartGpuBackgroundRefresh();
        }
    }

    private void ButtonTabCpu_Click(object? sender, RoutedEventArgs e) => ToggleNavigation(0);
    private void ButtonTabGpu_Click(object? sender, RoutedEventArgs e) => ToggleNavigation(1);
    private void ButtonTabAdvanced_Click(object? sender, RoutedEventArgs e) => ToggleNavigation(2);

    /// <summary>Prefetch the nvidia-smi/NVML-backed values on a worker (each
    /// call forks; 2-3 of them used to stutter the window open on NVIDIA
    /// machines), then build the rows on the UI thread. Prefetching
    /// GetCapabilities also warms the nvml-info cache LoadClockOffsetRows
    /// reads.</summary>
    private async void LoadGpuTuning()
    {
        var nv = App.GpuControl as Gpu.NVidia.LinuxNvidiaGpuControl;
        bool nvAvail = nv?.IsAvailable() == true;

        (Gpu.NVidia.LinuxNvidiaGpuControl.GpuCapabilities caps,
         (int core, int mem)? maxClocks,
         (int defaultW, int minW, int maxW, int enforcedW)? limits,
         string? name)? pre = null;
        if (nvAvail)
        {
            try
            {
                pre = await Task.Run(() =>
                    (nv!.GetCapabilities(), nv.GetMaxClocks(), nv.GetPowerLimits(), nv.GetGpuName()));
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"FansWindow: GPU tuning prefetch failed: {ex.Message}");
                nvAvail = false;
            }
        }
        LoadGpuTuningCore(nv, nvAvail, pre);
    }

    private void LoadGpuTuningCore(
        Gpu.NVidia.LinuxNvidiaGpuControl? nv, bool nvAvail,
        (Gpu.NVidia.LinuxNvidiaGpuControl.GpuCapabilities caps,
         (int core, int mem)? maxClocks,
         (int defaultW, int minW, int maxW, int enforcedW)? limits,
         string? name)? pre)
    {
        _updatingGpu = true;
        try
        {
            var wmi = App.Wmi;

            // Auto-apply ON for this mode  -> show the saved (persisted) tuning.
            // OFF -> the mode runs stock, so show stock/off values.
            bool autoApply = Helpers.AppConfig.IsMode("auto_apply_gpu");
            checkApplyGpu.IsChecked = autoApply;

            LoadNvBaseTgpRow(wmi);
            LoadNvTgpRow(wmi);
            LoadGpuBoostRow(wmi);
            LoadGpuTempRow(wmi);
            BuildTunableRows(panelGpuAdvanced, _gpuExtraDefs, wmi);

            if (!nvAvail)
            {
                labelGpuTuningInfo.Text = Labels.Get("nvidia_gpu_not_detected");
                rowGpuPowerLim.IsVisible = false;
                rowGpuClockLock.IsVisible = false;
                rowGpuMemClockLock.IsVisible = false;
                rowGpuClockCore.IsVisible = false;
                rowGpuClockMem.IsVisible = false;
                labelGpuOcWarning.IsVisible = false;
                headerGpuLive.IsVisible = false;
                gridGpuLive.IsVisible = false;
                return;
            }
            labelGpuTuningInfo.Text = pre?.name ?? Labels.Get("nvidia_gpu");

            // Only show tuning rows the card actually supports.
            var caps = pre?.caps ?? new Gpu.NVidia.LinuxNvidiaGpuControl.GpuCapabilities(false, false, false, false);
            // Firmware TGP (ASUS dynamic boost or Lenovo cTGP/PPAB) arbitrates GPU
            // power, so nvidia-smi -pl is overridden - hide the (ineffective) row.
            bool dynamicBoost = wmi != null && (
                wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.NvDynamicBoost)
                || wmi.IsFeatureSupported(Platform.Linux.LenovoAttributes.GpuNvPpab)
                || wmi.IsFeatureSupported(Platform.Linux.LenovoAttributes.GpuNvCtgp));

            var maxClocks = pre?.maxClocks;
            if (maxClocks != null)
            {
                sliderGpuClockLock.Maximum = maxClocks.Value.core;
                sliderGpuMemClockLock.Maximum = maxClocks.Value.mem;
            }

            headerGpuLive.IsVisible = true;
            gridGpuLive.IsVisible = true;

            var limits = pre?.limits;
            if (caps.PowerLimit && !dynamicBoost && limits != null)
            {
                var (defW, minW, maxW, _) = limits.Value;
                sliderGpuPowerLim.Minimum = minW;
                sliderGpuPowerLim.Maximum = maxW;
                // Persistence on -> saved cap; off -> the stable default_limit.
                // (Never the live enforced limit - Dynamic Boost makes it fluctuate.)
                int savedPower = autoApply ? Helpers.AppConfig.GetMode("gpu_power_lim", -1) : -1;
                double powerVal = savedPower > 0 ? savedPower : defW;
                sliderGpuPowerLim.Value = Math.Clamp(powerVal, minW, maxW);
                labelGpuPowerLim.Text = $"{(int)sliderGpuPowerLim.Value}W";
                rowGpuPowerLim.IsVisible = true;
            }
            else
                rowGpuPowerLim.IsVisible = false;

            // GPU clock lock - only if the card supports locked graphics clocks.
            bool gpuLockOk = caps.GpuClockLock && (maxClocks?.core ?? 0) > 0;
            rowGpuClockLock.IsVisible = gpuLockOk;
            if (gpuLockOk)
            {
                int savedLock = autoApply ? Helpers.AppConfig.GetMode("gpu_clock_lock", 0) : 0;
                checkGpuClockLock.IsChecked = savedLock > 0;
                sliderGpuClockLock.IsEnabled = savedLock > 0;
                if (savedLock > 0)
                {
                    sliderGpuClockLock.Value = Math.Clamp(savedLock, sliderGpuClockLock.Minimum, sliderGpuClockLock.Maximum);
                    labelGpuClockLock.Text = $"{(int)sliderGpuClockLock.Value} MHz";
                }
                else
                    labelGpuClockLock.Text = Labels.Get("off");
            }

            // VRAM clock lock - only if the card supports locked memory clocks.
            bool memLockOk = caps.MemClockLock && (maxClocks?.mem ?? 0) > 0;
            rowGpuMemClockLock.IsVisible = memLockOk;
            if (memLockOk)
            {
                int savedMemLock = autoApply ? Helpers.AppConfig.GetMode("gpu_mem_clock_lock", 0) : 0;
                checkGpuMemClockLock.IsChecked = savedMemLock > 0;
                sliderGpuMemClockLock.IsEnabled = savedMemLock > 0;
                if (savedMemLock > 0)
                {
                    sliderGpuMemClockLock.Value = Math.Clamp(savedMemLock, sliderGpuMemClockLock.Minimum, sliderGpuMemClockLock.Maximum);
                    labelGpuMemClockLock.Text = $"{(int)sliderGpuMemClockLock.Value} MHz";
                }
                else
                    labelGpuMemClockLock.Text = Labels.Get("off");
            }

            LoadClockOffsetRows(nv!, autoApply);
            StartGpuBackgroundRefresh();
        }
        finally { _updatingGpu = false; }
    }

    // Resolved GPU TGP attributes (ASUS nv_* or Lenovo gpu_nv_*), picked per hardware.
    private Platform.Linux.AttrDef? _attrNvTgp;
    private Platform.Linux.AttrDef? _attrGpuBoost;
    private Platform.Linux.AttrDef? _attrGpuTemp;

    /// <summary>First candidate attribute the active backend reports as supported.</summary>
    private static Platform.Linux.AttrDef? PickAttr(
        Platform.IHardwareControl? wmi, params Platform.Linux.AttrDef[] candidates)
    {
        if (wmi == null)
            return null;
        foreach (var a in candidates)
            if (wmi.IsFeatureSupported(a))
                return a;
        return null;
    }

    // Generic firmware-attribute tunable rows (Lenovo CPU/GPU extras), built in
    // code and gated by IsFeatureSupported + range. Technical labels (not i18n).
    private readonly List<(Platform.Linux.AttrDef Attr, Slider Slider, TextBlock Value, string Unit, string ConfigKey)> _advTunables = new();
    private System.Timers.Timer? _advDebounce;
    private bool _buildingTunables;

    private static readonly (Platform.Linux.AttrDef Attr, string Label, string Unit)[] _cpuExtraDefs =
    {
        (Platform.Linux.LenovoAttributes.PptApuSpl, "APU SPL", "W"),
        (Platform.Linux.LenovoAttributes.PptPl4Ipl, "Peak (PL4)", "W"),
        (Platform.Linux.LenovoAttributes.PptTau, "Tau", "s"),
        (Platform.Linux.LenovoAttributes.PptCpuCl, "CPU Cross-Load", "W"),
        (Platform.Linux.LenovoAttributes.PptPl1SplCl, "PL1 (CL)", "W"),
        (Platform.Linux.LenovoAttributes.PptPl2SpptCl, "PL2 (CL)", "W"),
        (Platform.Linux.LenovoAttributes.PptPl3FpptCl, "FPPT (CL)", "W"),
        (Platform.Linux.LenovoAttributes.PptPl4IplCl, "PL4 (CL)", "W"),
    };

    private static readonly (Platform.Linux.AttrDef Attr, string Label, string Unit)[] _gpuExtraDefs =
    {
        (Platform.Linux.LenovoAttributes.DgpuBoostClk, "Boost Clock", " MHz"),
        (Platform.Linux.LenovoAttributes.GpuNvCpuBoost, "CPU Boost", "W"),
        (Platform.Linux.LenovoAttributes.GpuNvAcOffset, "AC Offset", "W"),
        (Platform.Linux.LenovoAttributes.GpuNvBpl, "Battery Limit", "W"),
    };

    /// <summary>Build gated slider rows for a set of firmware-attribute tunables.</summary>
    private void BuildTunableRows(StackPanel panel,
        (Platform.Linux.AttrDef Attr, string Label, string Unit)[] defs,
        Platform.IHardwareControl? wmi)
    {
        // Drop previously built rows (keep the header at index 0) and their state.
        for (int i = panel.Children.Count - 1; i >= 1; i--)
            panel.Children.RemoveAt(i);
        _advTunables.RemoveAll(t =>
        {
            foreach (var d in defs)
                if (d.Attr.LegacyName == t.Attr.LegacyName)
                    return true;
            return false;
        });

        if (wmi == null)
        { panel.IsVisible = false; return; }

        _buildingTunables = true;
        bool any = false;
        foreach (var (attr, label, unit) in defs)
        {
            if (!wmi.IsFeatureSupported(attr))
                continue;
            var range = wmi.GetAttributeRange(attr);
            if (range == null)
                continue;
            int cur = wmi.GetPptLimit(attr);
            if (cur < 0)
                cur = range.Default >= 0 ? range.Default : range.Min;
            cur = Math.Clamp(cur, range.Min, range.Max);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("100,*,55"),
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            };
            var lbl = new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            lbl.Classes.Add("label-dim");
            var slider = new Slider
            {
                Minimum = range.Min,
                Maximum = range.Max,
                TickFrequency = Math.Max(1, range.Step),
                Value = cur,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var val = new TextBlock { Text = $"{cur}{unit}", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            val.Classes.Add("value");
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(val, 2);
            grid.Children.Add(lbl);
            grid.Children.Add(slider);
            grid.Children.Add(val);

            string u = unit;
            var vLabel = val;
            slider.ValueChanged += (_, e) =>
            {
                if (_buildingTunables)
                    return;
                vLabel.Text = $"{(int)e.NewValue}{u}";
                ScheduleAdvWrite();
            };
            panel.Children.Add(grid);
            _advTunables.Add((attr, slider, val, unit, "limit_" + attr.LegacyName));
            any = true;
        }
        _buildingTunables = false;
        panel.IsVisible = any;
    }

    /// <summary>Debounce advanced-tunable writes (300ms after last drag).</summary>
    private void ScheduleAdvWrite()
    {
        _advDebounce?.Stop();
        _advDebounce ??= new System.Timers.Timer(300) { AutoReset = false };
        _advDebounce.Elapsed -= AdvDebounce_Elapsed;
        _advDebounce.Elapsed += AdvDebounce_Elapsed;
        _advDebounce.Start();
    }

    private void AdvDebounce_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var wmi = App.Wmi;
            if (wmi == null)
                return;
            wmi.EnsureManualFanMode();
            foreach (var t in _advTunables)
            {
                int v = (int)t.Slider.Value;
                wmi.SetPptLimit(t.Attr, v);
                Helpers.AppConfig.SetMode(t.ConfigKey, v);
            }
        });
    }

    private void LoadNvBaseTgpRow(Platform.IHardwareControl? wmi)
    {
        if (wmi == null
            || !wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.NvBaseTgp))
        {
            rowNvBaseTgp.IsVisible = false;
            return;
        }
        var range = wmi.GetAttributeRange(Platform.Linux.AsusAttributes.NvBaseTgp);
        if (range == null)
        { rowNvBaseTgp.IsVisible = false; return; }
        int cur = wmi.GetPptLimit(Platform.Linux.AsusAttributes.NvBaseTgp);
        if (cur < 0)
            cur = range.Default;
        sliderNvBaseTgp.Minimum = range.Min;
        sliderNvBaseTgp.Maximum = range.Max;
        sliderNvBaseTgp.TickFrequency = Math.Max(1, range.Step);
        sliderNvBaseTgp.Value = Math.Clamp(cur, range.Min, range.Max);
        labelNvBaseTgp.Text = $"{(int)sliderNvBaseTgp.Value}W";
        rowNvBaseTgp.IsVisible = true;
    }

    private void LoadNvTgpRow(Platform.IHardwareControl? wmi)
    {
        // ASUS nv_tgp or Lenovo gpu_nv_ctgp (configurable TGP).
        _attrNvTgp = PickAttr(wmi, Platform.Linux.AsusAttributes.NvTgp,
            Platform.Linux.LenovoAttributes.GpuNvCtgp);
        if (_attrNvTgp == null)
        {
            rowNvTgp.IsVisible = false;
            return;
        }
        var range = wmi!.GetAttributeRange(_attrNvTgp);
        if (range == null)
        { rowNvTgp.IsVisible = false; return; }
        int cur = wmi.GetPptLimit(_attrNvTgp);
        if (cur < 0)
            cur = range.Default;
        sliderNvTgp.Minimum = range.Min;
        sliderNvTgp.Maximum = range.Max;
        sliderNvTgp.TickFrequency = Math.Max(1, range.Step);
        sliderNvTgp.Value = Math.Clamp(cur, range.Min, range.Max);
        labelNvTgp.Text = $"{(int)sliderNvTgp.Value}W";
        rowNvTgp.IsVisible = true;
    }

    private void LoadGpuBoostRow(Platform.IHardwareControl? wmi)
    {
        // ASUS nv_dynamic_boost or Lenovo gpu_nv_ppab (PPAB / dynamic boost).
        _attrGpuBoost = PickAttr(wmi, Platform.Linux.AsusAttributes.NvDynamicBoost,
            Platform.Linux.LenovoAttributes.GpuNvPpab);
        if (_attrGpuBoost == null)
        {
            rowGpuBoost.IsVisible = false;
            return;
        }
        var range = wmi!.GetAttributeRange(_attrGpuBoost);
        int min = range?.Min >= 0 ? range.Min : 5;
        int max = range?.Max >= 0 ? range.Max : 25;
        int cur = wmi.GetPptLimit(_attrGpuBoost);
        if (cur < 0)
            cur = Helpers.AppConfig.GetMode("gpu_boost", min);
        sliderGpuBoost.Minimum = min;
        sliderGpuBoost.Maximum = max;
        sliderGpuBoost.TickFrequency = Math.Max(1, range?.Step ?? 5);
        sliderGpuBoost.Value = Math.Clamp(cur, min, max);
        labelGpuBoost.Text = $"{(int)sliderGpuBoost.Value}W";
        rowGpuBoost.IsVisible = true;
    }

    private void LoadGpuTempRow(Platform.IHardwareControl? wmi)
    {
        // ASUS nv_temp_target or Lenovo gpu_temp (GPU temperature target).
        _attrGpuTemp = PickAttr(wmi, Platform.Linux.AsusAttributes.NvTempTarget,
            Platform.Linux.LenovoAttributes.GpuTemp);
        if (_attrGpuTemp == null)
        {
            rowGpuTemp.IsVisible = false;
            return;
        }
        var range = wmi!.GetAttributeRange(_attrGpuTemp);
        int min = range?.Min >= 0 ? range.Min : 75;
        int max = range?.Max >= 0 ? range.Max : 87;
        int cur = wmi.GetPptLimit(_attrGpuTemp);
        if (cur < 0)
            cur = Helpers.AppConfig.GetMode("gpu_temp", max);
        sliderGpuTemp.Minimum = min;
        sliderGpuTemp.Maximum = max;
        sliderGpuTemp.Value = Math.Clamp(cur, min, max);
        labelGpuTemp.Text = $"{(int)sliderGpuTemp.Value}\u00B0C";
        rowGpuTemp.IsVisible = true;
    }

    private void LoadClockOffsetRows(Gpu.NVidia.LinuxNvidiaGpuControl nv, bool fromConfig)
    {
        if (!nv.IsClockOffsetSupported())
        {
            rowGpuClockCore.IsVisible = false;
            rowGpuClockMem.IsVisible = false;
            labelGpuOcWarning.IsVisible = false;
            return;
        }
        var coreR = nv.GetCoreOffsetRange();
        var memR = nv.GetMemOffsetRange();
        if (coreR != null)
        {
            sliderGpuClockCore.Minimum = coreR.Value.min;
            sliderGpuClockCore.Maximum = coreR.Value.max;
        }
        if (memR != null)
        {
            sliderGpuClockMem.Minimum = memR.Value.min;
            sliderGpuClockMem.Maximum = memR.Value.max;
        }
        // Persistence on -> saved offsets; off -> stock (0).
        int core, mem;
        if (fromConfig)
        {
            core = Helpers.AppConfig.GetMode("gpu_clock_core", 0);
            mem = Helpers.AppConfig.GetMode("gpu_clock_mem", 0);
        }
        else
        {
            var cur = nv.GetClockOffsets();
            core = cur?.core ?? 0;
            mem = cur?.mem ?? 0;
        }
        core = (int)Math.Clamp(core, sliderGpuClockCore.Minimum, sliderGpuClockCore.Maximum);
        mem = (int)Math.Clamp(mem, sliderGpuClockMem.Minimum, sliderGpuClockMem.Maximum);
        sliderGpuClockCore.Value = core;
        sliderGpuClockMem.Value = mem;
        labelGpuClockCore.Text = $"{core} MHz";
        labelGpuClockMem.Text = $"{mem} MHz";
        rowGpuClockCore.IsVisible = true;
        rowGpuClockMem.IsVisible = true;
        labelGpuOcWarning.Text = Labels.Get("gpu_oc_warning");
        labelGpuOcWarning.IsVisible = true;
    }

    // Monitor

    private MonitorWindow? _monitorWindow;

    private void ButtonMonitor_Click(object? sender, RoutedEventArgs e)
    {
        if (_monitorWindow == null || !_monitorWindow.IsVisible)
        {
            _monitorWindow = new MonitorWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _monitorWindow.Topmost = true;
            Helpers.WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_monitorWindow);
            _monitorWindow.Show();
        }
        else
        {
            _monitorWindow.Activate();
        }
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("fans_title");
        headerFanCurves.Text = Labels.Get("fan_curves");
        InitModeCombo();
        labelMonitorButton.Text = Labels.Get("monitor_button");
        buttonApplyFans.Content = Labels.Get("apply");
        buttonReset.Content = Labels.Get("reset");
        buttonDisable.Content = Labels.Get("disable");
        checkApplyFans.Content = Labels.Get("auto_apply");
        Avalonia.Controls.ToolTip.SetTip(checkApplyFans, Labels.Get("auto_apply_fans_tooltip"));
        headerPowerLimits.Text = Labels.Get("power_limits");
        labelPL1Label.Text = Labels.Get("cpu_pl1");
        labelPL2Label.Text = Labels.Get("cpu_pl2");
        labelFpptLabel.Text = Labels.Get("cpu_fppt");
        labelCpuBoostLabel.Text = Labels.Get("cpu_boost");
        buttonBoostOff.Content = Labels.Get("off");
        buttonBoostOn.Content = Labels.Get("on");
        checkApplyPower.Content = Labels.Get("auto_apply_power_limits");
        Avalonia.Controls.ToolTip.SetTip(checkApplyPower, Labels.Get("auto_apply_power_tooltip"));
        checkApplyGpu.Content = Labels.Get("auto_apply_gpu_settings");
        Avalonia.Controls.ToolTip.SetTip(checkApplyGpu, Labels.Get("auto_apply_gpu_tooltip"));
        chartCPU.FanLabel = Labels.Get("cpu_fan");
        chartGPU.FanLabel = Labels.Get("gpu_fan");
        chartMid.FanLabel = Labels.Get("mid_fan");
        chartXGM.FanLabel = Labels.Get("xgm_fan");
        string dragHint = Labels.Get("fan_drag_all");
        Avalonia.Controls.ToolTip.SetTip(chartCPU, dragHint);
        Avalonia.Controls.ToolTip.SetTip(chartGPU, dragHint);
        Avalonia.Controls.ToolTip.SetTip(chartMid, dragHint);
        Avalonia.Controls.ToolTip.SetTip(chartXGM, dragHint);
        headerUndervolt.Text = Labels.Get("undervolt_header");
        labelUndervoltDesc.Text = Labels.Get("undervolt_desc");
        labelUndervoltCpu.Text = Labels.Get("undervolt_cpu");
        buttonApplyUV.Content = Labels.Get("apply");
        buttonResetUV.Content = Labels.Get("reset");
        checkApplyUV.Content = Labels.Get("undervolt_auto_apply");
        headerAdvanced.Text = Labels.Get("advanced_header");
        labelModeCmd.Text = Labels.Get("mode_command_label");
        labelModeCmdHint.Text = Labels.Get("mode_command_hint");
        labelReapply.Text = Labels.Get("reapply_power_label");
        labelReapplyUnit.Text = Labels.Get("reapply_power_unit");
        labelReapplyHint.Text = Labels.Get("reapply_power_hint");
        buttonTabCpu.Content = Labels.Get("tab_cpu");
        buttonTabGpu.Content = Labels.Get("tab_gpu");
        buttonTabAdvanced.Content = Labels.Get("tab_advanced");
        headerGpuTuning.Text = Labels.Get("gpu_tuning_header");
        labelNvBaseTgpLabel.Text = Labels.Get("gpu_base_tgp");
        labelNvTgpLabel.Text = Labels.Get("gpu_max_tgp");
        labelGpuBoostLabel.Text = Labels.Get("gpu_dynamic_boost");
        labelGpuTempLabel.Text = Labels.Get("gpu_temp_target");
        labelGpuPowerLimLabel.Text = Labels.Get("gpu_power_lim");
        labelGpuClockCoreLabel.Text = Labels.Get("gpu_clock_core_offset");
        labelGpuClockMemLabel.Text = Labels.Get("gpu_clock_mem_offset");
        checkGpuClockLock.Content = Labels.Get("gpu_clock_lock");
        checkGpuMemClockLock.Content = Labels.Get("gpu_mem_clock_lock");
        labelGpuHint.Text = Labels.Get("gpu_tuning_hint");
        buttonGpuApply.Content = Labels.Get("apply");
        buttonGpuReset.Content = Labels.Get("reset");
        headerGpuLive.Text = Labels.Get("gpu_status_header");
        labelLiveCoreKey.Text = Labels.Get("gpu_live_core");
        labelLiveMemKey.Text = Labels.Get("gpu_live_mem");
        labelLiveSmKey.Text = Labels.Get("gpu_live_sm");
        labelLiveTempKey.Text = Labels.Get("gpu_live_temp");
        labelLiveUtilKey.Text = Labels.Get("gpu_live_usage");
        labelLivePowerKey.Text = Labels.Get("gpu_live_power");
        labelLiveVramKey.Text = Labels.Get("gpu_live_vram");
        labelLivePstateKey.Text = Labels.Get("gpu_live_pstate");
        labelLiveOffsetsKey.Text = Labels.Get("gpu_live_offset");
        labelLiveThrottleKey.Text = Labels.Get("gpu_live_throttle");
        labelLivePcieKey.Text = Labels.Get("gpu_live_pcie");
        labelUndervoltIgpu.Text = Labels.Get("undervolt_igpu");
        labelCpuTempLabel.Text = Labels.Get("cpu_temp_target");
    }

    private void InitModeCombo()
    {
        _suppressModeCombo = true;
        try
        {
            int current = Mode.Modes.GetCurrent();
            comboMode.Items.Clear();
            int selectedIdx = 0;
            int idx = 0;
            foreach (var kv in Mode.Modes.GetDictionary())
            {
                comboMode.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = kv.Key });
                if (kv.Key == current)
                    selectedIdx = idx;
                idx++;
            }
            comboMode.SelectedIndex = selectedIdx;
        }
        finally
        {
            _suppressModeCombo = false;
        }
    }

    private void ComboMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressModeCombo)
            return;
        if (comboMode.SelectedItem is not ComboBoxItem item || item.Tag is not int modeId)
            return;
        if (modeId == Mode.Modes.GetCurrent())
            return;

        App.Mode?.SetPerformanceMode(modeId);
    }

    // Fan Curves

    private void LoadFanCurves()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        // Try reading current curves from hardware
        byte[]? cpuCurve = wmi.GetFanCurve(0);
        byte[]? gpuCurve = wmi.GetFanCurve(1);

        // Fall back to config or defaults if hardware returned no usable data
        if (!IsValidCurve(cpuCurve))
        {
            cpuCurve = Helpers.AppConfig.GetFanConfig(0);
            if (!IsValidCurve(cpuCurve))
                cpuCurve = Helpers.AppConfig.GetDefaultCurve(0);
        }

        if (!IsValidCurve(gpuCurve))
        {
            gpuCurve = Helpers.AppConfig.GetFanConfig(1);
            if (!IsValidCurve(gpuCurve))
                gpuCurve = Helpers.AppConfig.GetDefaultCurve(1);
        }

        chartCPU.CurveData = cpuCurve;
        chartGPU.CurveData = gpuCurve;

        // Mid fan detection - show chart if curve is valid or RPM is readable
        // (matches Windows G-Helper's InitFans logic)
        byte[]? midCurve = wmi.GetFanCurve(2);
        bool hasMidFan = IsValidCurve(midCurve) || wmi.GetFanRpm(2) > 0;

        if (hasMidFan)
        {
            if (!IsValidCurve(midCurve))
            {
                midCurve = Helpers.AppConfig.GetFanConfig(2);
                if (!IsValidCurve(midCurve))
                    midCurve = Helpers.AppConfig.GetDefaultCurve(2);
            }

            chartMid.CurveData = midCurve;
            chartMid.IsVisible = true;
            // Change third row from Auto to Star so all 3 charts share space equally
            chartGrid.RowDefinitions[2].Height = new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);
            // Keep in sync with the XAML Height so the GPU tab isn't truncated
            // when a third fan is present.
            this.Height = 800;

            Helpers.AppConfig.Set("mid_fan", 1);
        }
        else
        {
            Helpers.AppConfig.Set("mid_fan", 0);
        }

        bool xgmConnected = false;
        try
        { xgmConnected = USB.XGM.IsConnected(); }
        catch (Exception ex) { Helpers.Logger.WriteLine($"FansWindow.LoadFanCurves: XGM probe: {ex.Message}"); }

        if (xgmConnected)
        {
            byte[]? xgmCurve = Helpers.AppConfig.GetFanConfig(3);
            if (!IsValidCurve(xgmCurve))
                xgmCurve = Helpers.AppConfig.GetDefaultCurve(3);
            chartXGM.CurveData = xgmCurve;
            chartXGM.IsVisible = true;
        }
        else
        {
            chartXGM.IsVisible = false;
        }

        checkApplyFans.IsChecked = Helpers.AppConfig.IsMode("auto_apply_fans");

        UpdateDisabledState();
    }

    /// <summary>
    /// Clamp the PWM half of an XGM 16-byte curve to <see cref="XgmFanMaxPercent"/>
    /// before sending. The chart UI lets the user drag points up to 100% for
    /// consistency with the other charts; the dock firmware caps at 72% so we
    /// flatten anything above that on Apply.
    /// </summary>
    private const byte XgmFanMaxPercent = 72;

    private static byte[] ClampXgmCurve(byte[] curve)
    {
        if (curve == null || curve.Length != 16)
            return curve!;
        var clamped = new byte[16];
        Array.Copy(curve, clamped, 16);
        for (int i = 8; i < 16; i++)
        {
            if (clamped[i] > XgmFanMaxPercent)
                clamped[i] = XgmFanMaxPercent;
        }
        return clamped;
    }

    private void OnCurveChanged(int fanIndex, byte[] curve)
    {
        // Save to config
        Helpers.AppConfig.SetFanConfig(fanIndex, curve);

        // Auto-apply if enabled.
        if (checkApplyFans.IsChecked == true)
        {
            if (fanIndex == 3)
            {
                try
                { USB.XGM.SetFan(ClampXgmCurve(curve)); }
                catch (Exception ex) { Helpers.Logger.WriteLine($"XGM.SetFan: {ex.Message}"); }
            }
            else
            {
                App.Wmi?.SetFanCurve(fanIndex, curve);
            }
        }
    }

    private void ButtonApplyFans_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        if (chartCPU.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(0, chartCPU.CurveData);
            Helpers.AppConfig.SetFanConfig(0, chartCPU.CurveData);
        }

        if (chartGPU.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(1, chartGPU.CurveData);
            Helpers.AppConfig.SetFanConfig(1, chartGPU.CurveData);
        }

        if (chartMid.IsVisible && chartMid.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(2, chartMid.CurveData);
            Helpers.AppConfig.SetFanConfig(2, chartMid.CurveData);
        }

        if (chartXGM.IsVisible && chartXGM.CurveData is { Length: 16 })
        {
            var clamped = ClampXgmCurve(chartXGM.CurveData);
            try
            {
                USB.XGM.SetFan(clamped);
                Helpers.Logger.WriteLine($"XGM fan curve applied: {BitConverter.ToString(clamped)}");
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"XGM.SetFan: {ex.Message}");
            }
            Helpers.AppConfig.SetFanConfig(3, chartXGM.CurveData);
        }

        UpdateDisabledState();
        Helpers.Logger.WriteLine("Fan curves applied");
    }

    private void ButtonReset_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;

        // Phase 1: Reset ALL fans to factory defaults (pwm_enable=3).
        // Must do all resets before any re-apply because the kernel quirk
        // causes pwm_enable=3 on one fan to reset ALL fans.
        byte[]? cpuCurve = wmi?.ResetFanCurveToDefaults(0);
        byte[]? gpuCurve = wmi?.ResetFanCurveToDefaults(1);
        byte[]? midCurve = chartMid.IsVisible ? wmi?.ResetFanCurveToDefaults(2) : null;

        // Fall back to hardcoded defaults if kernel reset unsupported
        if (!IsValidCurve(cpuCurve))
            cpuCurve = Helpers.AppConfig.GetDefaultCurve(0);
        if (!IsValidCurve(gpuCurve))
            gpuCurve = Helpers.AppConfig.GetDefaultCurve(1);
        if (chartMid.IsVisible && !IsValidCurve(midCurve))
            midCurve = Helpers.AppConfig.GetDefaultCurve(2);

        // Phase 2: Update UI and save config
        chartCPU.CurveData = cpuCurve;
        chartGPU.CurveData = gpuCurve;
        Helpers.AppConfig.SetFanConfig(0, cpuCurve!);
        Helpers.AppConfig.SetFanConfig(1, gpuCurve!);

        if (chartMid.IsVisible)
        {
            chartMid.CurveData = midCurve;
            Helpers.AppConfig.SetFanConfig(2, midCurve!);
        }

        if (chartXGM.IsVisible)
        {
            try
            { USB.XGM.Reset(); }
            catch (Exception ex) { Helpers.Logger.WriteLine($"XGM.Reset: {ex.Message}"); }

            byte[] xgmCurve = Helpers.AppConfig.GetDefaultCurve(3);
            chartXGM.CurveData = xgmCurve;
            Helpers.AppConfig.SetFanConfig(3, xgmCurve);
        }

        // Phase 3: Re-apply ALL curves as active custom curves (pwm_enable=1).
        // Done after all resets so no subsequent pwm_enable=3 undoes them.
        if (cpuCurve is { Length: 16 })
            wmi?.SetFanCurve(0, cpuCurve);
        if (gpuCurve is { Length: 16 })
            wmi?.SetFanCurve(1, gpuCurve);
        if (chartMid.IsVisible && midCurve is { Length: 16 })
            wmi?.SetFanCurve(2, midCurve);

        UpdateDisabledState();
        Helpers.Logger.WriteLine("Fan curves reset to firmware defaults and re-applied");
    }

    private void ButtonDisable_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        wmi.DisableFanCurve(0);
        wmi.DisableFanCurve(1);
        if (chartMid.IsVisible)
            wmi.DisableFanCurve(2);
        UpdateDisabledState();

        Helpers.Logger.WriteLine("Custom fan curves disabled, using firmware defaults");
    }

    private void UpdateDisabledState()
    {
        var wmi = App.Wmi;
        bool cpuEnabled = wmi?.IsFanCurveEnabled(0) ?? false;
        bool gpuEnabled = wmi?.IsFanCurveEnabled(1) ?? false;
        bool midEnabled = !chartMid.IsVisible || (wmi?.IsFanCurveEnabled(2) ?? false);
        bool anyDisabled = !cpuEnabled || !gpuEnabled || !midEnabled;

        chartCPU.Disabled = !cpuEnabled;
        chartGPU.Disabled = !gpuEnabled;
        if (chartMid.IsVisible)
            chartMid.Disabled = !midEnabled;

        // Toggle button visual - accent border when disabled (active state)
        buttonDisable.BorderBrush = anyDisabled ? AccentBrush : TransparentBrush;
        buttonDisable.BorderThickness = new Avalonia.Thickness(2);
    }

    private void CheckApplyFans_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkApplyFans.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_fans", enabled ? 1 : 0);
    }

    private void CheckApplyPower_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkApplyPower.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_power", enabled ? 1 : 0);
    }

    private void CheckApplyGpu_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updatingGpu)
            return;
        bool enabled = checkApplyGpu.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_gpu", enabled ? 1 : 0);
    }

    // Power Limits

    private void LoadPowerLimits()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        _updatingPLSliders = true;

        int maxTotal = Mode.ModeControl.GetMaxTotal();

        // Seed sliders from saved config first. The hardware readback is only
        // a fallback because legacy ppt_* sysfs attributes read back a bogus
        // minimum (5) on some models (e.g. FX517ZR). Seeding sliders from that
        // value ends up persisting a 5W limit that cripples the machine.
        int pl1 = SanitizedLimit("limit_slow", maxTotal);
        if (pl1 <= 0)
        {
            pl1 = wmi.GetPptLimit(Platform.Linux.AsusAttributes.PptPl1Spl);
            if (pl1 <= Mode.ModeControl.MinTotal || pl1 > maxTotal)
                pl1 = Math.Min(maxTotal, (int)sliderPL1.Maximum);
        }

        int pl2 = SanitizedLimit("limit_fast", maxTotal);
        if (pl2 <= 0)
        {
            pl2 = wmi.GetPptLimit(Platform.Linux.AsusAttributes.PptPl2Sppt);
            if (pl2 <= Mode.ModeControl.MinTotal || pl2 > maxTotal)
                pl2 = Math.Min(maxTotal, (int)sliderPL2.Maximum);
        }

        sliderPL1.Value = pl1;
        labelPL1.Text = $"{pl1}W";

        sliderPL2.Value = pl2;
        labelPL2.Text = $"{pl2}W";

        // fPPT (fast boost) - only show if supported
        bool hasFppt = wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.PptFppt);
        gridFppt.IsVisible = hasFppt;
        if (hasFppt)
        {
            int fppt = SanitizedLimit("limit_fppt", maxTotal);
            if (fppt <= 0)
            {
                fppt = wmi.GetPptLimit(Platform.Linux.AsusAttributes.PptFppt);
                if (fppt <= Mode.ModeControl.MinTotal || fppt > maxTotal)
                    fppt = Math.Min(maxTotal, (int)sliderFppt.Maximum);
            }
            sliderFppt.Value = fppt;
            labelFppt.Text = $"{fppt}W";
        }

        checkApplyPower.IsChecked = Helpers.AppConfig.IsMode("auto_apply_power");

        // Lenovo extra CPU power tunables (APU/IPL/Tau/cross-loading).
        BuildTunableRows(panelLenovoCpuPpt, _cpuExtraDefs, wmi);

        BuildTdpPresets();

        // Guard stays up through the helper builds above: any slider change
        // they cause must not reach SchedulePLWrite (issue #151).
        _updatingPLSliders = false;
    }

    // Saved per-mode watt limit, or 0 when unset / poisoned. Values at or
    // below the slider floor come from bogus firmware readbacks, not the
    // user; drop them from config so auto-apply stops re-sending them
    // (issue #151: persisted 5W capped the CPU at 2000 MHz).
    private static int SanitizedLimit(string key, int maxTotal)
    {
        int v = Helpers.AppConfig.GetMode(key);
        if (v <= 0)
            return 0;
        if (v <= Mode.ModeControl.MinTotal || v > maxTotal)
        {
            Helpers.Logger.WriteLine($"FansWindow: dropping poisoned {key}={v}W from config");
            Helpers.AppConfig.SetMode(key, 0);
            return 0;
        }
        return v;
    }

    /// <summary>
    /// hhd-style one-tap TDP presets for handhelds (Ally, Legion Go): each
    /// button sets PL1 = PL2 = W and fPPT = W * 4/3 through the sliders, so
    /// the existing coupling, debounce, write and persistence all apply.
    /// Watts outside the slider range are skipped (e.g. 30W on a device
    /// whose firmware caps lower).
    /// </summary>
    private void BuildTdpPresets()
    {
        panelTdpPresets.Children.Clear();
        if (!Helpers.AppConfig.IsHandheldDevice())
        {
            panelTdpPresets.IsVisible = false;
            return;
        }

        // Per-family sweet spots: Ally Z1E steps vs Legion Go Z1/Z2 steps.
        int[] watts = Helpers.AppConfig.IsAlly()
            ? [10, 15, 25, 30]
            : [8, 15, 22, 30];
        foreach (int w in watts)
        {
            if (w < (int)sliderPL1.Minimum || w > (int)sliderPL1.Maximum)
                continue;
            int preset = w;
            var button = new Button
            {
                Content = $"{preset}W",
                MinWidth = 64,
                Height = 30,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            button.Classes.Add("ghelper");
            button.Click += (_, _) => ApplyTdpPreset(preset);
            panelTdpPresets.Children.Add(button);
        }
        panelTdpPresets.IsVisible = panelTdpPresets.Children.Count > 0;
    }

    private void ApplyTdpPreset(int watts)
    {
        // fPPT first so the PL1/PL2 coupling never fights the boost value.
        int fppt = Math.Min((int)sliderFppt.Maximum, watts * 4 / 3);
        if (gridFppt.IsVisible)
            sliderFppt.Value = fppt;
        sliderPL2.Value = Math.Min((int)sliderPL2.Maximum, watts);
        sliderPL1.Value = Math.Min((int)sliderPL1.Maximum, watts);
        Helpers.Logger.WriteLine($"TDP preset: {watts}W (fppt={fppt})");
    }

    private void SliderPL1_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPLSliders)
            return;
        labelPL1.Text = $"{(int)e.NewValue}W";
        // Enforce PL1 ≤ PL2 ≤ fPPT (matches Windows G-Helper coupling)
        if (sliderPL1.Value > sliderPL2.Value)
            sliderPL2.Value = sliderPL1.Value;
        if (sliderPL1.Value > sliderFppt.Value)
            sliderFppt.Value = sliderPL1.Value;
        SchedulePLWrite();
    }

    private void SliderPL2_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPLSliders)
            return;
        labelPL2.Text = $"{(int)e.NewValue}W";
        if (sliderPL2.Value < sliderPL1.Value)
            sliderPL1.Value = sliderPL2.Value;
        if (sliderPL2.Value > sliderFppt.Value)
            sliderFppt.Value = sliderPL2.Value;
        SchedulePLWrite();
    }

    private void SliderFppt_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPLSliders)
            return;
        labelFppt.Text = $"{(int)e.NewValue}W";
        if (sliderFppt.Value < sliderPL2.Value)
            sliderPL2.Value = sliderFppt.Value;
        if (sliderFppt.Value < sliderPL1.Value)
            sliderPL1.Value = sliderFppt.Value;
        SchedulePLWrite();
    }

    /// <summary>Debounce PL slider writes - only write 300ms after the user stops dragging.</summary>
    private void SchedulePLWrite()
    {
        _plDebounce?.Stop();
        _plDebounce ??= new System.Timers.Timer(300) { AutoReset = false };
        _plDebounce.Elapsed -= PLDebounce_Elapsed;
        _plDebounce.Elapsed += PLDebounce_Elapsed;
        _plDebounce.Start();
    }

    private void PLDebounce_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var wmi = App.Wmi;
            if (wmi == null)
                return;

            // Ensure FANM=4 so PPT writes are not silently dropped by
            // firmware. Mode-switch paths handle this via AutoFans /
            // AutoCpuPower, but direct slider writes bypass that flow.
            wmi.EnsureManualFanMode();

            int pl1 = (int)sliderPL1.Value;
            int pl2 = (int)sliderPL2.Value;
            int fppt = (int)sliderFppt.Value;

            wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptPl1Spl, pl1);
            Helpers.AppConfig.SetMode("limit_slow", pl1);

            wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptPl2Sppt, pl2);
            Helpers.AppConfig.SetMode("limit_fast", pl2);

            if (gridFppt.IsVisible)
            {
                wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptFppt, fppt);
                Helpers.AppConfig.SetMode("limit_fppt", fppt);
            }

            // Mirror to secondary PPT - prevents stale APU/Platform SPPT
            // from bottlenecking. Value = max(PL1, PL2).
            int ceiling = Math.Max(pl1, pl2);
            if (ceiling > 0)
            {
                if (wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.PptApuSppt))
                    wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptApuSppt, ceiling);
                if (wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.PptPlatformSppt))
                    wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptPlatformSppt, ceiling);
            }
        });
    }

    // CPU Boost

    private void RefreshBoostButton()
    {
        var power = App.Power;
        if (power == null)
            return;

        bool boostEnabled = power.GetCpuBoost();
        SetBoostButtonState(boostEnabled);
    }

    private void SetBoostButtonState(bool boostOn)
    {
        buttonBoostOn.BorderBrush = boostOn ? AccentBrush : TransparentBrush;
        buttonBoostOn.BorderThickness = new Avalonia.Thickness(2);
        buttonBoostOff.BorderBrush = !boostOn ? AccentBrush : TransparentBrush;
        buttonBoostOff.BorderThickness = new Avalonia.Thickness(2);
    }

    private void ButtonBoostOn_Click(object? sender, RoutedEventArgs e)
    {
        App.Power?.SetCpuBoost(true);
        Helpers.AppConfig.SetMode("auto_boost", 1);
        SetBoostButtonState(true);
    }

    private void ButtonBoostOff_Click(object? sender, RoutedEventArgs e)
    {
        App.Power?.SetCpuBoost(false);
        Helpers.AppConfig.SetMode("auto_boost", 0);
        SetBoostButtonState(false);
    }

    // Sensor refresh

    private void RefreshSensors()
    {
        try
        {
            var wmi = App.Wmi;
            if (wmi == null)
                return;

            int cpuTemp = wmi.DeviceGet(0x00120094);
            int gpuTemp = wmi.DeviceGet(0x00120097);
            int cpuFan = wmi.GetFanRpm(0);
            int gpuFan = wmi.GetFanRpm(1);
            int midFan = wmi.GetFanRpm(2);

            // GPU load comes from the background GPU poll (nvidia-smi), never read
            // it on the UI thread - it can block when the dGPU is wedged.
            string gpuLoadStr = _gpuLoadStr;

            string cpuFanStr = cpuFan > 0 ? Fan.FanSensorControl.FormatFan(0, cpuFan) : "--";
            string gpuFanStr = gpuFan > 0 ? Fan.FanSensorControl.FormatFan(1, gpuFan) : "--";

            string info = $"CPU: {(cpuTemp > 0 ? Helpers.TempHelper.FormatTemp(cpuTemp) : "--")} / {cpuFanStr}   " +
                          $"GPU: {(gpuTemp > 0 ? Helpers.TempHelper.FormatTemp(gpuTemp) : "--")}{gpuLoadStr} / {gpuFanStr}";

            if (midFan > 0)
                info += $"   Mid: {Fan.FanSensorControl.FormatFan(2, midFan)}";

            labelSensors.Text = info;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("FansWindow sensor refresh error", ex);
        }
    }

    private volatile bool _gpuRefreshBusy;
    private volatile string _gpuLoadStr = "";

    /// <summary>
    /// Fetch GPU load + live status + applied offsets on a background thread and
    /// post the results to the UI. nvidia-smi / gpu-helper can block (up to a
    /// timeout) when the dGPU is wedged, so this must never run on the UI thread.
    /// </summary>
    private void StartGpuBackgroundRefresh()
    {
        if (_gpuRefreshBusy)
            return;

        // Don't poke the dGPU (nvidia-smi / NVML) while a GPU mode switch is in
        // flight - entering Eco tears the driver down, and querying mid-teardown
        // can block for seconds and stall the UI.
        if (App.GpuModeCtrl?.IsSwitchInProgress == true)
        {
            _gpuLoadStr = "";
            return;
        }

        var nv = App.GpuControl as Gpu.NVidia.LinuxNvidiaGpuControl;
        bool eco = App.Wmi?.GetGpuEco() ?? false; // WMI read, fast
        if (nv == null || !nv.IsAvailable() || eco)
        {
            _gpuLoadStr = "";
            return;
        }
        bool liveVisible = _activeTab == 1 && gridGpuLive.IsVisible;

        _gpuRefreshBusy = true;
        Task.Run(() =>
        {
            string loadStr = "";
            Gpu.NVidia.LinuxNvidiaGpuControl.GpuLiveStatus? live = null;
            (int core, int mem)? offsets = null;
            bool haveOffsets = false;
            try
            {
                int? load = nv.GetGpuUse();
                if (load.HasValue && load.Value >= 0)
                    loadStr = $" Load: {load.Value}%";
                if (liveVisible)
                {
                    live = nv.GetLiveStatus();
                    offsets = nv.GetClockOffsets();
                    haveOffsets = true;
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("FansWindow: GPU background refresh failed", ex);
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _gpuLoadStr = loadStr;
                    if (liveVisible && live != null)
                        ApplyLiveStatus(live.Value);
                    if (liveVisible && haveOffsets)
                        ApplyAppliedOffsets(offsets);
                }
                finally
                {
                    _gpuRefreshBusy = false;
                }
            });
        });
    }

    /// <summary>Render a GPU live-status sample into the GPU Status block (UI thread).</summary>
    private void ApplyLiveStatus(Gpu.NVidia.LinuxNvidiaGpuControl.GpuLiveStatus v)
    {
        string na = Labels.Get("not_available_short");
        string C(int? x, string suffix) => x.HasValue ? $"{x.Value}{suffix}" : na;

        labelLiveCoreClock.Text = C(v.CoreClock, " MHz");
        labelLiveMemClock.Text = C(v.MemClock, " MHz");
        labelLiveSmClock.Text = C(v.SmClock, " MHz");
        labelLiveTemp.Text = C(v.Temp, "\u00B0C");
        labelLiveUtil.Text = C(v.Usage, "%");
        labelLivePower.Text = (v.PowerDraw.HasValue ? $"{v.PowerDraw.Value:0.#}" : na)
            + (v.PowerLimit.HasValue ? $" / {v.PowerLimit.Value:0.#} W" : " W");
        labelLiveVram.Text = (v.VramUsedMb.HasValue && v.VramTotalMb.HasValue)
            ? $"{v.VramUsedMb} / {v.VramTotalMb} MB"
            : na;
        labelLivePstate.Text = v.Pstate ?? na;
        labelLiveThrottle.Text = string.IsNullOrEmpty(v.ThrottleReason) ? Labels.Get("none") : v.ThrottleReason!;
        labelLivePcie.Text = (v.PcieGen.HasValue && v.PcieWidth.HasValue)
            ? $"Gen{v.PcieGen} x{v.PcieWidth}"
            : na;
    }

    /// <summary>Render the applied core/mem offset readback (UI thread).</summary>
    private void ApplyAppliedOffsets((int core, int mem)? off)
    {
        labelLiveOffsets.Text = off != null
            ? $"{off.Value.core:+#;-#;0} MHz core  {off.Value.mem:+#;-#;0} MHz mem"
            : Labels.Get("not_available_short");
    }

    /// <summary>
    /// Validate a fan curve read from hardware or config.
    /// Rejects null, wrong length, and completely-zero curves.
    /// Matches Windows G-Helper's IsEmptyCurve: a curve is invalid only if ALL 16 bytes are 0.
    /// Note: CPU/GPU fan curves from the Linux kernel often have all-zero temperatures but
    /// valid PWM duty cycles - these are valid curves (GetFanCurve synthesizes a temp ramp).
    /// </summary>
    private static bool IsValidCurve(byte[]? curve)
    {
        if (curve == null || curve.Length != 16)
            return false;

        // Reject only if every byte is zero (no useful data at all)
        for (int i = 0; i < 16; i++)
        {
            if (curve[i] > 0)
                return true;
        }
        return false;
    }

    // AMD Ryzen SMU power/temp/clock sliders

    private record RyzenSliderDef(string Param, Slider Slider, TextBlock Label, Grid Row, string Unit, float Divisor);

    private RyzenSliderDef[]? _ryzenSliders;

    // Params seeded from a valid in-range PM-table reading.
    private readonly HashSet<string> _ryzenSeeded = new();

    // Params the user moved since the window loaded.
    private readonly HashSet<string> _ryzenTouched = new();

    // True while seeding sliders programmatically (suppresses touch tracking).
    private bool _loadingRyzen;

    private RyzenSliderDef[] BuildRyzenSliderMap() =>
    [
        new("stapm-limit", sliderRyzenStapm, labelRyzenStapm, rowRyzenStapm, "W", 1000),
        new("fast-limit", sliderRyzenFast, labelRyzenFast, rowRyzenFast, "W", 1000),
        new("slow-limit", sliderRyzenSlow, labelRyzenSlow, rowRyzenSlow, "W", 1000),
        new("apu-slow-limit", sliderRyzenApuSlow, labelRyzenApuSlow, rowRyzenApuSlow, "W", 1000),
        new("stapm-time", sliderRyzenStapmTime, labelRyzenStapmTime, rowRyzenStapmTime, "s", 1),
        new("slow-time", sliderRyzenSlowTime, labelRyzenSlowTime, rowRyzenSlowTime, "s", 1),
        new("tctl-temp", sliderRyzenTctl, labelRyzenTctl, rowRyzenTctl, "\u00b0C", 1),
        new("apu-skin-temp", sliderRyzenApuSkin, labelRyzenApuSkin, rowRyzenApuSkin, "\u00b0C", 1),
        new("dgpu-skin-temp", sliderRyzenDgpuSkin, labelRyzenDgpuSkin, rowRyzenDgpuSkin, "\u00b0C", 1),
        new("vrm-current", sliderRyzenVrm, labelRyzenVrm, rowRyzenVrm, "A", 1000),
        new("vrmsoc-current", sliderRyzenVrmSoc, labelRyzenVrmSoc, rowRyzenVrmSoc, "A", 1000),
        new("vrmmax-current", sliderRyzenVrmMax, labelRyzenVrmMax, rowRyzenVrmMax, "A", 1000),
        new("vrmsocmax-current", sliderRyzenVrmSocMax, labelRyzenVrmSocMax, rowRyzenVrmSocMax, "A", 1000),
        new("max-gfxclk", sliderRyzenMaxGfx, labelRyzenMaxGfx, rowRyzenMaxGfx, "MHz", 1),
        new("min-gfxclk", sliderRyzenMinGfx, labelRyzenMinGfx, rowRyzenMinGfx, "MHz", 1),
    ];

    private void LoadRyzenPower()
    {
        // Building the map only references existing controls (UI thread).
        _ryzenSliders = BuildRyzenSliderMap();

        // Probe + PM-table read spawn sudo gpu-helper and can take seconds;
        // do them off the UI thread, then bind results back.
        Task.Run(() =>
        {
            bool available = Platform.Linux.RyzenPower.Available;
            var supported = new Dictionary<string, bool>();
            Dictionary<string, float>? info = null;
            if (available)
            {
                info = Platform.Linux.RyzenPower.ReadInfo();
                foreach (var s in _ryzenSliders)
                    supported[s.Param] = Platform.Linux.RyzenPower.IsSupported(s.Param);
            }
            Dispatcher.UIThread.Post(() => ApplyRyzenPowerInfo(available, supported, info));
        });
    }

    private void ApplyRyzenPowerInfo(bool available, Dictionary<string, bool> supported, Dictionary<string, float>? info)
    {
        if (_ryzenSliders == null)
            return;

        if (!available)
        {
            panelRyzenPower.IsVisible = false;
            return;
        }

        _ryzenSeeded.Clear();
        _ryzenTouched.Clear();
        _loadingRyzen = true;
        try
        {
            bool anyVisible = false;
            foreach (var s in _ryzenSliders)
            {
                bool sup = supported.TryGetValue(s.Param, out bool b) && b;
                s.Row.IsVisible = sup;
                if (!sup)
                    continue;
                anyVisible = true;

                // Seed only from an in-range reading. A junk/zero value (wrong
                // PM-table offsets on some chips) is ignored, not clamped to the
                // minimum - otherwise Apply would push that minimum to the SMU.
                if (info != null && info.TryGetValue(s.Param, out float raw))
                {
                    float display = raw / s.Divisor;
                    if (display >= s.Slider.Minimum && display <= s.Slider.Maximum)
                    {
                        s.Slider.Value = display;
                        _ryzenSeeded.Add(s.Param);
                    }
                }

                // Unreadable current value: say so instead of showing the
                // slider minimum as if it were real. Label turns numeric once the user moves
                // the slider.
                if (_ryzenSeeded.Contains(s.Param))
                    UpdateRyzenLabel(s);
                else
                    s.Label.Text = Labels.Get("not_available_short");
            }

            panelRyzenPower.IsVisible = anyVisible;
        }
        finally
        {
            _loadingRyzen = false;
        }
    }

    private void UpdateRyzenLabel(RyzenSliderDef s)
    {
        int v = (int)s.Slider.Value;
        s.Label.Text = $"{v}{s.Unit}";
    }

    private void SliderRyzenPower_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_ryzenSliders == null)
            return;
        foreach (var s in _ryzenSliders)
            if (sender == s.Slider)
            {
                UpdateRyzenLabel(s);
                if (!_loadingRyzen)
                    _ryzenTouched.Add(s.Param);
                break;
            }
    }

    private void ButtonRyzenApply_Click(object? sender, RoutedEventArgs e)
    {
        if (_ryzenSliders == null)
            return;

        // Only write rows we either read a real value for or the user moved.
        // Never push an untouched minimum default to the SMU (issue #151).
        var writes = new List<(string Param, int Raw)>();
        foreach (var s in _ryzenSliders)
        {
            if (!s.Row.IsVisible)
                continue;
            if (!_ryzenSeeded.Contains(s.Param) && !_ryzenTouched.Contains(s.Param))
                continue;
            writes.Add((s.Param, (int)(s.Slider.Value * s.Divisor)));
        }

        if (writes.Count == 0)
            return;

        Task.Run(() =>
        {
            foreach (var w in writes)
                Platform.Linux.RyzenPower.Set(w.Param, w.Raw);
        });
    }

    // Ryzen Curve Optimizer undervolt (mirrors Windows Fans.cs: trackUV / checkApplyUV)

    private void LoadUV()
    {
        bool amd = App.Smu?.IsAvailable == true;
        bool intel = App.IntelUv?.IsAvailable == true;
        if (!amd && !intel)
        {
            panelUV.IsVisible = false;
            return;
        }

        panelUV.IsVisible = true;

        _updatingUV = true;
        try
        {
            if (intel)
            {
                // Intel: core+cache voltage offset in millivolts (negative = undervolt).
                sliderCpuUV.Maximum = -Platform.Linux.IntelUndervolt.MinOffsetMv;
                int mv = Helpers.AppConfig.GetMode("cpu_uv_mv", 0);
                mv = Math.Clamp(mv, Platform.Linux.IntelUndervolt.MinOffsetMv, Platform.Linux.IntelUndervolt.MaxOffsetMv);
                sliderCpuUV.Value = -mv;
                labelCpuUV.Text = $"{mv} mV";
                rowIgpuUV.IsVisible = false;
            }
            else
            {
                // AMD: Curve Optimizer steps.
                sliderCpuUV.Maximum = -Platform.Linux.RyzenSmu.MinCPUUV;
                int cpuUV = Helpers.AppConfig.GetMode("cpu_uv", 0);
                cpuUV = Math.Clamp(cpuUV, Platform.Linux.RyzenSmu.MinCPUUV, Platform.Linux.RyzenSmu.MaxCPUUV);
                sliderCpuUV.Value = -cpuUV;
                labelCpuUV.Text = cpuUV.ToString();

                rowIgpuUV.IsVisible = App.Smu?.IsIGpuSupported == true;
                if (App.Smu?.IsIGpuSupported == true)
                {
                    int igpuUV = Helpers.AppConfig.GetMode("igpu_uv", 0);
                    igpuUV = Math.Clamp(igpuUV, Platform.Linux.RyzenSmu.MinCPUUV, Platform.Linux.RyzenSmu.MaxCPUUV);
                    sliderIgpuUV.Value = -igpuUV;
                    labelIgpuUV.Text = igpuUV.ToString();
                }
            }
            checkApplyUV.IsChecked = Helpers.AppConfig.IsMode("auto_uv");
        }
        finally
        {
            _updatingUV = false;
        }
    }

    private void SliderCpuUV_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUV)
            return;
        // Slider value is positive intensity; config stores the negated offset.
        if (App.IntelUv?.IsAvailable == true)
        {
            int intensity = Math.Clamp((int)e.NewValue, 0, -Platform.Linux.IntelUndervolt.MinOffsetMv);
            int mv = -intensity;
            labelCpuUV.Text = $"{mv} mV";
            Helpers.AppConfig.SetMode("cpu_uv_mv", mv);
            return;
        }
        int coIntensity = Math.Clamp((int)e.NewValue, 0, -Platform.Linux.RyzenSmu.MinCPUUV);
        int cpuUV = -coIntensity;
        labelCpuUV.Text = cpuUV.ToString();
        Helpers.AppConfig.SetMode("cpu_uv", cpuUV);
    }

    private void ButtonApplyUV_Click(object? sender, RoutedEventArgs e) => App.Mode?.ApplyCpuUndervolt();

    private void ButtonResetUV_Click(object? sender, RoutedEventArgs e)
    {
        bool intel = App.IntelUv?.IsAvailable == true;
        _updatingUV = true;
        try
        {
            sliderCpuUV.Value = 0;
            labelCpuUV.Text = intel ? "0 mV" : "0";
        }
        finally
        {
            _updatingUV = false;
        }
        Helpers.AppConfig.SetMode(intel ? "cpu_uv_mv" : "cpu_uv", 0);
        App.Mode?.ResetCpuUndervolt();
    }

    private void CheckApplyUV_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updatingUV)
            return;
        Helpers.AppConfig.SetMode("auto_uv", checkApplyUV.IsChecked == true ? 1 : 0);
        App.Mode?.AutoCpuUndervolt();
    }

    // Advanced: per-mode shell hook + reapply timer

    private void LoadAdvanced()
    {
        _updatingAdvanced = true;
        try
        {
            int mode = Mode.Modes.GetCurrent();
            textModeCommand.Text = Helpers.AppConfig.GetString($"mode_command_{mode}") ?? "";
            int reapply = Helpers.AppConfig.Get("reapply_time", 0);
            if (reapply < 0)
                reapply = 0;
            numReapplyTime.Value = reapply;
        }
        finally
        {
            _updatingAdvanced = false;
        }
    }

    private void TextModeCommand_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingAdvanced)
            return;
        int mode = Mode.Modes.GetCurrent();
        string val = textModeCommand.Text ?? "";
        Helpers.AppConfig.Set($"mode_command_{mode}", val);
    }

    private void NumReapplyTime_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updatingAdvanced)
            return;
        int v = (int)(e.NewValue ?? 0);
        if (v < 0)
            v = 0;
        Helpers.AppConfig.Set("reapply_time", v);
        App.Mode?.RefreshReapplyTimer();
    }

    // GPU Tuning handlers (Phase C wires the actual apply paths)

    private void SliderNvBaseTgp_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        labelNvBaseTgp.Text = $"{(int)e.NewValue}W";
    }

    private void SliderNvTgp_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        labelNvTgp.Text = $"{(int)e.NewValue}W";
    }

    private void SliderGpuBoost_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        labelGpuBoost.Text = $"{(int)e.NewValue}W";
    }

    private void SliderGpuTemp_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        labelGpuTemp.Text = $"{(int)e.NewValue}\u00B0C";
    }

    private void SliderGpuPowerLim_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        labelGpuPowerLim.Text = $"{(int)e.NewValue}W";
    }

    private void SliderGpuClockCore_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        labelGpuClockCore.Text = $"{(int)e.NewValue} MHz";
    }

    private void SliderGpuClockMem_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        labelGpuClockMem.Text = $"{(int)e.NewValue} MHz";
    }

    private void CheckGpuClockLock_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updatingGpu)
            return;
        bool enabled = checkGpuClockLock.IsChecked == true;
        sliderGpuClockLock.IsEnabled = enabled;
        labelGpuClockLock.Text = enabled
            ? $"{(int)sliderGpuClockLock.Value} MHz"
            : Labels.Get("off");
    }

    private void SliderGpuClockLock_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        if (checkGpuClockLock.IsChecked == true)
            labelGpuClockLock.Text = $"{(int)e.NewValue} MHz";
    }

    private void CheckGpuMemClockLock_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updatingGpu)
            return;
        bool enabled = checkGpuMemClockLock.IsChecked == true;
        sliderGpuMemClockLock.IsEnabled = enabled;
        labelGpuMemClockLock.Text = enabled
            ? $"{(int)sliderGpuMemClockLock.Value} MHz"
            : Labels.Get("off");
    }

    private void SliderGpuMemClockLock_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingGpu)
            return;
        if (checkGpuMemClockLock.IsChecked == true)
            labelGpuMemClockLock.Text = $"{(int)e.NewValue} MHz";
    }

    private void ButtonGpuApply_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        var nv = App.GpuControl as Gpu.NVidia.LinuxNvidiaGpuControl;

        int? baseTgp = rowNvBaseTgp.IsVisible ? (int)sliderNvBaseTgp.Value : null;
        int? maxTgp = rowNvTgp.IsVisible ? (int)sliderNvTgp.Value : null;
        int? boost = rowGpuBoost.IsVisible ? (int)sliderGpuBoost.Value : null;
        int? temp = rowGpuTemp.IsVisible ? (int)sliderGpuTemp.Value : null;
        int? smiPower = rowGpuPowerLim.IsVisible ? (int)sliderGpuPowerLim.Value : null;
        bool clockLock = rowGpuClockLock.IsVisible && checkGpuClockLock.IsChecked == true;
        int clockMhz = (int)sliderGpuClockLock.Value;
        bool memClockLock = checkGpuMemClockLock.IsChecked == true;
        int memClockMhz = (int)sliderGpuMemClockLock.Value;
        bool memLockRowVisible = rowGpuMemClockLock.IsVisible;
        int? coreOff = rowGpuClockCore.IsVisible ? (int)sliderGpuClockCore.Value : null;
        int? memOff = rowGpuClockMem.IsVisible ? (int)sliderGpuClockMem.Value : null;

        Helpers.AppConfig.SetMode("gpu_base_tgp", baseTgp ?? -1);
        Helpers.AppConfig.SetMode("gpu_tgp", maxTgp ?? -1);
        Helpers.AppConfig.SetMode("gpu_boost", boost ?? -1);
        Helpers.AppConfig.SetMode("gpu_temp", temp ?? -1);
        Helpers.AppConfig.SetMode("gpu_power_lim", smiPower ?? -1);
        Helpers.AppConfig.SetMode("gpu_clock_lock", clockLock ? clockMhz : 0);
        Helpers.AppConfig.SetMode("gpu_mem_clock_lock", memClockLock ? memClockMhz : 0);
        Helpers.AppConfig.SetMode("gpu_clock_core", coreOff ?? 0);
        Helpers.AppConfig.SetMode("gpu_clock_mem", memOff ?? 0);

        buttonGpuApply.IsEnabled = false;
        Task.Run(() =>
        {
            try
            {
                if (wmi != null)
                {
                    wmi.EnsureManualFanMode();

                    if (baseTgp != null)
                        wmi.SetPptLimit(Platform.Linux.AsusAttributes.NvBaseTgp, baseTgp.Value);
                    if (maxTgp != null && _attrNvTgp != null)
                        wmi.SetPptLimit(_attrNvTgp, maxTgp.Value);
                    if (boost != null && _attrGpuBoost != null)
                        wmi.SetPptLimit(_attrGpuBoost, boost.Value);
                    if (temp != null && _attrGpuTemp != null)
                        wmi.SetPptLimit(_attrGpuTemp, temp.Value);
                }
                if (nv != null && nv.IsAvailable())
                {
                    bool nvmlOk = nv.IsClockOffsetSupported();
                    nv.ApplyAll(
                        smiPower,
                        clockLock ? clockMhz : 0,
                        nvmlOk ? coreOff : null,
                        nvmlOk ? memOff : null);
                    if (memLockRowVisible)
                        nv.ApplyMemClockLock(memClockLock ? memClockMhz : 0);
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("FansWindow.ButtonGpuApply_Click failed", ex);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                buttonGpuApply.IsEnabled = true;
                StartGpuBackgroundRefresh();
                App.System?.ShowNotification(Labels.Get("gpu_tuning_notify"),
                    Labels.Get("gpu_tuning_applied"),
                    "dialog-information");
            });
        });
    }

    private void ButtonGpuReset_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        var nv = App.GpuControl as Gpu.NVidia.LinuxNvidiaGpuControl;

        Helpers.AppConfig.RemoveMode("gpu_base_tgp");
        Helpers.AppConfig.RemoveMode("gpu_tgp");
        Helpers.AppConfig.RemoveMode("gpu_boost");
        Helpers.AppConfig.RemoveMode("gpu_temp");
        Helpers.AppConfig.RemoveMode("gpu_power_lim");
        Helpers.AppConfig.RemoveMode("gpu_clock_lock");
        Helpers.AppConfig.RemoveMode("gpu_mem_clock_lock");
        Helpers.AppConfig.RemoveMode("gpu_clock_core");
        Helpers.AppConfig.RemoveMode("gpu_clock_mem");
        Helpers.AppConfig.RemoveMode("auto_apply_gpu");

        int? smiDefault = null;
        if (nv != null && nv.IsAvailable())
        {
            var limits = nv.GetPowerLimits();
            if (limits != null)
                smiDefault = limits.Value.defaultW;
        }

        buttonGpuReset.IsEnabled = false;
        Task.Run(() =>
        {
            try
            {
                if (wmi != null)
                {
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.NvBaseTgp);
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.NvTgp);
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.NvDynamicBoost);
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.NvTempTarget);
                }
                if (nv != null && nv.IsAvailable())
                {
                    bool nvmlOk = nv.IsClockOffsetSupported();
                    nv.ApplyAll(
                        smiDefault,
                        0,
                        nvmlOk ? 0 : null,
                        nvmlOk ? 0 : null);
                    nv.ApplyMemClockLock(0);
                    Gpu.NVidia.LinuxNvidiaGpuControl.ResetLastApplyState();
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("FansWindow.ButtonGpuReset_Click failed", ex);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LoadGpuTuning();
                buttonGpuReset.IsEnabled = true;
            });
        });
    }

    private static void WriteFwAttrDefault(Platform.IHardwareControl wmi, Platform.Linux.AttrDef attr)
    {
        if (!wmi.IsFeatureSupported(attr))
            return;
        var range = wmi.GetAttributeRange(attr);
        if (range == null || range.Default <= 0)
            return;
        wmi.SetPptLimit(attr, range.Default);
    }

    private void ButtonCpuReset_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;

        Helpers.AppConfig.RemoveMode("limit_slow");
        Helpers.AppConfig.RemoveMode("limit_fast");
        Helpers.AppConfig.RemoveMode("limit_fppt");
        Helpers.AppConfig.RemoveMode("auto_apply_power");

        buttonCpuReset.IsEnabled = false;
        Task.Run(() =>
        {
            try
            {
                if (wmi != null)
                {
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.PptPl1Spl);
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.PptPl2Sppt);
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.PptFppt);
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.PptApuSppt);
                    WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.PptPlatformSppt);
                }
                App.Power?.SetCpuBoost(true);
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("FansWindow.ButtonCpuReset_Click failed", ex);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LoadPowerLimits();
                RefreshBoostButton();
                buttonCpuReset.IsEnabled = true;
            });
        });
    }

    private void SliderIgpuUV_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUV)
            return;
        int intensity = Math.Clamp((int)e.NewValue, 0, -Platform.Linux.RyzenSmu.MinCPUUV);
        int igpuUV = -intensity;
        labelIgpuUV.Text = igpuUV.ToString();
        Helpers.AppConfig.SetMode("igpu_uv", igpuUV);
    }

    private void SliderCpuTemp_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUV)
            return;
        labelCpuTemp.Text = $"{(int)e.NewValue}\u00B0C";
    }
}
