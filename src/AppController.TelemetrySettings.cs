using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement CreateAnonymousUsageStatisticsSettingsRow()
    {
#if PAPERTODO_STORE_BUILD
        // Store builds do not collect or upload telemetry, so do not expose a dead setting.
        return new Border
        {
            Visibility = Visibility.Collapsed,
            Height = 0,
            Margin = new Thickness(0)
        };
#else
        var toggle = SettingsToggle(
            TelemetryStrings.Get("HelpImprove"),
            State.TelemetryEnabled,
            ToggleAnonymousUsageStatistics);
        return WrapWithCustomHint(toggle, TelemetryStrings.Get("Description"));
#endif
    }

    private UIElement WrapWithCustomHint(FrameworkElement option, string tipText)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        option.Margin = new Thickness(0);
        option.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(option, 0);
        grid.Children.Add(option);

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
            ToolTip = BuildSettingsHintTooltip(tipText)
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

    private void ToggleAnonymousUsageStatistics()
    {
#if PAPERTODO_STORE_BUILD
        State.TelemetryEnabled = false;
#else
        State.TelemetryEnabled = !State.TelemetryEnabled;
        SaveNow();
        TelemetryService.SetEnabled(State.TelemetryEnabled);
        RefreshSettingsWindowContent();
#endif
    }
}
