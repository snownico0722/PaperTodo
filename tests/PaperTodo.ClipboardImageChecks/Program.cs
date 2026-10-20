using System.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (ClipboardImageChecks.TryRunClipboardMode(args, out var result))
            return result;

        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = 0;
        ClipboardImageChecks.Run((name, check) =>
        {
            try { check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
        });
        Console.WriteLine($"Clipboard image checks: {failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }
}
