using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static bool _pluginTopBarLabelsLoadedHandlerRegistered;
    private StackPanel? _pluginTopBarLabelsHost;
    private bool _pluginTopBarLabelCapacityHookInstalled;

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
        var measuredWidth = 0.0;
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

            var element = new Border
            {
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(1, 0, 1, 0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = string.IsNullOrWhiteSpace(label.ToolTip) ? null : label.ToolTip,
                Child = content
            };
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            measuredWidth += element.DesiredSize.Width;
            _pluginTopBarLabelsHost.Children.Add(element);
        }

        _pluginTopBarLabelsHost.Width = Math.Ceiling(measuredWidth);
        _pluginTopBarLabelsHost.Visibility = Visibility.Collapsed;

        // Interactive controls settle first; labels only occupy width that remains afterwards.
        RefreshPluginTopBarActions();
        EnsurePluginTopBarLabelCapacityHook();
        ReconcilePluginTopBarLabelCapacity();
    }

    private void EnsurePluginTopBarLabelCapacityHook()
    {
        if (_pluginTopBarLabelCapacityHookInstalled || _topBar == null)
        {
            return;
        }

        _pluginTopBarLabelCapacityHookInstalled = true;
        _topBar.SizeChanged += (_, _) => ReconcilePluginTopBarLabelCapacity();
    }

    private void ReconcilePluginTopBarLabelCapacity()
    {
        if (_pluginTopBarLabelsHost == null || _topBarActionButtonsHost == null)
        {
            return;
        }

        if (_paper.IsCollapsed || _pluginTopBarLabelsHost.Children.Count == 0)
        {
            _pluginTopBarLabelsHost.Visibility = Visibility.Collapsed;
            UpdateTopBarResponsiveLayout();
            return;
        }

        _pluginTopBarLabelsHost.Visibility = Visibility.Visible;
        UpdateTopBarResponsiveLayout();
        if (_topBarActionButtonsHost.Visibility == Visibility.Visible)
        {
            return;
        }

        _pluginTopBarLabelsHost.Visibility = Visibility.Collapsed;
        UpdateTopBarResponsiveLayout();
    }
}
