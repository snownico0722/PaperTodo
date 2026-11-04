using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class AppController
{
    private WorkbenchWindow? _workbenchWindow;
    internal event Action? WorkbenchModelChanged;
    internal bool HasWorkbench => _workbenchWindow != null;

    internal void ToggleWorkbench()
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (_workbenchWindow is { } open) { open.Close(); return; }
        if (IsExiting || IsDataReloading || Mouse.Captured != null) return;
        var window = new WorkbenchWindow(this);
        _workbenchWindow = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_workbenchWindow, window)) return;
            _workbenchWindow = null;
            if (!IsExiting && !IsWorkspaceSwitching)
            {
                ArrangeDeepCapsules(animate: false);
                // Arrange updates existing masters but does not Show() a master hidden for the
                // workbench. Publish those masters again after the workbench has closed.
                foreach (var master in _masterCapsules.Values)
                {
                    if (!master.IsVisible)
                    {
                        master.Show();
                        master.RefreshEffectiveTopmost();
                    }
                }
            }
        };
        try
        {
            HideWorkbenchMasterCapsules();
            window.Show();
            window.Activate();
        }
        catch
        {
            window.Close();
            _workbenchWindow = null;
            throw;
        }
    }

    internal PaperWindow WorkbenchPaperWindow(string id) => GetOrCreatePaperWindow(
        FindPaper(id) ?? throw new InvalidOperationException("Paper no longer exists."));

    internal WorkbenchState WorkbenchLayout => State.Workbench ??= new WorkbenchState();

    internal WorkbenchWorkspaceSnapshot SnapshotWorkbench(AppState state, int number, bool live)
    {
        var papers = state.Papers.Select(p =>
        {
            var placement = state.Workbench?.Cards.GetValueOrDefault(p.Id)?.Copy();
            _windows.TryGetValue(p.Id, out var window);
            var plugin = p.Type == PaperTypes.Note && p.BodyProviderId != PaperBodyProviderIds.Markdown;
            var title = !string.IsNullOrWhiteSpace(p.Title) ? p.Title : p.BodyHeaderText;
            var content = p.Type == PaperTypes.Todo
                ? string.Join("\n", p.Items.Take(12).Select(i => (i.Done ? "[x] " : "[ ] ") + i.Text))
                : plugin ? p.BodyHeaderText : live && window != null ? window.CurrentMarkdownContentForExternalRead() : p.Content;
            var summary = (content ?? "");
            if (summary.Length > 800) summary = summary[..800];
            return new WorkbenchPaperSnapshot(p.Id, title ?? "", summary, plugin ? "plugin" : p.Type,
                live && window != null ? window.WorkbenchAffinity : plugin ? "plugin:" + p.BodyProviderId : "",
                live && window != null ? window.WorkbenchBindingTitle : "",
                placement?.Width ?? WorkbenchCardState.ValidSize(p.Width, 320, 240, 640),
                placement?.Height ?? WorkbenchCardState.ValidSize(p.Height, 360, 240, 720), placement);
        }).ToArray();
        var ids = papers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var links = state.Papers.SelectMany(p => p.Items
            .Where(i => !string.IsNullOrEmpty(i.LinkedPaperId) && ids.Contains(i.LinkedPaperId))
            .Select(i => new WorkbenchLayoutEdge(p.Id, i.LinkedPaperId!)))
            .Where(e => e.From != e.To && ids.Contains(e.From) && ids.Contains(e.To)).Distinct().ToArray();
        return new(number, papers, links);
    }

    internal WorkbenchWorkspaceSnapshot SnapshotCurrentWorkbench() => SnapshotWorkbench(State, WorkspaceNumber, true);

    internal async Task<IReadOnlyList<WorkbenchWorkspaceSnapshot>> ReadOtherWorkbenchWorkspacesAsync(CancellationToken token)
    {
        var active = WorkspaceNumber;
        var count = WorkspaceOptions.Enabled ? WorkspaceOptions.Count : 1;
        var root = AppContext.BaseDirectory;
        return await Task.Run(() =>
        {
            var result = new List<WorkbenchWorkspaceSnapshot>();
            for (var number = 1; number <= count; number++)
            {
                if (number == active) continue;
                token.ThrowIfCancellationRequested();
                try
                {
                    var state = StateStore.ReadWorkbenchOverview(new WorkspacePaths(root, number).Data);
                    // This projection deliberately does not access live windows on the worker.
                    result.Add(state == null ? new(number, [], []) : SnapshotInactiveWorkbench(state, number));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                {
                    result.Add(new(number, [], [], ex.Message));
                }
            }
            return (IReadOnlyList<WorkbenchWorkspaceSnapshot>)result;
        }, token);
    }

    private static WorkbenchWorkspaceSnapshot SnapshotInactiveWorkbench(AppState state, int number)
    {
        var papers = state.Papers.Select(p =>
        {
            var placement = state.Workbench?.Cards.GetValueOrDefault(p.Id)?.Copy();
            var plugin = p.Type == PaperTypes.Note && p.BodyProviderId != PaperBodyProviderIds.Markdown;
            var text = p.Type == PaperTypes.Todo ? string.Join("\n", p.Items.Take(12).Select(i => (i.Done ? "[x] " : "[ ] ") + i.Text))
                : plugin ? p.BodyHeaderText : p.Content;
            text ??= "";
            return new WorkbenchPaperSnapshot(p.Id, p.Title ?? "", text.Length > 800 ? text[..800] : text,
                plugin ? "plugin" : p.Type, plugin ? "plugin:" + p.BodyProviderId : "", "",
                placement?.Width ?? WorkbenchCardState.ValidSize(p.Width, 320, 240, 640),
                placement?.Height ?? WorkbenchCardState.ValidSize(p.Height, 360, 240, 720), placement);
        }).ToArray();
        var ids = papers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var links = state.Papers.SelectMany(p => p.Items.Where(i => i.LinkedPaperId != null)
                .Select(i => new WorkbenchLayoutEdge(p.Id, i.LinkedPaperId!)))
            .Where(e => e.From != e.To && ids.Contains(e.From) && ids.Contains(e.To)).Distinct().ToArray();
        return new(number, papers, links);
    }

    internal void StoreWorkbenchLayout(WorkbenchState layout)
    {
        if (IsExiting || IsDataReloading) return;
        layout.Normalize(State.Papers.Select(p => p.Id));
        State.Workbench = layout;
        MarkDirty();
    }

    internal bool FocusWorkbenchPaper(PaperData paper, bool activate)
    {
        if (_workbenchWindow is not { IsSuspended: false } window) return false;
        window.QueueModelRefresh(activate ? paper.Id : null);
        return true;
    }

    internal void CloseWorkbenchForDesktopAction() => _workbenchWindow?.Close();
    private void SuspendWorkbench() => _workbenchWindow?.SuspendWorkspace();
    private void HideWorkbenchMasterCapsules()
    {
        foreach (var master in _masterCapsules.Values)
            if (master.IsVisible) master.Hide();
    }

    private void ResumeWorkbench()
    {
        _workbenchWindow?.ResumeWorkspace();
        // Switching workspaces creates new master windows while the workbench is suspended.
        // Keep them behind the workbench too, then restore them on explicit close.
        if (_workbenchWindow != null) HideWorkbenchMasterCapsules();
    }
    private void CloseWorkbenchForShutdown() => _workbenchWindow?.CloseForShutdown();
}
