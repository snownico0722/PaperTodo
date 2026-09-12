using System.Diagnostics;
using System.Globalization;

namespace PaperTodo;

internal static class AppRestart
{
    private const string WaitForProcessPrefix = "--restart-after-pid=";

    internal static void WaitForPreviousInstance()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (!arg.StartsWith(WaitForProcessPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = arg[WaitForProcessPrefix.Length..];
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
                processId <= 0 || processId == Environment.ProcessId)
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                process.WaitForExit();
            }
            catch (ArgumentException)
            {
                // The previous process has already exited.
            }
            catch (InvalidOperationException)
            {
                // Treat an unavailable process as already exited and continue normal startup.
            }
            return;
        }
    }

    internal static bool TryLaunchAfterCurrentProcessExit(out string? error)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            error = "Environment.ProcessPath is unavailable.";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(
                $"{WaitForProcessPrefix}{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");

            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "The restart process could not be started.";
                return false;
            }

            process.Dispose();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }
}
