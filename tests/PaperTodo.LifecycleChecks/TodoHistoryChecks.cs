using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class TodoHistoryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run()
    {
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = false,
            UseCapsuleMode = false, UseDeepCapsuleMode = false,
            EnableTodoPaperLinks = true, HideLinkedPapersFromCapsules = false,
            ShowTodoBottomBar = false, AutoClearCompletedTodos = false,
            AutoMoveCompletedTodosToBottom = false,
            ExperimentalTodoReminders = true, ExperimentalEdgeCapsuleHoverPreview = false,
            UsePersistentPowerShellProcess = false, McpEnabled = false,
            FullscreenTopmostMode = FullscreenTopmostModes.StayOnTop,
            Theme = "light", PaperSkin = PaperSkins.Paper
        };
        var area = SystemParameters.WorkArea;
        state.Papers.Add(new PaperData
        {
            Id = "history", Type = PaperTypes.Todo,
            IsVisible = true, IsCollapsed = false,
            X = area.Left + 60, Y = area.Top + 60, Width = 520, Height = 390,
            Items = Items(12)
        });
        state.Papers.Add(new PaperData
        {
            Id = "link-target", Type = PaperTypes.Note, Content = "target",
            IsVisible = false, IsCollapsed = false,
            X = area.Left + 600, Y = area.Top + 60, Width = 300, Height = 240
        });
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Idle();
        var paper = controller.State.Papers.Single(value => value.Id == "history");
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        var window = windows[paper.Id];
        var panel = (StackPanel)Field(window, "_todoPanel");

        // Refresh the production list and check editing state, not its reconciliation strategy.
        foreach (var count in new[] { 1, 5, 10 })
        {
            await Reset(count);
            var focused = Editors(window)["row-0"];
            focused.Select(2, 4);
            var caret = (focused.SelectionStart, focused.SelectionLength, focused.CaretIndex);
            ReconcileRows(window);
            await Idle();
            RequirePresentation(window, paper, $"{count}-row unchanged refresh");
            focused = Editors(window)["row-0"];
            RequireSelection(focused, caret, $"{count}-row unchanged refresh");
            Require(focused.IsKeyboardFocused, "unchanged small-list refresh lost keyboard focus");
            controller.State.ShowTodoBottomBar = true;
            ReconcileRows(window);
            Require(panel.Children.Count == count + 1, "refresh failed to enable append area");
            controller.State.ShowTodoBottomBar = false;
            ReconcileRows(window);
            RequireNoAppendArea(window);
        }
        // Reordering/rebuilding another Todo must preserve the focused row's actual
        // native text history as well as its visible text, caret and selection.
        await Reset(6);
        var unaffected = Editors(window)["row-0"];
        var originalUnaffectedText = unaffected.Text;
        unaffected.Select(unaffected.Text.Length, 0);
        unaffected.SelectedText = " pending edit";
        Require(unaffected.CanUndo, "unrelated-row fixture did not record a native text edit");
        unaffected.Select(2, 4);
        var unaffectedSelection = (unaffected.SelectionStart, unaffected.SelectionLength, unaffected.CaretIndex);
        var changedRow = paper.Items[^1];
        paper.Items.RemoveAt(paper.Items.Count - 1);
        changedRow.Text += " refreshed";
        paper.Items.Insert(1, changedRow);
        ReconcileRows(window, [changedRow.Id]);
        await Idle();
        Require(paper.Items[1].Id == changedRow.Id, "reordering the changed Todo did not take effect");
        RequirePresentation(window, paper, "unrelated row moved and rebuilt");
        unaffected = Editors(window)["row-0"];
        Require(unaffected.IsKeyboardFocused && unaffected.CanUndo,
            "reordering an unrelated Todo lost the focused editor or its native undo history");
        RequireSelection(unaffected, unaffectedSelection, "unrelated row moved and rebuilt");
        unaffected.Undo();
        Require(unaffected.Text == originalUnaffectedText && paper.Items[0].Text == originalUnaffectedText,
            "the unrelated Todo refresh broke native undo or its live text binding");
        unaffected.Redo();
        Require(unaffected.Text == originalUnaffectedText + " pending edit" &&
                paper.Items[0].Text == unaffected.Text,
            "the unrelated Todo refresh broke native redo or its live text binding");
        await Reset(12);

        // Reordering and history must preserve the user's selection and keep live edits working.
        var originalOrder = paper.Items.Select(item => item.Id).ToArray();
        var move = typeof(PaperWindow).GetMethod("MoveItems", Private)!;
        var after = Enum.Parse(move.GetParameters()[2].ParameterType, "After");
        move.Invoke(window, [new[] { "row-0" }, "row-11", after, "row-0"]);
        await Idle();
        Require(paper.Items[^1].Id == "row-0", "drag did not move the row to the end");
        RequirePresentation(window, paper, "first-to-last drag");
        var editor = Editors(window)["row-0"];
        await Focus(window, editor);
        editor.Select(3, 6);
        var selection = (editor.SelectionStart, editor.SelectionLength, editor.CaretIndex);
        await SendKey(window, Key.Z);
        Require(paper.Items.Select(item => item.Id).SequenceEqual(originalOrder), "reorder undo did not restore list order");
        RequirePresentation(window, paper, "reorder undo");
        editor = Editors(window)["row-0"];
        RequireSelection(editor, selection, "reorder undo");
        Require(editor.IsKeyboardFocused, "reorder undo lost actual keyboard focus on the moved row");
        await SendKey(window, Key.Y);
        Require(paper.Items[^1].Id == "row-0", "reorder redo did not restore the moved row");
        RequirePresentation(window, paper, "reorder redo");
        editor = Editors(window)["row-0"];
        RequireSelection(editor, selection, "reorder redo");
        Require(editor.IsKeyboardFocused, "reorder redo lost actual keyboard focus on the moved row");
        RequireHistoryIsolation(window, paper);

        // Editor and checkbox handlers must keep writing to the current live items.
        editor.Select(editor.Text.Length, 0);
        editor.SelectedText = " live edit";
        Require(paper.Items.Single(item => item.Id == "row-0").Text == editor.Text,
            "editor wrote to a detached snapshot item");
        CheckBox(window, "row-1").IsChecked = true;
        Require(paper.Items.Single(item => item.Id == "row-1").Done,
            "checkbox wrote to a detached snapshot item");
        RequireHistoryIsolation(window, paper);

        // Group order and undo/redo do not depend on how many visual children were moved.
        await Reset(8);
        string[] groupOrder = ["row-2", "row-3", "row-4", "row-5", "row-6", "row-7", "row-0", "row-1"];
        move.Invoke(window, [new[] { "row-0", "row-1" }, "row-7", after, "row-0"]);
        await Idle();
        Require(paper.Items.Select(item => item.Id).SequenceEqual(groupOrder),
            "group drag did not retain the moved group's relative order");
        RequirePresentation(window, paper, "group drag");
        await SendKey(window, Key.Z);
        Require(paper.Items.Select(item => item.Id).SequenceEqual(Items(8).Select(item => item.Id)),
            "group undo did not restore the original order");
        RequirePresentation(window, paper, "group undo");
        await SendKey(window, Key.Y);
        Require(paper.Items.Select(item => item.Id).SequenceEqual(groupOrder),
            "group redo did not restore the moved group's order");
        RequirePresentation(window, paper, "group redo");

        // Arbitrary target orders exercise reconciliation independently of drag setup.
        await Reset(6);
        ReconcileRows(window);
        RequirePresentation(window, paper, "unchanged order");
        Invoke(window, "PushUndoSnapshot");
        paper.Items.Reverse();
        ReconcileRows(window);
        await Idle();
        Require(paper.Items.Select(item => item.Id).SequenceEqual(Items(6).Select(item => item.Id).Reverse()),
            "reconciliation did not preserve the requested reverse order");
        RequirePresentation(window, paper, "reversal");
        await Focus(window, Editors(window)["row-0"]);
        await SendKey(window, Key.Z);
        Require(paper.Items.Select(item => item.Id).SequenceEqual(Items(6).Select(item => item.Id)),
            "reversal undo did not restore the original order");
        RequirePresentation(window, paper, "reversal undo");

        // Mixed removals, a changed row and an insertion keep the current presentation in sync.
        await Reset(8);
        controller.State.ShowTodoBottomBar = true;
        Invoke(window, "SyncTodoAppendArea");
        await Idle();
        var oldItems = paper.Items.ToArray();
        Invoke(window, "PushUndoSnapshot");
        var changedItem = TodoRules.Clone(oldItems[2]);
        changedItem.Text += " rebuilt";
        paper.Items = [oldItems[6], new PaperItem { Id = "inserted", Text = "inserted row" }, oldItems[0], changedItem, oldItems[3], oldItems[7], oldItems[4]];
        ReconcileRows(window, ["row-2"]);
        await Idle();
        RequirePresentation(window, paper, "mixed reconciliation", appendArea: true);
        await Focus(window, Editors(window)["row-6"]);
        await SendKey(window, Key.Z);
        Require(paper.Items.Select(item => item.Id).SequenceEqual(Items(8).Select(item => item.Id)),
            "mixed undo did not restore removed rows and their order");
        RequirePresentation(window, paper, "mixed undo", appendArea: true);
        await SendKey(window, Key.Y);
        Require(paper.Items.Select(item => item.Id).SequenceEqual(["row-6", "inserted", "row-0", "row-2", "row-3", "row-7", "row-4"]),
            "mixed redo did not restore the inserted row and target order");
        RequirePresentation(window, paper, "mixed redo", appendArea: true);
        RequireHistoryIsolation(window, paper);

        // Native text undo has priority before list history. A list history boundary then
        // clears native history so old editor changes cannot be replayed against a new model.
        await Reset(4);
        editor = Editors(window)["row-0"];
        await Focus(window, editor);
        var originalText = editor.Text;
        editor.Select(editor.Text.Length, 0);
        editor.SelectedText = " human";
        Require(editor.CanUndo, "native text undo fixture did not record an edit");
        await SendKey(window, Key.Z, expectPaperHistory: false);
        Require(editor.Text == originalText && History(window, "_undoStack").Count == 0,
            "Ctrl+Z replayed list history ahead of native text undo");
        await SendKey(window, Key.Y, expectPaperHistory: false);
        Require(editor.Text == originalText + " human" && History(window, "_undoStack").Count == 0,
            "Ctrl+Y replayed list history ahead of native text redo");
        await Focus(window, Editors(window)["row-1"]);
        CheckBox(window, "row-1").IsChecked = true;
        Require(History(window, "_undoStack").Count == 2, "manual edit and checkbox did not form two history steps");
        await SendKey(window, Key.Z);
        RequirePresentation(window, paper, "checkbox undo");
        editor = Editors(window)["row-0"];
        Require(!editor.CanUndo && !editor.CanRedo, "list undo left stale native editor history on a live row");
        Require(!paper.Items[1].Done && paper.Items[0].Text == originalText + " human",
            "checkbox undo also replayed the earlier human edit");
        await SendKey(window, Key.Z);
        Require(paper.Items[0].Text == originalText, "second list undo did not restore the earlier human edit");
        await SendKey(window, Key.Y);
        await SendKey(window, Key.Y);
        Require(paper.Items[1].Done && paper.Items[0].Text == originalText + " human",
            "redo did not preserve human edit then checkbox ordering");
        RequireHistoryIsolation(window, paper);

        // Every persisted row field must survive undo/redo. The offset case deliberately
        // has the same UTC instant, but a different persisted DateTimeOffset.
        var reminder = DateTimeOffset.UtcNow.AddDays(30);
        (string Name, Action<PaperItem>? Prepare, Action<PaperItem> Change)[] fields =
        [
            ("text", null, item => item.Text += " changed"),
            ("done", null, item => item.Done = true),
            ("linked paper", null, item => item.LinkPaper("link-target")),
            ("linked path", null, item => item.LinkPath(AppContext.BaseDirectory, true)),
            ("path kind", item => item.LinkPath(AppContext.BaseDirectory, true), item => item.LinkPath(AppContext.BaseDirectory, false)),
            ("reminder time", null, item => item.ReminderAt = reminder),
            ("reminder offset", item => item.ReminderAt = reminder, item => item.ReminderAt = reminder.ToOffset(TimeSpan.FromHours(8))),
            ("reminder delivered", item => item.ReminderAt = reminder, item => item.ReminderTriggered = true)
        ];
        foreach (var (name, prepare, change) in fields)
        {
            await Reset(4, prepare);
            var previous = TodoRules.Clone(paper.Items[0]);
            Invoke(window, "PushUndoSnapshot");
            change(paper.Items[0]);
            var changed = TodoRules.Clone(paper.Items[0]);
            window.RefreshTodoRowsForExternalChange();
            await Idle();
            await Focus(window, Editors(window)["row-1"]);
            await SendKey(window, Key.Z);
            RequirePresentation(window, paper, name + " undo");
            RequireItem(paper.Items[0], previous, name + " undo model");
            await SendKey(window, Key.Y);
            RequirePresentation(window, paper, name + " redo");
            RequireItem(paper.Items[0], changed, name + " redo model");
            RequireHistoryIsolation(window, paper);
        }

        // A successful external mutation orders a pending human edit before the external step.
        await Reset(4);
        editor = Editors(window)["row-0"];
        await Focus(window, editor);
        originalText = editor.Text;
        editor.Select(editor.Text.Length, 0);
        editor.SelectedText = " pending human";
        var externalOriginal = paper.Items[1].Text;
        controller.PaperCommands.UpdateTodo(new UpdateTodoRequest
        {
            PaperId = paper.Id, TodoId = "row-1", Text = "external change"
        }, PaperOperationContext.Mcp());
        await Idle();
        Require(History(window, "_undoStack").Count == 2, "external write did not separate the pending human edit");
        await SendKey(window, Key.Z);
        Require(paper.Items[1].Text == externalOriginal && paper.Items[0].Text == originalText + " pending human",
            "external undo also removed the preceding human edit");
        await SendKey(window, Key.Z);
        Require(paper.Items[0].Text == originalText, "human edit was not the second undo after an external write");
        await SendKey(window, Key.Y);
        await SendKey(window, Key.Y);
        Require(paper.Items[0].Text == originalText + " pending human" && paper.Items[1].Text == "external change",
            "external redo order or current model was lost");
        RequireHistoryIsolation(window, paper);

        // The arrow only appears above four *visual* lines; folding leaves one editable text value.
        await Reset(4, item => item.Text = string.Join(Environment.NewLine,
            Enumerable.Range(1, 5).Select(index => "line " + index)));
        editor = Editors(window)["row-0"];
        window.UpdateLayout();
        await Idle();
        var longText = editor.Text;
        var foldButton = FoldButton(window, "row-0");
        Require(editor.LineCount == 5 && foldButton.Visibility == Visibility.Visible &&
                editor.MaxLines == int.MaxValue, "five-line todo did not offer an initially expanded fold control");
        Keyboard.ClearFocus();
        await Idle();
        ClickFold(foldButton);
        await Idle();
        Require(editor.MaxLines == 2 && editor.LineCount == 5 && editor.Text == longText &&
                paper.Items[0].Text == longText && paper.Items.Count == 4,
            "folding changed the model or failed to limit the visible lines");
        await Focus(window, editor);
        Require(editor.MaxLines == int.MaxValue, "focusing a folded todo did not expand it for editing");
        Keyboard.ClearFocus();
        await Idle();
        Require(editor.MaxLines == 2, "leaving a folded todo did not restore two visible lines");
        ReconcileRows(window, ["row-0"]);
        await Idle();
        editor = Editors(window)["row-0"];
        foldButton = FoldButton(window, "row-0");
        Require(editor.MaxLines == 2 && foldButton.Visibility == Visibility.Visible &&
                editor.Text == longText, "rebuilt todo row lost its folded display state");
        ClickFold(foldButton);
        await Idle();
        Require(editor.MaxLines == int.MaxValue && editor.Text == longText,
            "expanding a folded todo did not restore the original editor content");
        editor.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 4).Select(i => "line " + i));
        window.UpdateLayout();
        await Idle();
        Require(editor.LineCount == 4 && foldButton.Visibility == Visibility.Collapsed,
            "four-line todo incorrectly retained the fold button");

        // Wrapping and paper width also change eligibility without editing the text.
        editor.Text = new string('W', 70);
        editor.Width = 80;
        window.UpdateLayout();
        await Idle();
        Require(editor.LineCount > 4 && foldButton.Visibility == Visibility.Visible,
            "narrow wrapped todo did not offer folding");
        Keyboard.ClearFocus();
        ClickFold(foldButton);
        await Idle();
        Require(editor.MaxLines == 2, "wrapped todo did not fold");
        editor.Width = 450;
        window.UpdateLayout();
        await Idle();
        Require(editor.LineCount <= 4 && foldButton.Visibility == Visibility.Collapsed &&
                editor.MaxLines == int.MaxValue, "widening a folded todo failed to remove an unnecessary fold");

        // Shift+Enter edits one todo, respects the selected text and the input limit,
        // and stays within the native text undo/redo history.
        await Reset(4);
        editor = Editors(window)["row-0"];
        var originalLine = editor.Text;
        editor.Select(4, 0);
        await SendKey(window, Key.Enter, expectPaperHistory: false, control: false, shift: true);
        var multiline = originalLine.Insert(4, Environment.NewLine);
        Require(paper.Items.Count == 4 && editor.Text == multiline && paper.Items[0].Text == multiline &&
                editor.CaretIndex == 4 + Environment.NewLine.Length,
            "Shift+Enter did not insert a line break inside the current todo");
        Require(editor.CanUndo && History(window, "_undoStack").Count == 0,
            "Shift+Enter bypassed native text history");
        await SendKey(window, Key.Z, expectPaperHistory: false);
        Require(editor.Text == originalLine && paper.Items[0].Text == originalLine,
            "native undo did not remove the inserted line break");
        await SendKey(window, Key.Y, expectPaperHistory: false);
        Require(editor.Text == multiline && paper.Items[0].Text == multiline,
            "native redo did not restore the inserted line break");
        editor.MaxLength = editor.Text.Length;
        editor.Select(2, 0);
        await SendKey(window, Key.Enter, expectPaperHistory: false, control: false, shift: true);
        Require(editor.Text == multiline && paper.Items.Count == 4,
            "Shift+Enter exceeded the todo text length limit");
        editor.Select(2, 2);
        await SendKey(window, Key.Enter, expectPaperHistory: false, control: false, shift: true);
        Require(editor.Text == multiline.Remove(2, 2).Insert(2, Environment.NewLine) && paper.Items.Count == 4,
            "Shift+Enter did not replace a selection at the text length limit");

        // Enter insertion, last-row deletion and the disabled append area retain their contracts.
        await Reset(4);
        await Focus(window, Editors(window)["row-0"]);
        await SendKey(window, Key.Enter, expectPaperHistory: false, control: false);
        var insertedId = paper.Items[1].Id;
        Require(paper.Items.Count == 5 && Editors(window)[insertedId].IsKeyboardFocused, "Enter did not create and focus a new row");
        await SendKey(window, Key.Z);
        Require(paper.Items.Count == 4 && paper.Items.All(item => item.Id != insertedId), "insert undo retained the added row");
        RequirePresentation(window, paper, "insert undo");
        await Focus(window, Editors(window)["row-0"]);
        await SendKey(window, Key.Y);
        Require(paper.Items.Count == 5 && paper.Items[1].Id == insertedId, "insert redo changed the new row identity");
        await Reset(1);
        Invoke(window, "RemoveItem", paper.Items[0], true, null, true);
        await Idle();
        var placeholderId = paper.Items[0].Id;
        Require(placeholderId != "row-0" && TodoRules.IsPlaceholder(paper.Items[0]), "last-row deletion did not create a placeholder");
        await Focus(window, Editors(window)[placeholderId]);
        await SendKey(window, Key.Z);
        Require(paper.Items.Count == 1 && paper.Items[0].Id == "row-0", "last-row undo did not restore the original item");
        await Focus(window, Editors(window)["row-0"]);
        await SendKey(window, Key.Y);
        Require(paper.Items.Count == 1 && paper.Items[0].Id == placeholderId, "last-row redo changed the replacement identity");
        RequireNoAppendArea(window);

        // Interrupt the actual deletion animation before its asynchronous completion. Reflection
        // is used only here to make that timing deterministic; keyboard routing is covered above.
        await Reset(4);
        controller.State.EnableAnimations = true;
        Invoke(window, "RemoveItem", paper.Items[0], true, null, true);
        Require(Rows(window).ContainsKey("row-0") && !Rows(window)["row-0"].IsHitTestVisible,
            "deletion fixture did not retain its animating old row");
        Invoke(window, "Undo");
        await Task.Delay(350);
        await Idle();
        Require(paper.Items.Count == 4 && Rows(window)["row-0"].IsHitTestVisible,
            "a late deletion callback removed the restored history row");
        Require(Rows(window)["row-0"].Opacity > .99 &&
                !Rows(window)["row-0"].HasAnimatedProperties,
            "restored row retained its obsolete deletion animation");
        RequireNoAppendArea(window);
        RequireHistoryIsolation(window, paper);
        Console.WriteLine("PASS Todo history: native Ctrl+Z/Y, row order and presentation, multiline folding and focus, width changes, unrelated editor undo/redo and selection, all row fields, external ordering, insertion/deletion and animation interruption");

        async Task Reset(int count, Action<PaperItem>? prepare = null)
        {
            controller.State.EnableAnimations = false;
            controller.State.ShowTodoBottomBar = false;
            SetField(window, "_activeOriginalItemId", null);
            SetField(window, "_activeOriginalText", null);
            paper.Items = Items(count);
            prepare?.Invoke(paper.Items[0]);
            History(window, "_undoStack").Clear();
            History(window, "_redoStack").Clear();
            window.RefreshTodoRowsForExternalChange();
            await Idle();
            await Focus(window, Editors(window)["row-0"]);
            RequireNoAppendArea(window);
        }
    }

    private static List<PaperItem> Items(int count) => Enumerable.Range(0, count)
        .Select(index => new PaperItem { Id = "row-" + index, Order = index, Text = "Todo history row " + index })
        .ToList();

    private static async Task Focus(PaperWindow window, TodoTextBox editor)
    {
        LifecycleInput.Focus(window, editor);
        await Idle();
        Require(editor.IsKeyboardFocused, "fixture could not give the live Todo editor keyboard focus");
    }

    private static async Task SendKey(PaperWindow window, Key key, bool expectPaperHistory = true, bool control = true, bool shift = false)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = false;
        var handledAtWindow = false;
        KeyEventHandler down = (_, e) =>
        {
            if (e.Key != key) return;
            observed = true;
            handledAtWindow = e.Handled;
        };
        KeyEventHandler up = (_, e) => { if (e.Key == key && observed) completed.TrySetResult(true); };
        window.AddHandler(Keyboard.PreviewKeyDownEvent, down, handledEventsToo: true);
        window.AddHandler(Keyboard.PreviewKeyUpEvent, up, handledEventsToo: true);
        try
        {
            var virtualKey = (ushort)KeyInterop.VirtualKeyFromKey(key);
            LifecycleInput.KeyChord(control ? [0x11, virtualKey] : shift ? [0x10, virtualKey] : [virtualKey]);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Idle();
            Require(observed, "native keyboard input was not received by the PaperWindow");
            if (expectPaperHistory)
                Require(handledAtWindow, "native Ctrl+Z/Y did not enter the paper-level history handler");
        }
        finally
        {
            window.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
            window.RemoveHandler(Keyboard.PreviewKeyUpEvent, up);
        }
    }

    private static Border FoldButton(PaperWindow window, string id)
    {
        var grid = (Grid)Rows(window)[id].Child;
        var trailing = grid.Children.OfType<Grid>()
            .Single(child => Grid.GetColumn(child) == grid.ColumnDefinitions.Count - 1);
        return trailing.Children.OfType<Border>()
            .Single(child => Grid.GetRow(child) == 0);
    }

    private static void ClickFold(Border button)
    {
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent
        });
    }

    private static void RequirePresentation(PaperWindow window, PaperData paper, string operation,
        bool appendArea = false)
    {
        var rows = Rows(window);
        var editors = Editors(window);
        Require(rows.Count == paper.Items.Count && editors.Count == paper.Items.Count,
            operation + " left missing or stale row/editor registrations");
        foreach (var item in paper.Items)
        {
            Require(editors[item.Id].Text == item.Text && editors[item.Id].IsDone == item.Done &&
                    CheckBox(window, item.Id).IsChecked == item.Done,
                operation + " left stale text or completion presentation for " + item.Id);
        }
        Require(paper.Items.Select(item => item.Id).SequenceEqual(((List<Border>)Field(window, "_todoRows")).Select(row => (string)row.Tag)),
            operation + " did not synchronize visual row order");
        Require(paper.Items.Select(item => item.Id).SequenceEqual(((StackPanel)Field(window, "_todoPanel")).Children
                .OfType<Border>().Where(row => row.Tag is string).Select(row => (string)row.Tag)),
            operation + " did not synchronize the actual panel child order");
        Require(paper.Items.Select(item => item.Order).SequenceEqual(Enumerable.Range(0, paper.Items.Count)),
            operation + " did not normalize persisted order");
        if (appendArea)
        {
            var panel = (StackPanel)Field(window, "_todoPanel");
            Require(panel.Children.Count == rows.Count + 1 &&
                    ReferenceEquals(panel.Children[panel.Children.Count - 1], Field(window, "_appendArea")),
                operation + " did not preserve the bottom append area");
        }
        else RequireNoAppendArea(window);
    }

    private static void RequireItem(PaperItem actual, PaperItem expected, string operation) => Require(
        actual.Id == expected.Id && actual.Text == expected.Text && actual.Done == expected.Done && actual.Order == expected.Order &&
        actual.LinkedPaperId == expected.LinkedPaperId && actual.LinkedPath == expected.LinkedPath &&
        actual.LinkedPathIsDirectory == expected.LinkedPathIsDirectory && actual.ReminderAt == expected.ReminderAt &&
        actual.ReminderAt?.Offset == expected.ReminderAt?.Offset && actual.ReminderTriggered == expected.ReminderTriggered, operation);

    private static void RequireHistoryIsolation(PaperWindow window, PaperData paper) => Require(
        History(window, "_undoStack").Concat(History(window, "_redoStack")).SelectMany(items => items)
            .All(saved => paper.Items.All(current => !ReferenceEquals(saved, current))),
        "a retained live model became shared with a saved history snapshot");

    private static void RequireSelection(TodoTextBox editor, (int Start, int Length, int Caret) expected, string operation) => Require(
        (editor.SelectionStart, editor.SelectionLength, editor.CaretIndex) == expected, operation + " changed the text selection");

    private static void RequireNoAppendArea(PaperWindow window) => Require(
        typeof(PaperWindow).GetField("_appendArea", Private)!.GetValue(window) == null &&
        ((StackPanel)Field(window, "_todoPanel")).Children.Count == Rows(window).Count,
        "history replay restored a disabled bottom append area");

    private static void ReconcileRows(PaperWindow window, IEnumerable<string>? rebuildIds = null)
    {
        var method = typeof(PaperWindow).GetMethod("ReconcileTodoRows", Private)!;
        var placement = Enum.Parse(method.GetParameters()[2].ParameterType, "End");
        method.Invoke(window, [rebuildIds, null, placement]);
    }

    private static CheckBox CheckBox(PaperWindow window, string id) => ((Grid)Rows(window)[id].Child).Children.OfType<CheckBox>().Single();
    private static Dictionary<string, TodoTextBox> Editors(PaperWindow window) => (Dictionary<string, TodoTextBox>)Field(window, "_todoEditors");
    private static Dictionary<string, Border> Rows(PaperWindow window) => ((List<Border>)Field(window, "_todoRows")).ToDictionary(row => (string)row.Tag);
    private static List<List<PaperItem>> History(PaperWindow window, string name) => (List<List<PaperItem>>)Field(window, name);
    private static object Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
