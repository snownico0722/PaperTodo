using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    private WorkbenchWindow? _workbenchWindow;

    private void OpenWorkbench()
    {
        if (IsExiting) return;
        if (_workbenchWindow is { IsLoaded: true })
        {
            if (_workbenchWindow.WindowState == WindowState.Minimized)
                _workbenchWindow.WindowState = WindowState.Normal;
            _workbenchWindow.Activate();
            return;
        }

        var window = new WorkbenchWindow(this);
        _workbenchWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_workbenchWindow, window))
                _workbenchWindow = null;
        };
        window.Show();
        window.Activate();
    }

    internal void SaveWorkbenchMetadata()
    {
        if (IsExiting) return;
        MarkDirty();
        SaveNow();
    }
}
