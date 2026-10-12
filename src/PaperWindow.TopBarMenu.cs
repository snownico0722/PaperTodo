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
        // Built-in buttons follow the user's order; plugin actions always render last.
        var choices = PaperTopBarShortcuts.Choices(_paper.Type)
            .Where(action => action != PaperTopBarShortcuts.PluginActions);
        var buttons = choices.Select(GetTopBarUserButton)
            .OfType<Button>().Distinct().ToArray();
        var ordered = PaperTopBarShortcuts.Get(_controller.State, _paper.Type)
            .Where(action => action != PaperTopBarShortcuts.PluginActions)
            .Concat(choices).Distinct(StringComparer.Ordinal)
            .Select(GetTopBarUserButton).OfType<Button>()
            .Cast<FrameworkElement>().ToList();
        if (_pluginTopBarButtonsHost != null)
            ordered.Add(_pluginTopBarButtonsHost);

        var current = _topBarActionButtonsHost.Children.OfType<FrameworkElement>()
            .Where(element => element is Button ||
                ReferenceEquals(element, _pluginTopBarButtonsHost)).ToArray();
        if (current.SequenceEqual(ordered)) return;

        // Keep attached controls in place when order is unchanged (mouse capture/focus).
        foreach (var button in buttons)
            _topBarActionButtonsHost.Children.Remove(button);
        if (_pluginTopBarButtonsHost != null)
            _topBarActionButtonsHost.Children.Remove(_pluginTopBarButtonsHost);
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

        var reorderable = pinned.Where(action =>
            action != PaperTopBarShortcuts.PluginActions).ToList();
        if (reorderable.Count < 2) return;
        menu.Items.Add(MenuSeparator());
        var reorder = new MenuItem
        {
            Header = Strings.Get("MenuReorderTopBar"),
            Padding = new Thickness(8, 4, 10, 4)
        };
        // Keep the reorder menu one level deep: select "move action left/right"
        // directly, instead of opening another submenu for every action.
        for (var i = 0; i < reorderable.Count; i++)
        {
            var action = reorderable[i];
            var label = TopBarActionText(action);
            if (i > 0)
                reorder.Items.Add(MenuItem(
                    Strings.Get("MenuTopBarMoveLeft") + " · " + label,
                    (_, _) => _controller.MoveTopBarShortcut(paperType, action, -1)));
            if (i < reorderable.Count - 1)
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
