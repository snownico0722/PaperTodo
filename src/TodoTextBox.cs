using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;

namespace PaperTodo;

public sealed class TodoTextBox : TextBox
{
    private const double StrikeInset = 3;
    private const double StrikeThickness = 1.35;
    private const double StrikeVerticalRatio = 0.56;
    private bool _transientFindHighlightEnabled;

    public static readonly DependencyProperty IsDoneProperty =
        DependencyProperty.Register(
            nameof(IsDone),
            typeof(bool),
            typeof(TodoTextBox),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsDone
    {
        get => (bool)GetValue(IsDoneProperty);
        set => SetValue(IsDoneProperty, value);
    }

    public static readonly DependencyProperty IsSweepSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSweepSelected),
            typeof(bool),
            typeof(TodoTextBox),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsSweepSelected
    {
        get => (bool)GetValue(IsSweepSelectedProperty);
        set => SetValue(IsSweepSelectedProperty, value);
    }

    // PaperWindow.Find uses this flag only for its transient search selection. Keep the
    // state on TodoTextBox instead of enabling WPF's native inactive selection renderer,
    // which is unreliable across the separate search Popup HWND and can double-paint.
    internal new bool IsInactiveSelectionHighlightEnabled
    {
        get => _transientFindHighlightEnabled;
        set
        {
            if (_transientFindHighlightEnabled == value)
            {
                return;
            }

            _transientFindHighlightEnabled = value;
            InvalidateVisual();
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (e.ClickCount != 2)
        {
            return;
        }

        Focus();
        SelectAll();
        e.Handled = true;
    }

    protected override void OnSelectionChanged(RoutedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (_transientFindHighlightEnabled)
        {
            InvalidateVisual();
        }
    }

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        if (_transientFindHighlightEnabled)
        {
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            if (IsSweepSelected)
            {
                DrawSelectionRange(drawingContext, 0, (Text ?? "").Length);
            }

            if (_transientFindHighlightEnabled &&
                !IsKeyboardFocusWithin &&
                SelectionLength > 0)
            {
                DrawSelectionRange(drawingContext, SelectionStart, SelectionLength);
            }
        }

        base.OnRender(drawingContext);

        if (!IsDone || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var lineColor = Theme.BrightWeakTextBrush is SolidColorBrush solid
            ? Color.FromArgb(205, solid.Color.R, solid.Color.G, solid.Color.B)
            : Color.FromArgb(205, 138, 122, 99);
        var pen = new Pen(new SolidColorBrush(lineColor), StrikeThickness);
        pen.Freeze();

        if (DrawPerLineStrikes(drawingContext, pen))
        {
            return;
        }

        var y = Math.Max(ActualHeight / 2.0, 10);
        drawingContext.DrawLine(
            pen,
            new Point(StrikeInset, y),
            new Point(Math.Max(StrikeInset, ActualWidth - StrikeInset), y));
    }

    private void DrawSelectionRange(DrawingContext drawingContext, int requestedStart, int requestedLength)
    {
        var text = Text ?? "";
        if (text.Length == 0 || requestedLength <= 0)
        {
            return;
        }

        var selectionStart = Math.Clamp(requestedStart, 0, text.Length);
        var selectionEnd = Math.Clamp(
            selectionStart + requestedLength,
            selectionStart,
            text.Length);
        if (selectionEnd <= selectionStart)
        {
            return;
        }

        var selectionBrush = SelectionBrush ?? SystemColors.HighlightBrush;
        var opacity = IsFinite(SelectionOpacity)
            ? Math.Clamp(SelectionOpacity, 0, 1)
            : 1;
        if (opacity <= 0)
        {
            return;
        }

        int lineCount;
        try
        {
            lineCount = Math.Max(1, LineCount);
        }
        catch
        {
            return;
        }

        drawingContext.PushOpacity(opacity);
        try
        {
            for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
            {
                int lineStart;
                int lineLength;
                try
                {
                    lineStart = GetCharacterIndexFromLineIndex(lineIndex);
                    lineLength = GetLineLength(lineIndex);
                }
                catch
                {
                    continue;
                }

                if (lineStart < 0 || lineStart >= text.Length)
                {
                    continue;
                }

                var visibleLineEnd = Math.Min(
                    lineStart + Math.Max(0, lineLength),
                    text.Length);
                while (visibleLineEnd > lineStart && IsLineBreak(text[visibleLineEnd - 1]))
                {
                    visibleLineEnd--;
                }

                var segmentStart = Math.Max(selectionStart, lineStart);
                var segmentEnd = Math.Min(selectionEnd, visibleLineEnd);
                if (segmentEnd <= segmentStart)
                {
                    continue;
                }

                var startRect = CharacterRectOrEmpty(segmentStart, trailingEdge: false);
                var endRect = CharacterRectOrEmpty(segmentEnd - 1, trailingEdge: true);
                if (!IsUsableRect(startRect) || !IsUsableRect(endRect))
                {
                    continue;
                }

                var left = Math.Max(0, startRect.Left);
                var top = Math.Max(0, Math.Min(startRect.Top, endRect.Top));
                var right = Math.Min(ActualWidth, endRect.Right);
                var bottom = Math.Min(ActualHeight, Math.Max(startRect.Bottom, endRect.Bottom));
                if (right > left && bottom > top)
                {
                    drawingContext.DrawRectangle(
                        selectionBrush,
                        null,
                        new Rect(left, top, right - left, bottom - top));
                }
            }
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    private bool DrawPerLineStrikes(DrawingContext drawingContext, Pen pen)
    {
        int lineCount;
        try
        {
            lineCount = LineCount;
        }
        catch
        {
            return false;
        }

        if (lineCount <= 1)
        {
            return false;
        }

        var text = Text ?? "";
        var rightLimit = Math.Max(StrikeInset, ActualWidth - StrikeInset);
        var firstLineRect = CharacterRectOrEmpty(0, trailingEdge: false);
        if (!IsUsableRect(firstLineRect))
        {
            return false;
        }

        var dpiScaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var firstLineStrikeY = firstLineRect.Top + (firstLineRect.Height * StrikeVerticalRatio);
        var lineAdvance = firstLineRect.Height;
        var drewAny = false;

        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            int start;
            int length;
            try
            {
                start = GetCharacterIndexFromLineIndex(lineIndex);
                length = GetLineLength(lineIndex);
            }
            catch
            {
                continue;
            }

            if (start < 0 || start > text.Length)
            {
                continue;
            }

            var endExclusive = Math.Min(start + Math.Max(0, length), text.Length);
            while (endExclusive > start && IsLineBreak(text[endExclusive - 1]))
            {
                endExclusive--;
            }
            if (endExclusive <= start)
            {
                continue;
            }

            var y = SnapToDevicePixel(firstLineStrikeY + (lineIndex * lineAdvance), dpiScaleY);
            if (rightLimit <= StrikeInset + 1 || !IsFinite(y))
            {
                continue;
            }

            drawingContext.DrawLine(pen, new Point(StrikeInset, y), new Point(rightLimit, y));
            drewAny = true;
        }

        return drewAny;
    }

    private Rect CharacterRectOrEmpty(int characterIndex, bool trailingEdge)
    {
        try
        {
            return GetRectFromCharacterIndex(characterIndex, trailingEdge);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private static bool IsLineBreak(char c) => c is '\r' or '\n';

    private static double SnapToDevicePixel(double value, double dpiScale) =>
        dpiScale > 0 ? Math.Round(value * dpiScale) / dpiScale : value;

    private static bool IsUsableRect(Rect rect) =>
        !rect.IsEmpty &&
        IsFinite(rect.Left) &&
        IsFinite(rect.Right) &&
        IsFinite(rect.Top) &&
        IsFinite(rect.Height) &&
        rect.Height > 0;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
