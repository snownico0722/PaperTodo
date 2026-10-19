using System.Windows.Controls;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private CheckBox? _settingsStartupCheckBox;
    private DispatcherTimer? _settingsStartupRefreshTimer;

    private void StartSettingsStartupStateRefresh()
    {
        if (_settingsStartupRefreshTimer == null)
        {
            _settingsStartupRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _settingsStartupRefreshTimer.Tick += (_, _) => RefreshSettingsStartupStateFromWindows();
        }
        _settingsStartupRefreshTimer.Start();
        RefreshSettingsStartupStateFromWindows();
    }

    private void RefreshSettingsStartupStateFromWindows()
    {
        if (_settingsWindow is not { IsVisible: true } ||
            _settingsStartupCheckBox is not { IsVisible: true } checkBox) return;
        // SettingsToggle dispatches Click, not Checked: this read-only refresh cannot register
        // startup, rebuild the settings page or disturb another editor's caret/selection.
        checkBox.IsChecked = SystemSettingsHelper.IsStartupEnabled();
    }
}
