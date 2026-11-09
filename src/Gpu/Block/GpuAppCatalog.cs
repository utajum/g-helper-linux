using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GHelper.Linux.Gpu.Block;

/// <summary>An app that can be allowed before it runs. Entry is the list entry.</summary>
public sealed record CatalogApp(string Title, string Entry);

/// <summary>Installed apps (Exec process name) and Steam games (SteamAppId), to allow before they run.</summary>
public static class GpuAppCatalog
{
    private static readonly Regex SteamRun = new(@"steam://rungameid/(\d+)");
    private static readonly Regex AcfAppId = new("\"appid\"\\s+\"(\\d+)\"");
    private static readonly Regex AcfName = new("\"name\"\\s+\"([^\"]+)\"");
    private static readonly Regex VdfPath = new("\"path\"\\s+\"([^\"]+)\"");

    /// <summary>Steam's own runtimes and tools, not games.</summary>
    private static readonly string[] SteamTools = { "Proton", "Steam Linux Runtime", "Steamworks Common" };

    public static List<CatalogApp> Load()
    {
        var apps = new List<CatalogApp>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string home = Environment.GetEnvironmentVariable("HOME") ?? "";

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local/share");
        string dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share";
        foreach (var dir in (dataHome + ":" + dataDirs).Split(':', StringSplitOptions.RemoveEmptyEntries))
            foreach (var file in Files(Path.Combine(dir, "applications"), "*.desktop"))
                Add(apps, seen, Try(() => ParseDesktop(File.ReadLines(file))));

        foreach (var root in new[] { ".steam/steam", ".local/share/Steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
        {
            string vdf = Path.Combine(home, root, "steamapps/libraryfolders.vdf");
            foreach (var lib in Try(() => ParseLibraryFolders(File.ReadAllText(vdf))) ?? new List<string>())
                foreach (var acf in Files(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                    Add(apps, seen, Try(() => ParseSteamManifest(File.ReadAllText(acf))));
        }

        apps.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return apps;
    }

    private static void Add(List<CatalogApp> apps, HashSet<string> seen, CatalogApp? app)
    {
        if (app != null && seen.Add(app.Entry))
            apps.Add(app);
    }

    private static T? Try<T>(Func<T?> read) where T : class
    {
        try
        { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return null; }
    }

    private static IEnumerable<string> Files(string dir, string pattern)
        => Directory.Exists(dir) ? Try(() => Directory.GetFiles(dir, pattern)) ?? Array.Empty<string>() : Array.Empty<string>();

    /// <summary>Process name an Exec line starts, or null when it cannot be named (flatpak).</summary>
    public static string? CommFromExec(string exec)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in exec + " ")
        {
            if (c == '"')
                quoted = !quoted;
            else if (c == ' ' && !quoted)
            {
                if (cur.Length > 0)
                    tokens.Add(cur.ToString());
                cur.Clear();
            }
            else
                cur.Append(c);
        }

        int i = 0;
        while (i < tokens.Count && (tokens[i] == "env" || (tokens[i].Contains('=') && !tokens[i].StartsWith('/'))))
            i++;
        if (i >= tokens.Count)
            return null;
        string prog = Path.GetFileName(tokens[i]);
        if (prog == "flatpak" || prog.Length == 0)
            return null;
        if (prog.Length > GpuBlockPolicy.MaxNameLength)
            prog = prog[..GpuBlockPolicy.MaxNameLength];
        return GpuBlockPolicy.Validate(prog) == null ? prog : null;
    }

    /// <summary>The [Desktop Entry] group of a .desktop file as a catalog app.</summary>
    public static CatalogApp? ParseDesktop(IEnumerable<string> lines)
    {
        bool inEntry = false, hidden = false;
        string? name = null, exec = null, type = null;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }
            int eq = line.IndexOf('=');
            if (!inEntry || eq <= 0)
                continue;
            string key = line[..eq].Trim(), val = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "Name":
                    name ??= val;
                    break;
                case "Exec":
                    exec ??= val;
                    break;
                case "Type":
                    type ??= val;
                    break;
                case "NoDisplay" or "Hidden":
                    hidden |= val == "true";
                    break;
            }
        }
        if (hidden || type != "Application" || name == null || exec == null)
            return null;

        var steam = SteamRun.Match(exec);
        if (steam.Success)
            return new CatalogApp(name, GpuBlockPolicy.SteamEntry(steam.Groups[1].Value));
        string? comm = CommFromExec(exec);
        return comm == null ? null : new CatalogApp(name, comm);
    }

    /// <summary>A game from an appmanifest_*.acf; null for Steam's own tools.</summary>
    public static CatalogApp? ParseSteamManifest(string text)
    {
        var id = AcfAppId.Match(text);
        var name = AcfName.Match(text);
        if (!id.Success || !name.Success)
            return null;
        string title = name.Groups[1].Value;
        foreach (var tool in SteamTools)
            if (title.StartsWith(tool, StringComparison.Ordinal))
                return null;
        return new CatalogApp(title, GpuBlockPolicy.SteamEntry(id.Groups[1].Value));
    }

    /// <summary>Library paths listed in libraryfolders.vdf.</summary>
    public static List<string> ParseLibraryFolders(string vdf)
    {
        var list = new List<string>();
        foreach (Match m in VdfPath.Matches(vdf))
            list.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        return list;
    }
}
