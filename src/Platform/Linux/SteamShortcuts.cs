using System.Text;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Adds/removes G-Helper as a non-Steam app by editing Steam's binary
/// shortcuts.vdf, mirroring Heroic Games Launcher's implementation
/// (src/backend/shortcuts/nonesteamgame). Applies to every Steam user
/// found under userdata/ (folders "0" and "ac" are skipped, like Heroic).
/// The appid derivation is the steam-rom-manager algorithm: the crc32 of
/// quoted-exe + appname with the high bit forced, as a signed int32.
/// Steam only picks the change up after a restart.
/// </summary>
public static class SteamShortcuts
{
    public const string AppName = "G-Helper";

    // SteamOS detection, same env checks as Heroic (constants/environment.ts).
    // The vdf editing itself has no SteamOS special case; this is for logging.
    public static bool IsSteamDeckGameMode =>
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") == "gamescope";

    // Any SteamOS session that is not gamescope. Keyed off os-release, not
    // the /home/deck username, so renamed users and SteamOS derivatives
    // still match.
    public static bool IsSteamDeckDesktopMode =>
        ImmutableOs.IsSteamOs && !IsSteamDeckGameMode;

    public static bool IsSteamDeck => IsSteamDeckGameMode || IsSteamDeckDesktopMode;

    // Test hook: overrides the Steam root search (assigned by the external
    // vdf round-trip test harness, never in the app itself).
    internal static string? RootOverride = null;

    // Steam discovery

    /// <summary>Steam installation root, or null when Steam is not present.
    /// Covers native (also SteamOS), the classic ~/.steam symlink, flatpak
    /// and snap installs.</summary>
    public static string? FindSteamRoot()
    {
        if (RootOverride != null)
            return Directory.Exists(RootOverride) ? RootOverride : null;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
        ];
        foreach (var root in roots)
            if (Directory.Exists(Path.Combine(root, "userdata")))
                return root;
        return null;
    }

    /// <summary>The userdata dir shown in the integrity row, or null.</summary>
    public static string? UserdataPath()
    {
        var root = FindSteamRoot();
        return root == null ? null : Path.Combine(root, "userdata");
    }

    /// <summary>True when Steam is installed and has at least one user.</summary>
    public static bool IsSteamAvailable() => UserFolders().Count > 0;

    private static List<string> UserFolders()
    {
        var list = new List<string>();
        var userdata = UserdataPath();
        if (userdata == null)
            return list;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(userdata))
            {
                var name = Path.GetFileName(dir);
                if (name is "0" or "ac")
                    continue;
                list.Add(dir);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"SteamShortcuts: enumerate {userdata} failed: {ex.Message}");
        }
        return list;
    }

    // Public API (called from the Updates window row and bulk uninstall)

    /// <summary>True when our shortcut exists for at least one Steam user.</summary>
    public static bool IsAdded()
    {
        foreach (var user in UserFolders())
        {
            var file = Path.Combine(user, "config", "shortcuts.vdf");
            if (!File.Exists(file))
                continue;
            try
            {
                var root = ParseFile(file);
                if (FindOurEntry(GetShortcutsMap(root)) >= 0)
                    return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"SteamShortcuts: parse {file} failed: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>Non-Steam games in every user's library, without our own
    /// entry: unsigned appid, name and icon path ("" when unset).</summary>
    public static List<(uint Id, string Name, string Icon)> ListOthers()
    {
        var list = new List<(uint, string, string)>();
        foreach (var user in UserFolders())
        {
            var file = Path.Combine(user, "config", "shortcuts.vdf");
            if (!File.Exists(file))
                continue;
            try
            {
                foreach (var (_, value) in GetShortcutsMap(ParseFile(file)).Items)
                {
                    if (value is not VdfMap entry)
                        continue;
                    string name = "", icon = "";
                    foreach (var (key, v) in entry.Items)
                    {
                        if (v is string s && key.Equals("appname", StringComparison.OrdinalIgnoreCase))
                            name = s;
                        else if (v is string i && key.Equals("icon", StringComparison.OrdinalIgnoreCase))
                            icon = i.Trim('"');
                    }
                    uint id = EntryShortId(entry, 0);
                    if (id != 0 && name != "" && name != AppName)
                        list.Add((id, name, icon));
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"SteamShortcuts: parse {file} failed: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>Add the shortcut for every Steam user. True when at least one
    /// user was updated or already had it (Heroic's partial-success rule).</summary>
    public static bool Add(out string error)
    {
        error = "";
        var users = UserFolders();
        if (users.Count == 0)
        {
            error = "no Steam users found";
            return false;
        }

        string exe = QuoteField(ResolveExePath());
        string startDir = QuoteField(Path.GetDirectoryName(Unquote(exe)) ?? "/");
        string icon = ResolveIconPath();
        int appid = GenerateShortcutId(exe, AppName);
        Logger.WriteLine($"SteamShortcuts: add exe={exe} appid={appid} steamdeck={IsSteamDeck}");

        var errors = new List<string>();
        bool added = false;
        foreach (var user in users)
        {
            try
            {
                var configDir = Path.Combine(user, "config");
                Directory.CreateDirectory(configDir);
                var file = Path.Combine(configDir, "shortcuts.vdf");

                VdfMap root = File.Exists(file) ? ParseFile(file) : NewRoot();
                var shortcuts = GetShortcutsMap(root);

                int existing = FindOurEntry(shortcuts);
                if (existing < 0)
                {
                    shortcuts.Items.Add(("", BuildEntry(appid, exe, startDir, icon)));
                    RenumberChildren(shortcuts);
                    WriteFileAtomic(file, root);
                }

                // Grid artwork so Big Picture shows a real card, keyed by the
                // entry's actual appid (a pre-existing entry may have been
                // created with a different exe path, hence a different id).
                uint shortId = existing >= 0
                    ? EntryShortId((VdfMap)shortcuts.Items[existing].Value, appid)
                    : unchecked((uint)appid);
                SteamGridArt.Write(configDir, shortId, icon);
                added = true;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(user)}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            Logger.WriteLine($"SteamShortcuts: add errors: {error}");
        }
        return added;
    }

    /// <summary>Remove the shortcut from every Steam user. True when no user
    /// still has it afterwards.</summary>
    public static bool Remove(out string error)
    {
        error = "";
        var errors = new List<string>();
        foreach (var user in UserFolders())
        {
            var file = Path.Combine(user, "config", "shortcuts.vdf");
            if (!File.Exists(file))
                continue;
            try
            {
                var root = ParseFile(file);
                var shortcuts = GetShortcutsMap(root);
                int index = FindOurEntry(shortcuts);
                if (index < 0)
                    continue;
                uint shortId = EntryShortId((VdfMap)shortcuts.Items[index].Value, 0);
                shortcuts.Items.RemoveAt(index);
                RenumberChildren(shortcuts);
                WriteFileAtomic(file, root);
                if (shortId != 0)
                    SteamGridArt.Remove(Path.Combine(user, "config"), shortId);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(user)}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            Logger.WriteLine($"SteamShortcuts: remove errors: {error}");
            return false;
        }
        return true;
    }

    // Entry construction

    private static VdfMap NewRoot()
    {
        var root = new VdfMap();
        root.Items.Add(("shortcuts", new VdfMap()));
        return root;
    }

    /// <summary>The "shortcuts" child map (case-insensitive), created if absent.</summary>
    private static VdfMap GetShortcutsMap(VdfMap root)
    {
        foreach (var (key, value) in root.Items)
            if (key.Equals("shortcuts", StringComparison.OrdinalIgnoreCase) && value is VdfMap m)
                return m;
        var created = new VdfMap();
        root.Items.Add(("shortcuts", created));
        return created;
    }

    /// <summary>Index of our entry in the shortcuts map, matched by AppName
    /// (case-insensitive key, like Heroic's getAppName), or -1.</summary>
    private static int FindOurEntry(VdfMap shortcuts)
    {
        for (int i = 0; i < shortcuts.Items.Count; i++)
        {
            if (shortcuts.Items[i].Value is not VdfMap entry)
                continue;
            foreach (var (key, value) in entry.Items)
                if (key.Equals("appname", StringComparison.OrdinalIgnoreCase)
                    && value is string name && name == AppName)
                    return i;
        }
        return -1;
    }

    /// <summary>Same field set Heroic writes, plus the standard empty
    /// ShortcutPath/DevkitGameID/tags that Valve's own writer emits.</summary>
    private static VdfMap BuildEntry(int appid, string exe, string startDir, string icon)
    {
        var e = new VdfMap();
        e.Items.Add(("appid", appid));
        e.Items.Add(("AppName", AppName));
        e.Items.Add(("Exe", exe));
        e.Items.Add(("StartDir", startDir));
        e.Items.Add(("icon", icon));
        e.Items.Add(("ShortcutPath", ""));
        e.Items.Add(("LaunchOptions", ""));
        e.Items.Add(("IsHidden", 0));
        e.Items.Add(("AllowDesktopConfig", 1));
        e.Items.Add(("AllowOverlay", 1));
        e.Items.Add(("OpenVR", 0));
        e.Items.Add(("Devkit", 0));
        e.Items.Add(("DevkitGameID", ""));
        e.Items.Add(("DevkitOverrideAppID", 0));
        e.Items.Add(("LastPlayTime", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        e.Items.Add(("tags", new VdfMap()));
        return e;
    }

    /// <summary>The unsigned appid stored in an entry, used to key grid
    /// artwork file names. Falls back to the given id when absent.</summary>
    private static uint EntryShortId(VdfMap entry, int fallback)
    {
        foreach (var (key, value) in entry.Items)
            if (key.Equals("appid", StringComparison.OrdinalIgnoreCase) && value is int id)
                return unchecked((uint)id);
        return unchecked((uint)fallback);
    }

    /// <summary>Rewrite the shortcut map keys as contiguous "0".."n-1", the
    /// layout Valve's writer produces. Entry content is untouched.</summary>
    private static void RenumberChildren(VdfMap shortcuts)
    {
        for (int i = 0; i < shortcuts.Items.Count; i++)
            shortcuts.Items[i] = (i.ToString(), shortcuts.Items[i].Value);
    }

    /// <summary>Launcher path for the Exe field. AppImage and NixOS aware;
    /// a bare PATH name (NixOS) is resolved to an absolute path.</summary>
    private static string ResolveExePath()
    {
        string exe = LinuxSystemIntegration.ResolveLauncherExec();
        if (Path.IsPathRooted(exe))
            return exe;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            if (dir.Length == 0)
                continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return Environment.ProcessPath ?? exe;
    }

    /// <summary>Icon for the Steam library entry: the installed PNG when
    /// present, otherwise the embedded one written to the user icon path
    /// (user-writable on every distro, including SteamOS and Atomic).
    /// Empty string when nothing is available (Steam shows a default).</summary>
    private static string ResolveIconPath()
    {
        if (NixOS.IsNixOS)
        {
            var nix = NixOS.IconFilePath();
            if (nix != null)
                return nix;
        }

        const string system = "/usr/share/icons/hicolor/256x256/apps/ghelper.png";
        if (File.Exists(system))
            return system;

        string user = ImmutableOs.UserIconPath();
        if (File.Exists(user))
            return user;

        try
        {
            byte[]? png = Install.Installer.GetEmbedded("ghelper.png");
            if (png != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(user)!);
                File.WriteAllBytes(user, png);
                return user;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"SteamShortcuts: icon extract failed: {ex.Message}");
        }
        return "";
    }

    private static string QuoteField(string path) => $"\"{path}\"";

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    // appid (steam-rom-manager algorithm, as used by Heroic)

    /// <summary>crc32(quotedExe + appName) with the high bit forced, as a
    /// signed int32. This is what shortcuts.vdf stores in "appid".</summary>
    internal static int GenerateShortcutId(string quotedExe, string appName)
        => unchecked((int)(Crc32(quotedExe + appName) | 0x80000000u));

    private static uint Crc32(string s)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    // Binary VDF (the subset Steam uses in shortcuts.vdf)
    //
    // File layout: a root map body. Each entry starts with a type byte,
    // then a null-terminated UTF-8 key, then the payload. 0x08 ends a map.
    //   0x00 nested map   0x01 string (null-terminated)   0x02 int32 LE
    // Fixed-size types 0x03/0x04/0x06 (4 bytes) and 0x07/0x0A (8 bytes) are
    // preserved as raw bytes. Anything else aborts the parse, and a failed
    // parse never writes.

    internal sealed class VdfMap
    {
        public List<(string Key, object Value)> Items = new();
    }

    private readonly record struct VdfRaw(byte Type, byte[] Bytes);

    internal static VdfMap ParseFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int pos = 0;
        var root = ParseMapBody(data, ref pos, isRoot: true);
        if (pos != data.Length)
            throw new InvalidDataException($"trailing bytes at {pos}");
        return root;
    }

    /// <summary>Parse entries until the closing 0x08. The root map may also
    /// end at EOF (writers differ on the final terminator).</summary>
    private static VdfMap ParseMapBody(byte[] d, ref int pos, bool isRoot = false)
    {
        var map = new VdfMap();
        while (true)
        {
            if (pos >= d.Length)
            {
                if (isRoot)
                    return map;
                throw new InvalidDataException("truncated map");
            }
            byte type = d[pos++];
            if (type == 0x08)
                return map;

            string key = ReadCString(d, ref pos);
            object value = type switch
            {
                0x00 => ParseMapBody(d, ref pos),
                0x01 => ReadCString(d, ref pos),
                0x02 => ReadInt32(d, ref pos),
                0x03 or 0x04 or 0x06 => new VdfRaw(type, ReadBytes(d, ref pos, 4)),
                0x07 or 0x0A => new VdfRaw(type, ReadBytes(d, ref pos, 8)),
                _ => throw new InvalidDataException($"unsupported vdf type 0x{type:X2} at {pos - 1}"),
            };
            map.Items.Add((key, value));
        }
    }

    private static string ReadCString(byte[] d, ref int pos)
    {
        int start = pos;
        while (pos < d.Length && d[pos] != 0)
            pos++;
        if (pos >= d.Length)
            throw new InvalidDataException("unterminated string");
        var s = Encoding.UTF8.GetString(d, start, pos - start);
        pos++;
        return s;
    }

    private static int ReadInt32(byte[] d, ref int pos)
    {
        if (pos + 4 > d.Length)
            throw new InvalidDataException("truncated int32");
        int v = BitConverter.ToInt32(d, pos);
        pos += 4;
        return v;
    }

    private static byte[] ReadBytes(byte[] d, ref int pos, int count)
    {
        if (pos + count > d.Length)
            throw new InvalidDataException("truncated value");
        var b = new byte[count];
        Array.Copy(d, pos, b, 0, count);
        pos += count;
        return b;
    }

    internal static byte[] Serialize(VdfMap root)
    {
        using var ms = new MemoryStream();
        WriteMapBody(ms, root);
        return ms.ToArray();
    }

    private static void WriteMapBody(MemoryStream ms, VdfMap map)
    {
        foreach (var (key, value) in map.Items)
        {
            switch (value)
            {
                case VdfMap child:
                    ms.WriteByte(0x00);
                    WriteCString(ms, key);
                    WriteMapBody(ms, child);
                    break;
                case string s:
                    ms.WriteByte(0x01);
                    WriteCString(ms, key);
                    WriteCString(ms, s);
                    break;
                case int i:
                    ms.WriteByte(0x02);
                    WriteCString(ms, key);
                    ms.Write(BitConverter.GetBytes(i));
                    break;
                case VdfRaw raw:
                    ms.WriteByte(raw.Type);
                    WriteCString(ms, key);
                    ms.Write(raw.Bytes);
                    break;
                default:
                    throw new InvalidDataException($"unserializable value for '{key}'");
            }
        }
        ms.WriteByte(0x08);
    }

    private static void WriteCString(MemoryStream ms, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        ms.Write(b);
        ms.WriteByte(0x00);
    }

    /// <summary>Atomic replace via tmp + rename. Keeps a one-time backup of
    /// the pre-ghelper original next to the file.</summary>
    private static void WriteFileAtomic(string path, VdfMap root)
    {
        byte[] data = Serialize(root);
        string backup = path + ".ghelper-bak";
        try
        {
            if (File.Exists(path) && !File.Exists(backup))
                File.Copy(path, backup);
        }
        catch { }

        string tmp = path + ".ghnew";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
        Logger.WriteLine($"SteamShortcuts: wrote {path} ({data.Length} bytes)");
    }
}
