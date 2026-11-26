// Every ASUS mouse model of the port against the Windows G-Helper source.
// mouse-golden.txt holds what the Windows code sends and reads for MouseScenario;
// scripts/mouse-golden.sh writes it from ../g-helper.

using GHelper.Linux.AnimeMatrix.Communication.Platform;
using GHelper.Linux.Peripherals.Mouse;

namespace GHelper.Linux.Tests;

public static class MouseModelTests
{
    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- ASUS mouse models ---");

        var golden = Golden();
        var models = MouseScenario.Models(typeof(AsusMouse).Assembly);

        Harness.Scenario("Mouse_SameModelsAsWindows", _ =>
        {
            var names = models.Select(m => m.Name).ToHashSet();
            var missing = golden.Keys.Where(k => !names.Contains(k)).ToList();
            var extra = names.Where(n => !golden.ContainsKey(n)).ToList();
            Harness.Assert(missing.Count == 0 && extra.Count == 0,
                $"not ported: {string.Join(", ", missing)}; not in Windows: {string.Join(", ", extra)}");
        });

        foreach (var model in models)
        {
            Harness.Scenario($"Mouse_{model.Name}_SameAsWindows", _ =>
            {
                string actual = Quiet(() => MouseScenario.Run(model));
                string expected = golden.GetValueOrDefault(model.Name, "");
                Harness.Assert(actual == expected, FirstDifference(expected, actual));
            });
        }

        // Windows "mi_02&col03": interface 2 of the USB device; Linux has one hidraw node per interface.
        Harness.Scenario("Mouse_PathMatches_UsbInterfaceNumber", _ =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ghelper-hidraw-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                string hid = Path.Combine(root, "usb", "1-3:1.2", "0003:0B05:1ACE.0007");
                Directory.CreateDirectory(hid);
                File.WriteAllText(Path.Combine(root, "usb", "1-3:1.2", "bInterfaceNumber"), "02\n");
                string sysClass = Path.Combine(root, "class");
                Directory.CreateDirectory(Path.Combine(sysClass, "hidraw3"));
                Directory.CreateSymbolicLink(Path.Combine(sysClass, "hidraw3", "device"), hid);

                Harness.Assert(LinuxUsbProvider.PathMatches("/dev/hidraw3", "mi_02&col03", sysClass), "mi_02 matches interface 02");
                Harness.Assert(!LinuxUsbProvider.PathMatches("/dev/hidraw3", "mi_00", sysClass), "mi_00 does not match interface 02");
                Harness.Assert(!LinuxUsbProvider.PathMatches("/dev/hidraw4", "mi_02", sysClass), "no sysfs entry, no match");
                Harness.Assert(LinuxUsbProvider.PathMatches("/dev/hidraw3", "/dev/hidraw3", sysClass), "a full path matches as text");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });
    }

    // Class name -> golden section (starts with "[Name]").
    private static Dictionary<string, string> Golden()
    {
        var sections = new Dictionary<string, string>();
        var lines = File.ReadAllLines(Path.Combine(HoverHelpTests.RepoRoot(), "tests", "GHelper.Linux.Tests", "mouse-golden.txt"));
        string? name = null;
        var text = new System.Text.StringBuilder();
        foreach (var line in lines.Skip(1).Append("[]"))
        {
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (name is not null)
                    sections[name] = text.ToString();
                name = line[1..^1];
                text.Clear();
            }
            text.Append(line).Append('\n');
        }
        return sections;
    }

    // The packet logger of the Windows code is always on in a Debug build.
    private static string Quiet(Func<string> run)
    {
        var error = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            return run();
        }
        finally
        {
            Console.SetError(error);
        }
    }

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            string el = i < e.Length ? e[i] : "(end)";
            string al = i < a.Length ? a[i] : "(end)";
            if (el != al)
                return $"line {i + 1}: Windows '{el}', Linux '{al}'";
        }
        return "same";
    }
}
