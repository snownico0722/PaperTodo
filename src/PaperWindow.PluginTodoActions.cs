using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static readonly object PluginTodoInlineWrapperMarker = new();
    private static readonly object PluginTodoInlineHostMarker = new();
    private static readonly object PluginTodoContextSeparatorMarker = new();
    private static bool _pluginTodoActionsLoadedHandlerRegistered;

    private bool _pluginTodoActionHooksInstalled;
    private int _pluginTodoActionsAppliedRowsGeneration = -1;

    internal static void EnsurePluginTodoActionsLoadedHandler()
    {
        if (_pluginTodoActionsLoadedHandlerRegistered)
        {
            return;
        }
        _pluginTodoActionsLoadedHandlerRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(PaperWindow),
            LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is PaperWindow window && !window.IsClosed)
                {
                    window.RefreshPluginTodoActions();
                }
            }));
    }

    internal void RefreshPluginTodoActions(string? todoId = null)
    {
        if (_paper.Type != PaperTypes.Todo || IsClosed)
        {
            return;
        }

        EnsurePluginTodoActionHooks();
        foreach (var row in _todoRows)
        {
            if (row.Tag is not string itemId ||
                !string.IsNullOrWhiteSpace(todoId) &&
                !string.Equals(itemId, todoId, StringComparison.Ordinal))
            {
                continue;
            }

            var actions = _controller.GetPluginTodoActions(_paper.Id, itemId);
            ApplyPluginTodoInlineActions(row, itemId, actions);
        }
        _pluginTodoActionsAppliedRowsGeneration = _todoRowsGeneration;
    }

    private void EnsurePluginTodoActionHooks()
    {
        if (_pluginTodoActionHooksInstalled)
        {
            return;
        }
        _pluginTodoActionHooksInstalled = true;
        ContextMenuOpening += OnPluginTodoContextMenuOpening;
        LayoutUpdated += (_, _) =>
        {
            if (_paper.Type != PaperTypes.Todo ||
                _pluginTodoActionsAppliedRowsGeneration == _todoRowsGeneration)
            {
                return;
            }
            RefreshPluginTodoActions();
        };
    }

    private void ApplyPluginTodoInlineActions(
        Border row,
        string todoId,
        IReadOnlyList<PluginTodoActionBinding> bindings)
    {
        if (row.Child is not Grid grid)
        {
            return;
        }

        var inline = bindings
            .Where(binding =>
                binding.Action.Visible &&
                (binding.Action.Placement & PaperTodoActionPlacement.Inline) != 0)
            .ToArray();

        var wrapper = grid.Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => ReferenceEquals(panel.Tag, PluginTodoInlineWrapperMarker));
        if (wrapper == null && inline.Length == 0)
        {
            return;
        }

        StackPanel pluginHost;
        if (wrapper == null)
        {
            var nativeColumnChildren = grid.Children
                .Cast<UIElement>()
                .Where(element => Grid.GetColumn(element) == 2)
                .ToArray();
            foreach (var child in nativeColumnChildren)
            {
                grid.Children.Remove(child);
            }

            wrapper = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = PluginTodoInlineWrapperMarker
            };
            foreach (var child in nativeColumnChildren)
            {
                wrapper.Children.Add(child);
            }

            pluginHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = PluginTodoInlineHostMarker
            };
            wrapper.Children.Add(pluginHost);
            Grid.SetColumn(wrapper, 2);
            grid.Children.Add(wrapper);
        }
        else
        {
            pluginHost = wrapper.Children
                .OfType<StackPanel>()
                .First(panel => ReferenceEquals(panel.Tag, PluginTodoInlineHostMarker));
        }

        pluginHost.Children.Clear();
        foreach (var binding in inline)
        {
            pluginHost.Children.Add(CreatePluginTodoInlineAction(binding, todoId));
        }
    }

    private FrameworkElement CreatePluginTodoInlineAction(
        PluginTodoActionBinding binding,
        string todoId)
    {
        var action = binding.Action;
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(CreatePluginTodoActionIcon(
            action.Icon,
            action.Enabled ? WeakTextBrush : BrightWeakTextBrush,
            AppTypography.Scale(11)));
        content.Children.Add(new TextBlock
        {
            Text = action.Text,
            Foreground = action.Enabled ? WeakTextBrush : BrightWeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(10.5),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false
        });

        var button = new Border
        {
            Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(5, 1, 5, 1),
            MinHeight = AppTypography.Scale(22),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = LinkedPaperNormalBgBrush,
            Cursor = action.Enabled ? Cursors.Hand : Cursors.Arrow,
            Opacity = action.Enabled ? 0.82 : 0.48,
            ToolTip = string.IsNullOrWhiteSpace(action.ToolTip) ? action.Text : action.ToolTip,
            Child = content
        };
        if (action.Enabled)
        {
            button.MouseEnter += (_, _) =>
            {
                button.Background = LinkedPaperLightBgBrush;
                button.Opacity = 1.0;
            };
            button.MouseLeave += (_, _) =>
            {
                button.Background = LinkedPaperNormalBgBrush;
                button.Opacity = 0.82;
            };
            button.PreviewMouseLeftButtonUp += (_, e) =>
            {
                _controller.InvokePluginTodoAction(binding, _paper.Id, todoId);
                e.Handled = true;
            };
        }
        return button;
    }

    private void OnPluginTodoContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var row = FindTodoRowAncestor(e.OriginalSource as DependencyObject);
        if (row?.Tag is not string itemId)
        {
            return;
        }

        var menu = (e.Source as FrameworkElement)?.ContextMenu ?? row.ContextMenu;
        if (menu == null)
        {
            return;
        }

        for (var index = menu.Items.Count - 1; index >= 0; index--)
        {
            if (menu.Items[index] is FrameworkElement element &&
                (element.Tag is PluginTodoActionBinding ||
                 ReferenceEquals(element.Tag, PluginTodoContextSeparatorMarker)))
            {
                menu.Items.RemoveAt(index);
            }
        }

        var actions = _controller.GetPluginTodoActions(_paper.Id, itemId)
            .Where(binding =>
                binding.Action.Visible &&
                (binding.Action.Placement & PaperTodoActionPlacement.ContextMenu) != 0)
            .ToArray();
        if (actions.Length == 0)
        {
            return;
        }

        var insertIndex = Math.Min(1, menu.Items.Count);
        foreach (var binding in actions)
        {
            var action = binding.Action;
            var menuItem = new MenuItem
            {
                Header = new TextBlock { Text = action.Text },
                ToolTip = string.IsNullOrWhiteSpace(action.ToolTip) ? null : action.ToolTip,
                Icon = CreatePluginTodoActionIcon(
                    action.Icon,
                    TextBrush,
                    AppTypography.Scale(12)),
                IsEnabled = action.Enabled,
                Tag = binding
            };
            menuItem.Click += (_, _) =>
                _controller.InvokePluginTodoAction(binding, _paper.Id, itemId);
            menu.Items.Insert(insertIndex++, menuItem);
        }
        menu.Items.Insert(insertIndex, new Separator
        {
            Tag = PluginTodoContextSeparatorMarker
        });
    }

    private static UIElement CreatePluginTodoActionIcon(
        PaperTopBarIcon icon,
        Brush foreground,
        double size)
    {
        if (icon.Kind == PaperTopBarIconKind.SvgPath)
        {
            var path = new Path
            {
                Data = Geometry.Parse(icon.Value),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            if (icon.RenderMode == PaperTopBarSvgRenderMode.Stroke)
            {
                path.Fill = Brushes.Transparent;
                path.Stroke = foreground;
                path.StrokeThickness = icon.StrokeWidth;
                path.StrokeLineJoin = PenLineJoin.Round;
                path.StrokeStartLineCap = PenLineCap.Round;
                path.StrokeEndLineCap = PenLineCap.Round;
            }
            else
            {
                path.Fill = foreground;
            }
            return path;
        }

        return new TextBlock
        {
            Text = icon.Value,
            Foreground = foreground,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
    }
}
