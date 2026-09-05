using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static readonly object CopyTranslationMenuTag = new();

    static PaperWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(PaperWindow),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnCopyTranslationPreviewKeyDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(PaperWindow),
            ContextMenuService.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnCopyTranslationContextMenuOpening),
            handledEventsToo: true);
    }

    private static void OnCopyTranslationPreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Handled ||
            sender is not PaperWindow window ||
            e.Key != Key.C ||
            Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
        {
            return;
        }

        if (window._paper.Type == PaperTypes.Todo &&
            window.TryCopySelectedTodoItemsAsMarkdown())
        {
            e.Handled = true;
            return;
        }

        if (window._paper.Type == PaperTypes.Note &&
            window._noteBox is { IsKeyboardFocusWithin: true } noteBox &&
            noteBox.TryCopySelectionAsPlainText())
        {
            e.Handled = true;
        }
    }

    private static void OnCopyTranslationContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        if (sender is not PaperWindow window)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var owner = FindCopyTranslationContextMenuOwner(source, window);
        var menu = owner?.ContextMenu;
        if (menu == null || HasCopyTranslationMenuItem(menu))
        {
            return;
        }

        if (window._paper.Type == PaperTypes.Todo)
        {
            var row = window.FindTodoRowAncestor(source);
            if (row?.Tag is string itemId &&
                window._selectedTodoItemIds.Count > 1 &&
                window._selectedTodoItemIds.Contains(itemId))
            {
                window.InsertCopyTranslationMenuItem(
                    menu,
                    Strings.Get("MenuCopySelectedTodos"),
                    CopyTranslationStrings.Get("MenuCopyAsMarkdown"),
                    (_, _) => window.TryCopySelectedTodoItemsAsMarkdown());
            }
            return;
        }

        if (window._paper.Type == PaperTypes.Note &&
            window._noteBox is { IsPreviewMode: false } noteBox &&
            IsDescendantOf(source, noteBox))
        {
            window.InsertCopyTranslationMenuItem(
                menu,
                Strings.Get("MenuCopy"),
                CopyTranslationStrings.Get("MenuCopyAsPlainText"),
                (_, _) => noteBox.TryCopySelectionAsPlainText());
        }
    }

    private static FrameworkElement? FindCopyTranslationContextMenuOwner(
        DependencyObject? source,
        PaperWindow window)
    {
        var current = source;
        while (current != null && !ReferenceEquals(current, window))
        {
            if (current is FrameworkElement { ContextMenu: not null } element)
            {
                return element;
            }
            current = GetSafeParent(current);
        }
        return null;
    }

    private static bool HasCopyTranslationMenuItem(ContextMenu menu) =>
        menu.Items
            .OfType<MenuItem>()
            .Any(item => ReferenceEquals(item.Tag, CopyTranslationMenuTag));

    private void InsertCopyTranslationMenuItem(
        ContextMenu menu,
        string existingCopyHeader,
        string translationHeader,
        RoutedEventHandler click)
    {
        for (var index = 0; index < menu.Items.Count; index++)
        {
            if (menu.Items[index] is not MenuItem item ||
                !string.Equals(
                    item.Header as string,
                    existingCopyHeader,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var translationItem = MenuItem(translationHeader, click);
            translationItem.Tag = CopyTranslationMenuTag;
            menu.Items.Insert(index + 1, translationItem);
            return;
        }
    }

    private bool TryCopySelectedTodoItemsAsMarkdown()
    {
        if (_selectedTodoItemIds.Count == 0)
        {
            return false;
        }

        if (FocusManager.GetFocusedElement(this) is TodoTextBox { SelectionLength: > 0 })
        {
            return false;
        }

        var selected = SelectedTodoItems();
        return selected.Count > 0 && ClipboardHelper.TrySetText(
            TodoClipboardFormatter.ToMarkdown(
                selected.Select(item => (item.Text ?? string.Empty, item.Done))));
    }
}
