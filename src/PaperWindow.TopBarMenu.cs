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
        var choices = PaperTopBarShortcuts.Choices(_paper.Type);
        FrameworkElement? Resolve(string action) =>
            action == PaperTopBarShortcuts.PluginActions
                ? _pluginTopBarButtonsHost
                : GetTopBarUserButton(action);

        var elements = choices.Select(Resolve).OfType<FrameworkElement>().Distinct().ToArray();
        var ordered = PaperTopBarShortcuts.Get(_controller.State, _paper.Type)
            .Concat(choices).Distinct(StringComparer.Ordinal)
            .Select(Resolve).OfType<FrameworkElement>().ToArray();

        // Plugin actions are one host-owned group. Its position is configurable just like
        // the four built-in buttons; passive plugin labels still remain at the leading edge.
        var current = _topBarActionButtonsHost.Children.OfType<FrameworkElement>()
            .Where(element => element is Button ||
                ReferenceEquals(element, _pluginTopBarButtonsHost)).ToArray();
        if (current.SequenceEqual(ordered)) return;

        // Do not detach/reparent live controls when the order is already stable:
        // a drag source may be holding mouse capture or keyboard focus.
        foreach (var element in elements)
            _topBarActionButtonsHost.Children.Remove(element);
        var insertAt = _pluginTopBarLabelsHost == null ? 0 :
            _topBarActionButtonsHost.Children.IndexOf(_pluginTopBarLabelsHost) + 1;
        insertAt = Math.Max(0, insertAt);
        foreach (var element in ordered)
            _topBarActionButtonsHost.Children.Insert(insertAt++, element);
    }

    private Button? GetTopBarUserButton(string action) => action switch
    {
        PaperTopBarShortcuts.Association => _windowBindingButton,
        PaperTopBarShortcuts.ExternalEditor => _openMarkdownButton,
        PaperTopBarShortcuts.NewTodo => _newTodoButton,
        PaperTopBarShortcuts.NewNote => _newNoteButton,
        _ => null
    };

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

    private MenuItem BuildTopBarCustomizationMenu()
    {
        var item = new MenuItem
        {
            Header = Strings.Get("MenuCustomizeTopBar"),
            Padding = new Thickness(8, 4, 10, 4)
        };
        RefreshTopBarCustomizationMenu(item);
        item.SubmenuOpened += (_, e) =>
        {
            // SubmenuOpened bubbles from nested reorder entries. Do not rebuild their parent
            // while an inner submenu is opening (it would discard the active hover target).
            if (ReferenceEquals(e.OriginalSource, item))
                RefreshTopBarCustomizationMenu(item);
        };
        return item;
    }

    private void RefreshTopBarCustomizationMenu(MenuItem menu)
    {
        menu.Items.Clear();
        var paperType = _paper.Type;
        var pinned = PaperTopBarShortcuts.Get(_controller.State, paperType).ToList();
        foreach (var action in PaperTopBarShortcuts.Choices(paperType))
        {
            var isPinned = pinned.Contains(action, StringComparer.Ordinal);
            var option = MenuItem(TopBarActionText(action),
                (_, _) => _controller.SetTopBarShortcut(paperType, action, !isPinned));
            option.IsCheckable = true;
            option.IsChecked = isPinned;
            menu.Items.Add(option);
        }

        if (pinned.Count < 2) return;
        menu.Items.Add(MenuSeparator());
        var reorder = new MenuItem
        {
            Header = Strings.Get("MenuReorderTopBar"),
            Padding = new Thickness(8, 4, 10, 4)
        };
        // Keep the reorder menu one level deep: select "move action left/right"
        // directly, instead of opening another submenu for every action.
        for (var i = 0; i < pinned.Count; i++)
        {
            var action = pinned[i];
            var label = TopBarActionText(action);
            if (i > 0)
                reorder.Items.Add(MenuItem(
                    Strings.Get("MenuTopBarMoveLeft") + " · " + label,
                    (_, _) => _controller.MoveTopBarShortcut(paperType, action, -1)));
            if (i < pinned.Count - 1)
                reorder.Items.Add(MenuItem(
                    Strings.Get("MenuTopBarMoveRight") + " · " + label,
                    (_, _) => _controller.MoveTopBarShortcut(paperType, action, 1)));
        }
        menu.Items.Add(reorder);
    }

    private static string TopBarActionText(string action) => action switch
    {
        PaperTopBarShortcuts.Association => Strings.Get("MenuTopBarAssociationDrag"),
        PaperTopBarShortcuts.ExternalEditor => Strings.Get("SettingsShowTopBarExternalOpenButton"),
        PaperTopBarShortcuts.NewTodo => Strings.Get("MenuNewTodoPaper"),
        PaperTopBarShortcuts.NewNote => Strings.Get("MenuNewNotePaper"),
        PaperTopBarShortcuts.PluginActions => Strings.Get("MenuTopBarPluginActions"),
        _ => action
    };

    // The same paper menu is used by right-click and by the visible ellipsis. Top-bar
    // actions supplied by plugins remain reachable here even when not pinned or overflowed.
    private void AttachPluginTopBarActionsToMenu(ContextMenu menu)
    {
        var pluginMenu = new MenuItem
        {
            Header = Strings.Get("MenuTopBarPluginActions"),
            Padding = new Thickness(8, 4, 10, 4)
        };
        menu.Items.Add(pluginMenu);
        void Refresh()
        {
            pluginMenu.Items.Clear();
            foreach (var binding in _controller.GetPluginTopBarRenderState(_paper.Id).Actions)
            {
                if (!binding.Action.Visible) continue;
                var text = string.IsNullOrWhiteSpace(binding.Action.ToolTip)
                    ? binding.Action.Id : binding.Action.ToolTip;
                var command = MenuItem(text, (_, _) =>
                    _controller.InvokePluginTopBarAction(
                        binding, _paper.Id, _paper.Type,
                        _paper.Type == PaperTypes.Note
                            ? NormalizeBodyProviderId(_paper.BodyProviderId)
                            : string.Empty));
                command.IsEnabled = binding.Action.Enabled;
                pluginMenu.Items.Add(command);
            }
            pluginMenu.Visibility = pluginMenu.Items.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
        Refresh();
        menu.Opened += (_, _) => Refresh();
    }
}
