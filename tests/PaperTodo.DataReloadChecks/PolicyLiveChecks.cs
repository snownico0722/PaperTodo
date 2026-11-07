using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
    }

    private static void ConflictPanelValues()
    {
        static string[] VisibleLines(string own, string other, DataReloadConflictUi.LineMark mark)
        {
            var panel = DataReloadConflictUi.BuildDiffPanel("header", own, other, mark,
                new DataReloadConflictUi.ScrollSyncGroup());
            return LogicalDescendants((DependencyObject)panel.scrollViewer.Content)
                .OfType<TextBlock>().Select(text => text.Text).ToArray();
        }
        var memory = VisibleLines("common\ninternal-only", "common\nexternal-only", DataReloadConflictUi.LineMark.OnlyInMemory);
        var external = VisibleLines("common\nexternal-only", "common\ninternal-only", DataReloadConflictUi.LineMark.OnlyInExternal);
        Require(memory.SequenceEqual(new[] { "  common", "- internal-only" }) &&
            external.SequenceEqual(new[] { "  common", "+ external-only" }),
            "a conflict column shows or labels values that belong only to the opposite side");
        var empty = VisibleLines("", "opposite value", DataReloadConflictUi.LineMark.OnlyInMemory);
        Require(empty.Length == 1 && empty[0] == Strings.Get("DataReloadConflictEmpty"),
            "empty content was replaced by opposite-side text instead of an empty indication");
        Console.WriteLine("PASS actual conflict panel values, side-specific markers and empty content");
    }

    private static async Task MergePolicyLiveChecks(AppController controller)
    {
        ConflictPanelValues();
        const string settingId = "general.data_merge_conflicts";
        var settings = controller.PublicSettings;
        var metadata = settings.Get(settingId);
        Require(metadata.Value.GetString() == "ask" && metadata.Options!.SequenceEqual(new[] { "ask", "external", "internal" }),
            "public setting default or allowed values are incorrect");
        var page = (UIElement)typeof(AppController).GetMethod("BuildWorkspaceDataSettings", Private)!.Invoke(controller, null)!;
        var selector = LogicalDescendants(page).OfType<ComboBox>().Single(combo =>
            combo.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string)
                .SequenceEqual(new[] { "ask", "external", "internal" }));
        foreach (var index in new[] { 1, 2, 0 })
        {
            selector.SelectedIndex = index;
            var expected = new[] { "ask", "external", "internal" }[index];
            Require(controller.State.DataMergeConflictPolicy == expected &&
                settings.Get(settingId).Value.GetString() == expected &&
                Str(ReadLive()["dataMergeConflictPolicy"]) == expected,
                "actual settings control did not use the shared persistent setting service");
        }
        Throws<PaperSettingsException>(() => settings.Set(settingId, JsonSerializer.SerializeToElement("latest")));
        Console.WriteLine("PASS conflict policy actual settings control, shared API, default and validation");

        // The incoming document may change the preference for FUTURE operations, never
        // select the policy used to accept itself. Use the real controller and editor path.
        settings.Set(settingId, JsonSerializer.SerializeToElement("external"));
        controller.SaveNow(sync: true);
        var baseline = ReadLive();
        var original = baseline.ToJsonString();
        var windows = Field<Dictionary<string, PaperWindow>>(controller, "_windows");
        var editor = Field<MarkdownTextBox>(windows["note-a"], "_noteBox");
        editor.Text = "local conflict";
        LivePaper(baseline, "note-a")["content"] = "incoming conflict";
        baseline["dataMergeConflictPolicy"] = "internal";
        WriteLive(baseline);
        var savedDriver = AppController.ConflictDialogDriver;
        AppController.ConflictDialogDriver = (_, _) => throw new InvalidOperationException("automatic conflict policy prompted");
        try
        {
            var result = controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), original);
            Require(result.Outcome == "pending", "real merge did not start");
            await Until(() => !controller.IsDataReloading, "automatic merge policy");
            var final = controller.PaperCommands.GetDataReloadStatus().LastResult;
            Require(final?.Applied == true && controller.State.DataMergeConflictPolicy == "internal" &&
                editor.PersistentText == "incoming conflict", "incoming policy selected how to accept its own conflicting content");
            var archive = J(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, final.ConflictDiffFile!)));
            Require(Str(archive["policy"]) == "external" &&
                archive["conflicts"]!.AsArray().All(c => Str(c!["resolution"]) == "external"),
                "live archive recorded incoming rather than captured policy");
        }
        finally { AppController.ConflictDialogDriver = savedDriver; }
        settings = controller.PublicSettings;
        settings.Set(settingId, JsonSerializer.SerializeToElement("ask"));
        controller.SaveNow(sync: true);
        var failing = ReadLive();
        var failingBaseline = failing.ToJsonString();
        editor.Text = "retained local draft";
        controller.MarkDirty();
        LivePaper(failing, "note-a")["content"] = "external first";
        WriteLive(failing);
        var later = (System.Text.Json.Nodes.JsonObject)failing.DeepClone();
        LivePaper(later, "note-a")["content"] = "external arrived during review";
        AppController.ConflictDialogDriver = (_, _) =>
        {
            WriteLive(later);
            return PaperConflictChoice.KeepExternal;
        };
        try
        {
            Require(controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), failingBaseline).Outcome == "pending",
                "failed-commit fixture did not start");
            await Until(() => !controller.IsDataReloading, "rejected stale merge");
            var final = controller.PaperCommands.GetDataReloadStatus().LastResult;
            Require(final?.Applied == false && final.Outcome == "busy" &&
                editor.PersistentText == "retained local draft" &&
                Str(LivePaper(ReadLive(), "note-a")["content"]) == "external arrived during review",
                "aborted merge installed or wrote a stale candidate");
            Require(Field<bool>(controller, "_hasPendingDirty"),
                "rejected merge lost the pending local edits");
            controller.SaveNow(sync: true);
            Require(editor.PersistentText == "retained local draft" &&
                    Str(LivePaper(ReadLive(), "note-a")["content"]) == "retained local draft",
                "normal autosave did not resume after rejected explicit merge");
        }
        finally { AppController.ConflictDialogDriver = savedDriver; }
        var versionBeforeInvalid = Field<long>(controller, "_saveVersion");
        Require(controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), "{invalid").Outcome == "invalid" &&
                Field<long>(controller, "_saveVersion") == versionBeforeInvalid,
            "invalid merge changed the ordinary-save version");
        // A background autosave has cleared _hasPendingDirty but its queued snapshot
        // may be discarded by a valid merge preview. Simulate that pending snapshot
        // deterministically instead of relying on thread scheduling in the UI fixture.
        controller.SaveNow(sync: true);
        var inFlightBaseline = ReadLive().ToJsonString();
        editor.Text = "queued local snapshot";
        var outstanding = Field<HashSet<(StateStore Store, long Version)>>(controller, "_pendingAsyncSaves");
        var queuedToken = (Field<StateStore>(controller, "_store"), -17L);
        Require(outstanding.Add(queuedToken), "test queue token already exists");
        typeof(AppController).GetField("_hasPendingDirty", Private)!.SetValue(controller, false);
        Field<DispatcherTimer>(controller, "_saveTimer").Stop();
        Field<DispatcherTimer>(controller, "_forceSaveTimer").Stop();
        var competingDisk = ReadLive();
        LivePaper(competingDisk, "note-a")["content"] = "external competing change";
        WriteLive(competingDisk);
        var diskAfterReview = (System.Text.Json.Nodes.JsonObject)competingDisk.DeepClone();
        LivePaper(diskAfterReview, "note-a")["content"] = "external changed while reviewing";
        AppController.ConflictDialogDriver = (_, _) =>
        {
            WriteLive(diskAfterReview);
            return PaperConflictChoice.KeepExternal;
        };
        try
        {
            Require(!Field<bool>(controller, "_hasPendingDirty"),
                "test did not model a dispatched autosave with no pending dirty flag");
            Require(controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), inFlightBaseline).Outcome == "pending",
                "in-flight autosave merge did not start");
            await Until(() => !controller.IsDataReloading, "in-flight autosave merge failure");
            Require(controller.PaperCommands.GetDataReloadStatus().LastResult is { Outcome: "busy", Applied: false } &&
                    Field<bool>(controller, "_hasPendingDirty"),
                "rejected merge did not restore a superseded in-flight autosave");
        }
        finally
        {
            outstanding.Remove(queuedToken);
            AppController.ConflictDialogDriver = savedDriver;
        }
        controller.SaveNow(sync: true);
        Require(Str(LivePaper(ReadLive(), "note-a")["content"]) == "queued local snapshot",
            "restored autosave did not persist the rejected merge's local draft");
        Console.WriteLine("PASS failed merge reschedules autosave already dispatched to the writer");
        CheckUserExitIoFailure(controller, editor);
        // Finish the normal save explicitly before the rest of the fixture; no sleep/retry.
        editor.Text = "original";
        controller.SaveNow(sync: true);
        Console.WriteLine("PASS trusted policy capture, accurate archive, rejected candidate and normal autosave after rejection");
    }
}
