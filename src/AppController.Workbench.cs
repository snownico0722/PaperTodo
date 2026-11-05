using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class AppController
{
    private WorkbenchWindow? _workbenchWindow;
    private PaperWindow? _workbenchHiddenMaterialsNote;
    internal event Action? WorkbenchModelChanged;
    internal bool HasWorkbench => _workbenchWindow != null;

    internal void ToggleWorkbench()
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (_workbenchWindow is { } open) { open.Close(); return; }
        if (IsExiting || IsDataReloading || Mouse.Captured != null) return;
        EnsureWorkspaceMaterialsOwner();
        var window = new WorkbenchWindow(this);
        _workbenchWindow = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_workbenchWindow, window)) return;
            _workbenchWindow = null;
            if (!IsExiting && !IsWorkspaceSwitching)
            {
                RestoreWorkbenchMaterialsNote();
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

    internal string WorkbenchWorkspaceName(int number) => WorkspaceOptions.DisplayName(number);

    internal PaperWindow WorkbenchPaperWindow(string id) => GetOrCreatePaperWindow(
        FindPaper(id) ?? throw new InvalidOperationException("Paper no longer exists."));

    internal WorkbenchState WorkbenchLayout => EnsureWorkspaceMaterialsOwner().WorkspaceMaterials!.Layout;

    internal WorkbenchWorkspaceSnapshot SnapshotWorkbench(AppState state, int number, bool live)
    {
        var document = WorkspaceMaterials.FindOwner(state)?.WorkspaceMaterials;
        var layout = document?.Layout;
        var papers = state.Papers.Where(p => p.WorkspaceMaterials == null).Select(p =>
        {
            var placement = layout?.Cards.GetValueOrDefault(p.Id)?.Copy();
            _windows.TryGetValue(p.Id, out var window);
            var plugin = p.Type == PaperTypes.Note && p.BodyProviderId != PaperBodyProviderIds.Markdown;
            var title = !string.IsNullOrWhiteSpace(p.Title) ? p.Title : p.BodyHeaderText;
            var content = p.Type == PaperTypes.Todo
                ? string.Join("\n", p.Items.Take(12).Select(i => (i.Done ? "[x] " : "[ ] ") + i.Text))
                : plugin ? p.BodyHeaderText : live && window != null ? window.CurrentMarkdownContentForExternalRead() : p.Content;
            var searchText = p.Type == PaperTypes.Todo
                ? string.Join("\n", p.Items.Select(i => i.Text)) : content ?? "";
            var summary = (content ?? "");
            if (summary.Length > 800) summary = summary[..800];
            return new WorkbenchPaperSnapshot(p.Id, title ?? "", summary, searchText, plugin ? "plugin" : p.Type,
                live && window != null ? window.WorkbenchAffinity : plugin ? "plugin:" + p.BodyProviderId : "",
                live && window != null ? window.WorkbenchBindingTitle : "",
                placement?.Width ?? WorkbenchCardState.ValidSize(p.Width, 320, 240, 640),
                placement?.Height ?? WorkbenchCardState.ValidSize(p.Height, 360, 240, 720), placement, p.IsVisible);
        }).Concat(SnapshotWorkbenchMaterials(document)).ToArray();
        var ids = papers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var links = state.Papers.SelectMany(p => p.Items
            .Where(i => !string.IsNullOrEmpty(i.LinkedPaperId) && ids.Contains(i.LinkedPaperId))
            .Select(i => new WorkbenchLayoutEdge(p.Id, i.LinkedPaperId!)))
            .Where(e => e.From != e.To && ids.Contains(e.From) && ids.Contains(e.To)).Distinct().ToArray();
        links = links.Concat(layout?.Connections.Select(c => new WorkbenchLayoutEdge(c.From, c.To)) ?? [])
            .Where(e => e.From != e.To && ids.Contains(e.From) && ids.Contains(e.To)).Distinct().ToArray();
        return new(number, papers, links, Layout: layout?.Copy());
    }

    internal WorkbenchWorkspaceSnapshot SnapshotCurrentWorkbench() => SnapshotWorkbench(State, WorkspaceNumber, true);

    internal async Task<IReadOnlyList<WorkbenchWorkspaceSnapshot>> ReadOtherWorkbenchWorkspacesAsync(CancellationToken token)
    {
        var active = WorkspaceNumber;
        IReadOnlyList<int> slots = WorkspaceOptions.Enabled ? WorkspaceOptions.SlotNumbers : [1];
        var root = AppContext.BaseDirectory;
        return await Task.Run(() =>
        {
            var result = new List<WorkbenchWorkspaceSnapshot>();
            foreach (var number in slots)
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
        var document = WorkspaceMaterials.FindOwner(state)?.WorkspaceMaterials;
        var layout = document?.Layout;
        var papers = state.Papers.Where(p => p.WorkspaceMaterials == null).Select(p =>
        {
            var placement = layout?.Cards.GetValueOrDefault(p.Id)?.Copy();
            var plugin = p.Type == PaperTypes.Note && p.BodyProviderId != PaperBodyProviderIds.Markdown;
            var text = p.Type == PaperTypes.Todo ? string.Join("\n", p.Items.Take(12).Select(i => (i.Done ? "[x] " : "[ ] ") + i.Text))
                : plugin ? p.BodyHeaderText : p.Content;
            text ??= "";
            var searchText = p.Type == PaperTypes.Todo
                ? string.Join("\n", p.Items.Select(i => i.Text)) : text;
            return new WorkbenchPaperSnapshot(p.Id, p.Title ?? "", text.Length > 800 ? text[..800] : text,
                searchText, plugin ? "plugin" : p.Type, plugin ? "plugin:" + p.BodyProviderId : "", "",
                placement?.Width ?? WorkbenchCardState.ValidSize(p.Width, 320, 240, 640),
                placement?.Height ?? WorkbenchCardState.ValidSize(p.Height, 360, 240, 720), placement, p.IsVisible);
        }).Concat(SnapshotWorkbenchMaterials(document)).ToArray();
        var ids = papers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var links = state.Papers.SelectMany(p => p.Items.Where(i => i.LinkedPaperId != null)
                .Select(i => new WorkbenchLayoutEdge(p.Id, i.LinkedPaperId!)))
            .Where(e => e.From != e.To && ids.Contains(e.From) && ids.Contains(e.To)).Distinct().ToArray();
        links = links.Concat(layout?.Connections.Select(c => new WorkbenchLayoutEdge(c.From, c.To)) ?? [])
            .Where(e => e.From != e.To && ids.Contains(e.From) && ids.Contains(e.To)).Distinct().ToArray();
        return new(number, papers, links, Layout: layout?.Copy());
    }

    private static IEnumerable<WorkbenchPaperSnapshot> SnapshotWorkbenchMaterials(WorkspaceMaterialsDocument? document)
    {
        if (document == null) yield break;
        foreach (var item in document.Items.Values)
        {
            var placement = document.Layout.Cards.GetValueOrDefault(item.Id)?.Copy();
            var text = item.Content ?? "";
            yield return new WorkbenchPaperSnapshot(item.Id, item.Title,
                text.Length > 800 ? text[..800] : text,
                text + "\n" + item.Source, "material:" + item.Kind, "", "",
                placement?.Width ?? (item.Kind == WorkspaceMaterialKinds.Node ? 220 : 320),
                placement?.Height ?? (item.Kind == WorkspaceMaterialKinds.Node ? 120 : 260),
                placement, false, item.Collapsed);
        }
    }

    internal void StoreWorkbenchLayout(WorkbenchState layout)
    {
        if (IsExiting || IsDataReloading) return;
        var document = EnsureWorkspaceMaterialsOwner().WorkspaceMaterials!;
        layout.Normalize(State.Papers.Where(p => p.WorkspaceMaterials == null).Select(p => p.Id)
            .Concat(document.Items.Keys));
        document.Layout = layout;
        MarkDirty();
    }

    internal bool FocusWorkbenchPaper(PaperData paper, bool activate)
    {
        if (paper.WorkspaceMaterials != null)
        {
            CloseWorkbenchForDesktopAction();
            return false;
        }
        if (_workbenchWindow is not { IsSuspended: false } window) return false;
        window.QueueModelRefresh(activate ? paper.Id : null);
        return true;
    }

    // An internal reload/switch temporarily suspends the desk. Only an explicit desktop
    // action while the desk is active should close it.
    internal void CloseWorkbenchForDesktopAction()
    {
        if (_workbenchWindow is { IsSuspended: false } window) window.Close();
    }
    private void SuspendWorkbench() => _workbenchWindow?.SuspendWorkspace();
    private void HideWorkbenchMasterCapsules()
    {
        if (WorkspaceMaterials.FindOwner(State) is { } owner &&
            _windows.TryGetValue(owner.Id, out var window) && window.IsVisible)
        {
            _workbenchHiddenMaterialsNote = window;
            window.HideWithoutGeometrySave();
        }
        foreach (var master in _masterCapsules.Values)
            if (master.IsVisible) master.Hide();
    }

    private void RestoreWorkbenchMaterialsNote()
    {
        var window = _workbenchHiddenMaterialsNote;
        _workbenchHiddenMaterialsNote = null;
        if (window == null || window.IsClosed ||
            !_windows.TryGetValue(window.PaperId, out var current) || !ReferenceEquals(window, current) ||
            FindPaper(window.PaperId)?.IsVisible != true) return;
        var activate = window.ShowActivated;
        window.ShowActivated = false;
        try { window.Show(); }
        finally { window.ShowActivated = activate; }
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
