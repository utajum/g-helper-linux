// Hover help coverage: every control a user can see has help text, and no text is dead.
// Reads the XAML and C# sources of the app; the text table is compiled in.

using System.Text.RegularExpressions;
using GHelper.Linux.I18n.HelpText;

namespace GHelper.Linux.Tests;

public static class HoverHelpTests
{
    // RepeatButton is left out: the on-screen keyboard keys have no help on purpose,
    // a tooltip would cover the next row while you type.
    private const string Types = "Button|CheckBox|ToggleButton|ToggleSwitch|RadioButton|Slider|ComboBox|TextBox|NumericUpDown|MenuItem";
    private const string Clicks = "PointerPressed|PointerReleased|Tapped|DoubleTapped";

    // Clickable on purpose without help: a hidden easter egg, and a label inside
    // borderCoinMute, which has the text.
    private static readonly HashSet<string> NoHelp = ["MainWindow.labelVersion", "MainWindow.labelCoinMute"];

    private static readonly Regex XamlControl = new(
        $@"<({Types}|TabStrip|TabStripItem|ProgressBar|\w+:(?:KnobControl|FanCurveChart|EqResponseView|WaveformView|SpectrumView|NumberPadPreview|CartesianChart))(?=[\s/>])([^>]*)>",
        RegexOptions.Singleline);
    private static readonly Regex XamlClick = new($@"<([\w:]+)(\s[^>]*\b(?:{Clicks})=""[^>]*)>", RegexOptions.Singleline);
    private static readonly Regex CodeClick = new($@"\b(\w+)\.(?:{Clicks}) \+=");
    private static readonly Regex CodeControl = new($@"new ({Types})\b\s*(\(\))?\s*");
    private static readonly Regex NameArg = new(@"\bname: ""(\w+)""");

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- Hover help ---");

        string src = Path.Combine(RepoRoot(), "src");
        var xaml = Directory.GetFiles(Path.Combine(src, "UI", "Views"), "*.axaml")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        var code = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("I18n", "HelpText")))
            .ToDictionary(f => Path.GetRelativePath(src, f), File.ReadAllText);
        var codeNames = new HashSet<string>();

        Harness.Scenario("HoverHelp_EveryXamlControlHasText", _ =>
        {
            var missing = new List<string>();
            foreach (var (window, text) in xaml)
            {
                foreach (Match m in XamlControl.Matches(text).Concat(XamlClick.Matches(text)))
                {
                    var name = Regex.Match(m.Groups[2].Value, @"x:Name=""(\w+)""");
                    string key = window + "." + (name.Success ? name.Groups[1].Value : $"(unnamed {m.Groups[1].Value} line {Line(text, m.Index)})");
                    if (!English.Tips.ContainsKey(key))
                        missing.Add(key);
                }
                // Click handlers set in code-behind on named XAML elements.
                if (code.TryGetValue(Path.Combine("UI", "Views", window + ".axaml.cs"), out var behind))
                    foreach (Match m in CodeClick.Matches(behind))
                    {
                        string key = window + "." + m.Groups[1].Value;
                        if (text.Contains($"x:Name=\"{m.Groups[1].Value}\"") && !NoHelp.Contains(key) && !English.Tips.ContainsKey(key))
                            missing.Add(key);
                    }
            }
            Harness.Assert(missing.Count == 0, "no help text: " + string.Join(", ", missing));
        });

        // Controls built in code set Name = "key" in the initializer; shared helpers
        // take the name as an argument, and their call sites pass name: "key".
        Harness.Scenario("HoverHelp_EveryCodeControlHasText", _ =>
        {
            var missing = new List<string>();
            foreach (var (file, text) in code)
            {
                foreach (Match m in CodeControl.Matches(text))
                {
                    string where = $"{file}:{Line(text, m.Index)}";
                    string init = Initializer(text, m.Index + m.Length);
                    var name = Regex.Match(init, @"^\s*\{\s*(?:[^{}]*?,\s*)?Name = (""(\w+)""|name\b)");
                    if (!name.Success)
                        missing.Add($"{where} (no Name)");
                    else if (name.Groups[2].Success)
                        codeNames.Add(name.Groups[2].Value);
                }
                foreach (Match m in NameArg.Matches(text))
                    codeNames.Add(m.Groups[1].Value);
            }
            foreach (var name in codeNames)
                if (!English.Tips.ContainsKey(name))
                    missing.Add(name);
            Harness.Assert(missing.Count == 0, "no help text: " + string.Join(", ", missing));
        });

        // A code key counts as used when its literal is in the source, for example
        // in a table that feeds a helper.
        Harness.Scenario("HoverHelp_NoDeadText", _ =>
        {
            var dead = new List<string>();
            foreach (var key in English.Tips.Keys)
            {
                int dot = key.IndexOf('.');
                bool used = dot < 0
                    ? code.Values.Any(text => text.Contains($"\"{key}\""))
                    : xaml.TryGetValue(key[..dot], out var text) && text.Contains($"x:Name=\"{key[(dot + 1)..]}\"");
                if (!used)
                    dead.Add(key);
            }
            Harness.Assert(dead.Count == 0, "text for no control: " + string.Join(", ", dead));
        });
    }

    // The object initializer that starts at i ("{ ... }"), or "" when there is none.
    private static string Initializer(string text, int i)
    {
        if (i >= text.Length || text[i] != '{')
            return "";
        int depth = 0;
        for (int j = i; j < text.Length; j++)
        {
            if (text[j] == '{')
                depth++;
            else if (text[j] == '}' && --depth == 0)
                return text[i..(j + 1)];
        }
        return "";
    }

    private static int Line(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "GHelper.Linux.csproj")))
                return dir.FullName;
        throw new DirectoryNotFoundException("repo root not found");
    }
}
