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
        var buttons = choices.Select(GetTopBarUserButton).OfType<Button>().Distinct().ToArray();
        var ordered = PaperTopBarShortcuts.Get(_controller.State, _paper.Type)
            .Concat(choices).Distinct(StringComparer.Ordinal)
            .Select(GetTopBarUserButton).OfType<Button>().ToArray();
        // Do not detach and reattach live WPF controls on unrelated settings/plugin refreshes:
        // reparenting a drag origin can interrupt mouse capture and keyboard focus.
        if (_topBarActionButtonsHost.Children.OfType<Button>().SequenceEqual(ordered)) return;
        foreach (var button in buttons)
            _topBarActionButtonsHost.Children.Remove(button);

        // Retain the existing plugin host/labels, and reorder only PaperTodo's four actions.
        var insertAt = _pluginTopBarButtonsHost == null ? 0 :
            _topBarActionButtonsHost.Children.IndexOf(_pluginTopBarButtonsHost) + 1;
        insertAt = Math.Max(0, insertAt);
        foreach (var button in ordered)
            _topBarActionButtonsHost.Children.Insert(insertAt++, button);
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
        for (var i = 0; i < pinned.Count; i++)
        {
            var action = pinned[i];
            var entry = new MenuItem
            {
                Header = TopBarActionText(action),
                Padding = new Thickness(8, 4, 10, 4)
            };
            var left = MenuItem(Strings.Get("MenuTopBarMoveLeft"),
                (_, _) => _controller.MoveTopBarShortcut(paperType, action, -1));
            left.IsEnabled = i > 0;
            var right = MenuItem(Strings.Get("MenuTopBarMoveRight"),
                (_, _) => _controller.MoveTopBarShortcut(paperType, action, 1));
            right.IsEnabled = i < pinned.Count - 1;
            entry.Items.Add(left);
            entry.Items.Add(right);
            reorder.Items.Add(entry);
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
