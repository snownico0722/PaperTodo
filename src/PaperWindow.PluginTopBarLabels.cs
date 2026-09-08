using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static bool _pluginTopBarLabelsLoadedHandlerRegistered;
    private StackPanel? _pluginTopBarLabelsHost;

    internal static void EnsurePluginTopBarLabelsLoadedHandler() =>
        EnsurePluginLoadedRefreshHandler(
            ref _pluginTopBarLabelsLoadedHandlerRegistered,
            static window => window.RefreshPluginTopBarLabels());

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty)
        {
            RefreshPluginTopBarLabels();
        }
    }

    internal void RefreshPluginTopBarLabels()
    {
        if (IsClosed || !_isShellBuilt || _topBarActionButtonsHost == null)
        {
            return;
        }

        EnsurePluginTopBarButtonsHost();
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
                var icon = CreatePluginTodoActionIcon(
                    label.Icon,
                    WeakTextBrush,
                    AppTypography.Scale(10.5));
                ApplyPluginTopBarLabelTheme(icon, label.Icon);
                content.Children.Add(icon);
            }

            var text = new TextBlock
            {
                Text = label.Text,
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
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            content.Children.Add(text);

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

        _pluginTopBarLabelsHost.Visibility = Visibility.Collapsed;
        MeasurePluginTopBarLabelsWidth();

        // One layout authority: interactive controls settle first, then labels use the remainder.
        ReconcilePluginTopBarCapacity();
    }

    private static void ApplyPluginTopBarLabelTheme(
        UIElement element,
        PaperTopBarIcon icon)
    {
        if (element is TextBlock text)
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            return;
        }

        if (element is not Path path)
        {
            return;
        }

        if (icon.RenderMode == PaperTopBarSvgRenderMode.Stroke)
        {
            path.SetResourceReference(Shape.StrokeProperty, "WeakTextBrushKey");
        }
        else
        {
            path.SetResourceReference(Shape.FillProperty, "WeakTextBrushKey");
        }
    }

    private void MeasurePluginTopBarLabelsWidth()
    {
        if (_pluginTopBarLabelsHost == null)
        {
            return;
        }

        _pluginTopBarLabelsHost.Width = double.NaN;
        _pluginTopBarLabelsHost.Measure(
            new Size(double.PositiveInfinity, double.PositiveInfinity));
        _pluginTopBarLabelsHost.Width =
            Math.Ceiling(_pluginTopBarLabelsHost.DesiredSize.Width);
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
