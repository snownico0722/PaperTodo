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
                (ReferenceEquals(visual, _editorStatisticsButton) || _editorStatisticsButton.IsAncestorOf(visual))) return;
            CloseEditorStatisticsPopup();
        };
        _shell.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _editorStatisticsPopup?.IsOpen == true)
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
        _editorStatisticsBar.Margin = new Thickness(0, 0,
            IsExpandedEditorSidebarOpen ? ExpandedEditorSidebarWidth : 0, 0);
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
}
