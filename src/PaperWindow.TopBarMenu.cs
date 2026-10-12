using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void OpenPaperActionsMenu()
    {
        if (_moreActionsButton == null || _paper.IsCollapsed) return;

        var menu = BuildPaperContextMenu();
        menu.PlacementTarget = _moreActionsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_moreActionsButton?.ContextMenu, menu))
                _moreActionsButton.ContextMenu = null;
        };

        _moreActionsButton.ContextMenu = menu;
        BeginPaperContextMenuOpening();
        menu.IsOpen = true;
    }

    private void ReorderTopBarUserButtons()
    {
        if (_topBarActionButtonsHost == null) return;
        var choices = PaperTopBarShortcuts.All;
        var buttons = choices.Select(GetTopBarUserButton)
            .OfType<Button>().Distinct().ToArray();
        var ordered = PaperTopBarShortcuts.NormalizeOrder(_controller.State.TopBarShortcutOrder)
            .Select(GetTopBarUserButton).OfType<Button>()
            .Cast<FrameworkElement>().ToList();
        if (_pluginTopBarButtonsHost != null)
            ordered.Add(_pluginTopBarButtonsHost);

        var current = _topBarActionButtonsHost.Children.OfType<FrameworkElement>()
            .Where(element => element is Button ||
                ReferenceEquals(element, _pluginTopBarButtonsHost)).ToArray();
        if (current.SequenceEqual(ordered)) return;

        // Reparent only on a true order change; a drag gesture may hold mouse capture.
        foreach (var button in buttons)
            _topBarActionButtonsHost.Children.Remove(button);
        if (_pluginTopBarButtonsHost != null)
            _topBarActionButtonsHost.Children.Remove(_pluginTopBarButtonsHost);
        var at = _pluginTopBarLabelsHost == null ? 0 :
            _topBarActionButtonsHost.Children.IndexOf(_pluginTopBarLabelsHost) + 1;
        at = Math.Max(0, at);
        foreach (var element in ordered)
            _topBarActionButtonsHost.Children.Insert(at++, element);
    }

    private Button? GetTopBarUserButton(string action) => action switch
    {
        PaperTopBarShortcuts.Association => _windowBindingButton,
        PaperTopBarShortcuts.ExternalEditor => _openMarkdownButton,
        PaperTopBarShortcuts.NewTodo => _newTodoButton,
        PaperTopBarShortcuts.NewNote => _newNoteButton,
        _ => null
    };

    private bool IsTopBarCommandVisible(Button? button) =>
        !_paper.IsCollapsed && IsVisible && _topBarActionButtonsHost?.Visibility == Visibility.Visible &&
        button?.Visibility == Visibility.Visible;

    private MenuItem BuildAssociationDragMenuItem(ContextMenu menu)
    {
        var item = new MenuItem
        {
            Header = Strings.Get("MenuTopBarAssociationDrag"),
            ToolTip = AssociationDragHint(),
            Padding = new Thickness(8, 4, 10, 4),
            StaysOpenOnClick = true
        };
        // A regular Click cannot implement association. Let users drag out of the
        // overflow popup using the very same gesture and target adapters as the
        // title-bar button; close the popup when the gesture ends.
        ConfigureTopBarDragGesture(item,
            CreateAssociationDragBehavior(() => menu.IsOpen = false));
        return item;
    }

    private bool ShouldShowPinnedHostButton(Button button)
    {
        var state = _controller.State;
        if (ReferenceEquals(button, _windowBindingButton))
            return PaperTopBarShortcuts.Contains(state, _paper.Type, PaperTopBarShortcuts.Association) &&
                (state.EnableTodoPaperLinks || state.ExperimentalWindowTethering);
        if (ReferenceEquals(button, _openMarkdownButton))
            return PaperTopBarShortcuts.Contains(state, _paper.Type, PaperTopBarShortcuts.ExternalEditor) &&
                IsCurrentBodyProviderMarkdown;
        if (ReferenceEquals(button, _newTodoButton))
            return PaperTopBarShortcuts.Contains(state, _paper.Type, PaperTopBarShortcuts.NewTodo) &&
                !_pluginHiddenHostTopBarActions.HasFlag(PaperHostTopBarActions.NewTodoPaper);
        if (ReferenceEquals(button, _newNoteButton))
            return PaperTopBarShortcuts.Contains(state, _paper.Type, PaperTopBarShortcuts.NewNote) &&
                !_pluginHiddenHostTopBarActions.HasFlag(PaperHostTopBarActions.NewNotePaper);
        return false;
    }

    // Context and ellipsis menus both rebuild on Opened, so the plugin menu contains
    // only commands not already visible in the current title bar.
    private void AttachPluginTopBarActionsToMenu(ContextMenu menu)
    {
        var hidden = _controller.GetPluginTopBarRenderState(_paper.Id).Actions
            .Where(binding => binding.Action.Visible &&
                !(_pluginTopBarActionElements.Any(entry =>
                    Equals(entry.Binding, binding)) &&
                  !_paper.IsCollapsed && IsVisible &&
                  _topBarActionButtonsHost?.Visibility == Visibility.Visible &&
                  _pluginTopBarButtonsHost?.Visibility == Visibility.Visible))
            .ToArray();
        if (hidden.Length == 0) return;

        var pluginMenu = new MenuItem
        {
            Header = Strings.Get("MenuTopBarPluginActions"),
            Padding = new Thickness(8, 4, 10, 4)
        };
        foreach (var binding in hidden)
        {
            var label = string.IsNullOrWhiteSpace(binding.Action.ToolTip)
                ? binding.Action.Id : binding.Action.ToolTip;
            var command = MenuItem(label, (_, _) =>
                _controller.InvokePluginTopBarAction(
                    binding, _paper.Id, _paper.Type,
                    _paper.Type == PaperTypes.Note
                        ? NormalizeBodyProviderId(_paper.BodyProviderId)
                        : string.Empty));
            command.IsEnabled = binding.Action.Enabled;
            pluginMenu.Items.Add(command);
        }
        menu.Items.Add(pluginMenu);
    }
}
