// App profile rule matching and rule list edits. Pure functions, no sandbox state.

using GHelper.Linux.Helpers;
using GHelper.Linux.Mode;
using Act = GHelper.Linux.Mode.AppProfiles.Act;
using Proc = GHelper.Linux.Mode.AppProfiles.Proc;
using Rule = GHelper.Linux.Mode.AppProfiles.Rule;

namespace GHelper.Linux.Tests;

public static class AppProfilesTests
{
    private static readonly Rule AnyGame = new("game", "", 1);
    private static readonly Rule SteamClient = new("exe", "steam", 1);
    private static readonly Rule Game70 = new("steam", "70", 1);
    private static readonly Rule Game220780Balanced = new("steam", "220780", 0);

    private static Proc SteamGame(string appId) => new() { Comm = "game", Exe = "game", AppId = appId, Game = true };

    private static string Ids(IEnumerable<Rule> rules) => string.Join(",", rules.Select(r => $"{r.Kind}:{r.Value}|{r.Mode}"));

    // One State.Next per tick: (winner, current mode); actions joined, Start/Switch with their mode.
    private static string Run(AppProfiles.State s, bool anyRules, params (Rule? Winner, int Current)[] ticks) =>
        string.Join(",", ticks.Select(t =>
        {
            var (act, mode) = s.Next(t.Winner, t.Current, anyRules);
            return act is Act.Start or Act.Switch ? $"{act}({mode})" : act.ToString();
        }));

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- AppProfiles ---");

        Harness.Scenario("Pick_GameRuleBeatsAnyGameListedFirst", _ =>
            Harness.AssertEqual(Game220780Balanced,
                AppProfiles.Pick([AnyGame, Game220780Balanced], [SteamGame("220780")], _ => true), "rule"));

        Harness.Scenario("Pick_AnyGameCoversOtherGames", _ =>
            Harness.AssertEqual(AnyGame,
                AppProfiles.Pick([AnyGame, Game220780Balanced], [SteamGame("70")], _ => true), "rule"));

        Harness.Scenario("Pick_SteamClientMatchesExeRuleNotAnyGame", _ =>
        {
            var client = new Proc { Comm = "steam", Exe = "steam", Arg0 = "steam", ExePath = "/home/u/.local/share/Steam/ubuntu12_32/steam" };
            Harness.AssertEqual(SteamClient, AppProfiles.Pick([AnyGame, SteamClient], [client], _ => true), "rule");
            Harness.AssertEqual(null, AppProfiles.Pick([AnyGame], [client], _ => true), "rule without exe:steam");
        });

        Harness.Scenario("Pick_NonSteamShortcutByGameId", _ =>
        {
            // shortcuts.vdf appid 3547403854 runs as SteamGameId (id << 32) | 0x02000000
            var game = new Proc { Comm = "game", AppId = "0", GameId = "15235983538667913216", Game = true };
            var rule = new Rule("steam", "3547403854", 1);
            Harness.AssertEqual(rule, AppProfiles.Pick([rule], [game], _ => true), "rule");
        });

        Harness.Scenario("Pick_ProtonGameByCompatDataId", _ =>
        {
            var game = new Proc { Comm = "Game.exe", CompatId = "70", Game = true };
            Harness.AssertEqual(Game70, AppProfiles.Pick([Game70], [game], _ => true), "rule");
        });

        Harness.Scenario("Pick_ZombieMatchesNothing", _ =>
        {
            var zombie = SteamGame("70");
            zombie.Pid = 42;
            Harness.AssertEqual(null, AppProfiles.Pick([AnyGame, Game70], [zombie], p => p.Pid != 42), "rule");
        });

        Harness.Scenario("IsInterpreter_HidesScriptHostsOnly", _ =>
        {
            foreach (var exe in new[] { "python3.14", "python3", "bash", "sh", "node", "perl5.40" })
                Harness.Assert(AppProfiles.IsInterpreter(exe), $"{exe} is an interpreter");
            foreach (var exe in new[] { "steam", "heroic", "Game.exe", "blender", "shotcut", "bashtop" })
                Harness.Assert(!AppProfiles.IsInterpreter(exe), $"{exe} is not an interpreter");
        });

        Harness.Scenario("Without_DoubleClickKeepsOtherRules", _ =>
        {
            var once = AppProfiles.Without([AnyGame, SteamClient, Game70], SteamClient);
            Harness.AssertEqual("game:|1,steam:70|1", Ids(AppProfiles.Without(once, SteamClient)), "rules");
        });

        Harness.Scenario("Without_StaleRowWithOldModeStillRemoves", _ =>
            Harness.AssertEqual("game:|1",
                Ids(AppProfiles.Without([AnyGame, SteamClient with { Mode = 0 }], SteamClient)), "rules"));

        Harness.Scenario("WithMode_FindsRuleAfterEarlierRowRemoved", _ =>
            // the row for exe:steam was built at index 1; index 1 is now steam:70
            Harness.AssertEqual("exe:steam|0,steam:70|1",
                Ids(AppProfiles.WithMode([SteamClient, Game70], SteamClient, 0)), "rules"));

        // Modes: 0 Balanced, 1 Turbo, 2 Silent. AnyGame -> Turbo, Game220780Balanced -> Balanced.

        Harness.Scenario("State_Start_NeedsTwoTicks", _ =>
            Harness.AssertEqual("None,Start(1)",
                Run(new(), true, (AnyGame, 0), (AnyGame, 0)), "actions"));

        Harness.Scenario("State_ShortHelper_NeverApplies", _ =>
            // Steam start-up helper: one tick with a match, then gone
            Harness.AssertEqual("None,None,None,None,None",
                Run(new(), true, (AnyGame, 0), (null, 0), (null, 0), (AnyGame, 0), (null, 0)), "actions"));

        Harness.Scenario("State_Exit_GraceThenRestore", _ =>
            Harness.AssertEqual("None,Start(1),None,Restore",
                Run(new(), true, (AnyGame, 0), (AnyGame, 0), (null, 1), (null, 1)), "actions"));

        Harness.Scenario("State_GameRestart_WithinGrace_NoFlip", _ =>
            Harness.AssertEqual("None,Start(1),None,None,None",
                Run(new(), true, (AnyGame, 0), (AnyGame, 0), (null, 1), (AnyGame, 1), (null, 1)), "actions"));

        Harness.Scenario("State_HandPick_ReleasesAndStaysOff", _ =>
            // Silent picked by hand while the game runs, then the game exits
            Harness.AssertEqual("None,Start(1),Release,None,None,None,Release",
                Run(new(), true, (AnyGame, 0), (AnyGame, 0), (AnyGame, 2), (AnyGame, 2), (AnyGame, 2),
                    (null, 2), (null, 2)), "actions"));

        Harness.Scenario("State_RuleSwitch_Immediate", _ =>
            Harness.AssertEqual("None,Start(1),Switch(0)",
                Run(new(), true, (AnyGame, 0), (AnyGame, 0), (Game220780Balanced, 1)), "actions"));

        Harness.Scenario("State_LastRuleRemoved_RestoresThenStops", _ =>
        {
            var s = new AppProfiles.State();
            Harness.AssertEqual("None,Start(1)", Run(s, true, (AnyGame, 0), (AnyGame, 0)), "with rules");
            Harness.AssertEqual("None,Restore,Stop", Run(s, false, (null, 1), (null, 1), (null, 0)), "rules removed");
        });

        Harness.Scenario("RestoreAfterCrash_PutsSavedModeBack", _ =>
        {
            AppConfig.Set("app_profile_restore", 0);
            AppConfig.Set("performance_mode", 1);
            AppConfig.Set("performance_1", 2);
            AppProfiles.RestoreAfterCrash();
            Harness.AssertEqual(0, AppConfig.Get("performance_mode"), "performance_mode");
            Harness.AssertEqual(-1, AppConfig.Get("app_profile_restore"), "restore key");
            Harness.AssertEqual(2, AppConfig.Get("performance_1"), "AC memory");
        });

        Harness.Scenario("RestoreAfterCrash_NoKeyNoChange", _ =>
        {
            AppConfig.Set("performance_mode", 1);
            AppProfiles.RestoreAfterCrash();
            Harness.AssertEqual(1, AppConfig.Get("performance_mode"), "performance_mode");
        });
    }
}
