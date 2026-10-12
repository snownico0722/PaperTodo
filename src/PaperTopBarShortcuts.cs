using System;
using System.Collections.Generic;
using System.Linq;

namespace PaperTodo;

// The complete global order and its divider index are one preference. Applicability is
// filtered only when rendering a particular paper; plugins never enter this configuration.
internal static class PaperTopBarShortcuts
{
    internal const string Association = "association";
    internal const string ExternalEditor = "external-editor";
    internal const string NewTodo = "new-todo";
    internal const string NewNote = "new-note";

    // Kept only for deserializing layouts saved by the former per-paper model.
    internal const string PluginActions = "plugin-actions";

    private static readonly string[] Defaults = [NewTodo, NewNote, Association, ExternalEditor];

    internal static IReadOnlyList<string> All => Defaults;

    internal static List<string> NormalizeOrder(IEnumerable<string>? source)
    {
        var selected = (source ?? [])
            .Where(action => action != null && Defaults.Contains(action, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
        foreach (var action in Defaults)
            if (!selected.Contains(action, StringComparer.Ordinal))
                selected.Add(action);
        return selected;
    }

    internal static void Normalize(AppState state)
    {
        state.TopBarShortcutOrder = NormalizeOrder(state.TopBarShortcutOrder);
        state.TopBarPinnedCount = Math.Clamp(state.TopBarPinnedCount, 0, Defaults.Length);
        state.TodoTopBarShortcuts = null;
        state.NoteTopBarShortcuts = null;
    }

    // An old user can have two different shortcut selections. Preserve the union and
    // prefer the Todo ordering first, then still-pinned Note-only commands, with all
    // previously unpinned commands following the divider.
    internal static void MigrateLegacy(AppState state)
    {
        var pinned = (state.TodoTopBarShortcuts ?? [])
            .Concat(state.NoteTopBarShortcuts ?? [])
            .Where(action => Defaults.Contains(action, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
        state.TopBarShortcutOrder = NormalizeOrder(pinned);
        state.TopBarPinnedCount = pinned.Count;
        Normalize(state);
    }

    // Legacy 3.x saves predating the per-type shortcut model store three toggles.
    internal static void MigrateLegacyBooleans(AppState state, bool missingTodo, bool missingNote)
    {
        if (missingTodo)
        {
            var choices = new List<string> { Association };
            if (state.ShowTopBarNewTodoButton) choices.Add(NewTodo);
            if (state.ShowTopBarNewNoteButton) choices.Add(NewNote);
            state.TodoTopBarShortcuts = choices;
        }
        if (missingNote)
        {
            var choices = new List<string> { Association };
            if (state.ShowTopBarExternalOpenButton) choices.Add(ExternalEditor);
            if (state.ShowTopBarNewTodoButton) choices.Add(NewTodo);
            if (state.ShowTopBarNewNoteButton) choices.Add(NewNote);
            state.NoteTopBarShortcuts = choices;
        }
    }

    internal static IReadOnlyList<string> Pinned(AppState state) =>
        NormalizeOrder(state.TopBarShortcutOrder)
            .Take(Math.Clamp(state.TopBarPinnedCount, 0, Defaults.Length)).ToArray();

    internal static IReadOnlyList<string> Menu(AppState state) =>
        NormalizeOrder(state.TopBarShortcutOrder)
            .Skip(Math.Clamp(state.TopBarPinnedCount, 0, Defaults.Length)).ToArray();

    internal static bool IsApplicable(string paperType, string action) =>
        Defaults.Contains(action, StringComparer.Ordinal) &&
        (action != ExternalEditor || paperType == PaperTypes.Note);

    internal static bool IsPinned(AppState state, string action) =>
        Pinned(state).Contains(action, StringComparer.Ordinal);

    internal static bool Contains(AppState state, string paperType, string action) =>
        IsApplicable(paperType, action) && IsPinned(state, action);

    // Preserve old public settings API IDs as compatibility aliases for one global divider.
    internal static bool SetPinned(AppState state, string action, bool pinned)
    {
        if (!Defaults.Contains(action, StringComparer.Ordinal)) return false;
        var order = NormalizeOrder(state.TopBarShortcutOrder);
        var count = Math.Clamp(state.TopBarPinnedCount, 0, order.Count);
        var index = order.IndexOf(action);
        if ((index < count) == pinned) return false;
        order.RemoveAt(index);
        if (index < count) count--;
        order.Insert(count, action);
        if (pinned) count++;
        state.TopBarShortcutOrder = order;
        state.TopBarPinnedCount = count;
        return true;
    }

    // targetIndex is the index within the destination section with the dragged item
    // excluded. Using section-local indices handles both same-group and cross-group moves.
    internal static bool MoveTo(AppState state, string action, bool pinned, int targetIndex)
    {
        if (!Defaults.Contains(action, StringComparer.Ordinal)) return false;
        var order = NormalizeOrder(state.TopBarShortcutOrder);
        var count = Math.Clamp(state.TopBarPinnedCount, 0, order.Count);
        var top = order.Take(count).Where(x => x != action).ToList();
        var menu = order.Skip(count).Where(x => x != action).ToList();
        var target = pinned ? top : menu;
        target.Insert(Math.Clamp(targetIndex, 0, target.Count), action);
        var merged = top.Concat(menu).ToList();
        var nextCount = top.Count;
        if (nextCount == count && merged.SequenceEqual(order)) return false;
        state.TopBarShortcutOrder = merged;
        state.TopBarPinnedCount = nextCount;
        return true;
    }
}
