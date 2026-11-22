// In-app bug report: cleanup, request JSON, reply parsing, text rules, draft and issue links on the sandbox config.

using System.Text.Json;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Tests;

public static class BugReportTests
{
    private static readonly BugReport.Info Info = new(
        "1.0.100", "binary", "ROG Strix G614JVR_G614JVR", "G614JVR.330",
        "6.17.8-arch1-1", "Arch Linux", "KDE (wayland)");

    private static readonly BugReport.Draft Draft = new("3f2c9a1e-7b4d-4c2a-9e1f-0a8b6c5d4e3f",
        "Fan curve resets after resume", "After suspend the fans go back to the default curve.");

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- BugReport ---");

        Harness.Scenario("BugReport_Scrub_HomeUserHost", _ =>
        {
            string text = "config /home/anna/.config/ghelper/config.json user=anna host anna-laptop ANNA-LAPTOP";
            Harness.AssertEqual(
                "config ~/.config/ghelper/config.json user=<user> host <host> <host>",
                BugReport.Scrub(text, "/home/anna", "anna", "anna-laptop"), "scrubbed");
        });

        Harness.Scenario("BugReport_Scrub_KeepsOtherWords", _ =>
        {
            Harness.AssertEqual("annex planner <user>",
                BugReport.Scrub("annex planner ann", "/home/ann", "ann", "box"), "user ann");
            Harness.AssertEqual("me and you",
                BugReport.Scrub("me and you", "/home/me", "me", "pc"), "short names stay");
        });

        Harness.Scenario("BugReport_Scrub_CutsAtOneMillion", _ =>
        {
            string cut = BugReport.Scrub(new string('x', 1_200_000), "/home/a", "abc", "def");
            Harness.AssertEqual(1_000_000, cut.Length, "length");
            Harness.Assert(cut.EndsWith("\n[truncated]"), "marker at the end");
        });

        Harness.Scenario("BugReport_Collect_ScrubsThisHome", _ =>
        {
            string home = Environment.GetEnvironmentVariable("HOME") ?? "";
            var (info, diagnostics) = BugReport.Collect($"config {home}/.config/ghelper/config.json");
            Harness.AssertEqual(AppConfig.AppVersion, info.Version, "version");
            if (home.Length > 1)
                Harness.AssertEqual("config ~/.config/ghelper/config.json", diagnostics, "home replaced");
        });

        Harness.Scenario("BugReport_BuildJson_AllFields", _ =>
        {
            string json = BugReport.BuildJson(
                Draft with { Title = "  Fan curve resets  ", Description = "Ünïcode ✓ \"quoted\" <b>" }, Info, "diag\nline");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            Harness.AssertEqual("3f2c9a1e-7b4d-4c2a-9e1f-0a8b6c5d4e3f", r.GetProperty("report_id").GetString(), "report_id");
            Harness.AssertEqual("Fan curve resets", r.GetProperty("title").GetString(), "title");
            Harness.AssertEqual("Ünïcode ✓ \"quoted\" <b>", r.GetProperty("description").GetString(), "description");
            Harness.AssertEqual("1.0.100", r.GetProperty("app_version").GetString(), "app_version");
            Harness.AssertEqual("binary", r.GetProperty("install_mode").GetString(), "install_mode");
            Harness.AssertEqual("ROG Strix G614JVR_G614JVR", r.GetProperty("model").GetString(), "model");
            Harness.AssertEqual("G614JVR.330", r.GetProperty("bios").GetString(), "bios");
            Harness.AssertEqual("6.17.8-arch1-1", r.GetProperty("kernel").GetString(), "kernel");
            Harness.AssertEqual("Arch Linux", r.GetProperty("os").GetString(), "os");
            Harness.AssertEqual("KDE (wayland)", r.GetProperty("desktop").GetString(), "desktop");
            Harness.AssertEqual("diag\nline", r.GetProperty("diagnostics").GetString(), "diagnostics");
        });

        Harness.Scenario("BugReport_IsValid_TitleAndDescriptionLimits", _ =>
        {
            Harness.Assert(BugReport.IsValid(Draft), "ok");
            Harness.Assert(!BugReport.IsValid(Draft with { Title = "Fan" }), "short title");
            Harness.Assert(BugReport.IsValid(Draft with { Title = "Fans" }), "4-char title");
            Harness.Assert(!BugReport.IsValid(Draft with { Title = new string('x', 121) }), "long title");
            Harness.Assert(BugReport.IsValid(Draft with { Title = "  " + new string('x', 120) + "  " }), "trimmed title");
            Harness.Assert(!BugReport.IsValid(Draft with { Description = "it broke" }), "short description");
            Harness.Assert(!BugReport.IsValid(Draft with { Description = new string('x', 8001) }), "long description");
        });

        Harness.Scenario("BugReport_Parse_CreatedAndRepeat", _ =>
        {
            foreach (int status in new[] { 201, 200 })
            {
                var r = BugReport.Parse(status,
                    "{\"number\":42,\"url\":\"https://github.com/utajum/g-helper-linux/issues/42\"}");
                Harness.Assert(r.Ok, $"{status} ok");
                Harness.Assert(!r.Retry, $"{status} no retry");
                Harness.AssertEqual(42, r.Number, $"{status} number");
                Harness.AssertEqual("https://github.com/utajum/g-helper-linux/issues/42", r.Url, $"{status} url");
            }
        });

        Harness.Scenario("BugReport_Parse_ApiMessageAsIs", _ =>
        {
            var limited = BugReport.Parse(429,
                "{\"error\":\"rate_limited\",\"message\":\"Daily report limit reached. Try again after 00:00 UTC (in 5 h).\"}");
            Harness.Assert(!limited.Ok, "429 not ok");
            Harness.Assert(limited.Retry, "429 retry");
            Harness.AssertEqual("Daily report limit reached. Try again after 00:00 UTC (in 5 h).", limited.Message, "429 message");
            var invalid = BugReport.Parse(400,
                "{\"error\":\"invalid\",\"message\":\"title must be one line of 10-120 characters.\",\"field\":\"title\"}");
            Harness.Assert(!invalid.Retry, "400 means fix the text");
            Harness.AssertEqual("title", invalid.Field, "400 field");
            Harness.AssertEqual("title must be one line of 10-120 characters.", invalid.Message, "400 message");
        });

        Harness.Scenario("BugReport_Parse_OnlyGitHubIssueUrls", _ =>
        {
            var r = BugReport.Parse(201, "{\"number\":1,\"url\":\"file:///etc/passwd\"}");
            Harness.Assert(!r.Ok, "file url rejected");
            Harness.AssertEqual("", r.Url, "url dropped");
        });

        Harness.Scenario("BugReport_Parse_NotJson", _ =>
        {
            var r = BugReport.Parse(502, "<html>Bad gateway</html>");
            Harness.Assert(!r.Ok, "not ok");
            Harness.Assert(r.Retry, "retry");
            Harness.AssertEqual(
                "The report server sent an unexpected reply (status 502). Press Retry, or try again later.",
                r.Message, "message");
        });

        Harness.Scenario("BugReport_Send_BadEndpointIsANetworkError", _ =>
        {
            string? old = Environment.GetEnvironmentVariable("GHELPER_REPORT_URL");
            try
            {
                foreach (string url in new[] { "ftp://example.invalid/x", "http://[::1" })
                {
                    Environment.SetEnvironmentVariable("GHELPER_REPORT_URL", url);
                    var r = BugReport.SendAsync(Draft, Info, "diag").GetAwaiter().GetResult();
                    Harness.AssertEqual(0, r.Status, url + " status");
                    Harness.Assert(r.Retry, url + " retry");
                    Harness.Assert(r.Message.StartsWith("Could not reach the report server"), url + " message");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GHELPER_REPORT_URL", old);
            }
        });

        Harness.Scenario("BugReport_Draft_KeptUntilSentOrEmpty", _ =>
        {
            var fresh = BugReport.LoadDraft();
            Harness.Assert(Guid.TryParse(fresh.Id, out Guid id), "new id");
            Harness.AssertEqual("", fresh.Title, "no title");
            var d = fresh with { Title = Draft.Title, Description = Draft.Description };
            BugReport.SaveDraft(d);
            Harness.AssertEqual(d, BugReport.LoadDraft(), "saved");
            BugReport.SaveDraft(d with { Title = " ", Description = "" });
            Harness.Assert(BugReport.LoadDraft().Id != d.Id, "empty text drops the draft");
            BugReport.SaveDraft(d);
            BugReport.SaveDraft(null);
            Harness.Assert(BugReport.LoadDraft().Id != d.Id, "null drops the draft");
        });

        Harness.Scenario("BugReport_Links_NewestFirstMax10", _ =>
        {
            Harness.AssertEqual(0, BugReport.Links.Count, "none yet");
            AppConfig.Set("report_links", "not json");
            Harness.AssertEqual(0, BugReport.Links.Count, "broken config");
            for (int i = 1; i <= 12; i++)
                BugReport.Remember(BugReport.Parse(201,
                    $"{{\"number\":{i},\"url\":\"https://github.com/utajum/g-helper-linux/issues/{i}\"}}"), $"Bug {i}");
            var links = BugReport.Links;
            Harness.AssertEqual(10, links.Count, "count");
            Harness.AssertEqual(12, links[0].Number, "newest first");
            Harness.AssertEqual(3, links[9].Number, "oldest kept");
            Harness.AssertEqual("Bug 12", links[0].Title, "title");
            Harness.AssertEqual(DateTime.Now.ToString("yyyy-MM-dd"), links[0].Date, "date");
        });
    }
}
