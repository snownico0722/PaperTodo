using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

// A form transition changes the viewport, not the editor's layout slot. Giving a
// full-size child a shrinking Grid slot creates an implicit WPF layout clip BEFORE
// RenderTransform. Arrange the existing child at its fixed source size instead.
internal sealed class PaperFormContentHost : Decorator
{
    private Size? _expandedSize;
    internal Size? ExpandedSize
    {
        get => _expandedSize;
        set
        {
            if (_expandedSize == value) return;
            _expandedSize = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (_expandedSize is not { } sourceSize) return base.MeasureOverride(constraint);
        Child?.Measure(sourceSize);
        return new Size(Math.Min(constraint.Width, sourceSize.Width),
            Math.Min(constraint.Height, sourceSize.Height));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (_expandedSize is not { } sourceSize) return base.ArrangeOverride(arrangeSize);
        Child?.Arrange(new Rect(sourceSize));
        return arrangeSize;
    }
}
