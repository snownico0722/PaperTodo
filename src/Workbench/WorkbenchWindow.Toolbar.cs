using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

internal sealed partial class WorkbenchWindow
{
    // The commands' localized natural widths decide the layout. This also responds to
    // text scaling and the temporary group-focus command without a screen-size guess.
    private sealed class WorkbenchToolbarPanel : Panel
    {
        private const double RowGap = 4;
        private const double CommandGap = 12;
        private Size _leftNatural, _searchNatural, _rightNatural;
        private int _rows;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (InternalChildren.Count != 3) return new Size();
            var left = InternalChildren[0];
            var search = InternalChildren[1];
            var right = InternalChildren[2];
            var unconstrained = new Size(double.PositiveInfinity, double.PositiveInfinity);
            left.Measure(unconstrained); search.Measure(unconstrained); right.Measure(unconstrained);
            _leftNatural = left.DesiredSize;
            _searchNatural = search.DesiredSize;
            _rightNatural = right.DesiredSize;
            var singleWidth = _searchNatural.Width + 2 * Math.Max(_leftNatural.Width, _rightNatural.Width);
            var width = double.IsPositiveInfinity(availableSize.Width) ? singleWidth : availableSize.Width;
            _rows = width >= singleWidth ? 1 : width >= _leftNatural.Width + _rightNatural.Width + CommandGap ? 2 : 3;
            if (_rows == 1)
                return new Size(Math.Min(width, singleWidth), Math.Max(_searchNatural.Height, Math.Max(_leftNatural.Height, _rightNatural.Height)));
            search.Measure(new Size(width, double.PositiveInfinity));
            if (_rows == 2)
                return new Size(width, search.DesiredSize.Height + RowGap + Math.Max(_leftNatural.Height, _rightNatural.Height));
            // Each command group can wrap within its own row at the minimum window width.
            left.Measure(new Size(width, double.PositiveInfinity));
            right.Measure(new Size(width, double.PositiveInfinity));
            return new Size(width, search.DesiredSize.Height + left.DesiredSize.Height + right.DesiredSize.Height + RowGap * 2);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (InternalChildren.Count != 3) return finalSize;
            var left = InternalChildren[0];
            var search = InternalChildren[1];
            var right = InternalChildren[2];
            if (_rows == 1)
            {
                ArrangeAt(left, 0, 0, _leftNatural.Width, finalSize.Height);
                ArrangeAt(search, (finalSize.Width - _searchNatural.Width) / 2, 0, _searchNatural.Width, finalSize.Height);
                ArrangeAt(right, finalSize.Width - _rightNatural.Width, 0, _rightNatural.Width, finalSize.Height);
                return finalSize;
            }
            var searchWidth = Math.Min(finalSize.Width, search.DesiredSize.Width);
            ArrangeAt(search, (finalSize.Width - searchWidth) / 2, 0, searchWidth, search.DesiredSize.Height);
            var commandTop = search.DesiredSize.Height + RowGap;
            if (_rows == 2)
            {
                var commandHeight = Math.Max(_leftNatural.Height, _rightNatural.Height);
                ArrangeAt(left, 0, commandTop, _leftNatural.Width, commandHeight);
                ArrangeAt(right, finalSize.Width - _rightNatural.Width, commandTop, _rightNatural.Width, commandHeight);
            }
            else
            {
                ArrangeAt(left, 0, commandTop, finalSize.Width, left.DesiredSize.Height);
                ArrangeAt(right, 0, commandTop + left.DesiredSize.Height + RowGap, finalSize.Width, right.DesiredSize.Height);
            }
            return finalSize;
        }

        private static void ArrangeAt(UIElement element, double x, double y, double width, double height) =>
            element.Arrange(new Rect(x, y, Math.Max(0, width), Math.Max(0, height)));
    }
}
