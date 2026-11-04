using System;
using System.Linq;
using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    internal void SwitchExpandedEditorPaper(PaperWindow source, string paperId)
    {
        if (IsExiting || !source.IsExpandedEditorMode || source.PaperId == paperId)
            return;

        var paper = State.Papers.FirstOrDefault(item => item.Id == paperId);
        if (paper == null) return;

        var bounds = source.ExpandedEditorWindowBounds();
        var maximized = source.IsExpandedEditorMaximized;
        var sidebarOpen = source.IsExpandedEditorSidebarOpen;
        // Each PaperWindow owns its existing editing session, plugin runtime and callbacks.
        // Handoff between those owners rather than grafting one Paper's body into another.
        source.ReturnFromExpandedEditor();
        var wasCollapsed = paper.IsCollapsed;
        if (paper.IsCollapsed)
            SetPaperCollapsedRuntime(paper, collapsed: false, animate: false, saveGeometry: false);
        ShowPaper(paper);
        var target = GetOrCreatePaperWindow(paper);
        target.EnterExpandedEditorMode(
            bounds.IsEmpty ? null : bounds,
            openSidebar: sidebarOpen,
            restoreCollapsedPaper: wasCollapsed,
            maximized: maximized);
    }
}
