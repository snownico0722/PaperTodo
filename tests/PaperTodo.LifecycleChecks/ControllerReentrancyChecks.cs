using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class ControllerReentrancyChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task HideDuringCreate(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var hidden = controller.PaperCommands.CreatePaper(
            new CreatePaperRequest { Type = PaperTypes.Note, Show = false },
            PaperOperationContext.Mcp());
        Require(!windows.ContainsKey(hidden.PaperId), "hidden paper fixture already owns a window");
        var originalPapers = controller.State.Papers.ToArray();
        var originalWindows = windows.Values.ToArray();
        var proxies = await PublishProxy(controller, originalWindows);
        var insideHide = false;
        var createReentered = false;
        PaperMutationResult? created = null;
        Exception? createFailure = null;
        Exception? hideFailure = null;

        // McpApiHost dispatches the same command at Normal. Only the production handoff's
        // synchronous Render wait may execute it before HideAllPapers returns.
        var create = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            createReentered = insideHide;
            try
            {
                created = controller.PaperCommands.CreatePaper(
                    new CreatePaperRequest
                    {
                        Type = PaperTypes.Note,
                        Title = "新建",
                        Show = true
                    },
                    PaperOperationContext.Mcp());
                controller.PresentWorkspacePaper(hidden.PaperId, PaperPresentationAction.Show,
                    activate: false, PaperOperationContext.Mcp());
            }
            catch (Exception ex) { createFailure = ex; }
        }, DispatcherPriority.Normal);

        insideHide = true;
        try { controller.HideAllPapers(); }
        catch (Exception ex) { hideFailure = ex; }
        finally { insideHide = false; }

        Require(createReentered && create.Status == DispatcherOperationStatus.Completed,
            "hide fixture did not dispatch the real create command inside the active proxy handoff");
        if (createFailure != null)
            throw new InvalidOperationException("reentrant create command failed", createFailure);
        Require(created != null, "reentrant create did not return a paper");
        if (hideFailure != null)
            throw new InvalidOperationException(
                "HIDE_CREATE_REENTRY: HideAllPapers failed after the real create command added a window", hideFailure);

        Require(originalPapers.Where(paper => paper.Id != hidden.PaperId).All(paper => !paper.IsVisible),
            "the hide command did not mark its original papers hidden");
        Require(originalPapers.Single(paper => paper.Id == hidden.PaperId).IsVisible,
            "HIDE_ALL_SHOW_REENTRY: the older hide overwrote a later show of an existing paper without a window");
        await Until(() => proxies.Count == 0 && originalWindows.All(window => !window.HasVisibleSurface),
            "original windows and queue proxy did not finish hiding");
        var createdId = created!.PaperId;
        Require(controller.State.Papers.Single(paper => paper.Id == createdId).IsVisible,
            "the older hide command overwrote the later create command's visibility");
        await Until(() => windows.TryGetValue(createdId, out var window) && window.HasVisibleSurface,
            "the later create command did not retain its visible window");
        await Until(() => windows.TryGetValue(hidden.PaperId, out var window) && window.HasVisibleSurface,
            "the later show command did not retain the existing paper's new window");
        Console.WriteLine("PASS hide/create reentry: real create/show commands ran during handoff; original targets hid and later visible requests survived");
    }

    internal static async Task HideDuringShow(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var originalWindows = windows.Values.ToArray();
        var window = originalWindows[0];
        var paper = controller.State.Papers.Single(item => item.Id == window.PaperId);
        var proxies = await PublishProxy(controller, originalWindows);
        var insideHide = false;
        var showReentered = false;
        Exception? showFailure = null;
        var show = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            showReentered = insideHide;
            try
            {
                controller.PresentWorkspacePaper(paper.Id, PaperPresentationAction.Show,
                    activate: false, PaperOperationContext.Mcp());
            }
            catch (Exception ex) { showFailure = ex; }
        }, DispatcherPriority.Normal);

        insideHide = true;
        try { controller.HidePaper(paper); }
        finally { insideHide = false; }
        Require(showReentered && show.Status == DispatcherOperationStatus.Completed,
            "single-hide fixture did not dispatch the real show command inside proxy handoff");
        if (showFailure != null)
            throw new InvalidOperationException("reentrant show command failed", showFailure);
        Require(paper.IsVisible,
            "HIDE_SHOW_REENTRY: the older single-paper hide overwrote the later show command");
        await Until(() => proxies.Count == 0 && window.HasVisibleSurface,
            "the later show lost its real capsule after single-paper hide handoff");
        Console.WriteLine("PASS hide/show reentry: a later real show superseded the pending single-paper hide");
    }

    internal static async Task ZOrderDuringCreate(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var originalWindows = windows.Values.ToArray();
        var proxies = await PublishProxy(controller, originalWindows);
        var insideRefresh = false;
        var createReentered = false;
        PaperMutationResult? created = null;
        Exception? createFailure = null;
        Exception? refreshFailure = null;
        var create = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            createReentered = insideRefresh;
            try
            {
                created = controller.PaperCommands.CreatePaper(
                    new CreatePaperRequest { Type = PaperTypes.Note, Show = true },
                    PaperOperationContext.Mcp());
            }
            catch (Exception ex) { createFailure = ex; }
        }, DispatcherPriority.Normal);

        insideRefresh = true;
        // The controller entry used by a capsule menu changes the native topmost contract,
        // forcing the active proxy to complete before the z-order change.
        try { controller.SetDeepCapsuleContextMenuOpen(originalWindows[0].PaperId, true); }
        catch (Exception ex) { refreshFailure = ex; }
        finally { insideRefresh = false; }
        try
        {
            Require(createReentered && create.Status == DispatcherOperationStatus.Completed,
                "z-order fixture did not dispatch the real create command inside proxy handoff");
            if (createFailure != null)
                throw new InvalidOperationException("create during z-order refresh failed", createFailure);
            if (refreshFailure != null)
                throw new InvalidOperationException(
                    "ZORDER_CREATE_REENTRY: floating z-order enumeration failed after a real create added a window",
                    refreshFailure);
            Require(created != null, "z-order reentrant create did not return a paper");
            await Until(() => proxies.Count == 0 && windows.TryGetValue(created!.PaperId, out var window) &&
                    window.HasVisibleSurface,
                "z-order refresh lost the new window or retained its proxy");
            Require(controller.SuppressDeepCapsuleTopmostForContextMenu,
                "z-order handoff lost the open menu's non-topmost request");
            Console.WriteLine("PASS z-order/create reentry: a real create ran during menu z-order handoff without invalidating iteration");
        }
        finally { controller.SetDeepCapsuleContextMenuOpen(originalWindows[0].PaperId, false); }
    }

    internal static async Task CloseDuringDelete(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var originalWindows = windows.Values.ToArray();
        var victim = originalWindows[0];
        var paper = controller.State.Papers.Single(item => item.Id == victim.PaperId);
        var proxies = await PublishProxy(controller, originalWindows);
        var closed = 0;
        victim.Closed += (_, _) => closed++;
        var insideClose = false;
        var deleteReentered = false;
        Exception? deleteFailure = null;
        Exception? closeFailure = null;

        var delete = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            deleteReentered = insideClose;
            try { controller.PaperCommands.DeletePaper(paper.Id, PaperOperationContext.Mcp()); }
            catch (Exception ex) { deleteFailure = ex; }
        }, DispatcherPriority.Normal);

        // The UI delete removes the model after CloseForReal. A queued external delete can
        // therefore still resolve this same paper while the first close publishes its endpoint.
        insideClose = true;
        try { controller.DeletePaper(paper); }
        catch (Exception ex) { closeFailure = ex; }
        finally { insideClose = false; }

        Require(deleteReentered && delete.Status == DispatcherOperationStatus.Completed,
            "close fixture did not dispatch the real delete command inside the active proxy handoff");
        if (deleteFailure != null)
            throw new InvalidOperationException("reentrant delete command failed", deleteFailure);
        if (closeFailure != null)
            throw new InvalidOperationException("outer UI delete failed", closeFailure);
        Require(closed == 1, "a single paper deletion must raise Closed exactly once");
        Require(victim.IsClosed,
            "CLOSE_DELETE_REENTRY: the outer close changed a completed Closed lifecycle back to Closing");
        Require(!windows.ContainsKey(paper.Id) && controller.State.Papers.All(item => item.Id != paper.Id),
            "reentrant deletion left the closed window or its paper registered");
        await Until(() => proxies.Count == 0,
            "the deleted member left a queue proxy pending after close handoff");
        Console.WriteLine("PASS close/delete reentry: a real command ran during proxy handoff; Closed fired once and remained terminal");
    }

    private static async Task<IDictionary> PublishProxy(AppController controller, PaperWindow[] windows)
    {
        var proxies = (IDictionary)Part(controller, "_edgeCapsuleQueueCompositionProxies");
        var presenters = windows.Select(window => (EdgeCapsulePresenter)Part(window, "_edgeCapsule")).ToArray();
        await Until(() => proxies.Count == 0 && presenters.All(presenter =>
                !presenter.HasActiveTransition && !presenter.NativeBatchRetryPending &&
                presenter.AppliedPresentation.Visible),
            "initial real capsule presentations did not settle");
        controller.SuppressEdgeCapsulePreviewForMasterQueueLayout("", EdgeCapsuleEdge.Right);
        controller.ToggleCapsuleCollapseAllActive("", EdgeCapsuleEdge.Right);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        Require(proxies.Count > 0 && controller.IsEdgeCapsuleQueueProxyRetainingSource(windows[0]),
            "reentry fixture never admitted an active DComp queue proxy for its first window");
        return proxies;
    }

    private static object Part(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().Name, name);

    private static async Task Until(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
