using System;
using System.Collections.Generic;
using System.Linq;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly List<ExpandedEditorWindow> _expandedEditors = [];

    internal void OpenExpandedEditorFor(PaperWindow source)
    {
        if (IsExiting || !State.Papers.Any(p => p.Id == source.PaperId)) return;
        if (source.ExpandedEditorHostWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var editor = new ExpandedEditorWindow(this, source);
        _expandedEditors.Add(editor);
        editor.Show();
        editor.Activate();
    }

    internal void SwitchExpandedEditorPaper(PaperWindow source, string paperId)
    {
        var host = source.ExpandedEditorHostWindow;
        if (IsExiting || host == null || source.PaperId == paperId) return;

        var paper = State.Papers.FirstOrDefault(p => p.Id == paperId);
        if (paper == null) return;

        var alreadyOpen = _expandedEditors.FirstOrDefault(e => e.CurrentPaperId == paperId);
        if (alreadyOpen != null)
        {
            alreadyOpen.Activate();
            return;
        }

        // Change only the borrowed WPF visual, not the owning editor HWND or
        // PaperData. Each PaperWindow retains the same body session as before.
        host.SwitchTo(GetOrCreatePaperWindow(paper));
    }

    internal void RestoreExpandedEditorPaper(string id)
    {
        var paper = State.Papers.FirstOrDefault(p => p.Id == id);
        if (paper != null && !IsExiting) ShowPaper(paper);
    }

    internal void HideExpandedEditorPaper(string id)
    {
        var paper = State.Papers.FirstOrDefault(p => p.Id == id);
        if (paper != null && !IsExiting) HidePaper(paper);
    }

    internal void UnregisterExpandedEditor(ExpandedEditorWindow editor)
        => _expandedEditors.Remove(editor);

    internal void CloseExpandedEditorForPaper(string id)
    {
        var editor = _expandedEditors.FirstOrDefault(e => e.CurrentPaperId == id);
        editor?.CloseWithoutPaperAction();
    }

    private void CloseAllExpandedEditors()
    {
        foreach (var editor in _expandedEditors.ToArray())
            editor.CloseWithoutPaperAction();
    }
}
