using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed record WorkbenchWorkspacePreview(
    int Number,
    IReadOnlyList<PaperData> Papers,
    string? Unavailable);

/// <summary>
/// Workbench (工作台): a full-screen mode that shows every fixed workspace as its own free
/// canvas. Only the active workspace is live and editable; the others are read-only previews
/// loaded from their documents. The workbench owns no paper window, edge or desktop
/// geometry: it stores a canvas origin and paper links on <see cref="PaperData"/>, and routes
/// content edits through <see cref="PaperCommandService"/> like any external editor.
/// </summary>
public sealed partial class AppController
{
    private WorkbenchWindow? _workbench;

    internal bool IsWorkbenchOpen => _workbench != null;

    internal void ToggleWorkbench()
    {
        if (_workbench != null)
        {
            _workbench.RequestClose();
            return;
        }
        OpenWorkbench();
    }

    internal void OpenWorkbench()
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (IsExiting) return;
        if (_workbench != null)
        {
            _workbench.ActivateFromCommand();
            return;
        }

        var window = new WorkbenchWindow(this);
        _workbench = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_workbench, window)) return;
            _workbench = null;
            // Return every paper and capsule on that monitor to its own topmost policy.
            RefreshTopmostForForegroundWindow(forceGlobalScan: true);
        };
        window.Show();
        // The workbench is the avoidance target on its monitor: the existing fullscreen path
        // lowers papers and capsules there below it, without a second z-order owner.
        RefreshTopmostForForegroundWindow();
        window.ActivateFromCommand();
    }

    private bool TryGetWorkbenchAvoidance(out IntPtr window, out string monitorDeviceName)
    {
        window = IntPtr.Zero;
        monitorDeviceName = "";
        if (_workbench is not { IsVisible: true } workbench) return false;
        var handle = new WindowInteropHelper(workbench).Handle;
        if (handle == IntPtr.Zero) return false;
        window = handle;
        if (!WindowWorkAreaHelper.TryGetMonitorDeviceName(handle, out monitorDeviceName))
            monitorDeviceName = "";
        return true;
    }

    // ---------------------------------------------------------------- workspaces

    internal int WorkbenchWorkspaceCount => WorkspaceOptions.Enabled ? WorkspaceOptions.Count : 1;

    internal int WorkbenchActiveWorkspace => WorkspaceNumber;

    internal static string WorkbenchWorkspaceLabel(int number) => WorkspaceSlotLabel(number);

    internal WorkbenchWorkspacePreview LoadWorkbenchPreview(int number)
    {
        if (number == WorkspaceNumber) return new(number, State.Papers, null);
        try
        {
            var state = _workspaceCatalog.StoreFor(number).ReadDocumentForWorkbenchPreview();
            var papers = (state.Papers ?? new List<PaperData>())
                .Where(paper => paper != null)
                .ToList();
            foreach (var paper in papers) paper.Items?.RemoveAll(item => item == null);
            var ids = papers.Select(paper => paper.Id).Where(id => id != null)
                .ToHashSet(StringComparer.Ordinal);
            WorkbenchPaperState.NormalizeAfterLoad(papers, ids);
            return new(number, papers, null);
        }
        catch (FileNotFoundException)
        {
            return new(number, Array.Empty<PaperData>(), Strings.Get("WorkbenchWorkspaceNotCreated"));
        }
        catch (DirectoryNotFoundException)
        {
            return new(number, Array.Empty<PaperData>(), Strings.Get("WorkbenchWorkspaceNotCreated"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(number, Array.Empty<PaperData>(),
                Strings.Format("WorkbenchWorkspaceUnreadable", ex.GetBaseException().Message));
        }
    }

    internal bool CanSwitchWorkspaceFromWorkbench => !_workspaceSwitching && !IsDataReloading;

    // ---------------------------------------------------------------- card facts

    internal WorkbenchCardKind WorkbenchKindOf(PaperData paper, bool live)
    {
        if (live && _windows.TryGetValue(paper.Id, out var window) &&
            window.WorkbenchExternalAttachmentTitle != null)
        {
            return WorkbenchCardKind.Tethered;
        }
        if (paper.Type == PaperTypes.Todo) return WorkbenchCardKind.Todo;
        return string.Equals(paper.BodyProviderId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal)
            ? WorkbenchCardKind.Note
            : WorkbenchCardKind.Plugin;
    }

    internal static string WorkbenchGroupKey(PaperData paper, WorkbenchCardKind kind) =>
        kind == WorkbenchCardKind.Plugin ? "plugin:" + paper.BodyProviderId : kind.ToString();

    internal string WorkbenchTitle(PaperData paper, bool live, IReadOnlyList<PaperData> papers)
    {
        if (live) return PaperDisplayTitle(paper);
        if (paper.Type == PaperTypes.Note &&
            !string.Equals(paper.BodyProviderId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(paper.BodyHeaderText))
        {
            return paper.BodyHeaderText;
        }
        var type = paper.Type == PaperTypes.Note ? PaperTypes.Note : PaperTypes.Todo;
        var number = 1;
        foreach (var candidate in papers)
        {
            if (ReferenceEquals(candidate, paper)) break;
            if ((candidate.Type == PaperTypes.Note ? PaperTypes.Note : PaperTypes.Todo) == type) number++;
        }
        return PaperTitles.EffectiveTitle(paper, number);
    }

    internal string? WorkbenchTetherTitle(PaperData paper) =>
        _windows.TryGetValue(paper.Id, out var window) ? window.WorkbenchExternalAttachmentTitle : null;

    internal string WorkbenchPluginName(PaperData paper) =>
        PaperBodyPlugins.TryGet(paper.BodyProviderId, out var descriptor)
            ? descriptor.DisplayName
            : paper.BodyProviderId;

    internal string WorkbenchNoteText(PaperData paper, bool live) =>
        live ? CurrentMarkdownContentForExternalRead(paper) : paper.Content ?? "";

    internal bool IsWorkbenchPaperShown(PaperData paper) => IsPaperShown(paper);

    // ---------------------------------------------------------------- layout state

    internal void SetWorkbenchOrigins(IReadOnlyDictionary<string, Point> origins)
    {
        var changed = false;
        foreach (var paper in State.Papers)
        {
            if (!origins.TryGetValue(paper.Id, out var origin)) continue;
            var x = Math.Round(Math.Max(0, origin.X), 1);
            var y = Math.Round(Math.Max(0, origin.Y), 1);
            if (paper.WorkbenchX == x && paper.WorkbenchY == y) continue;
            paper.WorkbenchX = x;
            paper.WorkbenchY = y;
            changed = true;
        }
        if (changed) MarkDirty();
    }

    internal bool LinkWorkbenchPapers(PaperData source, PaperData target)
    {
        if (!State.Papers.Contains(source) || !State.Papers.Contains(target)) return false;
        if (!WorkbenchPaperState.TryLink(source, target)) return false;
        MarkDirty();
        return true;
    }

    internal bool UnlinkWorkbenchPapers(PaperData a, PaperData b)
    {
        if (!WorkbenchPaperState.Unlink(a, b)) return false;
        MarkDirty();
        return true;
    }

    internal bool ClearWorkbenchLinks(PaperData paper)
    {
        var changed = WorkbenchPaperState.RemoveLinksTo(State.Papers, paper.Id).Count > 0;
        if (paper.WorkbenchLinks != null)
        {
            paper.WorkbenchLinks = null;
            changed = true;
        }
        if (changed) MarkDirty();
        return changed;
    }

    /// <summary>The ordinary delete path removes workbench links the same way as todo links.</summary>
    private void ClearWorkbenchLinksToPaper(string paperId) =>
        WorkbenchPaperState.RemoveLinksTo(State.Papers, paperId);

    // ---------------------------------------------------------------- content edits

    internal PaperData? CreateWorkbenchPaper(string type, Point origin)
    {
        var result = PaperCommands.CreatePaper(
            new CreatePaperRequest { Type = type, Show = false },
            PaperOperationContext.User());
        var paper = FindPaper(result.PaperId);
        if (paper == null) return null;
        paper.WorkbenchX = Math.Round(Math.Max(0, origin.X), 1);
        paper.WorkbenchY = Math.Round(Math.Max(0, origin.Y), 1);
        MarkDirty();
        return paper;
    }

    internal void OpenPaperFromWorkbench(PaperData paper)
    {
        var id = paper.Id;
        _workbench?.RequestClose();
        // Let the workbench release its avoidance before the real paper comes forward.
        Application.Current.Dispatcher.InvokeAsync(
            () => { if (!IsExiting) OpenLinkedPaper(id); },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- cross-workspace move

    /// <summary>
    /// Returns why a paper cannot move to <paramref name="target"/>, or null. The first
    /// version only moves papers whose complete data lives in the core document: todo papers
    /// and built-in Markdown notes without images. Images are stored per workspace in LMDB
    /// and plugin state per workspace in plugins/dataN, so those papers stay put.
    /// </summary>
    internal string? WorkbenchTransferBlocker(PaperData paper, int target)
    {
        if (!WorkspaceOptions.Enabled || target < 1 || target > WorkspaceOptions.Count ||
            target == WorkspaceNumber)
        {
            return Strings.Get("WorkbenchMoveUnavailable");
        }
        if (IsDataReloading || _workspaceSwitching || IsExiting)
            return Strings.Get("WorkbenchMoveBusy");
        if (!State.Papers.Contains(paper)) return Strings.Get("WorkbenchMoveUnavailable");
        if (paper.Type == PaperTypes.Note &&
            !string.Equals(paper.BodyProviderId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal))
        {
            return Strings.Get("WorkbenchMovePluginPaper");
        }
        if (!string.IsNullOrEmpty(paper.StartupOwnerPluginId))
            return Strings.Get("WorkbenchMovePluginPaper");
        if (paper.Type == PaperTypes.Note &&
            MarkdownImageReferences.CollectImageIds(CurrentMarkdownContentForExternalRead(paper)).Count > 0)
        {
            return Strings.Get("WorkbenchMoveImageNote");
        }
        if (!File.Exists(_workspaceCatalog.PathsFor(target).Data))
            return Strings.Get("WorkbenchWorkspaceNotCreated");
        return null;
    }

    /// <summary>
    /// Moves one paper into an inactive workspace. The target document is written first, under
    /// its own store's lock and version fence; only then is the paper removed here through the
    /// shared delete command. A failure after the copy leaves the paper in both places, never
    /// in neither.
    /// </summary>
    internal bool TryTransferWorkbenchPaper(PaperData paper, int target, Point origin, out string message)
    {
        if (WorkbenchTransferBlocker(paper, target) is { } blocker)
        {
            message = blocker;
            return false;
        }

        // Settle pending text of this Markdown paper only, then re-check for images.
        PrepareExternalPaperOperation(paper);
        if (WorkbenchTransferBlocker(paper, target) is { } afterCommit)
        {
            message = afterCommit;
            return false;
        }

        var node = StateStore.SerializePaperForWorkbenchTransfer(paper);
        // Relations name papers of this workspace; they would dangle in the target.
        node.Remove("workbenchLinks");
        node["workbenchX"] = Math.Round(Math.Max(0, origin.X), 1);
        node["workbenchY"] = Math.Round(Math.Max(0, origin.Y), 1);
        if (node["items"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>()) item.Remove("linkedNoteId");
        }

        var label = WorkspaceSlotLabel(target);
        try
        {
            _workspaceCatalog.StoreFor(target).AppendWorkbenchTransferredPaper(node, NextWorkspaceSaveVersion());
        }
        catch (WorkbenchTransferException ex)
        {
            message = ex.Code switch
            {
                "missing" => Strings.Get("WorkbenchWorkspaceNotCreated"),
                "paper_limit" => Strings.Format("WorkbenchMoveTargetFull", label),
                "duplicate" => Strings.Format("WorkbenchMoveDuplicate", label),
                _ => Strings.Get("WorkbenchMoveBusy")
            };
            return false;
        }
        catch (ExternalDocumentChangedException)
        {
            message = Strings.Get("WorkbenchMoveBusy");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            message = Strings.Format("WorkbenchMoveFailed", label, ex.GetBaseException().Message);
            return false;
        }

        try
        {
            PaperCommands.DeletePaper(paper.Id, PaperOperationContext.User());
        }
        catch (PaperCommandException ex)
        {
            message = Strings.Format("WorkbenchMoveCopiedOnly", label, ex.Message);
            return false;
        }

        message = Strings.Format("WorkbenchMoved", label);
        return true;
    }
}
