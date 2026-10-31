using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.Mode;

/// <summary>
/// Per-app performance modes. While a process matching a rule runs, the
/// rule's mode is applied without touching the per-power-source memory; when
/// the last match exits, that memory's mode comes back. A mode picked by hand
/// (or by AC/battery switch) during a match wins until the matches exit.
/// Rules are kept in order, first match wins (game: last), one "kind:value|mode" per line:
///   game:         any game (Steam, Heroic, Lutris or Proton marks in environ)
///   steam:appid   SteamAppId / SteamGameId / compatdata id in environ;
///                 non-Steam shortcuts use their unsigned shortcuts.vdf appid
///   exe:name      basename of /proc/pid/exe or argv[0] (Wine: Game.exe)
///   exe:/path     /proc/pid/exe target; a Wine .exe by its file name
/// </summary>
public static partial class AppProfiles
{
    private const string RulesKey = "app_profiles";
    private const int PollMs = 3000;

    // Not games, but the running-programs list offers them for exe: rules.
    private static readonly HashSet<string> Launchers = ["steam", "heroic"];

    // Carry a game's environment but run without it (shader pre-caching,
    // launcher save sync) or outlive it (Wine services). comm is cut to 15.
    private static readonly HashSet<string> NotGames =
    [
        .. Launchers, "steamwebhelper", "fossilize_repla",
        "legendary", "gogdl", "nile", "comet", "lutris",
        "wineserver", "wineconsole", "winemenubuilder", "services.exe", "winedevice.exe",
        "plugplay.exe", "explorer.exe", "rpcss.exe", "svchost.exe", "tabtip.exe",
        "conhost.exe", "rundll32.exe", "mscorsvw.exe", "iexplore.exe", "winedbg.exe",
        "start.exe", "wineboot.exe", "steam.exe",
    ];

    private static readonly Dictionary<int, Proc?> _procs = new();
    private static Timer? _timer;
    private static readonly object _timerLock = new();
    private static int _running;
    private static readonly State _state = new();

    public static List<Rule> Load()
    {
        var rules = new List<Rule>();
        foreach (var line in (AppConfig.GetString(RulesKey) ?? "").Split('\n'))
        {
            int colon = line.IndexOf(':'), bar = line.LastIndexOf('|');
            if (colon > 0 && bar > colon && int.TryParse(line[(bar + 1)..], out int mode))
                rules.Add(new Rule(line[..colon], line[(colon + 1)..bar], mode));
        }
        return rules;
    }

    public static void Save(List<Rule> rules)
    {
        AppConfig.Set(RulesKey, string.Join("\n", rules.Select(r => $"{r.Kind}:{r.Value}|{r.Mode}")));
        Start();
    }

    /// <summary>Startup, before the saved mode is applied.</summary>
    public static void Init()
    {
        RestoreAfterCrash();
        Start();
    }

    // Polls only while rules exist; Step stops it once the last rule is gone.
    private static void Start()
    {
        lock (_timerLock)
            if (_timer == null && Load().Count > 0)
                _timer = new Timer(Tick, null, PollMs, PollMs);
    }

    private static void Stop()
    {
        lock (_timerLock)
        {
            // Save() may have added a rule since this tick loaded the list.
            if (Load().Count > 0)
                return;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static void Tick(object? state)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;
        try
        { Step(); }
        catch (Exception ex) { Logger.WriteLine("AppProfiles: tick failed", ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static void Step()
    {
        var rules = Load();
        // Stale entries would outlive pid reuse while nothing is scanned.
        if (rules.Count == 0)
            _procs.Clear();
        var winner = rules.Count > 0 ? Match(rules) : null;
        int current = Modes.GetCurrent();

        var (act, mode) = _state.Next(winner, current, rules.Count > 0);
        switch (act)
        {
            case Act.Start or Act.Switch:
                if (act == Act.Start)
                    AppConfig.Set(RestoreKey, current);
                Logger.WriteLine($"AppProfiles: {winner!.Kind}:{winner.Value} running, {Modes.GetName(mode)}");
                if (current != mode)
                    Apply(mode, persist: false);
                break;
            case Act.Restore:
                bool onAc = App.Power?.IsOnAcPower() ?? true;
                int back = AppConfig.Get($"performance_{(onAc ? 1 : 0)}", AppConfig.Get(RestoreKey, 0));
                Logger.WriteLine($"AppProfiles: no match left, back to {Modes.GetName(back)}");
                if (back != current)
                    Apply(back, persist: true);
                AppConfig.Remove(RestoreKey);
                break;
            case Act.Release:
                if (winner != null)
                    Logger.WriteLine($"AppProfiles: mode changed to {Modes.GetName(current)} outside, hands off until the match exits");
                AppConfig.Remove(RestoreKey);
                break;
            case Act.Stop:
                Stop();
                break;
        }
    }

    /// <summary>For diagnostics: mode ids only, no app names.</summary>
    public static string Status => _state.Applied >= 0
        ? $"active, mode {_state.Applied}, restore {AppConfig.Get(RestoreKey)}"
        : _state.UserOwned ? "mode changed by hand during a match" : "idle";

    private static void Apply(int mode, bool persist)
    {
        App.Mode?.SetPerformanceMode(mode, notify: true, persist: persist);
        App.UpdateTrayIcon();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => App.MainWindowInstance?.RefreshPerformanceMode());
    }

    private static Rule? Match(List<Rule> rules)
    {
        var alive = new HashSet<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid))
                continue;
            alive.Add(pid);
            // exec and Wine's argv rewrite change cmdline after the first read.
            if (!_procs.TryGetValue(pid, out var known) || (known != null && known.Cmdline != Cmdline(pid)))
                _procs[pid] = Read(pid);
        }
        foreach (int pid in _procs.Keys.Where(p => !alive.Contains(p)).ToList())
            _procs.Remove(pid);

        return Pick(rules.Where(r => Modes.Exists(r.Mode)), _procs.Values.OfType<Proc>(), p => !IsZombie(p.Pid));
    }

    /// <summary>Own processes only: environ and exe of others are unreadable.</summary>
    private static Proc? Read(int pid)
    {
        try
        {
            var p = new Proc
            {
                Pid = pid,
                Comm = File.ReadAllText($"/proc/{pid}/comm").Trim(),
                ExePath = new FileInfo($"/proc/{pid}/exe").LinkTarget ?? "",
            };
            p.Exe = Path.GetFileName(p.ExePath);
            p.Cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
            var argv = p.Cmdline.Split('\0')[0];
            p.Arg0 = argv[(argv.LastIndexOfAny(['/', '\\']) + 1)..];

            bool marked = false;
            foreach (var kv in Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{pid}/environ")).Split('\0'))
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0)
                    continue;
                switch (kv[..eq])
                {
                    case "SteamAppId":
                        p.AppId = kv[(eq + 1)..];
                        break;
                    case "SteamGameId":
                        p.GameId = kv[(eq + 1)..];
                        break;
                    case "STEAM_COMPAT_DATA_PATH":
                        marked = true;
                        p.CompatId = Path.GetFileName(kv[(eq + 1)..].TrimEnd('/'));
                        break;
                    case "HEROIC_APP_NAME" or "LUTRIS_GAME_UUID":
                        marked = true;
                        break;
                }
            }
            // Heroic sets SteamAppId=0 and SteamGameId=heroic-<id> for Proton.
            p.Game = !NotGames.Contains(p.Comm.ToLowerInvariant())
                && (marked || p.AppId is not (null or "" or "0") || p.GameId is not (null or "" or "0"));
            return p;
        }
        catch
        {
            return null;
        }
    }

    private static string? Cmdline(int pid)
    {
        try
        { return File.ReadAllText($"/proc/{pid}/cmdline"); }
        catch { return null; }
    }

    [DllImport("libc")]
    private static extern IntPtr realpath(string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr ptr);

    /// <summary>Path with every symlink resolved, as /proc/pid/exe shows it.</summary>
    public static string RealPath(string path)
    {
        IntPtr resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            return path;
        try
        { return Marshal.PtrToStringUTF8(resolved) ?? path; }
        finally { free(resolved); }
    }

    /// <summary>ELF binary or Wine .exe; scripts and .desktop files never show up as /proc/pid/exe.</summary>
    public static bool IsProgram(string path)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return f.Read(magic) == 4 && magic.SequenceEqual("\u007FELF"u8);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsZombie(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int i = stat.LastIndexOf(')');
            return i > 0 && i + 2 < stat.Length && stat[i + 2] == 'Z';
        }
        catch
        {
            return true;
        }
    }

    /// <summary>First entry of the add list; needs no scan.</summary>
    public static (Rule Rule, string Name) AnyGame() => (new Rule("game", "", 1), Labels.Get("app_profile_any_game"));

    /// <summary>Rules the user can add: any game, installed Steam games, own
    /// running programs. New rules default to Turbo.</summary>
    public static List<(Rule Rule, string Name)> Candidates()
    {
        var list = new List<(Rule, string)> { AnyGame() };
        foreach (var (id, name) in SteamGames().OrderBy(g => g.Name))
            list.Add((new Rule("steam", id, 1), $"Steam: {name}"));

        var exes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), out int pid) && Read(pid) is { } p
                && (Launchers.Contains(p.Comm) || !NotGames.Contains(p.Comm.ToLowerInvariant())))
                exes.Add(p.Arg0.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Arg0 : p.Exe);
        foreach (var exe in exes.Where(e => e != "" && !IsInterpreter(e)))
            list.Add((new Rule("exe", exe, 1), exe));
        return list;
    }

    private static List<(string Id, string Name)> SteamGames()
    {
        var games = new List<(string, string)>();
        string? root = Platform.Linux.SteamShortcuts.FindSteamRoot();
        if (root == null)
            return games;

        var libraries = new List<string> { root };
        try
        {
            string vdf = File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
            libraries.AddRange(Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value));
        }
        catch { }

        var seen = new HashSet<string>();
        foreach (var lib in libraries)
        {
            try
            {
                foreach (var acf in Directory.EnumerateFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                {
                    string text = File.ReadAllText(acf);
                    string id = Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    string name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    // Runtimes and Proton builds install as apps too.
                    if (id == "" || name == "" || name.StartsWith("Proton") || name.StartsWith("Steam Linux Runtime")
                        || name.StartsWith("Steamworks Common") || !seen.Add(id))
                        continue;
                    games.Add((id, name));
                }
            }
            catch { }
        }
        // Non-Steam games added to the library
        foreach (var (id, name, _) in Platform.Linux.SteamShortcuts.ListOthers())
            if (seen.Add(id.ToString()))
                games.Add((id.ToString(), name));
        return games;
    }

    /// <summary>Icon file lookup for rules: Steam's library icon, else the
    /// icon of the .desktop entry that starts the exe. Null when none.</summary>
    public static Func<Rule, string?> IconFinder()
    {
        string? steam = Platform.Linux.SteamShortcuts.FindSteamRoot();
        Dictionary<string, (string Icon, int Rank)>? desktop = null;
        Dictionary<string, string>? icons = null;
        Dictionary<string, string>? shortcuts = null;
        // Icon= value to a file: absolute path, else the icon index.
        string? Theme(string? name) => string.IsNullOrEmpty(name) ? null
            : Path.IsPathRooted(name) ? (File.Exists(name) ? name : null)
            : (icons ??= IconIndex()).GetValueOrDefault(name);
        string? ShortcutIcon(string id)
        {
            if (shortcuts == null)
            {
                shortcuts = new();
                foreach (var (sid, _, icon) in Platform.Linux.SteamShortcuts.ListOthers())
                    if (icon != "")
                        shortcuts.TryAdd(sid.ToString(), icon);
            }
            return shortcuts.GetValueOrDefault(id);
        }
        return rule => rule.Kind switch
        {
            "steam" => SteamIcon(steam, rule.Value) ?? Theme(ShortcutIcon(rule.Value)) ?? Theme($"steam_icon_{rule.Value}"),
            "exe" => Theme((desktop ??= DesktopIcons()).TryGetValue(Path.GetFileName(rule.Value), out var d) ? d.Icon
                : rule.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? null : Path.GetFileName(rule.Value)),
            _ => null,
        };
    }

    // Current clients: librarycache/<appid>/<sha1>.jpg (32x32). Older: <appid>_icon.jpg.
    private static string? SteamIcon(string? root, string appId)
    {
        if (root == null || appId == "" || !appId.All(char.IsAsciiDigit))
            return null;
        string cache = Path.Combine(root, "appcache", "librarycache");
        try
        {
            var icon = Directory.EnumerateFiles(Path.Combine(cache, appId), "*.jpg")
                .FirstOrDefault(f => Regex.IsMatch(Path.GetFileName(f), "^[0-9a-f]{40}\\.jpg$"));
            if (icon != null)
                return icon;
        }
        catch { }
        string old = Path.Combine(cache, $"{appId}_icon.jpg");
        return File.Exists(old) ? old : null;
    }

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // XDG data dirs, user first so its entries win.
    private static List<string> DataDirs()
    {
        var dirs = new List<string>
        {
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home ? home : Path.Combine(Home, ".local", "share"),
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } x ? x : "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(Home, ".local", "share", "flatpak", "exports", "share"));
        dirs.Add("/var/lib/flatpak/exports/share");
        return dirs.Distinct().ToList();
    }

    /// <summary>Exe name to Icon= of the .desktop entry that starts it. The
    /// file name outranks Exec, so launcher shortcuts (steam://, web apps)
    /// do not take over the launcher's own icon.</summary>
    private static Dictionary<string, (string Icon, int Rank)> DesktopIcons()
    {
        var map = new Dictionary<string, (string Icon, int Rank)>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var dir in DataDirs())
        {
            string apps = Path.Combine(dir, "applications");
            if (!Directory.Exists(apps))
                continue;
            foreach (var file in Directory.EnumerateFiles(apps, "*.desktop", options))
            {
                try
                {
                    string icon = "";
                    var names = new List<string>();
                    bool entry = false;
                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.StartsWith('['))
                        {
                            if (entry)
                                break;
                            entry = line.Trim() == "[Desktop Entry]";
                            continue;
                        }
                        int eq = line.IndexOf('=');
                        if (!entry || eq <= 0)
                            continue;
                        string value = line[(eq + 1)..].Trim();
                        switch (line[..eq].Trim())
                        {
                            case "Icon":
                                icon = value;
                                break;
                            case "TryExec" or "StartupWMClass":
                                names.Add(Path.GetFileName(value));
                                break;
                            case "Exec":
                                names.AddRange(ExecNames(value));
                                break;
                        }
                    }
                    if (icon == "")
                        continue;
                    var all = names.Select(n => (n, 1)).Prepend((Path.GetFileNameWithoutExtension(file), 0));
                    foreach (var (name, rank) in all)
                        if (name != "" && (!map.TryGetValue(name, out var had) || rank < had.Rank))
                            map[name] = (icon, rank);
                }
                catch { }
            }
        }
        return map;
    }

    // The program of an Exec line (after env and VAR=value) and flatpak's --command=.
    private static IEnumerable<string> ExecNames(string exec)
    {
        bool program = false;
        foreach (var raw in exec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim('"', '\'');
            if (token.StartsWith("--command="))
                yield return token["--command=".Length..];
            else if (!program && token != "env" && !token.Contains('='))
            {
                program = true;
                yield return Path.GetFileName(token);
            }
        }
    }

    private static readonly string[] IconSizes =
        ["48x48", "64x64", "32x32", "128x128", "96x96", "256x256", "512x512", "scalable", "24x24", "16x16"];

    /// <summary>Icon name to file from the hicolor apps dirs, best size first, then pixmaps.</summary>
    private static Dictionary<string, string> IconIndex()
    {
        var index = new Dictionary<string, string>();
        var roots = DataDirs().Select(d => Path.Combine(d, "icons", "hicolor")).Prepend(Path.Combine(Home, ".icons", "hicolor")).ToList();
        var dirs = IconSizes.SelectMany(size => roots.Select(root => Path.Combine(root, size, "apps"))).Append("/usr/share/pixmaps");
        foreach (var dir in dirs.Where(Directory.Exists))
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                    if (file.EndsWith(".png") || file.EndsWith(".svg"))
                        index.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
            catch { }
        return index;
    }
}
