using System;

namespace PaperTodo;

public partial class App
{
    static App()
    {
        // OnStartup is async void. Setting CurrentUICulture only inside that async execution
        // context can be restored to the OS culture when control returns to the WPF Dispatcher.
        // Establish the persisted UI culture on the root UI thread before App is constructed,
        // InitializeComponent runs, or any Dispatcher work is queued. The existing OnStartup
        // application remains a harmless idempotent re-apply during normal UI startup.
        if (!McpBridge.IsRequested(Environment.GetCommandLineArgs()))
        {
            ApplyStartupCulturePreference(UiLanguages.LoadPersistedPreference());
        }
    }
}
