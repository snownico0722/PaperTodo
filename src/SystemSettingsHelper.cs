using System;
using Microsoft.Win32;

namespace PaperTodo;

public static class SystemSettingsHelper
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppKeyName = "PaperTodo";

    public static bool IsStartupEnabled()
    {
        try
        {
            var processPath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(processPath))
            {
                return false;
            }

            using var runKey = Registry.CurrentUser.OpenSubKey(RegistryRunPath, false);
            using var approvalKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunPath, false);
            return IsStartupEnabled(
                runKey?.GetValue(AppKeyName),
                approvalKey?.GetValue(AppKeyName),
                processPath);
        }
        catch
        {
            // Ignored, fallback to false
        }
        return false;
    }

    public static bool ToggleStartup(bool enable)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (enable && string.IsNullOrEmpty(processPath))
            {
                return false;
            }

            using var runKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true);
            if (runKey == null)
            {
                return false;
            }

            if (!enable)
            {
                runKey.DeleteValue(AppKeyName, false);
                return runKey.GetValue(AppKeyName) == null;
            }

            // Re-register the current executable first. If Windows' Startup Apps / Task Manager
            // previously disabled this entry, clear only PaperTodo's approval record after the
            // user's explicit in-app request to enable startup again.
            runKey.SetValue(AppKeyName, $"\"{processPath}\"", RegistryValueKind.String);
            using var approvalKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunPath, true);
            var approvalValue = approvalKey?.GetValue(AppKeyName);
            if (approvalValue != null && !StartupApprovalAllowsLaunch(approvalValue))
            {
                approvalKey!.DeleteValue(AppKeyName, false);
            }

            // Read both layers back instead of treating a successful registry call as proof that
            // Windows now sees the startup item as enabled.
            approvalValue = approvalKey?.GetValue(AppKeyName);
            return IsStartupEnabled(runKey.GetValue(AppKeyName), approvalValue, processPath!);
        }
        catch
        {
            // Permission exceptions in locked down environments
        }
        return false;
    }

    internal static bool IsStartupEnabled(object? runValue, object? approvalValue, string processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        var value = runValue?.ToString();
        var pathMatches = !string.IsNullOrEmpty(value) &&
            (value == processPath || value == $"\"{processPath}\"");
        return pathMatches && StartupApprovalAllowsLaunch(approvalValue);
    }

    internal static bool StartupApprovalAllowsLaunch(object? approvalValue)
    {
        if (approvalValue == null)
        {
            return true;
        }

        if (approvalValue is not byte[] data || data.Length < 12)
        {
            return false;
        }

        // StartupApproved is an undocumented Explorer-owned binary value. The first DWORD
        // is the approval state (0x00/0x02/0x06 enabled, 0x03/0x07 disabled). The trailing
        // FILETIME is history, not the state: a re-enabled 0x06 record can retain it, and
        // a disabled 0x03 record can have no timestamp. Unknown states fail closed.
        return data[1] == 0 && data[2] == 0 && data[3] == 0 &&
            data[0] is 0x00 or 0x02 or 0x06;
    }
}
