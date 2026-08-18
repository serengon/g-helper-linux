// Test runner entry point.
//
// The test-only build symbol exposes an explicit sandbox configuration hook.
// Production binaries do not compile an environment-controlled path bypass.

using GHelper.Linux.Gpu;

namespace GHelper.Linux.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        string testRoot = Path.Combine(Path.GetTempPath(),
            "ghelper-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(testRoot);
        GPUModeControl.ConfigureTestPathPrefix(testRoot);

        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine(" GPUModeControl scenario tests");
        Console.WriteLine($" Sandbox: {testRoot}");
        Console.WriteLine("═══════════════════════════════════════════════════════");

        try
        {
            Scenarios.RunAll();
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
