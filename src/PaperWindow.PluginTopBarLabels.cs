using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static bool _pluginTopBarLabelsLoadedHandlerRegistered;
    private StackPanel? _pluginTopBarLabelsHost;

    internal static void EnsurePluginTopBarLabelsLoadedHandler()
    {
        if (_pluginTopBarLabelsLoadedHandlerRegistered)
        {
            return;
        }
        _pluginTopBarLabelsLoadedHandlerRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(PaperWindow),
            LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is PaperWindow window && !window.IsClosed)
                {
                    window.RefreshPluginTopBarLabels();
                }
            }));
    }

    internal void RefreshPluginTopBarLabels()
    {
        if (IsClosed || !_isShellBuilt || _topBarActionButtonsHost == null)
        {
            return;
        }

        _pluginTopBarLabelsHost ??= new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (_pluginTopBarLabelsHost.Parent == null)
        {
            _topBarActionButtonsHost.Children.Insert(0, _pluginTopBarLabelsHost);
        }

        _pluginTopBarLabelsHost.Children.Clear();
        foreach (var binding in _controller.GetPluginTopBarLabels(_paper.Id))
        {
            var label = binding.Label;
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            if (label.Icon != null)
            {
                content.Children.Add(CreatePluginTodoActionIcon(
                    label.Icon,
                    WeakTextBrush,
                    AppTypography.Scale(10.5)));
            }
            content.Children.Add(new TextBlock
            {
                Text = label.Text,
                Foreground = WeakTextBrush,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(10.5),
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = label.Icon == null
                    ? new Thickness(0)
                    : new Thickness(3, 0, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = AppTypography.Scale(120),
                IsHitTestVisible = false
            });

            _pluginTopBarLabelsHost.Children.Add(new Border
            {
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(1, 0, 1, 0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = string.IsNullOrWhiteSpace(label.ToolTip) ? null : label.ToolTip,
                Child = content
            });
        }

        _pluginTopBarLabelsHost.Visibility =
            _pluginTopBarLabelsHost.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateTopBarResponsiveLayout();
        RefreshPluginTopBarActions();
    }
}
