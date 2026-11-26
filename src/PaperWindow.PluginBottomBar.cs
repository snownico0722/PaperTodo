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
        _bottomBar = new PaperBottomBarOverlay(this)
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
        var entries = new List<BottomBarEntry>();
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
            entries.Add(new(null, zoom, _ => _controller.SetPaperTextZoom(_paper, 1.0)));
        }

        foreach (var (runtime, item) in _controller.GetPaperBottomBarItems(_paper)
            .OrderBy(entry => entry.Runtime.ProviderId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Item.Id, StringComparer.Ordinal))
        {
            entries.Add(new(runtime.ProviderId, item, position => runtime.Invoke(_paper, item, position)));
        }

        _bottomBar.SetItems(entries);
    }

    private sealed record BottomBarEntry(string? ProviderId, PaperBottomBarItem Item, Action<PaperPopupPosition?>? Invoke)
    {
        public (string?, string) Key => (ProviderId, Item.Id);
    }

    private sealed class BottomBarItemView
    {
        public readonly FrameworkElement Root;
        private TextBlock _text = null!;
        private UIElement _content = null!;
        private (PaperTopBarIcon? Icon, Brush Foreground, double Size, FontFamily Font) _iconStyle;
        private Action<PaperPopupPosition?>? _invoke;

        public BottomBarItemView(bool clickable)
        {
            var padding = new Thickness(6, 1, 6, 1);
            if (clickable)
            {
                var button = new Button
                {
                    Style = BottomBarButtonStyle, Padding = padding, Focusable = true, IsTabStop = true
                };
                button.MouseEnter += (_, _) => UpdateHighlight();
                button.MouseLeave += (_, _) => UpdateHighlight();
                button.IsKeyboardFocusedChanged += (_, _) => UpdateHighlight();
                button.Click += (_, _) =>
                {
                    PaperPopupPosition? position = null;
                    if (InputManager.Current.MostRecentInputDevice is KeyboardDevice && PresentationSource.FromVisual(button) != null)
                    {
                        var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight));
                        position = new PaperPopupPosition(point.X, point.Y);
                    }
                    _invoke?.Invoke(position);
                };
                Root = button;
            }
            else Root = new Border { Padding = padding, IsHitTestVisible = false };
            Root.ClipToBounds = true;
            Root.VerticalAlignment = VerticalAlignment.Center;
        }

        public void Update(PaperWindow owner, BottomBarEntry entry)
        {
            var item = entry.Item;
            _invoke = entry.Invoke; // Same key may now refer to a newer item or Runtime.
            var style = (Icon: item.Icon, Foreground: WeakTextBrush, Size: AppTypography.Scale(10.5), Font: AppTypography.UiFontFamily);
            if (_content == null || (item.Icon != null || _iconStyle.Icon != null) && !_iconStyle.Equals(style))
            {
                _text = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _content = _text;
                if (item.Icon != null)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(CreatePluginTodoActionIcon(item.Icon, style.Foreground, style.Size));
                    _text.Margin = new Thickness(3, 0, 0, 0);
                    row.Children.Add(_text);
                    _content = row;
                }
                if (Root is Button button) button.Content = _content;
                else ((Border)Root).Child = _content;
            }
            _iconStyle = style;
            _text.Text = item.Text;
            _text.FontSize = style.Size;
            _text.MaxWidth = AppTypography.Scale(120);
            _text.Visibility = item.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (Root is Button action)
            {
                action.FontFamily = owner.FontFamily;
                action.ToolTip = item.ToolTip.Length == 0 ? null : item.ToolTip;
                AutomationProperties.SetName(action, item.Text.Length > 0 ? item.Text :
                    item.ToolTip.Length > 0 ? item.ToolTip : item.Id);
            }
            UpdateHighlight();
        }

        private void UpdateHighlight()
        {
            var active = Root is Button button && (button.IsMouseOver || button.IsKeyboardFocused);
            _text.Foreground = active ? TextBrush : WeakTextBrush;
            _content.Opacity = active ? 1 : 0.55;
        }
    }

    private sealed class PaperBottomBarOverlay(PaperWindow owner) : Panel
    {
        private IReadOnlyList<BottomBarEntry> _items = [];
        private readonly Dictionary<(string?, string), BottomBarItemView> _views = [];
        private readonly BottomBarItemView _ellipsis = new(false);
        private static readonly BottomBarEntry Ellipsis = new(null, new() { Text = "…" }, null);
        private const double Gap = PaperBottomBarLayout.ItemGap;

        public void SetItems(IReadOnlyList<BottomBarEntry> entries)
        {
            var keys = entries.Select(entry => entry.Key).ToHashSet();
            foreach (var key in _views.Keys.Where(key => !keys.Contains(key)).ToArray())
            {
                Children.Remove(_views[key].Root);
                _views.Remove(key);
            }

            // Stable provider/ID order means retained siblings never need to be detached.
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!_views.TryGetValue(entry.Key, out var view) || (view.Root is Button) != entry.Item.Clickable)
                {
                    if (view != null) Children.Remove(view.Root);
                    _views[entry.Key] = view = new(entry.Item.Clickable);
                    view.Update(owner, entry);
                    Children.Insert(i, view.Root);
                }
                else view.Update(owner, entry);
            }

            _ellipsis.Update(owner, Ellipsis);
            if (_ellipsis.Root.Parent == null) Children.Add(_ellipsis.Root);
            _items = entries;
            InvalidateArrange(); // Side/order/priority can change without a size change.
        }

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
                _items.Select((entry, index) => new PaperBottomBarLayoutItem(entry.Item.Side, entry.Item.Order, entry.Item.Priority,
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
