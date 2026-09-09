using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement CreateAnonymousUsageStatisticsSettingsRow() =>
        WrapWithHint(
            SettingsToggle(
                TelemetryStrings.Get("HelpImprove"),
                State.TelemetryEnabled,
                ToggleAnonymousUsageStatistics),
            BuildSettingsHintTooltip(TelemetryStrings.Get("Description")));

    private void ToggleAnonymousUsageStatistics()
    {
        State.TelemetryEnabled = !State.TelemetryEnabled;
        SaveNow();
        TelemetryService.SetEnabled(State.TelemetryEnabled);
        RefreshSettingsRegions("general.telemetry");
    }
}
