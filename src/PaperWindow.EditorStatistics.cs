using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const double ExpandedEditorStatusHeight = 24;
    private Border? _editorStatisticsBar;
    private Button? _editorStatisticsButton;
    private TextBlock? _editorStatisticsText;
    private MarkdownTextBox? _editorStatisticsSource;
    private DispatcherTimer? _editorStatisticsTimer;
    private int _editorTextRevision;
    private int _editorCountRevision = -1;
    private bool _editorStatisticsRunning;
    private NoteTextStatistics? _editorCounts;

    private void BuildExpandedEditorStatistics()
    {
        _editorStatisticsText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = AppTypography.UiFontFamily
        };
        _editorStatisticsButton = new Button
        {
            Style = BuildIconButtonStyle(),
            Focusable = false,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 4, 0),
            Content = _editorStatisticsText
        };
        _editorStatisticsButton.Click += (_, _) => ToggleEditorStatisticsPopup();
        _editorStatisticsBar = new Border
        {
            Height = ExpandedEditorStatusHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Child = _editorStatisticsButton
        };
        _editorStatisticsBar.SetResourceReference(Border.BackgroundProperty, "PaperSurfaceBrushKey");
        Grid.SetRow(_editorStatisticsBar, 1);
        Panel.SetZIndex(_editorStatisticsBar, 80);
        _shell.Children.Add(_editorStatisticsBar);
        _shell.PreviewMouseDown += (_, e) =>
        {
            if (e.OriginalSource is System.Windows.Media.Visual visual &&
                (ReferenceEquals(visual, _editorStatisticsButton) || _editorStatisticsButton.IsAncestorOf(visual) ||
                 ReferenceEquals(visual, _editorStatisticsPopup) || _editorStatisticsPopup?.IsAncestorOf(visual) == true)) return;
            CloseEditorStatisticsPopup();
        };
        _shell.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _editorStatisticsPopup?.Visibility == Visibility.Visible)
            {
                CloseEditorStatisticsPopup();
                e.Handled = true;
            }
        };
        _editorStatisticsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _editorStatisticsTimer.Tick += (_, _) => UpdateExpandedEditorStatistics();
    }

    private void RefreshExpandedEditorStatistics(bool visible)
    {
        if (_editorStatisticsBar == null || _editorStatisticsText == null) return;
        _editorStatisticsBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _editorStatisticsBar.Margin = new Thickness(
            IsExpandedEditorSidebarOpen ? ExpandedEditorSidebarWidth : 0, 0, 0, 0);
        _editorStatisticsText.FontSize = AppTypography.Scale(10.5);
        _editorStatisticsText.FontFamily = AppTypography.UiFontFamily;
        System.Windows.Automation.AutomationProperties.SetName(_editorStatisticsButton!, Strings.Get("EditorStatistics"));
        RefreshEditorStatisticsPopupStyle();
        CloseEditorStatisticsPopup();
        var source = visible ? _noteBox : null;
        if (!ReferenceEquals(source, _editorStatisticsSource))
        {
            if (_editorStatisticsSource is { } previous)
            {
                previous.TextChanged -= OnEditorStatisticsTextChanged;
                previous.TextArea.SelectionChanged -= OnEditorStatisticsSelectionChanged;
            }
            _editorStatisticsTimer?.Stop();
            _editorStatisticsSource = source;
            _editorTextRevision++;
            _editorCounts = null;
            if (source != null)
            {
                source.TextChanged += OnEditorStatisticsTextChanged;
                source.TextArea.SelectionChanged += OnEditorStatisticsSelectionChanged;
            }
        }
        if (visible) QueueExpandedEditorStatistics();
    }

    private void OnEditorStatisticsTextChanged(object? sender, EventArgs e)
    {
        _editorTextRevision++;
        QueueExpandedEditorStatistics();
    }

    private void OnEditorStatisticsSelectionChanged(object? sender, EventArgs e)
        => QueueExpandedEditorStatistics();

    private void QueueExpandedEditorStatistics()
    {
        if (_editorStatisticsSource != null && _editorStatisticsTimer is { IsEnabled: false })
            _editorStatisticsTimer.Start();
    }

    private async void UpdateExpandedEditorStatistics()
    {
        _editorStatisticsTimer?.Stop();
        if (_editorStatisticsRunning || _editorStatisticsSource is not { } box) return;
        var revision = _editorTextRevision;
        _editorStatisticsRunning = true;
        try
        {
            if (_editorCountRevision != revision || _editorCounts == null)
            {
                // Only immutable text crosses threads; at most one parse is in flight.
                var text = box.Text;
                var counts = await System.Threading.Tasks.Task.Run(() => NoteTextStatistics.Create(text));
                if (!ReferenceEquals(box, _editorStatisticsSource) || revision != _editorTextRevision) return;
                _editorCounts = counts;
                _editorCountRevision = revision;
            }
            // Read the current selection after the await, so a slow parse cannot restore
            // stale selected counts. Binary-search queries never allocate SelectedText.
            DisplayEditorStatistics();
        }
        finally
        {
            _editorStatisticsRunning = false;
            if (_editorStatisticsSource != null && _editorCountRevision != _editorTextRevision)
                QueueExpandedEditorStatistics();
        }
    }

    private void DisplayEditorStatistics()
    {
        if (_editorStatisticsSource is not { } box || _editorCounts is not { } counts ||
            _editorCountRevision != _editorTextRevision) return;
        var hasSelection = !box.TextArea.Selection.IsEmpty;
        var selected = default(NoteTextCounts);
        foreach (var segment in box.TextArea.Selection.Segments)
        {
            var part = counts.Select(segment.StartOffset, segment.Length);
            selected = new(selected.Lines + part.Lines, selected.Body + part.Body, selected.Source + part.Source);
        }
        _editorStatisticsText!.Text = hasSelection
            ? Strings.Format("EditorSelectionCount", counts.Total.Body, selected.Body)
            : Strings.Format("EditorCharacterCount", counts.Total.Body);
        DisplayEditorStatisticsPopup(counts.Total, selected, hasSelection);
    }

    private Border? _editorStatisticsPopup;
    private Grid? _editorStatisticsGrid;
    private TextBlock[]? _editorStatisticsLabels;
    private TextBlock[]? _editorStatisticsTotals;
    private TextBlock[]? _editorStatisticsSelected;

    private void ToggleEditorStatisticsPopup()
    {
        if (_editorStatisticsSource == null) return;
        if (_editorStatisticsPopup?.Visibility == Visibility.Visible)
        {
            CloseEditorStatisticsPopup();
            return;
        }
        if (_editorStatisticsPopup == null) BuildEditorStatisticsPopup();
        RefreshEditorStatisticsPopupStyle();
        DisplayEditorStatistics();
        _editorStatisticsPopup!.Visibility = Visibility.Visible;
        // Cached results appear immediately. New text still uses the coalesced worker.
        QueueExpandedEditorStatistics();
    }

    private void CloseEditorStatisticsPopup()
    {
        if (_editorStatisticsPopup != null) _editorStatisticsPopup.Visibility = Visibility.Collapsed;
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
        _editorStatisticsPopup = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer
            {
                Content = grid,
                Focusable = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        // Stay in the borrowed shell so layout, clipping and theme belong to the editor.
        _editorStatisticsPopup.SetResourceReference(Border.BackgroundProperty, "PaperBrushKey");
        Grid.SetRow(_editorStatisticsPopup, 1);
        Panel.SetZIndex(_editorStatisticsPopup, 90);
        _shell.Children.Add(_editorStatisticsPopup);

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
        _editorStatisticsPopup!.CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusBlock);
        _editorStatisticsPopup.Margin = new Thickness(
            8 + (IsExpandedEditorSidebarOpen ? ExpandedEditorSidebarWidth : 0), 8, 8,
            ExpandedEditorStatusHeight + 8);
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
