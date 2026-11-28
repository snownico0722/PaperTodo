namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal void RefreshUserInjectionRuntime(IReadOnlySet<string> changedKeys)
    {
        if (_todoDragEdgeScrollTimer != null && changedKeys.Contains(InjectionParameters.TodoAutoScrollTickMs.Id))
            _todoDragEdgeScrollTimer.Interval = TimeSpan.FromMilliseconds(InjectionParameters.TodoAutoScrollTickMs.Value);
    }

    private static bool IsInjectedMenuActionVisible(string action, bool edge) => action switch
    {
        PaperTopBarShortcuts.NewTodo => edge ? InjectionParameters.MenuEdgeNewTodo.Value : InjectionParameters.MenuPaperNewTodo.Value,
        PaperTopBarShortcuts.NewNote => edge ? InjectionParameters.MenuEdgeNewNote.Value : InjectionParameters.MenuPaperNewNote.Value,
        PaperTopBarShortcuts.Workspace => edge ? InjectionParameters.MenuEdgeWorkspace.Value : InjectionParameters.MenuPaperWorkspace.Value,
        PaperTopBarShortcuts.Association => edge ? InjectionParameters.MenuEdgeAssociation.Value : InjectionParameters.MenuPaperAssociation.Value,
        PaperTopBarShortcuts.ExternalEditor => edge ? InjectionParameters.MenuEdgeExternalEditor.Value : InjectionParameters.MenuPaperExternalEditor.Value,
        PaperTopBarShortcuts.RunScript => InjectionParameters.MenuPaperRunScript.Value,
        _ => true
    };
}
