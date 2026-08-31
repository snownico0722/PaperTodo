using System;
using Microsoft.Win32;

namespace PaperTodo;

public static class SystemSettingsHelper
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppKeyName = "PaperTodo";

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunPath, false);
            if (key != null)
            {
                var val = key.GetValue(AppKeyName)?.ToString();
                var processPath = Environment.ProcessPath ?? "";
                return !string.IsNullOrEmpty(val) && (val == processPath || val == $"\"{processPath}\"");
            }
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
            // Permission exceptions in locked down environments
        }
        return false;
    }
}
