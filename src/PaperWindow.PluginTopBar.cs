using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static bool _pluginTopBarLoadedHandlerRegistered;
    private StackPanel? _pluginTopBarButtonsHost;
    private PluginTopBarActionBinding[] _pluginTopBarDesiredActions = [];
    private readonly List<(FrameworkElement Element, PluginTopBarActionBinding Binding)>
        _pluginTopBarActionElements = [];
    private readonly List<(FrameworkElement Element, PluginTopBarActionBinding Binding)>
        _pluginTopBarDetachedPaperActionElements = [];
    private (FrameworkElement Element, PluginTopBarActionBinding Binding)?
        _pluginTopBarRejectedGlobalActionElement;
    private PaperHostTopBarActions _pluginHiddenHostTopBarActions;
    private bool _pluginTopBarCapacityHookInstalled;

    private static bool _pluginTopBarLabelsLoadedHandlerRegistered;
    private StackPanel? _pluginTopBarLabelsHost;

    internal static void EnsurePluginTopBarLoadedHandler() =>
        EnsurePluginLoadedRefreshHandler(
            ref _pluginTopBarLoadedHandlerRegistered,
            static window => window.RefreshPluginTopBarActions());

    internal void RefreshPluginTopBarActions()
    {
        if (IsClosed || _topBarActionButtonsHost == null)
        {
            return;
        }

        var state = _controller.GetPluginTopBarRenderState(_paper.Id);
        EnsurePluginTopBarButtonsHost();
        if (_pluginTopBarButtonsHost == null)
        {
            return;
        }

        // Keep descriptors cheap. WPF controls are materialized only for the currently fitting
        // prefix, so an plugin runtime cannot multiply thousands of hidden Buttons across papers.
        _pluginTopBarDesiredActions = state.Actions
            .Where(binding => binding.Action.Visible && !binding.Action.MenuOnly)
            .ToArray();
        ResetPluginTopBarMaterialization();
        _pluginHiddenHostTopBarActions = state.HiddenHostActions;
        UpdateTopBarNewPaperButtons();
    }

    private void EnsurePluginTopBarButtonsHost()
    {
        if (_topBarActionButtonsHost == null)
        {
            return;
        }

        _pluginTopBarButtonsHost ??= new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (_pluginTopBarButtonsHost.Parent == null)
        {
            _topBarActionButtonsHost.Children.Insert(0, _pluginTopBarButtonsHost);
            // Plugin actions have the same user-defined ordering as built-in shortcuts.
            // Position the empty host on first attach; don't reparent active controls later.
            ReorderTopBarUserButtons();
        }

        EnsurePluginTopBarCapacityHook();
    }

    private void EnsurePluginTopBarCapacityHook()
    {
        if (_pluginTopBarCapacityHookInstalled || _topBar == null) return;
        _pluginTopBarCapacityHookInstalled = true;
        _topBar.SizeChanged += (_, _) => UpdateTopBarResponsiveLayout();
    }

    // IconButton uses a 23 DIP button plus its two 1 DIP horizontal margins.
    // Count descriptors without constructing/Measuring a hidden WPF subtree.
    private const double PluginTopBarActionSlotWidth = 25;

    private int PluginTopBarFittingCount(double availableWidth)
    {
        if (_pluginTopBarButtonsHost == null || _pluginTopBarDesiredActions.Length == 0)
            return 0;
        var availableSlots = Math.Max(0, (int)Math.Floor((availableWidth + 0.01) /
            PluginTopBarActionSlotWidth));
        var paperCount = _pluginTopBarDesiredActions.Count(item =>
            item.Scope == PaperTopBarActionScope.Paper);
        // All paper-session actions appear together; global actions are a flexible tail.
        if (availableSlots < paperCount) return 0;
        return Math.Min(availableSlots, _pluginTopBarDesiredActions.Length);
    }

    private void ReconcilePluginTopBarCapacity(int targetCount)
    {
        if (_pluginTopBarButtonsHost == null) return;
        while (_pluginTopBarActionElements.Count > targetCount)
            RemoveLastPluginTopBarActionElement(cacheForReuse: true);
        while (_pluginTopBarActionElements.Count < targetCount)
            AddPluginTopBarActionElement(
                _pluginTopBarDesiredActions[_pluginTopBarActionElements.Count]);
        UpdatePluginTopBarHostWidth();
    }

    private FrameworkElement AddPluginTopBarActionElement(
        PluginTopBarActionBinding binding)
    {
        FrameworkElement element;
        if (binding.Scope == PaperTopBarActionScope.Paper)
        {
            var cachedIndex = _pluginTopBarDetachedPaperActionElements.FindIndex(item =>
                Equals(item.Binding, binding));
            if (cachedIndex >= 0)
            {
                element = _pluginTopBarDetachedPaperActionElements[cachedIndex].Element;
                _pluginTopBarDetachedPaperActionElements.RemoveAt(cachedIndex);
            }
            else
            {
                element = CreatePluginTopBarActionElement(binding);
            }
        }
        else if (_pluginTopBarRejectedGlobalActionElement is { } cachedGlobal &&
                 Equals(cachedGlobal.Binding, binding))
        {
            element = cachedGlobal.Element;
            _pluginTopBarRejectedGlobalActionElement = null;
        }
        else
        {
            _pluginTopBarRejectedGlobalActionElement = null;
            element = CreatePluginTopBarActionElement(binding);
        }

        _pluginTopBarButtonsHost!.Children.Add(element);
        _pluginTopBarActionElements.Add((element, binding));
        return element;
    }

    private FrameworkElement CreatePluginTopBarActionElement(
        PluginTopBarActionBinding binding)
    {
        var button = IconButton("", binding.Action.ToolTip);
        button.SetBinding(
            Control.FontFamilyProperty,
            new Binding(nameof(FontFamily)) { Source = this });
        button.SetBinding(
            Control.FontSizeProperty,
            new Binding(nameof(FontSize)) { Source = this });
        button.IsEnabled = binding.Action.Enabled;
        button.Opacity = binding.Action.Enabled ? 1.0 : 0.5;
        button.Width = 23;
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Content = CreatePluginTopBarIcon(button, binding.Action.Icon);
        button.Click += (_, _) =>
            _controller.InvokePluginTopBarAction(
                binding,
                _paper.Id,
                _paper.Type,
                _paper.Type == PaperTypes.Note
                    ? NormalizeBodyProviderId(_paper.BodyProviderId)
                    : string.Empty);
        return button;
    }

    private void RemoveLastPluginTopBarActionElement(bool cacheForReuse)
    {
        if (_pluginTopBarButtonsHost == null || _pluginTopBarActionElements.Count == 0)
        {
            return;
        }

        var index = _pluginTopBarActionElements.Count - 1;
        var entry = _pluginTopBarActionElements[index];
        _pluginTopBarButtonsHost.Children.Remove(entry.Element);
        _pluginTopBarActionElements.RemoveAt(index);

        if (!cacheForReuse)
        {
            return;
        }

        if (entry.Binding.Scope == PaperTopBarActionScope.Paper)
        {
            _pluginTopBarDetachedPaperActionElements.Add(entry);
        }
        else
        {
            // Keep only the nearest missing Global action. If several are removed while shrinking,
            // the last one removed is exactly the first action that should return on expansion.
            _pluginTopBarRejectedGlobalActionElement = entry;
        }
    }

    private void ResetPluginTopBarMaterialization()
    {
        _pluginTopBarButtonsHost?.Children.Clear();
        _pluginTopBarActionElements.Clear();
        _pluginTopBarDetachedPaperActionElements.Clear();
        _pluginTopBarRejectedGlobalActionElement = null;
        UpdatePluginTopBarHostWidth();
    }

    private void UpdatePluginTopBarHostWidth()
    {
        if (_pluginTopBarButtonsHost == null)
        {
            return;
        }

        var width = _pluginTopBarActionElements
            .Where(item => item.Element.Visibility == Visibility.Visible)
            .Sum(item => TopBarOuterWidth(item.Element));
        _pluginTopBarButtonsHost.Width = width;
        _pluginTopBarButtonsHost.Visibility = width > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static UIElement CreatePluginTopBarIcon(
        Button button,
        PaperTopBarIcon icon)
    {
        if (icon.Kind == PaperTopBarIconKind.SvgPath)
        {
            var path = new Path
            {
                Data = Geometry.Parse(icon.Value),
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            path.SetBinding(
                FrameworkElement.WidthProperty,
                new Binding(nameof(Control.FontSize)) { Source = button });
            path.SetBinding(
                FrameworkElement.HeightProperty,
                new Binding(nameof(Control.FontSize)) { Source = button });

            if (icon.RenderMode == PaperTopBarSvgRenderMode.Stroke)
            {
                path.Fill = Brushes.Transparent;
                path.StrokeThickness = icon.StrokeWidth;
                path.StrokeLineJoin = PenLineJoin.Round;
                path.StrokeStartLineCap = PenLineCap.Round;
                path.StrokeEndLineCap = PenLineCap.Round;
                path.SetBinding(
                    Shape.StrokeProperty,
                    new Binding(nameof(Control.Foreground)) { Source = button });
            }
            else
            {
                path.SetBinding(
                    Shape.FillProperty,
                    new Binding(nameof(Control.Foreground)) { Source = button });
            }
            return path;
        }

        var text = new TextBlock
        {
            Text = icon.Value,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        text.SetBinding(
            TextBlock.ForegroundProperty,
            new Binding(nameof(Control.Foreground)) { Source = button });
        return text;
    }

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

        if (e.Property == FontFamilyProperty ||
            e.Property == FontSizeProperty ||
            e.Property == TextOptions.TextFormattingModeProperty ||
            e.Property == TextOptions.TextRenderingModeProperty ||
            e.Property == TextOptions.TextHintingModeProperty)
        {
            RefreshBuiltInFindTypographyFromOwner();
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

        // The normal title-bar layout allocates remaining space to passive labels.
        UpdateTopBarResponsiveLayout();
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
}
