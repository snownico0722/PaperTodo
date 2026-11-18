using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class WorkbenchChecks
{
    private static async Task RunSearch()
    {
        var initial = FixtureState();
        initial.UseCapsuleMode = false;
        initial.UseDeepCapsuleMode = false;
        var activeDocument = WorkspaceMaterials.CreateOwner(initial).WorkspaceMaterials!;
        activeDocument.Layout.Groups["active-group"] = new()
        {
            Id = "active-group", Title = "Active group", Members = ["note-a", "note-b"], Collapsed = true
        };
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(initial), 1);

        var other = FixtureState();
        other.Workspaces = null;
        other.Papers =
        [
            new() { Id = "other-visible", Type = PaperTypes.Note, Title = "visible-preview-result" },
            new() { Id = "other-grouped", Type = PaperTypes.Note, Title = "grouped-preview-result" },
            new() { Id = "other-peer", Type = PaperTypes.Note, Title = "Group peer" }
        ];
        var document = WorkspaceMaterials.CreateOwner(other).WorkspaceMaterials!;
        document.Layout.Groups["preview-group"] = new()
        {
            Id = "preview-group", Title = "Preview group", Members = ["other-grouped", "other-peer"], Collapsed = true
        };
        const string root = "material:root", child = "material:child", leaf = "material:leaf";
        const string unrelatedRoot = "material:unrelated-root", unrelatedLeaf = "material:unrelated-leaf";
        foreach (var (id, title, collapsed) in new[]
        {
            (root, "Branch root", true), (child, "Branch child", true), (leaf, "branch-preview-result", false),
            (unrelatedRoot, "Other branch", true), (unrelatedLeaf, "Other descendant", false)
        })
            document.Items[id] = new() { Id = id, Kind = WorkspaceMaterialKinds.Node, Title = title, Collapsed = collapsed };
        document.Layout.Connections =
        [
            new() { From = root, To = child, IsBranch = true },
            new() { From = child, To = leaf, IsBranch = true },
            new() { From = unrelatedRoot, To = unrelatedLeaf, IsBranch = true }
        ];
        var otherStore = new StateStore(AppContext.BaseDirectory, DurableAtomicFileWriter.Shared, 2);
        otherStore.SaveJsonSync(otherStore.SerializeState(other), 1);
        var savedOther = File.ReadAllBytes(otherStore.FilePath);

        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        controller.ToggleWorkbench();
        var desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        var scenes = (IDictionary)Member(desk, "_scenes");
        await Until(() => desk.HostedPaperCount == 3 && scenes.Contains(2) &&
            ((IDictionary)Member(scenes[2]!, "Cards")).Contains(leaf), "Search fixture did not load its overview");
        await (Task)typeof(WorkbenchWindow).GetMethod("LayoutActiveAsync", Private)!
            .Invoke(desk, [false, null, null, null])!;
        var preview = scenes[2]!;
        var previewCanvas = (Canvas)Member(preview, "Canvas");
        var previewCards = (IDictionary)Member(preview, "Cards");
        var originalSnapshot = (WorkbenchWorkspaceSnapshot)Member(preview, "Snapshot");
        Border PreviewFrame(string id) => (Border)Member(previewCards[id]!, "Frame");

        Assert(PreviewFrame("other-grouped").Visibility == Visibility.Collapsed &&
               PreviewFrame(child).Visibility == Visibility.Collapsed &&
               PreviewFrame(leaf).Visibility == Visibility.Collapsed &&
               PreviewFrame(unrelatedLeaf).Visibility == Visibility.Collapsed,
            "Preview did not initially honor saved groups and collapsed branch ancestors");

        InvokeWorkbench(desk, "FocusGroup", "active-group");
        Assert(previewCanvas.Visibility == Visibility.Collapsed, "Group focus did not hide the other workspace");
        Search("visible-preview-result");
        Assert(previewCanvas.IsVisible && PreviewFrame("other-visible").IsVisible &&
               typeof(WorkbenchWindow).GetField("_focusedGroupId", Private)!.GetValue(desk) == null,
            "Cross-workspace search kept the target scene hidden by group focus");
        Assert(controller.WorkbenchLayout.Groups["active-group"].Collapsed,
            "Leaving group focus for search changed the active group's saved collapse state");

        Search("grouped-preview-result");
        Assert(PreviewFrame("other-grouped").IsVisible && PreviewFrame("other-peer").IsVisible,
            "Search did not reveal a matching card inside a collapsed preview group");
        Assert(originalSnapshot.Layout!.Groups["preview-group"].Collapsed &&
               !((WorkbenchWorkspaceSnapshot)Member(preview, "Snapshot")).Layout!.Groups["preview-group"].Collapsed,
            "Preview reveal mutated the original snapshot instead of replacing its copied layout");

        Search("branch-preview-result");
        Assert(PreviewFrame(child).IsVisible && PreviewFrame(leaf).IsVisible &&
               PreviewFrame(unrelatedLeaf).Visibility == Visibility.Collapsed,
            "Search did not reveal every branch ancestor or expanded an unrelated branch");
        Assert(originalSnapshot.Papers.Single(p => p.Id == root).MaterialCollapsed &&
               originalSnapshot.Papers.Single(p => p.Id == child).MaterialCollapsed,
            "Preview search mutated the original immutable branch summaries");
        Assert(controller.WorkspaceNumber == 1 && desk.HostedPaperCount == 3 && controller.FindWorkbenchMaterial(root) == null,
            "Preview search activated or imported the other workspace instead of revealing its read-only cards");
        controller.SaveNow(sync: true);
        Assert(File.ReadAllBytes(otherStore.FilePath).SequenceEqual(savedOther),
            "Searching an inactive workspace changed its saved file");

        // The expansion belongs only to the current overview presentation. A fresh read
        // must restore saved collapse state, even after the active workspace was saved.
        await (Task)typeof(WorkbenchWindow).GetMethod("RefreshOverviewAsync", Private)!.Invoke(desk, null)!;
        Assert(PreviewFrame("other-grouped").Visibility == Visibility.Collapsed &&
               PreviewFrame(child).Visibility == Visibility.Collapsed && PreviewFrame(leaf).Visibility == Visibility.Collapsed,
            "Refreshing the overview retained search-only expansion instead of the saved collapse state");
        controller.ToggleWorkbench();
        controller.SaveNow(sync: true);
        Assert(File.ReadAllBytes(otherStore.FilePath).SequenceEqual(savedOther),
            "Closing the workbench persisted a read-only search expansion");

        void Search(string query)
        {
            var search = (TextBox)Member(desk, "_search");
            search.Text = query;
            search.Focus();
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(desk), 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent };
            search.RaiseEvent(args);
            Assert(args.Handled, "Enter in the search field did not navigate to a result");
            var target = ((WorkbenchWorkspaceSnapshot)Member(preview, "Snapshot")).Papers.Single(p => p.Title == query);
            var bounds = ((WorkbenchRect)Member(previewCards[target.Id]!, "Bounds")).Offset((WorkbenchPoint)Member(preview, "Origin"));
            var scale = (ScaleTransform)Member(desk, "_scale");
            var pan = (TranslateTransform)Member(desk, "_pan");
            var viewport = (FrameworkElement)Member(desk, "_viewport");
            Assert(Math.Abs(bounds.Center.X * scale.ScaleX + pan.X - viewport.ActualWidth / 2) < .01 &&
                   Math.Abs(bounds.Center.Y * scale.ScaleY + pan.Y - viewport.ActualHeight / 2) < .01,
                "Search did not center the visible result in the viewport");
        }
    }
}
