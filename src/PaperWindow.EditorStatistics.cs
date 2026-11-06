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
    private int _editorTextRevision;
    private int _editorTotalCountRevision = -1;
    private bool _editorStatisticsRunning;
    private string? _editorCountedSelection;
    private int _editorSelectionCount;
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
            _editorCountedSelection = null;
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
    {
        QueueExpandedEditorStatistics();
    }

    private void QueueExpandedEditorStatistics()
    {
        // Coalesce typing/selection changes; moving the caret never reparses the whole note.
        if (_editorStatisticsSource != null && _editorStatisticsTimer is { IsEnabled: false })
            _editorStatisticsTimer.Start();
    }

    private async void UpdateExpandedEditorStatistics()
    {
        _editorStatisticsTimer?.Stop();
        if (_editorStatisticsRunning || _editorStatisticsSource is not { } box ||
            _editorStatisticsText == null) return;

        var revision = _editorTextRevision;
        var text = _editorTotalCountRevision != revision ? box.Text : null;
        var selection = box.SelectedText;
        var selectAll = box.SelectionLength == box.Document.TextLength;
        var countSelection = !string.Equals(selection, _editorCountedSelection, StringComparison.Ordinal);
        var total = _editorTotalCount;
        var selected = _editorSelectionCount;
        _editorStatisticsRunning = true;
        try
        {
            if (text != null || countSelection)
            {
                // Only immutable strings cross threads. There is at most one count in
                // flight per editor, and no second parse for a whole-document selection.
                (total, selected) = await System.Threading.Tasks.Task.Run(() =>
                {
                    var full = text != null ? NoteTextStatistics.Count(text) : total;
                    var part = selection.Length == 0 ? 0 :
                        selectAll ? full :
                        countSelection ? NoteTextStatistics.Count(selection) : selected;
                    return (full, part);
                });
            }
            if (!ReferenceEquals(box, _editorStatisticsSource) || revision != _editorTextRevision)
                return;
            _editorTotalCount = total;
            _editorTotalCountRevision = revision;
            _editorCountedSelection = selection;
            _editorSelectionCount = selected;
            if (selection != box.SelectedText) return;
            _editorStatisticsText.Text = selection.Length == 0
                ? Strings.Format("EditorCharacterCount", total)
                : Strings.Format("EditorSelectionCount", total, selected);
        }
        finally
        {
            _editorStatisticsRunning = false;
            if (_editorStatisticsSource is { } current &&
                (_editorTotalCountRevision != _editorTextRevision ||
                 _editorCountedSelection != current.SelectedText))
                QueueExpandedEditorStatistics();
        }
    }
}
