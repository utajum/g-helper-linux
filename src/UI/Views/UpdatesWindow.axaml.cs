using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GHelper.Linux.I18n;
using GHelper.Linux.Install;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// BIOS and Driver Updates window - Linux port of G-Helper's Updates form.
/// Queries ASUS ROG support API for BIOS and driver updates,
/// compares versions, shows download links.
/// 
/// API URLs:
///   BIOS:    https://rog.asus.com/support/webapi/product/GetPDBIOS?website=global&model={model}&cpu={model}&systemCode=rog
///   Drivers: https://rog.asus.com/support/webapi/product/GetPDDrivers?website=global&model={model}&cpu={model}&osid=52&systemCode=rog
/// 
/// Model comes from DMI BIOS version string split on ".": first part is model, second is BIOS version.
/// </summary>
public partial class UpdatesWindow : Window
{
    private static readonly IBrush ColorGreen = new SolidColorBrush(Color.Parse("#06B48A"));
    private static readonly IBrush ColorRed = new SolidColorBrush(Color.Parse("#FF2020"));
    private static readonly IBrush ColorGray = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush ColorWhite = new SolidColorBrush(Color.Parse("#F0F0F0"));
    private static readonly IBrush ColorDim = new SolidColorBrush(Color.Parse("#999999"));
    private static readonly IBrush RowBg1 = new SolidColorBrush(Color.Parse("#2A2A2A"));
    private static readonly IBrush RowBg2 = new SolidColorBrush(Color.Parse("#232323"));
    private static readonly Geometry ChevronCollapsed = Geometry.Parse("M 0,0 L 0,12 L 12,6 Z");
    private static readonly Geometry ChevronExpanded = Geometry.Parse("M 0,0 L 12,0 L 6,12 Z");

    private static readonly List<string> SkipList = new()
    {
        "Armoury Crate & Aura Creator Installer",
        "Armoury Crate Control Interface",
        "MyASUS",
        "ASUS Smart Display Control",
        "Aura Wallpaper",
        "Virtual Pet",
        "Virtual Pet- Ultimate Edition",
        "Virtual Assistant",
        "ROG Font V1.5",
    };

    private const string GitHubRepo = "utajum/g-helper-linux";

    private string? _model;
    private string? _biosVersion;
    private int _updatesCount;
    private bool _sysFilesExpanded;
    private bool _focusEbpf;
    // Rows the user tried to repair; a kernel row Repair cannot fix shows the manual step.
    private readonly HashSet<string> _repairTried = new();

    public UpdatesWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        Loaded += (_, _) =>
        {
            LoadUpdates();
            RefreshSystemFiles();
        };
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("updates_title");
        labelTitle.Text = Labels.Get("updates_header");
        buttonDiagnostics.Content = Labels.Get("copy_diagnostics");
        buttonExportDiag.Content = Labels.Get("export_diagnostics");
        buttonRefresh.Content = Labels.Get("refresh");
        buttonChangelog.Content = Labels.Get("changelog_title");
        labelLegendUpToDate.Text = Labels.Get("up_to_date");
        labelLegendUpdateAvailable.Text = Labels.Get("update_available");
        labelLegendCantCheck.Text = Labels.Get("cant_check");
        labelGHelperSection.Text = Labels.Get("ghelper_linux");
        labelBiosSection.Text = Labels.Get("bios");
        labelDriversSection.Text = Labels.Get("drivers_software");
        labelSystemFilesSection.Text = Labels.Get("sysfiles_section");
        buttonSysFilesRecheck.Content = Labels.Get("sysfiles_recheck");
        buttonSysFilesFix.Content = Labels.Get("sysfiles_fix");
        buttonSysFilesUninstall.Content = Labels.Get("sysfiles_uninstall");
        // NixOS: removal is declarative (services.ghelper.enable = false +
        // rebuild, or install-local.sh --uninstall); the in-app uninstall
        // can't remove module-managed files, so hide it.
        buttonSysFilesUninstall.IsVisible = !Platform.Linux.NixOS.IsNixOS;
    }

    private void ButtonRefresh_Click(object? sender, RoutedEventArgs e)
    {
        LoadUpdates();
    }

    private async void RefreshSystemFiles()
    {
        try
        {
            // One background status pass (spawns a sudo probe, hashes every
            // managed file); the panel build resumes on the UI thread.
            var results = await Task.Run(Installer.ComputeStatus);
            Installer.PopulateIntegrityPanel(panelSystemFiles, results,
                OnRepairOneAsync, OnRemoveOneAsync, OnShowDiffAsync, _repairTried);
            AppendSteamRow();
            _sysFilesExpanded = _focusEbpf || results.Any(r =>
                r.State != Installer.FileState.Ok &&
                r.State != Installer.FileState.Unknown &&
                r.State != Installer.FileState.NotApplicable &&
                r.State != Installer.FileState.Unavailable);
            ApplySysFilesExpanded();
            if (_focusEbpf)
            {
                _focusEbpf = false;
                int row = results.FindIndex(r => r.File.Id.StartsWith("ebpf_", StringComparison.Ordinal));
                if (row >= 0)
                    Dispatcher.UIThread.Post(() => panelSystemFiles.Children[row].BringIntoView(),
                        DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.RefreshSystemFiles failed: {ex.Message}");
        }
    }

    /// <summary>Optional "Steam library shortcut" row at the bottom of the
    /// integrity list. Hidden when Steam is not installed. The toggle adds or
    /// removes G-Helper as a non-Steam app (SteamShortcuts); it is never part
    /// of Install / Repair and never triggers the auto-expand or the startup
    /// popup.</summary>
    private void AppendSteamRow()
    {
        if (!SteamShortcuts.IsSteamAvailable())
            return;

        bool added = SteamShortcuts.IsAdded();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Avalonia.Thickness(0, 2, 0, 2),
        };

        var mark = new TextBlock
        {
            Text = added ? "\u2713" : "\u2013",
            Foreground = added ? ColorGreen : ColorGray,
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        Grid.SetColumn(mark, 0);
        grid.Children.Add(mark);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        info.Children.Add(new TextBlock
        {
            Text = Labels.Get("sysfiles_name_steam"),
            FontSize = 13,
            Foreground = ColorWhite,
        });
        info.Children.Add(new TextBlock
        {
            Text = SteamShortcuts.UserdataPath() ?? "",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var status = new TextBlock
        {
            Text = added ? Labels.Get("sysfiles_status_ok") : "",
            FontSize = 11,
            Foreground = ColorGreen,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        // Content set to null so the default "On"/"Off" strings never show.
        var toggle = new ToggleSwitch
        {
            IsChecked = added,
            OnContent = null,
            OffContent = null,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        toggle.IsCheckedChanged += async (_, _) => await OnSteamToggleAsync(toggle);
        Grid.SetColumn(toggle, 3);
        grid.Children.Add(toggle);

        panelSystemFiles.Children.Add(grid);
    }

    private async Task OnSteamToggleAsync(ToggleSwitch toggle)
    {
        bool turnOn = toggle.IsChecked == true;
        if (turnOn == SteamShortcuts.IsAdded())
            return; // programmatic rebuild, no change

        toggle.IsEnabled = false;
        try
        {
            string error = "";
            bool ok = await Task.Run(() =>
                turnOn ? SteamShortcuts.Add(out error) : SteamShortcuts.Remove(out error));
            labelSysFilesResult.Text = ok
                ? Labels.Get(turnOn ? "steam_added" : "steam_removed")
                : Labels.Get("steam_failed");
            labelSysFilesResult.IsVisible = true;
            if (!string.IsNullOrEmpty(error))
                Helpers.Logger.WriteLine($"UpdatesWindow: Steam toggle error: {error}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.OnSteamToggleAsync failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("steam_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            RefreshSystemFiles();
        }
    }

    /// <summary>Expand the system files and scroll to the eBPF rows once they are built.</summary>
    public void FocusEbpfRows()
    {
        _focusEbpf = true;
        if (IsLoaded)
            RefreshSystemFiles();
    }

    private async Task OnRepairOneAsync(Installer.ManagedFile file)
    {
        _repairTried.Add(file.Id);
        buttonSysFilesFix.IsEnabled = false;
        buttonSysFilesRecheck.IsEnabled = false;
        try
        {
            string msg = await Installer.RepairOneFromUiAsync(file);
            labelSysFilesResult.Text = msg;
            labelSysFilesResult.IsVisible = true;
            RefreshSystemFiles();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.OnRepairOneAsync failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("sysfiles_apply_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            buttonSysFilesFix.IsEnabled = true;
            buttonSysFilesRecheck.IsEnabled = true;
        }
    }

    private async Task OnRemoveOneAsync(Installer.ManagedFile file)
    {
        buttonSysFilesFix.IsEnabled = false;
        buttonSysFilesRecheck.IsEnabled = false;
        buttonSysFilesUninstall.IsEnabled = false;
        try
        {
            string msg = await Installer.RemoveOneFromUiAsync(file);
            labelSysFilesResult.Text = msg;
            labelSysFilesResult.IsVisible = true;
            RefreshSystemFiles();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.OnRemoveOneAsync failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("sysfiles_remove_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            buttonSysFilesFix.IsEnabled = true;
            buttonSysFilesRecheck.IsEnabled = true;
            buttonSysFilesUninstall.IsEnabled = true;
        }
    }

    private async Task OnShowDiffAsync(Installer.ManagedFile file)
    {
        try
        {
            await Installer.ShowDiffAsync(this, file);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.OnShowDiffAsync failed: {ex.Message}");
        }
    }

    private void ButtonSysFilesRecheck_Click(object? sender, RoutedEventArgs e)
    {
        labelSysFilesResult.IsVisible = false;
        RefreshSystemFiles();
    }

    private void SysFilesHeader_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _sysFilesExpanded = !_sysFilesExpanded;
        ApplySysFilesExpanded();
    }

    private void ApplySysFilesExpanded()
    {
        panelSysFilesBody.IsVisible = _sysFilesExpanded;
        iconSysFilesToggle.Data = _sysFilesExpanded ? ChevronExpanded : ChevronCollapsed;
    }

    private async void ButtonSysFilesFix_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var f in Installer.Manifest)
            _repairTried.Add(f.Id);
        buttonSysFilesFix.IsEnabled = false;
        try
        {
            string msg = await Installer.RunFixFromUiAsync();
            labelSysFilesResult.Text = msg;
            labelSysFilesResult.IsVisible = true;
            RefreshSystemFiles();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.ButtonSysFilesFix failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("sysfiles_apply_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            buttonSysFilesFix.IsEnabled = true;
        }
    }

    private async void ButtonSysFilesUninstall_Click(object? sender, RoutedEventArgs e)
    {
        bool confirmed = await Dialogs.ConfirmDialog.ShowAsync(
            this,
            Labels.Get("sysfiles_uninstall_title"),
            Labels.Get("sysfiles_uninstall_message"));
        if (!confirmed)
            return;

        buttonSysFilesUninstall.IsEnabled = false;
        buttonSysFilesFix.IsEnabled = false;
        buttonSysFilesRecheck.IsEnabled = false;
        try
        {
            // Best-effort: the shortcut points at the binary being removed.
            await Task.Run(() => SteamShortcuts.Remove(out _));
            string msg = await Installer.RunRemoveFromUiAsync();
            // The eBPF backend left with its helper; the GPU panel must stop using it.
            if (Helpers.AppConfig.IsBpfGpuBackend() && !File.Exists(SysfsHelper.GpuHelperPath))
            {
                string fallback = Helpers.AppConfig.DefaultGpuBackend();
                Helpers.AppConfig.Set("gpu_backend", fallback);
                await Task.Run(() => App.GpuModeCtrl?.OnBackendChanged("ebpf", fallback));
            }
            // Expand the panel so the result line (which lives in the collapsed
            // body) is actually visible.
            _sysFilesExpanded = true;
            ApplySysFilesExpanded();
            labelSysFilesResult.Text = msg;
            labelSysFilesResult.IsVisible = true;
            RefreshSystemFiles();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"UpdatesWindow.ButtonSysFilesUninstall failed: {ex.Message}");
            labelSysFilesResult.Text = Labels.Get("sysfiles_remove_failed");
            labelSysFilesResult.IsVisible = true;
        }
        finally
        {
            buttonSysFilesUninstall.IsEnabled = true;
            buttonSysFilesFix.IsEnabled = true;
            buttonSysFilesRecheck.IsEnabled = true;
        }
    }

    private ChangelogWindow? _changelogWindow;

    private void ButtonChangelog_Click(object? sender, RoutedEventArgs e)
    {
        if (_changelogWindow is { IsVisible: true })
        {
            _changelogWindow.Activate();
            return;
        }
        _changelogWindow = new ChangelogWindow();
        _changelogWindow.Closed += (_, _) => _changelogWindow = null;
        _changelogWindow.Show(this);
    }

    private async void ButtonDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            buttonDiagnostics.IsEnabled = false;
            buttonDiagnostics.Content = Labels.Get("collecting");

            var report = await Task.Run(() => Helpers.Diagnostics.GenerateReport());

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(report);
                buttonDiagnostics.Content = Labels.Get("copied");
                Helpers.Logger.WriteLine($"Diagnostics: copied {report.Length} chars to clipboard");
            }
            else
            {
                buttonDiagnostics.Content = Labels.Get("clipboard_unavailable");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Diagnostics failed: {ex.Message}");
            buttonDiagnostics.Content = Labels.Get("failed");
        }

        // Reset button after 2 seconds
        _ = Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                buttonDiagnostics.Content = Labels.Get("copy_diagnostics");
                buttonDiagnostics.IsEnabled = true;
            }));
    }

    private async void ButtonExportDiag_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            buttonExportDiag.IsEnabled = false;
            buttonExportDiag.Content = Labels.Get("collecting");

            var report = await Task.Run(() => Helpers.Diagnostics.GenerateReport());

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var suggestedName = $"ghelper-diagnostics-{timestamp}.txt";

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { } storage)
            {
                buttonExportDiag.Content = Labels.Get("failed");
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Labels.Get("export_diagnostics"),
                SuggestedFileName = suggestedName,
                DefaultExtension = "txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Text files") { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } },
                },
            });

            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new System.IO.StreamWriter(stream);
                await writer.WriteAsync(report);
                buttonExportDiag.Content = Labels.Get("saved");
                Helpers.Logger.WriteLine($"Diagnostics: exported {report.Length} chars to {file.Name}");
            }
            else
            {
                buttonExportDiag.Content = Labels.Get("export_diagnostics");
                buttonExportDiag.IsEnabled = true;
                return;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Diagnostics export failed: {ex.Message}");
            buttonExportDiag.Content = Labels.Get("failed");
        }

        _ = Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                buttonExportDiag.Content = Labels.Get("export_diagnostics");
                buttonExportDiag.IsEnabled = true;
            }));
    }

    private void LoadUpdates()
    {
        bool isLenovo = Helpers.AppConfig.IsLenovoDevice();

        // Get model and BIOS from DMI sysfs
        var biosRaw = App.System?.GetBiosVersion() ?? "";
        if (isLenovo)
        {
            // Lenovo: the support catalog is keyed by machine type (DMI
            // product_name, e.g. "83DX"); BIOS version is the whole DMI
            // string (e.g. "NZCN26WW"), not a dot-separated pair.
            _model = Helpers.AppConfig.GetModel();
            _biosVersion = string.IsNullOrWhiteSpace(biosRaw) ? null : biosRaw.Trim();
        }
        else
        {
            var parts = biosRaw.Split('.');
            if (parts.Length >= 2)
            {
                _model = parts[0];
                _biosVersion = parts[1];
            }
            else
            {
                _model = biosRaw;
                _biosVersion = null;
            }

            // ROG NUC: BIOS prefix (e.g. JNARLR79) is not a support catalog
            // model, the DMI product name is. Upstream seerge/g-helper#6073.
            if (Helpers.AppConfig.ContainsModel("NUC"))
                _model = Helpers.AppConfig.GetModelShort();
        }

        var modelName = App.System?.GetModelName() ?? Labels.Get("unknown");
        Title = Labels.Format("updates_title_format", modelName, _model ?? "", _biosVersion ?? "?");
        labelAppVersion.Text = Labels.Format("app_version_format", Helpers.AppConfig.AppVersion, modelName);

        _updatesCount = 0;
        labelUpdates.Text = Labels.Get("checking");
        labelUpdates.Foreground = ColorGreen;

        // Clear tables
        panelBios.Children.Clear();
        panelBios.Children.Add(new TextBlock { Text = Labels.Get("loading_bios"), Foreground = ColorDim, FontSize = 12 });
        panelDrivers.Children.Clear();
        panelDrivers.Children.Add(new TextBlock { Text = Labels.Get("loading_drivers"), Foreground = ColorDim, FontSize = 12 });

        // Check for G-Helper Linux self-update
        panelSelfUpdate.Children.Clear();
        panelSelfUpdate.Children.Add(labelSelfUpdateStatus);
        labelSelfUpdateStatus.Text = Labels.Get("checking_updates");
        labelSelfUpdateStatus.Foreground = ColorDim;
        Task.Run(async () => await CheckSelfUpdateAsync());

        if (isLenovo)
        {
            // One catalog call covers BIOS and drivers.
            Task.Run(async () => await FetchLenovoUpdatesAsync());
            return;
        }

        string rogParam = Helpers.AppConfig.IsROG() ? "&systemCode=rog" : "";

        // Fetch BIOS
        Task.Run(async () =>
        {
            await FetchDriversAsync(
                $"https://rog.asus.com/support/webapi/product/GetPDBIOS?website=global&model={_model}&cpu={_model}{rogParam}",
                isBios: true);
        });

        // Fetch Drivers
        Task.Run(async () =>
        {
            await FetchDriversAsync(
                $"https://rog.asus.com/support/webapi/product/GetPDDrivers?website=global&model={_model}&cpu={_model}&osid=52{rogParam}",
                isBios: false);
        });
    }

    /// <summary>Check if running inside an AppImage (APPIMAGE env var is set by AppImage runtime).</summary>
    private static bool IsAppImage => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE"));

    /// <summary>Get the path to the .AppImage file on disk (only valid when IsAppImage is true).</summary>
    private static string? AppImagePath => Environment.GetEnvironmentVariable("APPIMAGE");

    private async Task CheckSelfUpdateAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "G-Helper-Linux/" + Helpers.AppConfig.AppVersion);
            http.Timeout = TimeSpan.FromSeconds(10);

            // GitHub API: get latest release
            var json = await http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var latestVersion = tagName.TrimStart('v');
            var releaseUrl = root.GetProperty("html_url").GetString() ?? $"https://github.com/{GitHubRepo}/releases/latest";

            // Pick the right asset: AppImage when running as AppImage, bare binary otherwise
            string targetAsset = IsAppImage ? "GHelper-x86_64.AppImage" : "ghelper";
            var downloadUrl = $"https://github.com/{GitHubRepo}/releases/latest/download/{targetAsset}";
            if (root.TryGetProperty("assets", out var assets))
            {
                for (int i = 0; i < assets.GetArrayLength(); i++)
                {
                    var name = assets[i].GetProperty("name").GetString() ?? "";
                    if (name == targetAsset)
                    {
                        downloadUrl = assets[i].GetProperty("browser_download_url").GetString() ?? downloadUrl;
                        break;
                    }
                }
            }

            var isNewer = CompareVersions(latestVersion, Helpers.AppConfig.AppVersion) > 0;

            Dispatcher.UIThread.Post(() =>
            {
                panelSelfUpdate.Children.Clear();

                if (isNewer)
                {
                    _updatesCount++;
                    labelUpdates.Text = Labels.Format("updates_available_format", _updatesCount);
                    labelUpdates.Foreground = ColorRed;
                    labelUpdates.FontWeight = FontWeight.Bold;

                    var row = new StackPanel { Spacing = 6 };

                    row.Children.Add(new TextBlock
                    {
                        Text = Labels.Format("new_version_format", latestVersion, Helpers.AppConfig.AppVersion),
                        Foreground = ColorRed,
                        FontSize = 12,
                        FontWeight = FontWeight.Bold,
                    });

                    var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                    var btnUpdate = new Button
                    {
                        Content = Platform.Linux.NixOS.IsNixOS
                            ? Labels.Get("update_nixos_button") : Labels.Get("download_install"),
                        MinWidth = 130,
                        Foreground = ColorWhite,
                    };
                    btnUpdate.Click += async (_, _) =>
                    {
                        btnUpdate.IsEnabled = false;
                        btnUpdate.Content = Labels.Get("downloading");
                        await Task.Run(async () => await DownloadAndInstallUpdate(downloadUrl, btnUpdate));
                    };
                    btnRow.Children.Add(btnUpdate);

                    var btnRelease = new Button
                    {
                        Content = Labels.Get("view_release"),
                        MinWidth = 100,
                        Foreground = ColorDim,
                        Background = Brushes.Transparent,
                        BorderThickness = new Avalonia.Thickness(0),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    string url = releaseUrl;
                    btnRelease.Click += (_, _) =>
                    {
                        try
                        { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch { }
                    };
                    btnRow.Children.Add(btnRelease);

                    row.Children.Add(btnRow);
                    panelSelfUpdate.Children.Add(row);
                }
                else
                {
                    panelSelfUpdate.Children.Add(new TextBlock
                    {
                        Text = Labels.Format("up_to_date_format", Helpers.AppConfig.AppVersion),
                        Foreground = ColorGreen,
                        FontSize = 12,
                    });
                }
            });

            Helpers.Logger.WriteLine($"Self-update: current=v{Helpers.AppConfig.AppVersion} latest=v{latestVersion} newer={isNewer} mode={(IsAppImage ? "AppImage" : "binary")}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Self-update check failed: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                panelSelfUpdate.Children.Clear();
                panelSelfUpdate.Children.Add(new TextBlock
                {
                    Text = Labels.Format("cant_check_format", Helpers.AppConfig.AppVersion),
                    Foreground = ColorDim,
                    FontSize = 12,
                });
            });
        }
    }

    internal static async Task DownloadAndInstallUpdate(string downloadUrl, Button btn)
    {
        // NixOS: the binary is in the read-only /nix/store and can't be
        // replaced in place; a generic binary wouldn't run anyway. Updating
        // goes through nixos-rebuild instead - see RunNixOSUpdate.
        if (Platform.Linux.NixOS.IsNixOS)
        {
            await RunNixOSUpdate(btn);
            return;
        }

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "G-Helper-Linux/" + Helpers.AppConfig.AppVersion);
            http.Timeout = TimeSpan.FromMinutes(5);

            // Determine the target path to replace
            string? targetPath;
            string tmpFileName;

            if (IsAppImage && AppImagePath != null && File.Exists(AppImagePath))
            {
                // AppImage mode: replace the .AppImage file on disk
                targetPath = AppImagePath;
                tmpFileName = "ghelper-update.AppImage";
                Helpers.Logger.WriteLine($"Self-update: AppImage mode, target={targetPath}");
            }
            else
            {
                // Bare binary mode: replace the running binary
                targetPath = Environment.ProcessPath;
                tmpFileName = "ghelper-update";
            }

            var tmpPath = Path.Combine(Path.GetTempPath(), tmpFileName);

            // Download with progress reporting
            using (var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fs = File.Create(tmpPath);

                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, bytesRead);
                    downloaded += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = (int)(downloaded * 100 / totalBytes.Value);
                        var mb = downloaded / (1024.0 * 1024.0);
                        var totalMb = totalBytes.Value / (1024.0 * 1024.0);
                        Dispatcher.UIThread.Post(() =>
                            btn.Content = Labels.Format("download_progress", $"{mb:F1}", $"{totalMb:F1}", pct));
                    }
                }
            }

            // Make executable
#pragma warning disable CA1416
            File.SetUnixFileMode(tmpPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416


            if (targetPath != null && File.Exists(targetPath))
            {
                // Rename-then-place: Linux allows renaming a running executable
                // (the process keeps using the old inode), but does NOT allow
                // overwriting it directly ("Text file busy" / ETXTBSY).
                var backupPath = targetPath + ".bak";
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(targetPath, backupPath);  // rename running file → .bak (allowed)
                File.Move(tmpPath, targetPath);      // place new file at original path

                var binaryDir = Path.GetDirectoryName(targetPath);
                var restartPath = targetPath;

                Dispatcher.UIThread.Post(() =>
                {
                    btn.Content = Labels.Get("restart_to_apply");
                    btn.IsEnabled = true;
                    btn.Click -= null!; // clear old handlers
                    btn.Click += (_, _) =>
                    {
                        Helpers.Logger.WriteLine($"Self-update: restarting via {restartPath}");
                        Process.Start(new ProcessStartInfo(restartPath) { UseShellExecute = false });
                        Environment.Exit(0);
                    };
                });

                var mode = IsAppImage ? "AppImage" : "binary";
                Helpers.Logger.WriteLine($"Self-update: downloaded and replaced {mode} at {targetPath}. Restart required.");
            }
            else
            {
                // Can't determine target path - save to downloads
                var fileName = IsAppImage ? "GHelper-x86_64.AppImage" : "ghelper";
                var savePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", fileName);
                File.Move(tmpPath, savePath, overwrite: true);

                Dispatcher.UIThread.Post(() =>
                {
                    btn.Content = Labels.Format("saved_to_downloads", fileName);
                    btn.IsEnabled = false;
                });

                Helpers.Logger.WriteLine($"Self-update: saved to {savePath}");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Self-update download failed: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                btn.Content = Labels.Get("download_failed");
                btn.IsEnabled = true;
            });
        }
    }

    /// <summary>
    /// NixOS update: download the installer and re-run its NixOS branch via
    /// pkexec. That fetches the latest release binary, re-stages
    /// /etc/nixos/ghelper, and runs nixos-rebuild switch. The in-place
    /// binary-replace path cannot work on the read-only /nix/store.
    /// </summary>
    private static async Task RunNixOSUpdate(Button btn)
    {
        Dispatcher.UIThread.Post(() =>
        {
            btn.IsEnabled = false;
            btn.Content = Labels.Get("update_nixos_running");
        });

        var (ok, log) = await Platform.Linux.NixOS.RunModuleUpdate(
            $"https://raw.githubusercontent.com/{GitHubRepo}/master/install/install.sh",
            "G-Helper-Linux/" + Helpers.AppConfig.AppVersion);

        if (ok)
        {
            Helpers.Logger.WriteLine("NixOS update: nixos-rebuild succeeded");
            Dispatcher.UIThread.Post(() =>
            {
                btn.Content = Labels.Get("restart_to_apply");
                btn.IsEnabled = true;
                btn.Click += (_, _) =>
                {
                    // Relaunch via the stable profile symlink (repointed by the rebuild).
                    Process.Start(new ProcessStartInfo(Platform.Linux.NixOS.LauncherPath) { UseShellExecute = false });
                    Environment.Exit(0);
                };
            });
        }
        else
        {
            Helpers.Logger.WriteLine($"NixOS update failed: {log}");
            Dispatcher.UIThread.Post(() =>
            {
                btn.Content = Labels.Get("download_failed");
                btn.IsEnabled = true;
            });
        }
    }

    /// <summary>
    /// Compare two semver strings. Returns >0 if a > b, 0 if equal, &lt;0 if a &lt; b.
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var partsA = a.Split('.');
        var partsB = b.Split('.');
        var len = Math.Max(partsA.Length, partsB.Length);

        for (int i = 0; i < len; i++)
        {
            int numA = i < partsA.Length && int.TryParse(partsA[i], out var na) ? na : 0;
            int numB = i < partsB.Length && int.TryParse(partsB[i], out var nb) ? nb : 0;
            if (numA != numB)
                return numA - numB;
        }
        return 0;
    }

    private struct DriverInfo
    {
        public string Category;
        public string Title;
        public string Version;
        public string DownloadUrl;
        public string Date;
    }

    private async Task FetchDriversAsync(string url, bool isBios)
    {
        var panel = isBios ? panelBios : panelDrivers;

        try
        {
            Helpers.Logger.WriteLine($"Updates: fetching {url}");

            using var httpClient = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            });
            httpClient.DefaultRequestHeaders.Add("User-Agent", "G-Helper-Linux/1.0");
            httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var json = await httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement;
            var result = data.GetProperty("Result");

            // Fallback for bugged API (empty result)
            JsonDocument? doc2 = null;
            if (result.ToString() == "" || !result.TryGetProperty("Obj", out var objProp)
                || objProp.ValueKind != System.Text.Json.JsonValueKind.Array || objProp.GetArrayLength() == 0)
            {
                var urlFallback = url + "&tag=" + new Random().Next(10, 99);
                Helpers.Logger.WriteLine($"Updates: retrying with fallback {urlFallback}");
                json = await httpClient.GetStringAsync(urlFallback);
                doc2 = JsonDocument.Parse(json);
                data = doc2.RootElement;
            }

            var resultObj = data.GetProperty("Result");
            var drivers = new List<DriverInfo>();

            if (!resultObj.TryGetProperty("Obj", out var groups)
                || groups.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                // API returned no data for this model (non-ASUS hardware, unknown model)
                groups = default;
            }

            for (int i = 0; i < (groups.ValueKind == System.Text.Json.JsonValueKind.Array ? groups.GetArrayLength() : 0); i++)
            {
                var categoryName = groups[i].GetProperty("Name").GetString() ?? "";
                var files = groups[i].GetProperty("Files");
                string? oldTitle = null;

                for (int j = 0; j < files.GetArrayLength(); j++)
                {
                    var file = files[j];
                    var title = file.GetProperty("Title").GetString() ?? "";

                    if (title != oldTitle && !SkipList.Contains(title))
                    {
                        var version = (file.GetProperty("Version").GetString() ?? "").Replace("V", "");
                        var downloadUrl = "";
                        if (file.TryGetProperty("DownloadUrl", out var dlProp) &&
                            dlProp.TryGetProperty("Global", out var globalProp))
                        {
                            downloadUrl = globalProp.GetString() ?? "";
                        }
                        var date = file.GetProperty("ReleaseDate").GetString() ?? "";

                        drivers.Add(new DriverInfo
                        {
                            Category = categoryName,
                            Title = title,
                            Version = version,
                            DownloadUrl = downloadUrl,
                            Date = date,
                        });
                    }
                    oldTitle = title;
                }
            }

            // Compare versions for BIOS entries
            int localUpdates = 0;
            foreach (var driver in drivers)
            {
                int status = 0; // 0 = can't check, 1 = newer available, -1 = up to date
                string tooltip = driver.Version;

                if (isBios && !driver.Title.Contains("Firmware") && _biosVersion != null)
                {
                    try
                    {
                        int remote = int.Parse(driver.Version);
                        int local = int.Parse(_biosVersion);
                        status = remote > local ? 1 : -1;
                        tooltip = Labels.Format("download_tooltip", driver.Version, _biosVersion);
                    }
                    catch
                    {
                        status = 0;
                    }
                }
                else if (!isBios)
                {
                    // On Linux we can't easily check installed driver versions via WMI,
                    // so we show them all as "can't check" (gray) - user can click to download
                    status = 0;
                }

                if (status == 1)
                    localUpdates++;

                // Must capture for closure
                var d = driver;
                int s = status;
                string t = tooltip;

                Dispatcher.UIThread.Post(() => AddDriverRow(panel, d, s, t));
            }

            if (localUpdates > 0)
            {
                _updatesCount += localUpdates;
                Dispatcher.UIThread.Post(() =>
                {
                    labelUpdates.Text = Labels.Format("updates_available_format", _updatesCount);
                    labelUpdates.Foreground = ColorRed;
                    labelUpdates.FontWeight = FontWeight.Bold;
                });
            }

            // Clear loading label
            Dispatcher.UIThread.Post(() =>
            {
                // Remove the "Loading..." text if it's still there
                var loading = isBios ? labelBiosLoading : labelDriversLoading;
                if (panel.Children.Contains(loading))
                    panel.Children.Remove(loading);

                if (drivers.Count == 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = Labels.Get("no_entries"),
                        Foreground = ColorDim,
                        FontSize = 12
                    });
                }

                // Update header if no updates found
                if (_updatesCount == 0)
                {
                    labelUpdates.Text = Labels.Get("no_new_updates");
                    labelUpdates.Foreground = ColorGreen;
                }
            });

            doc2?.Dispose();

            Helpers.Logger.WriteLine($"Updates: fetched {drivers.Count} entries from {(isBios ? "BIOS" : "Drivers")} API");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Updates fetch error: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                panel.Children.Clear();
                panel.Children.Add(new TextBlock
                {
                    Text = Labels.Format("fetch_failed", ex.Message),
                    Foreground = ColorRed,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            });
        }
    }

    private const string LenovoCatalogBaseUrl = "https://pcsupport.lenovo.com/us/en/api/v4/downloads/drivers?productId=";

    /// <summary>
    /// Lenovo pcsupport catalog. One call returns BIOS and drivers together; entries
    /// with category "BIOS/UEFI" go to the BIOS panel with a version compare
    /// against the DMI BIOS string (NZCN26WW vs NZCN37WW), the rest land in
    /// the drivers panel as download links.
    /// </summary>
    private async Task FetchLenovoUpdatesAsync()
    {
        string url = LenovoCatalogBaseUrl + WebUtility.UrlEncode(_model ?? "");
        try
        {
            Helpers.Logger.WriteLine($"Updates: fetching {url}");

            using var httpClient = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            });
            // The catalog endpoint rejects non-browser user agents.
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Referrer = new Uri("https://pcsupport.lenovo.com/");
            httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
            httpClient.Timeout = TimeSpan.FromSeconds(20);

            var json = await httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var biosEntries = new List<DriverInfo>();
            var driverEntries = new List<DriverInfo>();

            if (doc.RootElement.TryGetProperty("body", out var body)
                && body.TryGetProperty("DownloadItems", out var items))
            {
                for (int i = 0; i < items.GetArrayLength(); i++)
                {
                    var entry = ParseLenovoDownloadItem(items[i]);
                    if (entry == null)
                        continue;
                    if (entry.Value.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase))
                        biosEntries.Add(entry.Value);
                    else
                        driverEntries.Add(entry.Value);
                }
            }

            int localUpdates = 0;
            foreach (var entry in biosEntries)
            {
                int status = CompareLenovoBiosVersions(entry.Version, _biosVersion);
                string tooltip = status == 0
                    ? entry.Version
                    : Labels.Format("download_tooltip", entry.Version, _biosVersion ?? "");
                if (status == 1)
                    localUpdates++;
                var d = entry;
                int s = status;
                string t = tooltip;
                Dispatcher.UIThread.Post(() => AddDriverRow(panelBios, d, s, t));
            }
            foreach (var entry in driverEntries)
            {
                var d = entry;
                Dispatcher.UIThread.Post(() => AddDriverRow(panelDrivers, d, 0, d.Version));
            }

            if (localUpdates > 0)
            {
                _updatesCount += localUpdates;
                Dispatcher.UIThread.Post(() =>
                {
                    labelUpdates.Text = Labels.Format("updates_available_format", _updatesCount);
                    labelUpdates.Foreground = ColorRed;
                    labelUpdates.FontWeight = FontWeight.Bold;
                });
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (panelBios.Children.Contains(labelBiosLoading))
                    panelBios.Children.Remove(labelBiosLoading);
                if (panelDrivers.Children.Contains(labelDriversLoading))
                    panelDrivers.Children.Remove(labelDriversLoading);

                if (biosEntries.Count == 0)
                    panelBios.Children.Add(new TextBlock { Text = Labels.Get("no_entries"), Foreground = ColorDim, FontSize = 12 });
                if (driverEntries.Count == 0)
                    panelDrivers.Children.Add(new TextBlock { Text = Labels.Get("no_entries"), Foreground = ColorDim, FontSize = 12 });

                if (_updatesCount == 0)
                {
                    labelUpdates.Text = Labels.Get("no_new_updates");
                    labelUpdates.Foreground = ColorGreen;
                }
            });

            Helpers.Logger.WriteLine($"Updates: fetched {biosEntries.Count} BIOS + {driverEntries.Count} driver entries from Lenovo catalog");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Updates fetch error (Lenovo): {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var panel in new[] { panelBios, panelDrivers })
                {
                    panel.Children.Clear();
                    panel.Children.Add(new TextBlock
                    {
                        Text = Labels.Format("fetch_failed", ex.Message),
                        Foreground = ColorRed,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            });
        }
    }

    /// One catalog DownloadItem -> DriverInfo. Prefers the EXE file, then ZIP,
    /// then the first file for the download link. Null when no file exists.
    private static DriverInfo? ParseLenovoDownloadItem(JsonElement item)
    {
        try
        {
            string category = item.TryGetProperty("Category", out var cat)
                && cat.TryGetProperty("Name", out var catName)
                ? catName.GetString() ?? "" : "";
            string title = item.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
            string version = item.TryGetProperty("SummaryInfo", out var si)
                && si.TryGetProperty("Version", out var v)
                ? v.GetString() ?? "" : "";

            if (!item.TryGetProperty("Files", out var files) || files.GetArrayLength() == 0)
                return null;

            JsonElement? mainFile = null;
            foreach (var preferred in new[] { "exe", "zip" })
            {
                for (int i = 0; i < files.GetArrayLength() && mainFile == null; i++)
                {
                    if (files[i].TryGetProperty("TypeString", out var ts)
                        && string.Equals(ts.GetString(), preferred, StringComparison.OrdinalIgnoreCase))
                        mainFile = files[i];
                }
                if (mainFile != null)
                    break;
            }
            mainFile ??= files[0];

            string downloadUrl = mainFile.Value.TryGetProperty("URL", out var u) ? u.GetString() ?? "" : "";
            string date = "";
            if (mainFile.Value.TryGetProperty("Date", out var dateNode)
                && dateNode.TryGetProperty("Unix", out var unix)
                && long.TryParse(unix.ToString(), out long unixMs))
            {
                date = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy/MM/dd");
            }

            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(downloadUrl))
                return null;

            return new DriverInfo
            {
                Category = category,
                Title = title,
                Version = version,
                DownloadUrl = downloadUrl,
                Date = date,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compare Lenovo BIOS version strings like "NZCN37WW" vs "NZCN26WW":
    /// same alpha prefix, numeric build in the middle. Returns 1 when the
    /// remote is newer, -1 when up to date, 0 when not comparable.
    /// </summary>
    private static int CompareLenovoBiosVersions(string? remote, string? local)
    {
        var (remotePrefix, remoteNum) = SplitLenovoBiosVersion(remote);
        var (localPrefix, localNum) = SplitLenovoBiosVersion(local);
        if (remoteNum < 0 || localNum < 0)
            return 0;
        if (!string.Equals(remotePrefix, localPrefix, StringComparison.OrdinalIgnoreCase))
            return 0;
        return remoteNum > localNum ? 1 : -1;
    }

    private static (string prefix, int number) SplitLenovoBiosVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return ("", -1);
        string s = version.Trim();
        int start = 0;
        while (start < s.Length && !char.IsDigit(s[start]))
            start++;
        int end = start;
        while (end < s.Length && char.IsDigit(s[end]))
            end++;
        if (start == end)
            return ("", -1);
        return (s.Substring(0, start), int.Parse(s.Substring(start, end - start)));
    }

    private int _rowIndex;

    private void AddDriverRow(StackPanel panel, DriverInfo driver, int status, string tooltip)
    {
        // Row: [Category | Title | Date | Version (link)]
        var rowBg = (_rowIndex % 2 == 0) ? RowBg1 : RowBg2;
        _rowIndex++;

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("100,*,80,120"),
            Background = rowBg,
            Margin = new Avalonia.Thickness(0, 1),
        };

        // Category
        grid.Children.Add(new TextBlock
        {
            Text = driver.Category,
            Foreground = ColorDim,
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [Grid.ColumnProperty] = 0,
        });

        // Title
        grid.Children.Add(new TextBlock
        {
            Text = driver.Title,
            Foreground = ColorWhite,
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            [Grid.ColumnProperty] = 1,
        });

        // Date
        grid.Children.Add(new TextBlock
        {
            Text = driver.Date,
            Foreground = ColorDim,
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 2,
        });

        // Version (clickable link)
        var versionColor = status switch
        {
            1 => ColorRed,     // newer available
            -1 => ColorGreen,  // up to date
            _ => ColorGray     // can't check
        };

        var versionText = driver.Version.Replace("latest version at the ", "");
        var versionBtn = new Button
        {
            Content = versionText,
            Foreground = versionColor,
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(6, 4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            FontSize = 11,
            FontWeight = status == 1 ? FontWeight.Bold : FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 3,
        };

        if (!string.IsNullOrEmpty(driver.DownloadUrl))
        {
            string url = driver.DownloadUrl;
            versionBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Helpers.Logger.WriteLine($"Failed to open URL: {ex.Message}");
                }
            };
            ToolTip.SetTip(versionBtn, tooltip + "\n" + Labels.Get("click_to_download"));
        }
        else
        {
            ToolTip.SetTip(versionBtn, tooltip);
        }

        grid.Children.Add(versionBtn);
        panel.Children.Add(grid);
    }

    public static void CheckForUpdateAtStartup(Window owner)
    {
        if (Helpers.AppConfig.Is("skip_update_prompt"))
            return;

        Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "G-Helper-Linux/" + Helpers.AppConfig.AppVersion);
                http.Timeout = TimeSpan.FromSeconds(10);

                var json = await http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString() ?? "";
                var latestVersion = tagName.TrimStart('v');

                if (CompareVersions(latestVersion, Helpers.AppConfig.AppVersion) <= 0)
                    return;

                string targetAsset = IsAppImage ? "GHelper-x86_64.AppImage" : "ghelper";
                var downloadUrl = $"https://github.com/{GitHubRepo}/releases/latest/download/{targetAsset}";
                if (root.TryGetProperty("assets", out var assets))
                {
                    for (int i = 0; i < assets.GetArrayLength(); i++)
                    {
                        var name = assets[i].GetProperty("name").GetString() ?? "";
                        if (name == targetAsset)
                        {
                            downloadUrl = assets[i].GetProperty("browser_download_url").GetString() ?? downloadUrl;
                            break;
                        }
                    }
                }

                Helpers.Logger.WriteLine($"Startup update check: v{latestVersion} available (current v{Helpers.AppConfig.AppVersion})");

                var dlUrl = downloadUrl;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        ShowUpdatePrompt(owner, latestVersion, dlUrl);
                    }
                    catch (Exception ex)
                    {
                        Helpers.Logger.WriteLine($"Startup update prompt failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Startup update check failed: {ex.Message}");
            }
        });
    }

    private static void ShowUpdatePrompt(Window owner, string latestVersion, string downloadUrl)
    {
        var dialog = new Window
        {
            Title = Labels.Get("update_prompt_title"),
            Width = 420,
            MinWidth = 420,
            MaxWidth = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
        };
        try
        { dialog.Icon = owner.Icon; }
        catch { }

        Helpers.WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(dialog);

        var root = new StackPanel { Margin = new Avalonia.Thickness(20, 16, 20, 16), Spacing = 12 };

        root.Children.Add(new TextBlock
        {
            Text = Labels.Get("update_prompt_title"),
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F0F0F0")),
        });

        root.Children.Add(new TextBlock
        {
            Text = Labels.Format("update_prompt_message", latestVersion, Helpers.AppConfig.AppVersion),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
        });

        var dontShow = new CheckBox
        {
            Content = Labels.Get("dont_show_again"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#999999")),
            IsChecked = false,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        dontShow.IsCheckedChanged += (_, _) =>
            Helpers.AppConfig.Set("skip_update_prompt", (dontShow.IsChecked ?? false) ? 1 : 0);
        root.Children.Add(dontShow);

        var btnUpdate = new Button
        {
            Content = Platform.Linux.NixOS.IsNixOS
                ? Labels.Get("update_nixos_button") : Labels.Get("update_now"),
            MinWidth = 130,
            Padding = new Avalonia.Thickness(14, 8),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var btnLater = new Button
        {
            Content = Labels.Get("not_now"),
            MinWidth = 100,
            Padding = new Avalonia.Thickness(14, 8),
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        btnLater.Click += (_, _) => dialog.Close();

        var url = downloadUrl;
        btnUpdate.Click += async (_, _) =>
        {
            btnUpdate.IsEnabled = false;
            btnLater.IsEnabled = false;
            dontShow.IsEnabled = false;
            btnUpdate.Content = Labels.Get("downloading");
            await Task.Run(async () => await DownloadAndInstallUpdate(url, btnUpdate));
        };

        var btnChangelog = new Button
        {
            Content = Labels.Get("changelog_title"),
            MinWidth = 100,
            Padding = new Avalonia.Thickness(14, 8),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ChangelogWindow? changelog = null;
        btnChangelog.Click += (_, _) =>
        {
            if (changelog is { IsVisible: true })
            {
                changelog.Activate();
                return;
            }
            changelog = new ChangelogWindow();
            changelog.Closed += (_, _) => changelog = null;
            // Parented to the main window, not this prompt - dismissing the
            // prompt must not take the release notes with it.
            if (owner.IsVisible)
                changelog.Show(owner);
            else
                changelog.Show();
        };

        var primaryRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        primaryRow.Children.Add(btnUpdate);
        primaryRow.Children.Add(btnLater);

        // Secondary action left, primary actions right.
        var btnRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(btnChangelog, 0);
        Grid.SetColumn(primaryRow, 1);
        btnRow.Children.Add(btnChangelog);
        btnRow.Children.Add(primaryRow);
        root.Children.Add(btnRow);

        dialog.Content = root;
        dialog.Show();
    }
}
