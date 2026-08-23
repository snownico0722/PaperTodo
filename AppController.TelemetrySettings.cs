using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement CreateGeneralSettingsSectionLabel()
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 10, 0, 2)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = Strings.Get("SettingsGeneral"),
            Foreground = TrayWeakTextBrush,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var hintGlyph = new TextBlock
        {
            Text = "ⓘ",
            Foreground = TrayWeakTextBrush,
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(12),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var hint = new Border
        {
            Width = 18,
            Height = 18,
            Margin = new Thickness(6, 0, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Help,
            VerticalAlignment = VerticalAlignment.Center,
            Child = hintGlyph,
            ToolTip = BuildSettingsHintTooltip(TelemetryStrings.Get("Description"))
        };
        ToolTipPreferences.SetAlwaysEnabled(hint, true);
        ToolTipService.SetInitialShowDelay(hint, 200);
        ToolTipService.SetShowDuration(hint, 20000);
        ToolTipService.SetBetweenShowDelay(hint, 0);
        hint.MouseEnter += (_, _) => hintGlyph.Foreground = TrayTextBrush;
        hint.MouseLeave += (_, _) => hintGlyph.Foreground = TrayWeakTextBrush;
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        return grid;
    }

    private UIElement CreateAnonymousUsageStatisticsSettingsRow()
    {
        return SettingsToggle(
            TelemetryStrings.Get("HelpImprove"),
            TelemetryService.Enabled,
            ToggleAnonymousUsageStatistics);
    }

    private void ToggleAnonymousUsageStatistics()
    {
        TelemetryService.SetEnabled(!TelemetryService.Enabled);
        RefreshSettingsWindowContent();
    }
}
