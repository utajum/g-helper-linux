// Regressions found while splitting GPUModeControl into modules.

using GHelper.Linux.Gpu;

namespace GHelper.Linux.Tests;

public static class GpuModuleTests
{
    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- GPU switching modules ---");

        Harness.Scenario("SafetyState_HasOneWriter", sb =>
        {
            // The MUX guard and the boot files only hold if nothing goes around their modules.
            string dir = AppContext.BaseDirectory;
            while (!Directory.Exists(Path.Combine(dir, "src", "Gpu")))
                dir = Path.GetDirectoryName(dir) ?? throw new Exception("repo root not found");
            var owners = new Dictionary<string, string[]>
            {
                [".SetGpuMuxMode("] = new[] { "MuxLatch.cs", "FirmwareGpuBackend.cs" },
                ["ghelper-gpu-block.conf"] = new[] { "GpuBootFiles.cs" },
                ["50-ghelper-remove-dgpu.rules"] = new[] { "GpuBootFiles.cs" },
                ["gpu-block-helper.sh\""] = new[] { "GpuBootFiles.cs", "Installer.cs", "NixOS.cs" },
            };
            foreach (var file in Directory.EnumerateFiles(Path.Combine(dir, "src"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains("/obj/") || file.Contains("/bin/"))
                    continue;
                foreach (var line in File.ReadLines(file))
                {
                    string code = line.TrimStart();
                    if (code.StartsWith("//"))
                        continue;
                    foreach (var (needle, allowed) in owners)
                        Harness.Assert(!code.Contains(needle) || allowed.Contains(Path.GetFileName(file)),
                            $"{Path.GetFileName(file)} uses {needle}: go through its module");
                }
            }
        });

        Harness.Scenario("Notice_EveryResultTheUserMustSee_HasText", sb =>
        {
            // Tray and main window share this map; a failed re-enable gets a dialog, not a notice.
            foreach (GpuSwitchResult r in Enum.GetValues<GpuSwitchResult>())
            {
                var notice = GpuSwitchNotice.For(r, GpuMode.Standard);
                bool silent = r == GpuSwitchResult.AlreadySet || r == GpuSwitchResult.DriverBlocking
                    || r == GpuSwitchResult.DgpuReenableFailed;
                Harness.Assert(silent == (notice == null), $"{r}: notice {(notice == null ? "missing" : "unexpected")}");
                if (notice is { } n)
                    Harness.Assert(n.Text.Length > 0 && n.Icon.Length > 0, $"{r}: empty notice");
            }
        });
    }
}
