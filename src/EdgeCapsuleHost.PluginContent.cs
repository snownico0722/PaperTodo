using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    private Border? _pluginContentLayer;

    public void SetPluginContent(FrameworkElement? content, string? toolTip)
    {
        if (_disposed)
        {
            return;
        }

        _pluginContentLayer ??= CreatePluginContentLayer();
        if (content == null)
        {
            _pluginContentLayer.Child = null;
            _pluginContentLayer.Visibility = Visibility.Collapsed;
            ApplyDefaultContentVisibility(_appliedFrame.TitleVisible);
            ContentArea.ToolTip = null;
            return;
        }

        if (content is Window ||
            (content.Parent != null &&
             !ReferenceEquals(content.Parent, _pluginContentLayer)))
        {
            throw new InvalidOperationException(
                "Capsule content must be a fresh FrameworkElement or the current hosted view.");
        }

        content.IsHitTestVisible = false;
        content.Focusable = false;
        if (!ReferenceEquals(_pluginContentLayer.Child, content))
        {
            _pluginContentLayer.Child = content;
        }
        // Keep the compact plugin tree layout-resident for the same reason as ContentGrid: a
        // closing preview can restore it without exposing a one-frame empty shell. Opacity is bound
        // to ContentGrid so built-in icon/title and custom capsule content always share the exact
        // same 35 ms compact fade clock.
        _pluginContentLayer.Visibility = Visibility.Visible;
        ApplyDefaultContentVisibility(_appliedFrame.TitleVisible);
        ContentArea.ToolTip = toolTip;
    }

    private void ApplyDefaultContentVisibility(bool titleVisible)
    {
        // Frame updates and content refreshes must agree: plugin content replaces both defaults,
        // while restoring ordinary content must retain the last applied title visibility.
        var hasPluginContent = _pluginContentLayer?.Child != null;
        Icon.Visibility = hasPluginContent ? Visibility.Collapsed : Visibility.Visible;
        Label.Visibility = !hasPluginContent && titleVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border CreatePluginContentLayer()
    {
        var layer = new Border
        {
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed
        };
        BindingOperations.SetBinding(
            layer,
            UIElement.OpacityProperty,
            new Binding(nameof(UIElement.Opacity))
            {
                Source = ContentGrid,
                Mode = BindingMode.OneWay
            });
        Panel.SetZIndex(layer, 10);
        ContentHost.Children.Add(layer);
        return layer;
    }
}
