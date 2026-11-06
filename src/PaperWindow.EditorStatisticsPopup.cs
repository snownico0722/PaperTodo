using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private Popup? _editorStatisticsPopup;
    private Border? _editorStatisticsPopupBorder;
    private Grid? _editorStatisticsGrid;
    private TextBlock[]? _editorStatisticsLabels;
    private TextBlock[]? _editorStatisticsTotals;
    private TextBlock[]? _editorStatisticsSelected;

    private void ToggleEditorStatisticsPopup()
    {
        if (_editorStatisticsSource == null) return;
        if (_editorStatisticsPopup?.IsOpen == true)
        {
            CloseEditorStatisticsPopup();
            return;
        }
        if (_editorStatisticsPopup == null) BuildEditorStatisticsPopup();
        RefreshEditorStatisticsPopupStyle();
        DisplayEditorStatistics();
        _editorStatisticsPopup!.IsOpen = true;
        // Cached results appear immediately. New text still uses the coalesced worker.
        QueueExpandedEditorStatistics();
    }

    private void CloseEditorStatisticsPopup()
    {
        if (_editorStatisticsPopup != null) _editorStatisticsPopup.IsOpen = false;
    }

    private void BuildEditorStatisticsPopup()
    {
        var grid = _editorStatisticsGrid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _editorStatisticsLabels = new[]
        {
            Label(0, 0), Label(1, 1), Label(1, 2),
            Label(2, 0), Label(3, 0), Label(4, 0)
        };
        Grid.SetColumnSpan(_editorStatisticsLabels[0], 3);
        _editorStatisticsLabels[0].FontWeight = FontWeights.SemiBold;
        _editorStatisticsLabels[0].Margin = new Thickness(0, 0, 0, 12);
        _editorStatisticsTotals = new[] { Label(2, 1), Label(3, 1), Label(4, 1) };
        _editorStatisticsSelected = new[] { Label(2, 2), Label(3, 2), Label(4, 2) };
        foreach (var label in _editorStatisticsTotals) label.Text = "—";
        foreach (var label in _editorStatisticsSelected) label.Visibility = Visibility.Collapsed;
        _editorStatisticsLabels[2].Visibility = Visibility.Collapsed;
        _editorStatisticsPopupBorder = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(8),
            BorderThickness = new Thickness(1),
            Child = grid,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.18 }
        };
        // Popup has its own presentation source; explicitly share the owner's live theme.
        _editorStatisticsPopupBorder.Resources.MergedDictionaries.Add(Resources);
        _editorStatisticsPopupBorder.SetResourceReference(Border.BackgroundProperty, "PaperBrushKey");
        _editorStatisticsPopupBorder.SetResourceReference(Border.BorderBrushProperty, "PaperBorderBrushKey");
        _editorStatisticsPopup = new Popup
        {
            Child = _editorStatisticsPopupBorder,
            PlacementTarget = _editorStatisticsButton,
            Placement = PlacementMode.Custom,
            AllowsTransparency = true,
            Focusable = false,
            StaysOpen = true,
            CustomPopupPlacementCallback = (popup, target, _) => new[]
            {
                new CustomPopupPlacement(new Point(target.Width - popup.Width + 8, -popup.Height), PopupPrimaryAxis.Horizontal)
            }
        };

        TextBlock Label(int row, int column)
        {
            var label = new TextBlock
            {
                Margin = new Thickness(column == 0 ? 0 : 24, 5, 0, 5),
                HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                MinWidth = column == 0 ? 0 : 48
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, row <= 1 ? "WeakTextBrushKey" : "TextBrushKey");
            Grid.SetRow(label, row);
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
            return label;
        }
    }

    private void RefreshEditorStatisticsPopupStyle()
    {
        if (_editorStatisticsGrid == null) return;
        string[] keys = ["EditorStatistics", "EditorStatisticsDocument", "EditorStatisticsSelection",
            "EditorStatisticsLines", "EditorStatisticsBody", "EditorStatisticsSource"];
        for (var i = 0; i < keys.Length; i++) _editorStatisticsLabels![i].Text = Strings.Get(keys[i]);
        foreach (TextBlock label in _editorStatisticsGrid.Children)
        {
            label.FontFamily = AppTypography.UiFontFamily;
            label.FontSize = AppTypography.Scale(12);
        }
        _editorStatisticsPopupBorder!.CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusBlock);
    }

    private void DisplayEditorStatisticsPopup(NoteTextCounts total, NoteTextCounts selected, bool hasSelection)
    {
        if (_editorStatisticsGrid == null) return;
        int[] totals = [total.Lines, total.Body, total.Source];
        int[] selections = [selected.Lines, selected.Body, selected.Source];
        _editorStatisticsLabels![2].Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < 3; i++)
        {
            _editorStatisticsTotals![i].Text = totals[i].ToString("N0", UiLanguages.EffectiveCulture);
            _editorStatisticsSelected![i].Text = selections[i].ToString("N0", UiLanguages.EffectiveCulture);
            _editorStatisticsSelected[i].Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
