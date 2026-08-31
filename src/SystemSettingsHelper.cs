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
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunPath, false);
            if (key != null)
            {
                var val = key.GetValue(AppKeyName)?.ToString();
                var processPath = Environment.ProcessPath ?? "";
                return !string.IsNullOrEmpty(val) &&
                    (val == processPath || val == $"\"{processPath}\"");
            }
        }
        catch
        {
            // Ignored, fallback to false.
        }

        return false;
    }

    private static bool ToggleLegacyRunStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true);
            if (key != null)
            {
                if (enable)
                {
                    var path = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        key.SetValue(AppKeyName, $"\"{path}\"");
                        return true;
                    }
                }
                else
                {
                    key.DeleteValue(AppKeyName, false);
                    return true;
                }
            }
        }
        catch
        {
            // Permission exceptions in locked down environments.
        }

        return false;
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
