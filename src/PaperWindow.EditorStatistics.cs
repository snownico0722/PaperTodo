using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const double ExpandedEditorStatusHeight = 24;
    private Border? _editorStatisticsBar;
    private TextBlock? _editorStatisticsText;
    private MarkdownTextBox? _editorStatisticsSource;
    private DispatcherTimer? _editorStatisticsTimer;
    private bool _editorTotalCountDirty = true;
    private int _editorTotalCount;

    private void BuildExpandedEditorStatistics()
    {
        _editorStatisticsText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        _editorStatisticsText.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        _editorStatisticsBar = new Border
        {
            Height = ExpandedEditorStatusHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Child = _editorStatisticsText
        };
        _editorStatisticsBar.SetResourceReference(Border.BackgroundProperty, "PaperSurfaceBrushKey");
        Grid.SetRow(_editorStatisticsBar, 1);
        Panel.SetZIndex(_editorStatisticsBar, 80);
        _shell.Children.Add(_editorStatisticsBar);
        _editorStatisticsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _editorStatisticsTimer.Tick += (_, _) => UpdateExpandedEditorStatistics();
    }

    private void RefreshExpandedEditorStatistics(bool visible)
    {
        if (_editorStatisticsBar == null || _editorStatisticsText == null) return;
        _editorStatisticsBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _editorStatisticsBar.Margin = new Thickness(0, 0,
            _isExpandedEditorSidebarOpen ? ExpandedEditorSidebarWidth : 0, 0);
        _editorStatisticsText.FontSize = AppTypography.Scale(10.5);
        var source = visible ? _noteBox : null;
        if (!ReferenceEquals(source, _editorStatisticsSource))
        {
            if (_editorStatisticsSource is { } previous)
            {
                previous.TextChanged -= OnEditorStatisticsTextChanged;
                previous.TextArea.SelectionChanged -= OnEditorStatisticsSelectionChanged;
                previous.TextArea.Caret.PositionChanged -= OnEditorPositionChanged;
                previous.TextArea.TextView.ScrollOffsetChanged -= OnEditorPositionChanged;
            }
            _editorStatisticsTimer?.Stop();
            _editorStatisticsSource = source;
            _editorTotalCountDirty = true;
            if (source != null)
            {
                source.TextChanged += OnEditorStatisticsTextChanged;
                source.TextArea.SelectionChanged += OnEditorStatisticsSelectionChanged;
                source.TextArea.Caret.PositionChanged += OnEditorPositionChanged;
                source.TextArea.TextView.ScrollOffsetChanged += OnEditorPositionChanged;
            }
        }
        if (visible) UpdateExpandedEditorStatistics();
    }

    private void OnEditorStatisticsTextChanged(object? sender, EventArgs e)
    {
        _editorTotalCountDirty = true;
        QueueExpandedEditorStatistics();
    }

    private void OnEditorStatisticsSelectionChanged(object? sender, EventArgs e)
    {
        QueueExpandedEditorStatistics();
        MarkExpandedEditorViewDirty();
    }

    private void OnEditorPositionChanged(object? sender, EventArgs e) => MarkExpandedEditorViewDirty();

    private void QueueExpandedEditorStatistics()
    {
        // Coalesce typing/selection changes; moving the caret never reparses the whole note.
        if (_editorStatisticsSource != null && _editorStatisticsTimer is { IsEnabled: false })
            _editorStatisticsTimer.Start();
    }

    private void UpdateExpandedEditorStatistics()
    {
        _editorStatisticsTimer?.Stop();
        if (_editorStatisticsSource is not { } box || _editorStatisticsText == null) return;
        if (_editorTotalCountDirty)
        {
            _editorTotalCount = NoteTextStatistics.Count(box.Text);
            _editorTotalCountDirty = false;
        }
        _editorStatisticsText.Text = box.SelectionLength == 0
            ? Strings.Format("EditorCharacterCount", _editorTotalCount)
            : Strings.Format("EditorSelectionCount", _editorTotalCount, NoteTextStatistics.Count(box.SelectedText));
    }
}
