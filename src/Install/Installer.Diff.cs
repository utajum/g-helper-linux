using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Install;

// Whole file is Linux-only; the Unix file-mode APIs are guarded at runtime.
#pragma warning disable CA1416

/// <summary>
/// Visual-diff half of <see cref="Installer"/>: the read-only colored diff shown
/// by the integrity panel's per-row "Diff" button on Outdated files. Split out of
/// Installer.cs to keep the provisioning logic and the diff UI in separate files;
/// it deliberately reuses Installer's private normalization helpers
/// (ExpectedBytes / SubstituteExec / StripIgnored) so the diff shows exactly the
/// inputs <see cref="Installer.ComputeState"/> compares.
/// </summary>
public static partial class Installer
{
    // UI: visual diff (the integrity panel's per-row "Diff" button on Outdated files)
    private const string DiffRemovedColor = "#FF6B6B"; // on-disk lines (red)
    private const string DiffAddedColor = "#06B48A";   // bundled lines (green)
    private const string DiffContextColor = "#9A9A9A"; // unchanged lines (gray)

    private enum DiffKind { Context, Removed, Added }

    /// <summary>Captured content for the diff dialog: both the raw view (literal
    /// bytes, nothing hidden) and the normalized view (exactly what ComputeState
    /// compares - desktop Exec resolved, ignore-prefixed lines dropped).</summary>
    private sealed class DiffData
    {
        public bool Unreadable;       // on-disk copy could not be read
        public bool Binary;           // either side is binary (no line diff)
        public long DiskBytes;
        public long BundledBytes;
        public string RawDisk = "";
        public string RawBundled = "";
        public string NormDisk = "";
        public string NormBundled = "";
    }

    private static bool HasNul(byte[] b)
    {
        foreach (var x in b)
            if (x == 0)
                return true;
        return false;
    }

    private static DiffData GatherDiff(ManagedFile f)
    {
        var d = new DiffData();

        byte[]? expected = ExpectedBytes(f);
        byte[] disk;
        try
        {
            disk = File.Exists(f.Dest) ? File.ReadAllBytes(f.Dest) : [];
        }
        catch (Exception)
        {
            d.Unreadable = true;
            return d;
        }

        d.DiskBytes = disk.Length;
        d.BundledBytes = expected?.Length ?? 0;

        // Binaries (gpu-helper, icon) have no meaningful line diff.
        if (HasNul(disk) || (expected != null && HasNul(expected)))
        {
            d.Binary = true;
            return d;
        }

        // Raw: literal bytes, nothing hidden (placeholder Exec=, # Version: line).
        d.RawDisk = Encoding.UTF8.GetString(disk);
        d.RawBundled = expected != null ? Encoding.UTF8.GetString(expected) : "";

        // Normalized: the exact inputs ComputeState compares, so the diff shows
        // only the lines that actually caused the Outdated verdict.
        byte[] normBundled = expected ?? [];
        if (f.Id == "desktop" && expected != null)
            normBundled = SubstituteExec(expected, LinuxSystemIntegration.ResolveLauncherExecField());
        d.NormBundled = Encoding.UTF8.GetString(StripIgnored(normBundled, f.IgnoreLinePrefixes));
        d.NormDisk = Encoding.UTF8.GetString(StripIgnored(disk, f.IgnoreLinePrefixes));
        return d;
    }

    private static string[] SplitLines(string s) => s.Replace("\r\n", "\n").Split('\n');

    /// <summary>Line-level LCS diff of <paramref name="oldLines"/> (on disk) vs
    /// <paramref name="newLines"/> (bundled). Emits every line in order tagged
    /// Context / Removed / Added (full file, no hunking - managed files are
    /// small). Falls back to a block diff if a file is pathologically large.</summary>
    private static List<(DiffKind kind, string text)> DiffLines(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length, m = newLines.Length;
        var result = new List<(DiffKind, string)>(n + m);

        // LCS is O(n*m); managed config files are tiny. Guard against a user
        // having dropped something huge in place so we never blow up memory.
        if ((long)n * m > 4_000_000L)
        {
            foreach (var l in oldLines)
                result.Add((DiffKind.Removed, l));
            foreach (var l in newLines)
                result.Add((DiffKind.Added, l));
            return result;
        }

        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (oldLines[x] == newLines[y])
            {
                result.Add((DiffKind.Context, oldLines[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add((DiffKind.Removed, oldLines[x]));
                x++;
            }
            else
            {
                result.Add((DiffKind.Added, newLines[y]));
                y++;
            }
        }
        while (x < n)
            result.Add((DiffKind.Removed, oldLines[x++]));
        while (y < m)
            result.Add((DiffKind.Added, newLines[y++]));
        return result;
    }

    private static void SetDiffMessage(SelectableTextBlock body, string message)
    {
        body.Inlines!.Clear();
        body.Inlines.Add(new Run(message) { Foreground = new SolidColorBrush(Color.Parse(DiffContextColor)) });
    }

    private static void FillDiff(SelectableTextBlock body, string diskText, string bundledText)
    {
        var diff = DiffLines(SplitLines(diskText), SplitLines(bundledText));
        var inlines = body.Inlines!;
        inlines.Clear();
        for (int i = 0; i < diff.Count; i++)
        {
            var (kind, text) = diff[i];
            if (i > 0)
                inlines.Add(new LineBreak());
            string prefix = kind switch
            {
                DiffKind.Removed => "- ",
                DiffKind.Added => "+ ",
                _ => "  ",
            };
            string color = kind switch
            {
                DiffKind.Removed => DiffRemovedColor,
                DiffKind.Added => DiffAddedColor,
                _ => DiffContextColor,
            };
            inlines.Add(new Run(prefix + text) { Foreground = new SolidColorBrush(Color.Parse(color)) });
        }
    }

    /// <summary>Show a read-only colored diff of one managed file: red lines are
    /// on disk now, green lines are what G-Helper bundles. A checkbox toggles
    /// between the raw view (everything) and the normalized view (only the lines
    /// that affect the up-to-date check). Close-only; repair stays on the row.</summary>
    public static async Task ShowDiffAsync(Window? owner, ManagedFile f)
    {
        DiffData data;
        try
        {
            data = GatherDiff(f);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer: diff {f.Id} failed: {ex.Message}");
            return;
        }

        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = Labels.Format("sysfiles_diff_title", Labels.Get(f.NameKey)),
            Width = 700,
            Height = 560,
            MinWidth = 420,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.Manual,
            CanResize = true,
            WindowDecorations = WindowDecorations.Full,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
        };
        try
        { dialog.Icon = owner?.Icon; }
        catch { }

        var rootDock = new DockPanel { Margin = new Thickness(16, 12, 16, 12), LastChildFill = true };

        var path = new TextBlock
        {
            Text = f.Dest,
            FontFamily = new FontFamily("monospace"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(path, Dock.Top);
        rootDock.Children.Add(path);

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            Margin = new Thickness(0, 0, 0, 8),
        };
        legend.Children.Add(new TextBlock
        {
            Text = Labels.Get("sysfiles_diff_legend_disk"),
            FontFamily = new FontFamily("monospace"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(DiffRemovedColor)),
        });
        legend.Children.Add(new TextBlock
        {
            Text = Labels.Get("sysfiles_diff_legend_bundled"),
            FontFamily = new FontFamily("monospace"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(DiffAddedColor)),
        });
        DockPanel.SetDock(legend, Dock.Top);
        rootDock.Children.Add(legend);

        var onlyCompared = new CheckBox
        {
            Name = "checkSysfilesDiffOnlyCompared",
            Content = Labels.Get("sysfiles_diff_only_compared"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            IsChecked = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !data.Binary && !data.Unreadable,
        };
        var btnClose = new Button
        {
            Name = "buttonSysfilesDiffClose",
            Content = Labels.Get("sysfiles_diff_close"),
            Classes = { "ghelper" },
            MinWidth = 110,
            Padding = new Thickness(14, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnClose.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };

        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        Grid.SetColumn(onlyCompared, 0);
        Grid.SetColumn(btnClose, 1);
        bottom.Children.Add(onlyCompared);
        bottom.Children.Add(btnClose);
        DockPanel.SetDock(bottom, Dock.Bottom);
        rootDock.Children.Add(bottom);

        var body = new SelectableTextBlock
        {
            FontFamily = new FontFamily("monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
        };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        };
        rootDock.Children.Add(scroll);

        void Render()
        {
            if (data.Unreadable)
            {
                SetDiffMessage(body, Labels.Get("sysfiles_diff_unreadable"));
                return;
            }
            if (data.Binary)
            {
                SetDiffMessage(body, Labels.Format("sysfiles_diff_binary", data.DiskBytes, data.BundledBytes));
                return;
            }
            bool norm = onlyCompared.IsChecked ?? false;
            FillDiff(body, norm ? data.NormDisk : data.RawDisk, norm ? data.NormBundled : data.RawBundled);
        }
        onlyCompared.IsCheckedChanged += (_, _) => Render();
        Render();

        dialog.Content = rootDock;
        dialog.Closed += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(true);
        };

        WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(dialog);
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
        await tcs.Task;
    }
}
