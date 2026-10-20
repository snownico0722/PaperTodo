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
        var page = (UIElement)typeof(AppController).GetMethod("BuildSettingsGeneralOptions", Private)!.Invoke(controller, null)!;
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
            Require(Field<bool>(controller, "_hasPendingDirty") &&
                !Field<DispatcherTimer>(controller, "_forceSaveTimer").IsEnabled,
                "aborted merge scheduled an overwrite of the newer external input");
            var ignored = Field<bool>(controller, "_ignoreSaveFailures");
            typeof(AppController).GetField("_ignoreSaveFailures", Private)!.SetValue(controller, true);
            try
            {
                controller.MarkDirty();
                controller.SaveNow(sync: true);
                controller.SaveNow();
                await Until(() => Field<bool>(controller, "_hasPendingDirty"), "rejected queued normal save");
                controller.Exit();
                Require(controller.IsRunning && editor.PersistentText == "retained local draft" &&
                    Str(LivePaper(ReadLive(), "note-a")["content"]) == "external arrived during review",
                    "later save/ordinary exit discarded a conflict side");
            }
            finally { typeof(AppController).GetField("_ignoreSaveFailures", Private)!.SetValue(controller, ignored); }
            AppController.ConflictDialogDriver = (_, _) => PaperConflictChoice.KeepMemory;
            Require(controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), failingBaseline).Outcome == "pending",
                "explicit conflict retry did not start");
            await Until(() => !controller.IsDataReloading, "explicit retry resolves paused saves");
            Require(controller.PaperCommands.GetDataReloadStatus().LastResult?.Applied == true,
                "explicit retry did not restore normal saving");
        }
        finally { AppController.ConflictDialogDriver = savedDriver; }
        CheckUserExitIoFailure(controller, editor);
        // Finish the normal save explicitly before the rest of the fixture; no sleep/retry.
        editor.Text = "original";
        controller.SaveNow(sync: true);
        Console.WriteLine("PASS trusted policy capture, accurate live archive, rejected candidate, blocked overwrite/exit and explicit recovery");
    }
}
