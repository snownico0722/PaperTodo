using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement CreateAnonymousUsageStatisticsSettingsCard()
    {
        var content = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 4)
        };
        content.Children.Add(SettingsSectionLabel(TelemetryStrings.Get("SectionTitle")));
        content.Children.Add(SettingsToggle(
            TelemetryStrings.Get("HelpImprove"),
            State.TelemetryEnabled,
            ToggleAnonymousUsageStatistics));
        content.Children.Add(new TextBlock
        {
            Text = TelemetryStrings.Get("Description"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(10.5),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 4, 0, 0),
            Opacity = 0.86
        });
        return content;
    }

    private void ToggleAnonymousUsageStatistics()
    {
        State.TelemetryEnabled = !State.TelemetryEnabled;
        SaveNow();
        TelemetryService.SetEnabled(State.TelemetryEnabled);
        RefreshSettingsWindowContent();
    }
}