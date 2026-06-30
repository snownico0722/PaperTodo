using System.Windows;
using System.Windows.Controls;
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

    protected override void OnRender(DrawingContext drawingContext)
    {
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

        if (lineCount <= 0)
        {
            return false;
        }

        var text = Text ?? "";
        var textLength = text.Length;
        var rightLimit = Math.Max(StrikeInset, ActualWidth - StrikeInset);
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

            if (start < 0 || start > textLength)
            {
                continue;
            }

            var endExclusive = Math.Min(start + Math.Max(0, length), textLength);
            while (endExclusive > start && IsLineBreak(text[endExclusive - 1]))
            {
                endExclusive--;
            }

            var startRect = CharacterRectOrEmpty(start, trailingEdge: false);
            if (!IsUsableRect(startRect) && endExclusive > start)
            {
                startRect = CharacterRectOrEmpty(endExclusive - 1, trailingEdge: false);
            }
            if (!IsUsableRect(startRect))
            {
                continue;
            }

            var x1 = Math.Max(StrikeInset, startRect.Left + 1);
            var x2 = rightLimit;
            if (endExclusive > start)
            {
                var endRect = CharacterRectOrEmpty(endExclusive - 1, trailingEdge: true);
                if (IsUsableRect(endRect))
                {
                    x2 = Math.Min(rightLimit, Math.Max(x1 + 8, endRect.Right - 1));
                }
            }
            else
            {
                x2 = Math.Min(rightLimit, x1 + 24);
            }

            var y = startRect.Top + (startRect.Height * StrikeVerticalRatio);
            if (x2 <= x1 + 1 || !IsFinite(y))
            {
                continue;
            }

            drawingContext.DrawLine(pen, new Point(x1, y), new Point(x2, y));
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

    private static bool IsUsableRect(Rect rect) =>
        !rect.IsEmpty &&
        IsFinite(rect.Left) &&
        IsFinite(rect.Right) &&
        IsFinite(rect.Top) &&
        IsFinite(rect.Height) &&
        rect.Height > 0;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
