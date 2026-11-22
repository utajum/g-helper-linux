using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Helpers;

/// <summary>In-app bug report: collects system info, sends the draft to the report endpoint, keeps the draft and the issue links.</summary>
public static class BugReport
{
    public const int TitleMin = 10, TitleMax = 120, DescriptionMin = 20, DescriptionMax = 8000;
    private const string DefaultEndpoint = "https://g-helper-linux.elevatech.xyz/api/report";
    private const int MaxDiagnostics = 1_000_000;
    private const int MaxLinks = 10;
    private const string Truncated = "\n[truncated]";
    private const string LinksKey = "report_links";
    private const string DraftIdKey = "report_draft_id", DraftTitleKey = "report_draft_title", DraftDescriptionKey = "report_draft_description";

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("GHELPER_REPORT_URL") is { Length: > 0 } url ? url : DefaultEndpoint;

    /// <summary>What the user writes; Id is the report_id that makes Retry safe.</summary>
    public sealed record Draft(string Id, string Title, string Description);

    public sealed record Info(string Version, string InstallMode, string Model, string Bios,
        string Kernel, string Os, string Desktop);

    public readonly record struct Result(int Status, int Number, string Url, string Message, string Field)
    {
        public bool Ok => Status is 200 or 201 && Number > 0 && Url.Length > 0;

        // 400 means fix the text; any other failure can be sent again
        public bool Retry => !Ok && Status != 400;
    }

    public sealed record Link(int Number, string Url, string Title, string Date);

    /// <summary>System info and the diagnostics with home, user and host replaced.</summary>
    public static (Info Info, string Diagnostics) Collect(string diagnostics) =>
        (CollectInfo(), Scrub(diagnostics, Environment.GetEnvironmentVariable("HOME") ?? "",
            Environment.UserName, Environment.MachineName));

    public static bool IsValid(Draft d) =>
        d.Title.Trim().Length is >= TitleMin and <= TitleMax
        && d.Description.Trim().Length is >= DescriptionMin and <= DescriptionMax;

    private static Info CollectInfo()
    {
        static string Dmi(string name) =>
            SysfsHelper.ReadAttribute(Path.Combine(SysfsHelper.DmiId, name)) ?? "";
        static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "?";

        string mode = NixOS.IsNixOS ? "NixOS"
            : string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE")) ? "binary" : "AppImage";
        string os = "";
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("PRETTY_NAME="))
                {
                    os = line[12..].Trim('"');
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new Info(AppConfig.AppVersion, mode, Dmi("product_name"), Dmi("bios_version"),
            SysfsHelper.RunCommand("uname", "-r") ?? "", os,
            $"{Env("XDG_CURRENT_DESKTOP")} ({Env("XDG_SESSION_TYPE")})");
    }

    /// <summary>Replace home path, host and user name; cut at MaxDiagnostics.</summary>
    internal static string Scrub(string text, string home, string user, string host)
    {
        if (home.Length > 1)
            text = text.Replace(home, "~");
        if (host.Length >= 3)
            text = Regex.Replace(text, $@"\b{Regex.Escape(host)}\b", "<host>", RegexOptions.IgnoreCase);
        if (user.Length >= 3)
            text = Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "<user>");
        if (text.Length <= MaxDiagnostics)
            return text;
        int cut = MaxDiagnostics - Truncated.Length;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + Truncated;
    }

    internal static string BuildJson(Draft d, Info info, string diagnostics)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("report_id", d.Id);
            w.WriteString("title", d.Title.Trim());
            w.WriteString("description", d.Description.Trim());
            w.WriteString("app_version", info.Version);
            w.WriteString("install_mode", info.InstallMode);
            w.WriteString("model", info.Model);
            w.WriteString("bios", info.Bios);
            w.WriteString("kernel", info.Kernel);
            w.WriteString("os", info.Os);
            w.WriteString("desktop", info.Desktop);
            w.WriteString("diagnostics", diagnostics);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Only issue pages of this project are opened from a reply
    private static bool IsIssueUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host == "github.com" && u.AbsolutePath.StartsWith("/utajum/g-helper-linux", StringComparison.Ordinal);

    private static string Unexpected(int status) =>
        $"The report server sent an unexpected reply (status {status}). Press Retry, or try again later.";

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Number(JsonElement e) =>
        e.TryGetProperty("number", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

    /// <summary>Endpoint reply; error messages are English and shown as is.</summary>
    internal static Result Parse(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return new Result(status, 0, "", Unexpected(status), "");
            string url = Text(r, "url");
            if (!IsIssueUrl(url))
                url = "";
            var result = new Result(status, Number(r), url, Text(r, "message"), Text(r, "field"));
            if (result.Ok)
                return result;
            string message = result.Message.Length is > 0 and <= 300 && status is not (200 or 201)
                ? result.Message : Unexpected(status);
            return result with { Message = message };
        }
        catch (JsonException)
        {
            return new Result(status, 0, "", Unexpected(status), "");
        }
    }

    private static Result NetworkError(string reason) =>
        new(0, 0, "", $"Could not reach the report server ({reason}). Check your internet connection and press Retry, or try again later from Updates > Report a Bug.", "");

    public static async Task<Result> SendAsync(Draft d, Info info, string diagnostics, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 65_536 };
            http.DefaultRequestHeaders.Add("User-Agent", "G-Helper-Linux/" + AppConfig.AppVersion);
            using var content = new StringContent(BuildJson(d, info, diagnostics), Encoding.UTF8, "application/json");
            using var res = await http.PostAsync(Endpoint, content, ct);
            return Parse((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return NetworkError(ex.Message);
        }
    }

    /// <summary>Saved draft, or a new one with a fresh report id.</summary>
    public static Draft LoadDraft() =>
        new(AppConfig.GetString(DraftIdKey) is { } id && Guid.TryParseExact(id, "D", out _) ? id : Guid.NewGuid().ToString(),
            AppConfig.GetString(DraftTitleKey) ?? "", AppConfig.GetString(DraftDescriptionKey) ?? "");

    /// <summary>Keep the draft for the next window; null or empty text drops it.</summary>
    public static void SaveDraft(Draft? d)
    {
        if (d == null || d.Title.Trim().Length + d.Description.Trim().Length == 0)
        {
            AppConfig.Remove(DraftIdKey);
            AppConfig.Remove(DraftTitleKey);
            AppConfig.Remove(DraftDescriptionKey);
            return;
        }
        AppConfig.Set(DraftIdKey, d.Id);
        AppConfig.Set(DraftTitleKey, d.Title);
        AppConfig.Set(DraftDescriptionKey, d.Description);
    }

    /// <summary>Issues sent from this machine, newest first.</summary>
    public static List<Link> Links => ReadLinks(AppConfig.GetString(LinksKey));

    public static void Remember(Result r, string title) =>
        AppConfig.Set(LinksKey, AddLink(AppConfig.GetString(LinksKey),
            new Link(r.Number, r.Url, title, DateTime.Now.ToString("yyyy-MM-dd"))));

    private static List<Link> ReadLinks(string? json)
    {
        var links = new List<Link>();
        if (string.IsNullOrEmpty(json))
            return links;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return links;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                    continue;
                string url = Text(e, "url");
                if (Number(e) > 0 && IsIssueUrl(url))
                    links.Add(new Link(Number(e), url, Text(e, "title"), Text(e, "date")));
            }
        }
        catch (JsonException)
        {
            links.Clear();
        }
        return links;
    }

    /// <summary>Links JSON with this link first, at most MaxLinks.</summary>
    private static string AddLink(string? json, Link link)
    {
        var links = ReadLinks(json);
        links.Insert(0, link);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var l in links.Take(MaxLinks))
            {
                w.WriteStartObject();
                w.WriteNumber("number", l.Number);
                w.WriteString("url", l.Url);
                w.WriteString("title", l.Title);
                w.WriteString("date", l.Date);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
