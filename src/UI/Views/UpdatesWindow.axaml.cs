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
        labelAppVersion.Text = $"G-Helper Linux v1.0.0 — {modelName}";

        _updatesCount = 0;
        labelUpdates.Text = "Checking...";
        labelUpdates.Foreground = ColorGreen;

        // Clear tables
        panelBios.Children.Clear();
        panelBios.Children.Add(new TextBlock { Text = "Loading BIOS info...", Foreground = ColorDim, FontSize = 12 });
        panelDrivers.Children.Clear();
        panelDrivers.Children.Add(new TextBlock { Text = "Loading drivers...", Foreground = ColorDim, FontSize = 12 });

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
