using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace PaperTodo;

internal static class AppRestart
{
    internal const string WaitForProcessPrefix = "--restart-after-pid=";
    internal const string WaitForStartTimePrefix = "--restart-after-start-ticks=";

    internal static void WaitForPreviousInstance() =>
        WaitForPreviousInstance(Environment.GetCommandLineArgs());

    internal static void WaitForPreviousInstance(IReadOnlyList<string> args)
    {
        if (!TryParseRestartTarget(args, Environment.ProcessId, out var processId, out var startTicks))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            // Retain one process handle before checking identity. On Windows, subsequent
            // StartTime/WaitForExit calls reuse it rather than reopening a possibly reused PID.
            _ = process.SafeHandle;
            if (process.StartTime.ToUniversalTime().Ticks != startTicks)
            {
                return;
            }

            // Do not time out a genuine parent that is still finishing its shutdown.
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // The previous process has already exited.
        }
        catch (InvalidOperationException)
        {
            // The process exited before its handle or start time could be obtained.
        }
        catch (Win32Exception ex)
        {
            // This runs before WPF startup/error handlers. Fall back to the existing
            // single-instance mutex, rather than crashing the App static constructor.
            Trace.TraceWarning("Could not wait for the previous PaperTodo process: {0}", ex.Message);
        }
    }

    internal static bool TryParseRestartTarget(IReadOnlyList<string> args, int currentProcessId,
        out int processId, out long startTicks)
    {
        processId = 0;
        startTicks = 0;
        string? pidValue = null;
        string? ticksValue = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith(WaitForProcessPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (pidValue != null) return false;
                pidValue = arg[WaitForProcessPrefix.Length..];
            }
            else if (arg.StartsWith(WaitForStartTimePrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (ticksValue != null) return false;
                ticksValue = arg[WaitForStartTimePrefix.Length..];
            }
        }

        if (!int.TryParse(pidValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPid) ||
            parsedPid <= 0 || parsedPid == currentProcessId ||
            !long.TryParse(ticksValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTicks) ||
            parsedTicks <= 0 || parsedTicks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        processId = parsedPid;
        startTicks = parsedTicks;
        return true;
    }

    internal static bool IsDotnetHost(string processPath) =>
        string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    internal static ProcessStartInfo CreateStartInfo(string processPath, string? managedEntryPath,
        string baseDirectory, int parentProcessId, long parentStartTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        if (parentStartTicks <= 0 || parentStartTicks > DateTime.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(nameof(parentStartTicks));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = baseDirectory,
            UseShellExecute = false
        };
        if (IsDotnetHost(processPath))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(managedEntryPath);
            // ArgumentList performs quoting. In particular, do not manually quote paths
            // with spaces, or place application options before the managed entry point.
            startInfo.ArgumentList.Add(Path.GetFullPath(managedEntryPath, baseDirectory));
        }

        startInfo.ArgumentList.Add(
            $"{WaitForProcessPrefix}{parentProcessId.ToString(CultureInfo.InvariantCulture)}");
        startInfo.ArgumentList.Add(
            $"{WaitForStartTimePrefix}{parentStartTicks.ToString(CultureInfo.InvariantCulture)}");
        // Do not replay one-shot commands such as --note, --todo or --exit on restart.
        return startInfo;
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification =
        "Assembly.Location is used only for a dotnet-hosted DLL; single-file apphosts bypass that branch.")]
    internal static bool TryLaunchAfterCurrentProcessExit(out string? error)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                error = "Environment.ProcessPath is unavailable.";
                return false;
            }
            string? entryPath = null;
            if (IsDotnetHost(processPath))
            {
                entryPath = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
                {
                    error = "The managed entry assembly required to restart with dotnet is unavailable.";
                    return false;
                }
            }

            using var currentProcess = Process.GetCurrentProcess();
            var startInfo = CreateStartInfo(processPath, entryPath, AppContext.BaseDirectory,
                Environment.ProcessId, currentProcess.StartTime.ToUniversalTime().Ticks);
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "The restart process could not be started.";
                return false;
            }

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
