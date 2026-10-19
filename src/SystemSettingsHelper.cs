using System;
using System.IO;
using Microsoft.Win32;
#if PAPERTODO_STORE_BUILD
using Windows.ApplicationModel;
#endif

namespace PaperTodo;

public static class SystemSettingsHelper
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppKeyName = "PaperTodo";
    private const string StartupApprovedRunPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
#if PAPERTODO_STORE_BUILD
    private const string StartupTaskId = "PaperTodoStartup";
#endif

    public static bool IsStartupEnabled()
    {
#if PAPERTODO_STORE_BUILD
        if (AppDataDirectory.IsPackaged)
        {
            return IsPackagedStartupEnabled();
        }
#endif

        return IsLegacyRunStartupEnabled();
    }

    public static bool ToggleStartup(bool enable)
    {
#if PAPERTODO_STORE_BUILD
        if (AppDataDirectory.IsPackaged)
        {
            return TogglePackagedStartup(enable);
        }
#endif

        return ToggleLegacyRunStartup(enable);
    }

    internal static string? TryGetLegacyStartupDirectory()
    {
        var executable = TryGetLegacyRunExecutablePath();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(executable);
        }
        catch
        {
            return null;
        }
    }

    internal static void TryMigrateLegacyStartupRegistration(string sourceDirectory)
    {
#if PAPERTODO_STORE_BUILD
        if (!AppDataDirectory.IsPackaged || string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return;
        }

        try
        {
            var legacyExecutable = TryGetLegacyRunExecutablePath();
            if (string.IsNullOrWhiteSpace(legacyExecutable))
            {
                return;
            }

            var legacyDirectory = Path.GetDirectoryName(legacyExecutable);
            if (!string.Equals(
                    NormalizeDirectory(legacyDirectory),
                    NormalizeDirectory(sourceDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Preserve the user's previous "start with Windows" choice. Only remove the old
            // portable Run entry after the package startup task was actually enabled.
            if (!TogglePackagedStartup(enable: true))
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true);
            key?.DeleteValue(AppKeyName, false);
        }
        catch
        {
            // Migration is best-effort. Keeping the old Run entry is safer than losing startup.
        }
#endif
    }

    private static bool IsLegacyRunStartupEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RegistryRunPath, false);
            using var approvalKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunPath, false);
            return IsStartupEnabled(runKey?.GetValue(AppKeyName),
                approvalKey?.GetValue(AppKeyName), Environment.ProcessPath ?? "");
        }
        catch { return false; }
    }

    private static bool ToggleLegacyRunStartup(bool enable)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (enable && string.IsNullOrEmpty(processPath)) return false;
            using var runKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true);
            if (runKey == null) return false;
            if (!enable)
            {
                runKey.DeleteValue(AppKeyName, false);
                return runKey.GetValue(AppKeyName) == null;
            }
            runKey.SetValue(AppKeyName, $"\"{processPath}\"", RegistryValueKind.String);
            using var approvalKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunPath, true);
            var approval = approvalKey?.GetValue(AppKeyName);
            // Only an explicit enable click may clear our own disabled entry. Reading external
            // Startup Apps state never silently re-enables an entry disabled by the user.
            if (approval != null && !StartupApprovalAllowsLaunch(approval))
                approvalKey!.DeleteValue(AppKeyName, false);
            return IsStartupEnabled(runKey.GetValue(AppKeyName),
                approvalKey?.GetValue(AppKeyName), processPath!);
        }
        catch { return false; }
    }

    internal static bool IsStartupEnabled(object? runValue, object? approvalValue, string processPath)
    {
        if (string.IsNullOrEmpty(processPath)) return false;
        var value = runValue?.ToString();
        return (value == processPath || value == $"\"{processPath}\"") &&
            StartupApprovalAllowsLaunch(approvalValue);
    }

    internal static bool StartupApprovalAllowsLaunch(object? approvalValue)
    {
        if (approvalValue == null) return true;
        if (approvalValue is not byte[] data || data.Length < 12) return false;
        // Explorer-owned, undocumented format: the first DWORD is the approval state;
        // the FILETIME records history and is not evidence that an entry is disabled.
        return data[1] == 0 && data[2] == 0 && data[3] == 0 && data[0] is 0x00 or 0x02 or 0x06;
    }

    private static string? TryGetLegacyRunExecutablePath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunPath, false);
            var raw = key?.GetValue(AppKeyName)?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (raw[0] == '"')
            {
                var endQuote = raw.IndexOf('"', 1);
                return endQuote > 1 ? raw[1..endQuote] : null;
            }

            var firstSpace = raw.IndexOf(' ');
            return firstSpace > 0 ? raw[..firstSpace] : raw;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

#if PAPERTODO_STORE_BUILD
    private static bool IsPackagedStartupEnabled()
    {
        try
        {
            var task = GetPackagedStartupTask();
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch
        {
            return false;
        }
    }

    private static bool TogglePackagedStartup(bool enable)
    {
        try
        {
            var task = GetPackagedStartupTask();
            if (enable)
            {
                if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
                {
                    return true;
                }

                if (task.State is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy)
                {
                    return false;
                }

                var state = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }

            if (task.State == StartupTaskState.Enabled)
            {
                task.Disable();
                return true;
            }

            return task.State is StartupTaskState.Disabled or StartupTaskState.DisabledByUser;
        }
        catch
        {
            return false;
        }
    }

    private static StartupTask GetPackagedStartupTask()
    {
        return StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
    }
#endif
}
