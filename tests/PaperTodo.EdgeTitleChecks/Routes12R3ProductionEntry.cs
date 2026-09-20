using System.Windows;

internal static class Routes12R3ProductionEntry
{
    [STAThread]
    private static int Main(string[] args) => Program.RunRoutes12R3ProductionOnly(args);
}

internal static partial class Program
{
    internal static int RunRoutes12R3ProductionOnly(string[] args)
    {
        if (TryRunProxyNativeInputChild(args, out var childExit)) return childExit;
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Routes12PolicyChecks();
            // The legacy H8 NativeInputWindowProbe uses SetWindowSubclass from the WPF thread.
            // R3 deliberately moves the finite input HWND to another owner thread, so that fixture
            // can no longer attach and is not a valid negative control for this ownership model.
            // R0-R2 preserve the original H8 evidence; this entry tests the new production boundary.
            Routes12R5TimerDiagnostics();
            Routes12R3InputOnlyProductionBoundaryCase();
            Console.WriteLine($"ROUTES12 R3 production-boundary checks: {assertions} assertions passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }
}