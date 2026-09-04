// Test runner entry point.
//
// We set GHELPER_TEST_ROOT before touching any GPUModeControl code so
// the static path constants resolve under the sandbox. The variable is
// captured at static-ctor time of GPUModeControl and cannot be changed
// thereafter, which is fine because every scenario shares the same root
// and the Sandbox helper wipes the relevant subdirs between tests.

namespace GHelper.Linux.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        string testRoot = Path.Combine(Path.GetTempPath(),
            "ghelper-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Environment.SetEnvironmentVariable("GHELPER_TEST_ROOT", testRoot);
        Directory.CreateDirectory(testRoot);

        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine(" GPUModeControl scenario tests");
        Console.WriteLine($" Sandbox: {testRoot}");
        Console.WriteLine("═══════════════════════════════════════════════════════");

        try
        {
            Scenarios.RunAll();
            ManualFanTests.RunAll();
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($" Total:  {Harness.Passed + Harness.Failed}");
        Console.WriteLine($" Passed: {Harness.Passed}");
        Console.WriteLine($" Failed: {Harness.Failed}");
        if (Harness.Failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine(" Failed scenarios:");
            foreach (var name in Harness.FailedNames)
                Console.WriteLine($"   - {name}");
        }
        Console.WriteLine("═══════════════════════════════════════════════════════");

        return Harness.Failed == 0 ? 0 : 1;
    }
}
