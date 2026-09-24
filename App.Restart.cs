using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PaperTodo;

public partial class App
{
    private bool _restartAfterExitRequested;

    internal void RequestRestartAfterExit()
    {
        if (_restartAfterExitRequested)
        {
            return;
        }

        _restartAfterExitRequested = true;
        Exit += OnRestartAfterExit;
    }

    private void OnRestartAfterExit(object? sender, ExitEventArgs e)
    {
        Exit -= OnRestartAfterExit;
        if (!_restartAfterExitRequested)
        {
            return;
        }

        _restartAfterExitRequested = false;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            // Restart is best-effort. The language preference is already persisted, so a normal
            // manual launch still applies it if process creation is blocked by the environment.
        }
    }
}
