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
    private bool _pluginHostActionVisibilityHooksInstalled;
    private bool _pluginTopBarCapacityHookInstalled;
    private bool _reconcilingPluginHostActionVisibility;
    private bool _reconcilingPluginTopBarCapacity;

    internal static void EnsurePluginTopBarLoadedHandler()
    {
        if (_pluginTopBarLoadedHandlerRegistered)
        {
            return;
        }
        _pluginTopBarLoadedHandlerRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(PaperWindow),
            LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is PaperWindow window && !window.IsClosed)
                {
                    window.RefreshPluginTopBarActions();
                }
            }));
    }

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
            .Where(binding => binding.Action.Visible)
            .ToArray();
        ResetPluginTopBarMaterialization();
        _pluginHiddenHostTopBarActions = state.HiddenHostActions;
        ReconcilePluginHiddenHostTopBarActions();
        ReconcilePluginTopBarCapacity();
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
        }

        EnsurePluginTopBarCapacityHook();
        EnsurePluginHostActionVisibilityHooks();
    }

    private void EnsurePluginTopBarCapacityHook()
    {
        if (_pluginTopBarCapacityHookInstalled || _topBar == null)
        {
            return;
        }

        _pluginTopBarCapacityHookInstalled = true;
        _topBar.SizeChanged += (_, e) =>
            ReconcilePluginTopBarCapacity(e.NewSize.Width > e.PreviousSize.Width);
    }

    private void ReconcilePluginTopBarCapacity(bool allowExpansion = true)
    {
        if (_reconcilingPluginTopBarCapacity ||
            _pluginTopBarButtonsHost == null ||
            _topBarActionButtonsHost == null)
        {
            return;
        }

        _reconcilingPluginTopBarCapacity = true;
        try
        {
            UpdatePluginTopBarHostWidth();
            UpdateTopBarResponsiveLayout();

            if (_paper.IsCollapsed || _pluginTopBarDesiredActions.Length == 0)
            {
                ResetPluginTopBarMaterialization();
                UpdateTopBarResponsiveLayout();
                return;
            }

            var paperActionCount = _pluginTopBarDesiredActions.Count(item =>
                item.Scope == PaperTopBarActionScope.Paper);

            // Shrink only the tail that no longer fits. Global actions are the flexible tail;
            // paper actions keep their existing all-or-nothing rule.
            while (_topBarActionButtonsHost.Visibility != Visibility.Visible &&
                   _pluginTopBarActionElements.Count > paperActionCount)
            {
                RemoveLastPluginTopBarActionElement(cacheForReuse: true);
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
            }

            if (_topBarActionButtonsHost.Visibility != Visibility.Visible)
            {
                while (_pluginTopBarActionElements.Count > 0)
                {
                    RemoveLastPluginTopBarActionElement(cacheForReuse: true);
                }
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                return;
            }

            // A shrinking window can only reduce capacity. Do not probe actions that are already
            // known not to fit; growth or a non-size state change will try the cached tail again.
            if (!allowExpansion)
            {
                return;
            }

            if (_pluginTopBarActionElements.Count < paperActionCount)
            {
                while (_pluginTopBarActionElements.Count < paperActionCount)
                {
                    AddPluginTopBarActionElement(
                        _pluginTopBarDesiredActions[_pluginTopBarActionElements.Count]);
                }
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                if (_topBarActionButtonsHost.Visibility != Visibility.Visible)
                {
                    while (_pluginTopBarActionElements.Count > 0)
                    {
                        RemoveLastPluginTopBarActionElement(cacheForReuse: true);
                    }
                    UpdatePluginTopBarHostWidth();
                    UpdateTopBarResponsiveLayout();
                    return;
                }
            }

            // Global descriptors are already in Priority order. Add only the missing tail and stop
            // at the first action that would displace host controls. The rejected element is kept
            // detached for reuse, so resize no longer recreates Buttons or reparses SVG geometry.
            while (_pluginTopBarActionElements.Count < _pluginTopBarDesiredActions.Length)
            {
                AddPluginTopBarActionElement(
                    _pluginTopBarDesiredActions[_pluginTopBarActionElements.Count]);
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                if (_topBarActionButtonsHost.Visibility == Visibility.Visible)
                {
                    continue;
                }

                RemoveLastPluginTopBarActionElement(cacheForReuse: true);
                UpdatePluginTopBarHostWidth();
                UpdateTopBarResponsiveLayout();
                break;
            }
        }
        finally
        {
            _reconcilingPluginTopBarCapacity = false;
        }
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

    private void EnsurePluginHostActionVisibilityHooks()
    {
        if (_pluginHostActionVisibilityHooksInstalled ||
            _newTodoButton == null ||
            _newNoteButton == null)
        {
            return;
        }

        _pluginHostActionVisibilityHooksInstalled = true;
        _newTodoButton.IsVisibleChanged += OnPluginHostActionVisibilityChanged;
        _newNoteButton.IsVisibleChanged += OnPluginHostActionVisibilityChanged;
    }

    private void OnPluginHostActionVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_reconcilingPluginHostActionVisibility)
        {
            return;
        }

        if (_pluginHiddenHostTopBarActions != PaperHostTopBarActions.None)
        {
            ReconcilePluginHiddenHostTopBarActions();
        }
        ReconcilePluginTopBarCapacity();
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

    private void ReconcilePluginHiddenHostTopBarActions()
    {
        if (_reconcilingPluginHostActionVisibility)
        {
            return;
        }

        _reconcilingPluginHostActionVisibility = true;
        try
        {
            // The user setting is the base visibility; plugin suppression is the final paper-local
            // layer. Reapplying this from the visibility hooks prevents a later settings refresh
            // from temporarily resurrecting actions that the active provider asked to hide.
            UpdateTopBarNewPaperButtons();

            if (_newTodoButton != null &&
                _pluginHiddenHostTopBarActions.HasFlag(
                    PaperHostTopBarActions.NewTodoPaper))
            {
                _newTodoButton.Visibility = Visibility.Collapsed;
            }
            if (_newNoteButton != null &&
                _pluginHiddenHostTopBarActions.HasFlag(
                    PaperHostTopBarActions.NewNotePaper))
            {
                _newNoteButton.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _reconcilingPluginHostActionVisibility = false;
        }
    }
}
