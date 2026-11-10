using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PaperTodo;

public partial class App
{
    private bool _restartAfterExitRequested;
    private string? _restartReturnPaperId;

    internal void RequestRestartAfterExit(string? returnToPaperId = null)
    {
        if (returnToPaperId != null) _restartReturnPaperId = returnToPaperId;
        if (_restartAfterExitRequested)
        {
            return;
        }

        _restartAfterExitRequested = true;
        Exit += OnRestartAfterExit;
    }

    internal void CancelRestartAfterExit()
    {
        Exit -= OnRestartAfterExit;
        _restartAfterExitRequested = false;
        _restartReturnPaperId = null;
    }

    private void OnRestartAfterExit(object? sender, ExitEventArgs e)
    {
        Exit -= OnRestartAfterExit;
        if (!_restartAfterExitRequested)
        {
            return;
        }

        _restartAfterExitRequested = false;
        var returnToPaperId = _restartReturnPaperId;
        _restartReturnPaperId = null;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                return;
            }

            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(returnToPaperId))
            {
                start.ArgumentList.Add("--return-paper");
                start.ArgumentList.Add(returnToPaperId);
            }
            Process.Start(start);
        }
        catch
        {
            // Restart is best-effort. Saved preferences and user.json are still applied by a
            // normal manual launch if process creation is blocked by the environment.
        }
    }
}
