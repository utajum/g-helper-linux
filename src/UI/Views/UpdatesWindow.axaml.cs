using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// BIOS and Driver Updates window — Linux port of G-Helper's Updates form.
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

    private static readonly List<string> SkipList = new()
    {
        "Armoury Crate & Aura Creator Installer",
        "Armoury Crate Control Interface",
        "MyASUS",
        "ASUS Smart Display Control",
        "Aura Wallpaper",
        "Virtual Pet",
        "Virtual Pet- Ultimate Edition",
        "ROG Font V1.5",
    };

    private const string GitHubRepo = "utajum/g-helper-linux";

    private string? _model;
    private string? _biosVersion;
    private int _updatesCount = 0;

    public UpdatesWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => LoadUpdates();
    }

    private void ButtonRefresh_Click(object? sender, RoutedEventArgs e)
    {
        LoadUpdates();
    }

    private async void ButtonDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            buttonDiagnostics.IsEnabled = false;
            buttonDiagnostics.Content = "Collecting...";

            var report = await Task.Run(() => Helpers.Diagnostics.GenerateReport());

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(report);
                buttonDiagnostics.Content = "Copied!";
                Helpers.Logger.WriteLine($"Diagnostics: copied {report.Length} chars to clipboard");
            }
            else
            {
                buttonDiagnostics.Content = "Clipboard unavailable";
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Diagnostics failed: {ex.Message}");
            buttonDiagnostics.Content = "Failed";
        }

        // Reset button after 2 seconds
        _ = Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                buttonDiagnostics.Content = "Copy Diagnostics";
                buttonDiagnostics.IsEnabled = true;
            }));
    }

    private void LoadUpdates()
    {
        // Get model and BIOS from DMI sysfs
        var biosRaw = App.System?.GetBiosVersion() ?? "";
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

        var modelName = App.System?.GetModelName() ?? "Unknown";
        Title = $"BIOS & Driver Updates: {modelName} ({_model} BIOS {_biosVersion ?? "?"})";
        labelAppVersion.Text = $"G-Helper Linux v{Helpers.AppConfig.AppVersion} — {modelName}";

        _updatesCount = 0;
        labelUpdates.Text = "Checking...";
        labelUpdates.Foreground = ColorGreen;

        // Clear tables
        panelBios.Children.Clear();
        panelBios.Children.Add(new TextBlock { Text = "Loading BIOS info...", Foreground = ColorDim, FontSize = 12 });
        panelDrivers.Children.Clear();
        panelDrivers.Children.Add(new TextBlock { Text = "Loading drivers...", Foreground = ColorDim, FontSize = 12 });

        // Check for G-Helper Linux self-update
        panelSelfUpdate.Children.Clear();
        panelSelfUpdate.Children.Add(labelSelfUpdateStatus);
        labelSelfUpdateStatus.Text = "Checking for updates...";
        labelSelfUpdateStatus.Foreground = ColorDim;
        Task.Run(async () => await CheckSelfUpdateAsync());

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
                    labelUpdates.Text = $"Updates available: {_updatesCount}";
                    labelUpdates.Foreground = ColorRed;
                    labelUpdates.FontWeight = FontWeight.Bold;

                    var row = new StackPanel { Spacing = 6 };

                    row.Children.Add(new TextBlock
                    {
                        Text = $"New version available: v{latestVersion}  (current: v{Helpers.AppConfig.AppVersion})",
                        Foreground = ColorRed,
                        FontSize = 12,
                        FontWeight = FontWeight.Bold,
                    });

                    var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                    var btnUpdate = new Button
                    {
                        Content = "Download & Install",
                        MinWidth = 130,
                        Foreground = ColorWhite,
                    };
                    btnUpdate.Click += async (_, _) =>
                    {
                        btnUpdate.IsEnabled = false;
                        btnUpdate.Content = "Downloading...";
                        await Task.Run(async () => await DownloadAndInstallUpdate(downloadUrl, btnUpdate));
                    };
                    btnRow.Children.Add(btnUpdate);

                    var btnRelease = new Button
                    {
                        Content = "View Release",
                        MinWidth = 100,
                        Foreground = ColorDim,
                        Background = Brushes.Transparent,
                        BorderThickness = new Avalonia.Thickness(0),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    string url = releaseUrl;
                    btnRelease.Click += (_, _) =>
                    {
                        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
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
                        Text = $"v{Helpers.AppConfig.AppVersion} — Up to date",
                        Foreground = ColorGreen,
                        FontSize = 12,
                    });
                }
            });

            Helpers.Logger.WriteLine($"Self-update: current=v{Helpers.AppConfig.AppVersion} latest=v{latestVersion} newer={isNewer} mode={( IsAppImage ? "AppImage" : "binary" )}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Self-update check failed: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                panelSelfUpdate.Children.Clear();
                panelSelfUpdate.Children.Add(new TextBlock
                {
                    Text = $"v{Helpers.AppConfig.AppVersion} — Could not check for updates",
                    Foreground = ColorDim,
                    FontSize = 12,
                });
            });
        }
    }

    private async Task DownloadAndInstallUpdate(string downloadUrl, Button btn)
    {
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
                            btn.Content = $"Downloading... {mb:F1}/{totalMb:F1} MB ({pct}%)");
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
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(targetPath, backupPath);  // rename running file → .bak (allowed)
                File.Move(tmpPath, targetPath);      // place new file at original path

                var restartPath = targetPath;

                Dispatcher.UIThread.Post(() =>
                {
                    btn.Content = "Restart to apply";
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
                // Can't determine target path — save to downloads
                var fileName = IsAppImage ? "GHelper-x86_64.AppImage" : "ghelper";
                var savePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", fileName);
                File.Move(tmpPath, savePath, overwrite: true);

                Dispatcher.UIThread.Post(() =>
                {
                    btn.Content = $"Saved to ~/Downloads/{fileName}";
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
                btn.Content = "Download failed";
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
            if (numA != numB) return numA - numB;
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
            if (result.ToString() == "" || !result.TryGetProperty("Obj", out var objProp) || objProp.GetArrayLength() == 0)
            {
                var urlFallback = url + "&tag=" + new Random().Next(10, 99);
                Helpers.Logger.WriteLine($"Updates: retrying with fallback {urlFallback}");
                json = await httpClient.GetStringAsync(urlFallback);
                doc2 = JsonDocument.Parse(json);
                data = doc2.RootElement;
            }

            var groups = data.GetProperty("Result").GetProperty("Obj");
            var drivers = new List<DriverInfo>();

            for (int i = 0; i < groups.GetArrayLength(); i++)
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
                        tooltip = $"Download: {driver.Version}\nInstalled: {_biosVersion}";
                    }
                    catch
                    {
                        status = 0;
                    }
                }
                else if (!isBios)
                {
                    // On Linux we can't easily check installed driver versions via WMI,
                    // so we show them all as "can't check" (gray) — user can click to download
                    status = 0;
                }

                if (status == 1) localUpdates++;

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
                    labelUpdates.Text = $"Updates available: {_updatesCount}";
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
                        Text = "No entries found",
                        Foreground = ColorDim,
                        FontSize = 12
                    });
                }

                // Update header if no updates found
                if (_updatesCount == 0)
                {
                    labelUpdates.Text = "No new updates";
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
                    Text = $"Failed to fetch: {ex.Message}",
                    Foreground = ColorRed,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            });
        }
    }

    private int _rowIndex = 0;

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
            ToolTip.SetTip(versionBtn, tooltip + "\nClick to download");
        }
        else
        {
            ToolTip.SetTip(versionBtn, tooltip);
        }

        grid.Children.Add(versionBtn);
        panel.Children.Add(grid);
    }
}
