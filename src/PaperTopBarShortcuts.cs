using System;
using System.Collections.Generic;
using System.Linq;

namespace PaperTodo;

// The per-type shortcut order is the single source of truth. The complete paper menu remains
// available regardless of whether a command is pinned or currently fits in the title bar.
internal static class PaperTopBarShortcuts
{
    internal const string Association = "association";
    internal const string ExternalEditor = "external-editor";
    internal const string NewTodo = "new-todo";
    internal const string NewNote = "new-note";
    internal const string PluginActions = "plugin-actions";

    private static readonly string[] TodoChoices = [Association, NewTodo, NewNote, PluginActions];
    private static readonly string[] NoteChoices = [Association, ExternalEditor, NewTodo, NewNote, PluginActions];

    internal static IReadOnlyList<string> Choices(string paperType) =>
        paperType == PaperTypes.Note ? NoteChoices : TodoChoices;

    internal static bool IsAllowed(string paperType, string action) =>
        Choices(paperType).Contains(action, StringComparer.Ordinal);

    internal static List<string> Normalize(string paperType, IEnumerable<string>? actions)
    {
        var ordered = (actions ?? [])
            .Where(action => action != null && IsAllowed(paperType, action))
            .Distinct(StringComparer.Ordinal).ToList();
        // Plugin buttons are always below the built-in shortcut group, independent
        // of old saved positions or customized order.
        if (ordered.Remove(PluginActions)) ordered.Add(PluginActions);
        return ordered;
    }

    internal static IReadOnlyList<string> Get(AppState state, string paperType) =>
        paperType == PaperTypes.Note
            ? state.NoteTopBarShortcuts ?? []
            : state.TodoTopBarShortcuts ?? [];

    internal static bool Contains(AppState state, string paperType, string action) =>
        Get(state, paperType).Contains(action, StringComparer.Ordinal);

    internal static void Set(AppState state, string paperType, string action, bool pinned)
    {
        if (!IsAllowed(paperType, action)) return;
        var list = Normalize(paperType, Get(state, paperType));
        if (pinned)
        {
            if (!list.Contains(action, StringComparer.Ordinal)) list.Add(action);
        }
        else
        {
            list.Remove(action);
        }
        Store(state, paperType, list);
    }

    internal static bool Move(AppState state, string paperType, string action, int direction)
    {
        var list = Normalize(paperType, Get(state, paperType));
        var at = list.IndexOf(action);
        var next = at + direction;
        if (action == PluginActions ||
            at < 0 || next < 0 || next >= list.Count ||
            list[next] == PluginActions || Math.Abs(direction) != 1)
            return false;
        (list[at], list[next]) = (list[next], list[at]);
        Store(state, paperType, list);
        return true;
    }

    internal static void SetBoth(AppState state, string action, bool pinned)
    {
        Set(state, PaperTypes.Todo, action, pinned);
        Set(state, PaperTypes.Note, action, pinned);
    }

    internal static bool InBoth(AppState state, string action) =>
        Contains(state, PaperTypes.Todo, action) && Contains(state, PaperTypes.Note, action);

    // A legacy global write is complete only when both per-type values agree with it.
    internal static bool MatchesBoth(AppState state, string action, bool pinned) =>
        Contains(state, PaperTypes.Todo, action) == pinned &&
        Contains(state, PaperTypes.Note, action) == pinned;

    internal static void MigrateLegacy(AppState state, bool todo, bool note)
    {
        if (todo)
        {
            var actions = new List<string> { Association };
            if (state.ShowTopBarNewTodoButton) actions.Add(NewTodo);
            if (state.ShowTopBarNewNoteButton) actions.Add(NewNote);
            actions.Add(PluginActions);
            state.TodoTopBarShortcuts = actions;
        }
        if (note)
        {
            var actions = new List<string> { Association };
            if (state.ShowTopBarExternalOpenButton) actions.Add(ExternalEditor);
            if (state.ShowTopBarNewTodoButton) actions.Add(NewTodo);
            if (state.ShowTopBarNewNoteButton) actions.Add(NewNote);
            actions.Add(PluginActions);
            state.NoteTopBarShortcuts = actions;
        }
    }

    private static void Store(AppState state, string paperType, List<string> list)
    {
        if (paperType == PaperTypes.Note) state.NoteTopBarShortcuts = list;
        else state.TodoTopBarShortcuts = list;
    }
}
