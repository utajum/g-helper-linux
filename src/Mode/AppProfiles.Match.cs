using System.Text.RegularExpressions;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Mode;

// Pure part of AppProfiles (no /proc, no mode changes), compiled into the tests.
public static partial class AppProfiles
{
    private const string RestoreKey = "app_profile_restore";

    public record Rule(string Kind, string Value, int Mode);

    internal sealed class Proc
    {
        public int Pid;
        public string Comm = "", Exe = "", ExePath = "", Arg0 = "", Cmdline = "";
        public string? AppId, GameId, CompatId;
        public bool Game;
    }

    internal enum Act { None, Start, Switch, Restore, Release, Stop }

    /// <summary>Override state per tick; Step does the config writes and mode changes.</summary>
    internal sealed class State
    {
        public int Applied = -1;
        public bool UserOwned;
        private int _missed, _seen;

        public (Act Act, int Mode) Next(Rule? winner, int current, bool anyRules)
        {
            if (winner == null)
            {
                _seen = 0;
                if (Applied < 0 && !UserOwned)
                    return (anyRules ? Act.None : Act.Stop, -1);
                // One tick of grace so a game restart does not flip modes twice.
                if (++_missed < 2)
                    return (Act.None, -1);
                bool restore = Applied >= 0 && current == Applied;
                Applied = -1;
                UserOwned = false;
                return (restore ? Act.Restore : Act.Release, -1);
            }

            _missed = 0;
            if (UserOwned)
                return (Act.None, -1);
            if (Applied >= 0 && current != Applied)
            {
                Applied = -1;
                UserOwned = true;
                return (Act.Release, -1);
            }
            if (winner.Mode == Applied)
                return (Act.None, -1);
            // Wait one more tick: short helpers (seen at Steam start-up) match once.
            if (Applied < 0 && ++_seen < 2)
                return (Act.None, -1);
            var act = Applied < 0 ? Act.Start : Act.Switch;
            Applied = winner.Mode;
            return (act, winner.Mode);
        }
    }

    /// <summary>Startup: put back the mode saved before a crash or quit during a match.</summary>
    internal static void RestoreAfterCrash()
    {
        int saved = AppConfig.Get(RestoreKey);
        if (saved < 0)
            return;
        AppConfig.Set("performance_mode", saved);
        AppConfig.Remove(RestoreKey);
        Logger.WriteLine($"AppProfiles: restored mode {saved} after unclean exit");
    }

    /// <summary>First rule with a live matching process; game: goes last so a specific game wins.</summary>
    internal static Rule? Pick(IEnumerable<Rule> rules, IEnumerable<Proc> procs, Func<Proc, bool> live)
    {
        foreach (var rule in rules.OrderBy(r => r.Kind == "game"))
            foreach (var p in procs)
                if (Matches(rule, p) && live(p))
                    return rule;
        return null;
    }

    private static bool Matches(Rule r, Proc p) => r.Kind switch
    {
        "game" => p.Game,
        "steam" => p.Game && (p.AppId == r.Value || p.GameId == r.Value || p.CompatId == r.Value
            || (p.GameId != null && p.GameId == ShortcutGameId(r.Value))),
        "exe" when r.Value.Contains('/') => p.ExePath == r.Value
            || (r.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && p.Arg0.Equals(Path.GetFileName(r.Value), StringComparison.OrdinalIgnoreCase)),
        "exe" => p.Exe.Equals(r.Value, StringComparison.OrdinalIgnoreCase)
              || p.Arg0.Equals(r.Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    // Non-Steam shortcut ids have the high bit set; their 64-bit game id is
    // (id << 32) | 0x02000000, the form steam://rungameid takes.
    private static string? ShortcutGameId(string value) =>
        uint.TryParse(value, out uint id) && id >= 0x80000000 ? (((ulong)id << 32) | 0x02000000).ToString() : null;

    // A rule for an interpreter would match every script it runs.
    private static readonly Regex Interpreter =
        new(@"^(python|perl|ruby|lua|node|nodejs|java|bash|sh|dash|zsh|fish|ksh)[\d.]*$");

    internal static bool IsInterpreter(string exe) => Interpreter.IsMatch(exe);

    // Found by kind and value, so a stale row (old index or mode) cannot edit another rule.
    internal static List<Rule> Without(List<Rule> rules, Rule rule) =>
        rules.Where(r => !SameApp(r, rule)).ToList();

    internal static List<Rule> WithMode(List<Rule> rules, Rule rule, int mode) =>
        rules.Select(r => SameApp(r, rule) ? r with { Mode = mode } : r).ToList();

    /// <summary>Same target app (kind and value); the mode may differ.</summary>
    internal static bool SameApp(Rule a, Rule b) => a.Kind == b.Kind && a.Value == b.Value;
}
