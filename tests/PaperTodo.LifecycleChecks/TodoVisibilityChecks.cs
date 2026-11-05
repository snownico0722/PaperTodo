using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static class TodoVisibilityChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] LinkedIds = ["note-link", "script-link"];
    private static readonly string[] UnchangedIds = ["ordinary", "missing-link", "path-link", "self-link"];

    internal static async Task Run()
    {
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = false,
            UseCapsuleMode = true, UseDeepCapsuleMode = false,
            EnableTodoPaperLinks = true, HideLinkedPapersFromCapsules = true,
            CollapseExpandedDeepCapsuleOnClick = true,
            ShowLinkedPaperName = false, RunLinkedScriptCapsulesOnClick = true,
            ExperimentalEdgeCapsuleHoverPreview = false,
            UsePersistentPowerShellProcess = false, McpEnabled = false,
            FullscreenTopmostMode = FullscreenTopmostModes.StayOnTop,
            Theme = "light", PaperSkin = PaperSkins.Paper
        };
        var note = Paper("note", PaperTypes.Note);
        var script = Paper("script", PaperTypes.Note);
        script.Content = "!ps5\nWrite-Output 'This fixture never executes a script.'";
        var unlinked = Paper("unlinked", PaperTypes.Todo);
        unlinked.Items = [Item("plain-a"), Item("plain-b")];
        var linked = Paper("linked", PaperTypes.Todo);
        linked.Items = [
            Item("ordinary"), Item("missing-link", "missing-paper"), Item("path-link"),
            Item("note-link", note.Id), Item("script-link", script.Id), Item("self-link", linked.Id)
        ];
        linked.Items.Single(item => item.Id == "path-link").LinkPath(AppContext.BaseDirectory, isDirectory: true);
        state.Papers.AddRange([note, script, unlinked, linked]);
        foreach (var paper in state.Papers)
            for (var index = 0; index < paper.Items.Count; index++) paper.Items[index].Order = index;

        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Idle();
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        Require(windows.Count == 4, "Todo visibility fixture did not restore its windows");
        controller.ShowAllPapers();
        await Idle();
        var linkedWindow = windows[linked.Id];
        var plainWindow = windows[unlinked.Id];
        Require(controller.TryGetLinkedPaperTitle(note.Id, out _), "an empty Note title stopped being a valid link");
        Require(controller.State.Papers.Single(paper => paper.Id == linked.Id).Items
                    .Single(item => item.Id == "self-link").LinkedPaperId == null &&
                LinkButton(linkedWindow, "self-link") == null,
            "a persisted self-link was not normalized before the visibility refresh");
        Require(controller.ShouldRunLinkedScriptCapsule(script.Id), "script target was not recognized");
        Require(LinkText(linkedWindow, "script-link").Text == "⚡", "script button lost its run presentation");
        Require(LinkButton(linkedWindow, "missing-link") == null, "a missing target acquired a linked-paper button");

        var editor = Editors(linkedWindow)["ordinary"];
        editor.Text = "ordinary edit remains attached during bulk visibility changes";
        linkedWindow.Activate();
        editor.Focus();
        FocusManager.SetFocusedElement(linkedWindow, editor);
        editor.Select(3, 8);
        var selection = (editor.SelectionStart, editor.SelectionLength, editor.CaretIndex);
        var linkedBefore = Snapshot.Capture(linkedWindow);
        linkedWindow.RefreshLinkedPaperRowsForVisibility();
        await Idle();
        RequireReconcile(linkedWindow, linkedBefore, [], "direct visibility refresh");
        Require(ReferenceEquals(FocusManager.GetFocusedElement(linkedWindow), editor),
            "an ordinary editor lost logical focus during a linked-button refresh");
        RequireSelection(editor, selection, "direct visibility refresh");

        var plainBefore = Snapshot.Capture(plainWindow);
        linkedBefore = Snapshot.Capture(linkedWindow);
        controller.HideAllPapers();
        await Idle();
        Require(windows.Values.All(window => !window.HasVisibleSurface), "hide-all left a paper surface visible");
        RequireReconcile(plainWindow, plainBefore, [], "hide-all without links");
        RequireReconcile(linkedWindow, linkedBefore, [], "hide-all with links");
        RequireSelection(editor, selection, "hide-all");
        foreach (var id in LinkedIds) RequireLinkActivity(linkedWindow, id, active: false);

        plainBefore = Snapshot.Capture(plainWindow);
        linkedBefore = Snapshot.Capture(linkedWindow);
        controller.ShowAllPapers();
        await Idle();
        Require(windows.Values.All(window => window.HasExpandedPaperSurface), "show-all did not restore expanded paper surfaces");
        RequireReconcile(plainWindow, plainBefore, [], "show-all without links");
        RequireReconcile(linkedWindow, linkedBefore, [], "show-all with links");
        RequireSelection(editor, selection, "show-all");
        Require(editor.Text == controller.State.Papers.Single(paper => paper.Id == linked.Id).Items.Single(item => item.Id == "ordinary").Text,
            "visibility refresh lost the in-progress ordinary edit");
        foreach (var id in LinkedIds) RequireLinkActivity(linkedWindow, id, active: true);
        Require(LinkText(linkedWindow, "script-link").Text == "⚡", "bulk visibility refresh changed a script link into an ordinary link");

        var noteWindow = windows[note.Id];
        RequireNoteImagesSuspended(noteWindow, expected: false, "before linked-note hide");
        Click(LinkButton(linkedWindow, "note-link")!);
        await Idle();
        Require(!controller.IsLinkedPaperShown(note.Id), "a refreshed active link did not hide its target on click");
        RequireNoteImagesSuspended(noteWindow, expected: true, "after linked-note hide");
        RequireLinkActivity(linkedWindow, "note-link", active: false);
        Click(LinkButton(linkedWindow, "note-link")!);
        await Idle();
        Require(controller.IsLinkedPaperShown(note.Id), "a refreshed inactive link did not reopen its target on click");
        RequireNoteImagesSuspended(noteWindow, expected: false, "after linked-note reopen");
        RequireLinkActivity(linkedWindow, "note-link", active: true);

        var linkedEditor = Editors(linkedWindow)["note-link"];
        linkedWindow.Activate();
        linkedEditor.Focus();
        FocusManager.SetFocusedElement(linkedWindow, linkedEditor);
        var linkedText = linkedEditor.Text;
        linkedEditor.Select(linkedEditor.Text.Length, 0);
        linkedEditor.SelectedText = " preserved edit";
        linkedEditor.Select(2, 5);
        var linkedSelection = (linkedEditor.SelectionStart, linkedEditor.SelectionLength, linkedEditor.CaretIndex);
        Require(linkedEditor.CanUndo, "linked editor fixture has no native text history");
        linkedBefore = Snapshot.Capture(linkedWindow);
        linkedWindow.RefreshLinkedPaperRowsForVisibility();
        await Idle();
        RequireReconcile(linkedWindow, linkedBefore, [], "edited linked row visibility refresh");
        Require(ReferenceEquals(linkedEditor, Editors(linkedWindow)["note-link"]) &&
            ReferenceEquals(FocusManager.GetFocusedElement(linkedWindow), linkedEditor) && linkedEditor.CanUndo,
            "visibility-only refresh replaced a linked editor, lost focus or cleared native text history");
        RequireSelection(linkedEditor, linkedSelection, "linked visibility refresh");
        linkedEditor.Undo();
        Require(linkedEditor.Text == linkedText, "visibility refresh broke the linked editor's native undo");

        controller.State.RunLinkedScriptCapsulesOnClick = false;
        linkedWindow.RefreshLinkedPaperRowsForVisibility();
        await Idle();
        Require(LinkText(linkedWindow, "script-link").Text == "\uE71B", "disabled script execution retained the run-button presentation");

        controller.State.EnableTodoPaperLinks = false;
        linkedWindow.UpdateTodoLinkFeature();
        plainWindow.UpdateTodoLinkFeature();
        await Idle();
        Require(LinkedIds.All(id => LinkButton(linkedWindow, id) == null), "disabled paper links retained their buttons");
        Require(LinkButton(linkedWindow, "path-link") != null, "disabling paper links also removed the path button");
        plainBefore = Snapshot.Capture(plainWindow);
        linkedBefore = Snapshot.Capture(linkedWindow);
        controller.HideAllPapers();
        controller.ShowAllPapers();
        await Idle();
        RequireReconcile(plainWindow, plainBefore, [], "disabled links on ordinary Todo");
        RequireReconcile(linkedWindow, linkedBefore, [], "disabled links on linked Todo");

        controller.State.EnableTodoPaperLinks = true;
        controller.State.ShowTodoBottomBar = false;
        linkedWindow.UpdateTodoLinkFeature();
        plainWindow.UpdateTodoLinkFeature();
        await Idle();
        RequireNoAppendArea(linkedWindow);
        RequireNoAppendArea(plainWindow);
        controller.HideAllPapers();
        await Idle();
        RequireNoAppendArea(linkedWindow);
        RequireNoAppendArea(plainWindow);
        controller.ShowAllPapers();
        await Idle();
        RequireNoAppendArea(linkedWindow);
        RequireNoAppendArea(plainWindow);

        var deferredPaper = Paper("deferred", PaperTypes.Todo);
        deferredPaper.IsVisible = false;
        deferredPaper.Items = [Item("deferred-link", note.Id)];
        controller.State.Papers.Add(deferredPaper);
        var deferredWindow = (PaperWindow)typeof(AppController).GetMethod("GetOrCreatePaperWindow", Private)!
            .Invoke(controller, [deferredPaper, true])!;
        Require(!deferredWindow.IsShellBuilt && OptionalField(deferredWindow, "_todoPanel") == null,
            "the deferred fixture already built its Todo body");
        linkedBefore = Snapshot.Capture(linkedWindow);
        controller.HideAllPapers();
        await Idle();
        Require(!deferredWindow.IsShellBuilt && OptionalField(deferredWindow, "_todoPanel") == null,
            "hide-all built a deferred Todo body just to refresh linked-paper visibility");
        RequireReconcile(linkedWindow, linkedBefore, [], "hidden live Todo");
        RequireNoAppendArea(linkedWindow);
        foreach (var id in LinkedIds) RequireLinkActivity(linkedWindow, id, active: false);

        Console.WriteLine("PASS todo visibility: ordinary row/editor/selection retention; in-place linked-button updates; active buttons and clicks; script/path links and normalized invalid links; disabled links and bottom bar; hidden/deferred bodies");
    }

    private static PaperData Paper(string id, string type) => new()
    {
        Id = id, Type = type, IsVisible = true, IsCollapsed = false,
        X = SystemParameters.WorkArea.Left + 80, Y = SystemParameters.WorkArea.Top + 80,
        Width = 440, Height = 380
    };

    private static PaperItem Item(string id, string? linkedPaperId = null)
    {
        var item = new PaperItem { Id = id, Text = id + " editable text" };
        if (linkedPaperId != null) item.LinkPaper(linkedPaperId);
        return item;
    }

    private static Dictionary<string, TextBox> Editors(PaperWindow window)
    {
        var editors = (IDictionary)Field(window, "_todoEditors");
        return editors.Keys.Cast<string>().ToDictionary(id => id, id => (TextBox)editors[id]!);
    }

    private static Border? LinkButton(PaperWindow window, string itemId)
    {
        var row = ((IEnumerable)Field(window, "_todoRows")).Cast<Border>().Single(row => Equals(row.Tag, itemId));
        return ((Grid)row.Child).Children.OfType<Border>().SingleOrDefault(child => Grid.GetColumn(child) == 2);
    }

    private static TextBlock LinkText(PaperWindow window, string itemId) => (TextBlock)LinkButton(window, itemId)!.Child;

    private static void RequireLinkActivity(PaperWindow window, string itemId, bool active)
    {
        var button = LinkButton(window, itemId) ?? throw new InvalidOperationException("Missing linked-paper button: " + itemId);
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
        var opacity = ((TextBlock)button.Child).Opacity;
        Require(active ? opacity == 1 : opacity < 1, $"Linked-paper button cached the wrong activity state: {itemId}, expected {active}");
    }

    private static void Click(Border button) => button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
    {
        RoutedEvent = UIElement.MouseLeftButtonUpEvent
    });

    private static void RequireNoteImagesSuspended(PaperWindow window, bool expected, string stage)
    {
        var noteBox = OptionalField(window, "_noteBox");
        Require(noteBox != null && (bool)Field(noteBox, "_imageRenderingSuspended") == expected,
            "Linked Note image rendering state was not restored " + stage);
    }

    private static void RequireSelection(TextBox editor, (int Start, int Length, int Caret) expected, string operation) =>
        Require((editor.SelectionStart, editor.SelectionLength, editor.CaretIndex) == expected,
            operation + " moved the ordinary editor caret or selection");

    private static void RequireNoAppendArea(PaperWindow window) =>
        Require(OptionalField(window, "_appendArea") == null &&
            ((StackPanel)Field(window, "_todoPanel")).Children.Count == Snapshot.Capture(window).Rows.Count,
            "visibility refresh restored a disabled Todo bottom bar");

    private static void RequireReconcile(PaperWindow window, Snapshot previous, string[] rebuiltIds, string operation)
    {
        var current = Snapshot.Capture(window);
        Require(current.Generation - previous.Generation == (rebuiltIds.Length == 0 ? 0 : 1),
            operation + " did not use exactly one reconcile for the affected linked rows");
        Require(current.Rows.Keys.Order().SequenceEqual(previous.Rows.Keys.Order()), operation + " changed the Todo row set");
        foreach (var id in previous.Rows.Keys)
        {
            var retained = !rebuiltIds.Contains(id);
            Require(ReferenceEquals(previous.Rows[id], current.Rows[id]) == retained &&
                ReferenceEquals(previous.Editors[id], current.Editors[id]) == retained,
                operation + " rebuilt the wrong row/editor: " + id);
        }
        if (window.PaperId == "linked")
            Require(UnchangedIds.All(id => ReferenceEquals(previous.Rows[id], current.Rows[id])),
                operation + " replaced an ordinary, invalid-link, or path-link row");
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;
    private static object Field(object target, string name) => OptionalField(target, name)!;
    private static object? OptionalField(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Snapshot(int Generation, Dictionary<string, Border> Rows, Dictionary<string, TextBox> Editors)
    {
        internal static Snapshot Capture(PaperWindow window) => new(
            (int)Field(window, "_todoRowsGeneration"),
            ((IEnumerable)Field(window, "_todoRows")).Cast<Border>().ToDictionary(row => (string)row.Tag),
            TodoVisibilityChecks.Editors(window));
    }
}
