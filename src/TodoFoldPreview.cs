using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace PaperTodo;

// WPF TextBox has MaxLines but no TextTrimming. This display-only overlay uses
// its actual first two visual-line boundaries and adds a display-only ellipsis.
// TextBlock's native character trimming keeps that ellipsis inside the second
// line when it is full. It never modifies PaperItem.Text or editor history.
internal sealed class TodoFoldPreview
{
    private readonly TodoTextBox _editor;
    private readonly TextBlock _preview;
    private bool _folded;
    private bool _refreshQueued;

    internal TodoFoldPreview(TodoTextBox editor, Grid row)
    {
        _editor = editor;
        _preview = new TextBlock
        {
            Tag = "TodoFoldPreview",
            Text = editor.Text,
            FontFamily = editor.FontFamily,
            FontSize = editor.FontSize,
            FontWeight = editor.FontWeight,
            TextAlignment = editor.TextAlignment,
            Padding = editor.Padding,
            // Line breaks come from the TextBox's visual layout; do not wrap
            // those extracted lines a second time with TextBlock metrics.
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed
        };
        _preview.SetBinding(TextBlock.ForegroundProperty,
            new Binding(nameof(Control.Foreground)) { Source = editor });
        Grid.SetColumn(_preview, 1);
        Panel.SetZIndex(_preview, 1);
        row.Children.Add(_preview);

        editor.Loaded += (_, _) => QueueRefresh();
        editor.SizeChanged += (_, _) => QueueRefresh();
        editor.TextChanged += (_, _) => QueueRefresh();
        editor.FoldSelectionVisualChanged += (_, _) => QueueRefresh();
    }

    internal void SetDone(bool done)
    {
        _preview.TextDecorations = done ? TextDecorations.Strikethrough : null;
    }

    internal void SetFolded(bool folded)
    {
        _folded = folded;
        if (!folded)
        {
            _preview.Visibility = Visibility.Collapsed;
            _editor.Opacity = 1;
            return;
        }

        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (!_folded || _refreshQueued) return;

        _refreshQueued = true;
        _ = _editor.Dispatcher.BeginInvoke((Action)Refresh,
            DispatcherPriority.Loaded);
    }

    private void Refresh()
    {
        _refreshQueued = false;
        if (!_folded) return;

        // Search selection and multi-row sweep selection are already drawn by
        // TodoTextBox. Show that original clipped surface for these transient
        // highlights rather than hide them beneath the ellipsis preview.
        if (_editor.IsSweepSelected ||
            (_editor.IsInactiveSelectionHighlightEnabled && _editor.SelectionLength > 0))
        {
            _preview.Visibility = Visibility.Collapsed;
            _editor.Opacity = 1;
            return;
        }

        if (!_editor.IsLoaded || _editor.IsKeyboardFocusWithin ||
            _editor.MaxLines != 2 || _editor.LineCount <= 2 ||
            _editor.ActualWidth <= 0 || _editor.ActualHeight <= 0)
            return;

        // The TextBlock must be constrained to exactly the two visible text
        // lines, even if other controls make the parent row taller. The caret
        // rectangle uses the TextBox's *actual* wrapping and current DPI.
        var secondStart = _editor.GetCharacterIndexFromLineIndex(1);
        var secondLength = _editor.GetLineLength(1);
        if (secondStart < 0 || secondLength < 0) return;
        var secondEnd = Math.Clamp(secondStart + secondLength, secondStart, _editor.Text.Length);
        var secondLine = _editor.GetRectFromCharacterIndex(secondStart);
        if (secondLine.IsEmpty || secondLine.Height <= 0) return;

        var lineHeight = _editor.FontSize * _editor.FontFamily.LineSpacing;
        var twoLineHeight = Math.Min(_editor.ActualHeight,
            Math.Max(secondLine.Bottom + _editor.Padding.Bottom + 1,
                     _editor.Padding.Top + 2 * lineHeight + _editor.Padding.Bottom));

        if (twoLineHeight <= 0) return;

        // A two-line TextBlock with the *entire* source can silently clip
        // vertical overflow without drawing a trimming glyph. Explicitly
        // extract the visible lines and append an ellipsis to line two.
        var first = _editor.Text[..secondStart].TrimEnd('\r', '\n');
        var second = _editor.Text[secondStart..secondEnd].TrimEnd('\r', '\n', ' ', '\t');
        _preview.Text = first + "\n" + second + "…";
        _preview.Width = _editor.ActualWidth;
        _preview.Height = twoLineHeight;
        _preview.Visibility = Visibility.Visible;
        _editor.Opacity = 0;
    }
}
