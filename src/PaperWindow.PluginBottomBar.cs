using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private PaperBottomBarOverlay? _bottomBar;
    private static readonly Style BottomBarButtonStyle = BuildIconButtonStyle();

    private void BuildPaperBottomBar()
    {
        _bottomBar = new PaperBottomBarOverlay
        {
            Margin = new Thickness(12, 0, 12, 4)
        };
        Grid.SetRow(_bottomBar, 1);
        Panel.SetZIndex(_bottomBar, 20);
        _shell.Children.Add(_bottomBar);
        RefreshPaperBottomBar();
    }

    internal void RefreshPaperBottomBar()
    {
        if (_bottomBar == null) return;
        _bottomBar.ClearItems();
        if (_paper.Type == PaperTypes.Note && BodySupports(PaperBodyCapabilities.TextZoom) &&
            Math.Abs(CurrentTextZoom() - 1.0) >= 0.001)
        {
            var zoom = new PaperBottomBarItem
            {
                Id = "zoom",
                Text = $"{(int)Math.Round(CurrentTextZoom() * 100)}%",
                ToolTip = Strings.Get("ToolTipResetTextZoom"),
                Order = 999, Priority = 999, Clickable = true
            };
            _bottomBar.Add(zoom, CreateBottomBarItem(zoom, _ => _controller.SetPaperTextZoom(_paper, 1.0)));
        }

        foreach (var (runtime, item) in _controller.GetPaperBottomBarItems(_paper)
            .OrderBy(entry => entry.Runtime.ProviderId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Item.Id, StringComparer.Ordinal))
        {
            _bottomBar.Add(item, CreateBottomBarItem(item, position => runtime.Invoke(_paper, item, position)));
        }

        _bottomBar.SetEllipsis(CreateBottomBarItem(new PaperBottomBarItem { Text = "…" }, null));
    }

    private FrameworkElement CreateBottomBarItem(PaperBottomBarItem item, Action<PaperPopupPosition?>? invoke)
    {
        var text = new TextBlock
        {
            Text = item.Text, FontSize = AppTypography.Scale(10.5),
            Foreground = WeakTextBrush, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = AppTypography.Scale(120),
            VerticalAlignment = VerticalAlignment.Center
        };
        UIElement content = text;
        if (item.Icon != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(CreatePluginTodoActionIcon(item.Icon, WeakTextBrush, AppTypography.Scale(10.5)));
            if (item.Text.Length > 0)
            {
                text.Margin = new Thickness(3, 0, 0, 0);
                row.Children.Add(text);
            }
            content = row;
        }

        content.Opacity = 0.55;
        var padding = new Thickness(6, 1, 6, 1);
        if (!item.Clickable)
            return new Border
            {
                Padding = padding, Child = content, IsHitTestVisible = false, ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center
            };

        var button = new Button
        {
            Style = BottomBarButtonStyle, FontFamily = FontFamily, Padding = padding, Content = content,
            Focusable = true, IsTabStop = true, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = item.ToolTip.Length == 0 ? null : item.ToolTip
        };
        AutomationProperties.SetName(button, item.Text.Length > 0 ? item.Text :
            item.ToolTip.Length > 0 ? item.ToolTip : item.Id);
        void UpdateHighlight(object sender, DependencyPropertyChangedEventArgs e)
        {
            var active = button.IsMouseOver || button.IsKeyboardFocused;
            text.Foreground = active ? TextBrush : WeakTextBrush;
            content.Opacity = active ? 1 : 0.55;
        }
        button.IsMouseOverChanged += UpdateHighlight;
        button.IsKeyboardFocusedChanged += UpdateHighlight;
        button.Click += (_, _) =>
        {
            PaperPopupPosition? position = null;
            if (InputManager.Current.MostRecentInputDevice is KeyboardDevice && PresentationSource.FromVisual(button) != null)
            {
                var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight));
                position = new PaperPopupPosition(point.X, point.Y);
            }
            invoke?.Invoke(position);
        };
        return button;
    }

    private sealed class PaperBottomBarOverlay : Panel
    {
        private readonly List<PaperBottomBarItem> _items = [];
        private const double Gap = PaperBottomBarLayout.ItemGap;

        public void ClearItems()
        {
            Children.Clear();
            _items.Clear();
        }

        public void Add(PaperBottomBarItem item, UIElement element)
        {
            _items.Add(item);
            Children.Add(element);
        }

        public void SetEllipsis(UIElement element) => Children.Add(element);

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            return new Size();
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (InternalChildren.Count == 0) return finalSize;
            var ellipsis = InternalChildren[_items.Count];
            var plan = PaperBottomBarLayout.Plan(
                _items.Select((item, index) => new PaperBottomBarLayoutItem(item.Side, item.Order, item.Priority,
                    InternalChildren[index].DesiredSize.Width)).ToArray(), finalSize.Width, ellipsis.DesiredSize.Width);

            void Place(UIElement child, double x)
            {
                var height = Math.Min(finalSize.Height, child.DesiredSize.Height);
                child.Arrange(new Rect(x, finalSize.Height - height, child.DesiredSize.Width, height));
            }

            var visible = plan.Left.Concat(plan.Right).ToHashSet();
            for (var i = 0; i < _items.Count; i++)
                InternalChildren[i].Visibility = visible.Contains(i) ? Visibility.Visible : Visibility.Hidden;
            ellipsis.Visibility = plan.Omitted ? Visibility.Visible : Visibility.Hidden;
            var left = 0d;
            foreach (var index in plan.Left)
            {
                var item = InternalChildren[index];
                Place(item, left);
                left += item.DesiredSize.Width + Gap;
            }

            var right = finalSize.Width;
            if (plan.Omitted)
            {
                right -= ellipsis.DesiredSize.Width;
                Place(ellipsis, right);
                right -= Gap;
            }
            foreach (var index in plan.Right)
            {
                var item = InternalChildren[index];
                right -= item.DesiredSize.Width;
                Place(item, right);
                right -= Gap;
            }

            return finalSize;
        }
    }
}
