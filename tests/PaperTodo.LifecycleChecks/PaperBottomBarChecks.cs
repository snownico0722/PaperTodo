using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PaperTodo;
using PaperTodo.Plugin;

internal static class PaperBottomBarChecks
{
    internal static void CheckLayout()
    {
        PaperBottomBarLayoutItem[] entries =
        [
            new(PaperBottomBarSide.Left, 60, 5, 25),
            new(PaperBottomBarSide.Right, 999, 999, 30),
            new(PaperBottomBarSide.Left, 70, 40, 30),
            new(PaperBottomBarSide.Right, 20, 35, 25)
        ];
        var wide = PaperBottomBarLayout.Plan(entries, 130, 12);
        Require(wide.Left.SequenceEqual(new[] { 2, 0 }) && wide.Right.SequenceEqual(new[] { 1, 3 }) && !wide.Omitted,
            "split groups must both order from the outside in");
        var merged = PaperBottomBarLayout.Plan(entries, 118, 12);
        Require(merged.Left.Length == 0 && merged.Right.SequenceEqual(new[] { 1, 2, 0, 3 }) && !merged.Omitted,
            "both sides must merge before any item is omitted");
        var narrow = PaperBottomBarLayout.Plan(entries, 77, 12);
        Require(narrow.Left.Length == 0 && narrow.Right.SequenceEqual(new[] { 1, 2 }) && narrow.Omitted,
            "omission must use global priority independently of order or side");
        foreach (var side in new[] { PaperBottomBarSide.Left, PaperBottomBarSide.Right })
        {
            var items = entries.Take(2).Select(i => i with { Side = side }).ToArray();
            var exact = PaperBottomBarLayout.Plan(items, 57, 12);
            Require((side == PaperBottomBarSide.Left ? exact.Left : exact.Right).SequenceEqual(new[] { 1, 0 }) && !exact.Omitted,
                "one side at its exact width must keep its alignment without an ellipsis");
            var overflow = PaperBottomBarLayout.Plan(items, 44, 12);
            Require(overflow.Left.Length == 0 && overflow.Right.SequenceEqual(new[] { 1 }) && overflow.Omitted,
                "one overflowing side must reserve the ellipsis and right-align the highest-priority item");
            var ellipsisOnly = PaperBottomBarLayout.Plan(items, 12, 12);
            var noRoom = PaperBottomBarLayout.Plan(items, 11, 12);
            Require(ellipsisOnly.Left.Length + ellipsisOnly.Right.Length == 0 && ellipsisOnly.Omitted &&
                noRoom.Left.Length + noRoom.Right.Length == 0 && !noRoom.Omitted,
                "an overflowing side must not draw outside the available width, even for the ellipsis");
        }

        PaperBottomBarLayoutItem[] tied =
        [
            new(PaperBottomBarSide.Left, 1, 1, 20),
            new(PaperBottomBarSide.Right, 1, 1, 20)
        ];
        var tie = PaperBottomBarLayout.Plan(tied, 34, 12);
        Require(tie.Right.SequenceEqual(new[] { 0 }) && tie.Omitted,
            "equal priorities must preserve stable item order");
    }

    internal static void Prepare(AppState state)
    {
        state.Papers.Clear();
        state.Papers.Add(new PaperData
        {
            Id = "note-bottom", Type = PaperTypes.Note, Title = "Note",
            TextZoom = 0.9, Width = 340, Height = 230, X = 140, Y = 140
        });
        state.Papers.Add(new PaperData
        {
            Id = "todo-bottom", Type = PaperTypes.Todo, Title = "Todo",
            Width = 340, Height = 230, X = 530, Y = 140
        });
    }

    internal static void Run(AppController controller, Dictionary<string, PaperWindow> windows)
    {
        // Drive the same Native API a provider receives, with an isolated Runtime lifetime.
        using var api = new PaperPluginRuntimeBottomBarApi(controller, "checks.bottom-bar", [PaperTypes.Todo, PaperTypes.Note], () => true);
        var runtimes = (Dictionary<string, PaperPluginRuntimeBottomBarApi>)typeof(AppController)
            .GetField("_pluginBottomBars", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        runtimes.Add(api.ProviderId, api);
        using var second = new PaperPluginRuntimeBottomBarApi(controller, "checks.second-bar", [PaperTypes.Todo], () => true);
        runtimes.Add(second.ProviderId, second);
        Require(api.ListTargets().Select(p => p.PaperId).SequenceEqual(new[] { "note-bottom", "todo-bottom" }),
            "a both-types plugin must see existing notes and todos without bindings");
        Require(second.ListTargets().Single().PaperId == "todo-bottom", "a todo-only plugin must not list notes");
        try
        {
            second.SetItems("note-bottom", []);
            throw new InvalidOperationException("a todo-only plugin was allowed to publish on a note");
        }
        catch (PaperTodoPluginException ex) when (ex.Code == "bottom_bar_target_unavailable") { }

        api.SetItems("note-bottom", [new() { Id = "words", Text = "4 chars", ToolTip = "ignored", Side = PaperBottomBarSide.Left }]);
        api.SetItems("todo-bottom", [new() { Id = "review", Text = "Review", ToolTip = "Open review", Clickable = true }]);
        second.SetItems("todo-bottom", [new() { Id = "review", Text = "Other", Clickable = true }]);
        PaperActionInvocation? otherClick = null;
        second.SetActionHandler(action => otherClick = action);
        PaperActionInvocation? clicked = null;
        api.SetActionHandler(action => clicked = action);

        var note = windows["note-bottom"];
        var todo = windows["todo-bottom"];
        note.UpdateLayout();
        todo.UpdateLayout();

        var noteBar = FindBar(note);
        var todoBar = FindBar(todo);
        var zoom = VisibleButton(noteBar, "90%");
        var wordCount = noteBar.Children.OfType<Border>().Single(b => b.IsVisible && (b.Child as TextBlock)?.Text == "4 chars");
        var review = VisibleButton(todoBar, "Review");
        Require(noteBar.Margin.Bottom == 4 && noteBar.Margin.Right == 12 &&
            zoom.TranslatePoint(new Point(), noteBar).X > wordCount.TranslatePoint(new Point(), noteBar).X,
            "note must keep passive text to the left and zoom to the right");
        Require(!wordCount.IsHitTestVisible && !wordCount.Focusable && wordCount.ToolTip == null &&
            api.GetItems("note-bottom").Single().ToolTip == string.Empty,
            "a passive label must pass through pointer input and never expose a tooltip");
        Require(review.IsHitTestVisible && review.Focusable && review.IsTabStop && (string?)review.ToolTip == "Open review",
            "a clickable item must be a keyboard-accessible button with its own tooltip");
        var other = VisibleButton(todoBar, "Other");
        var originalContent = review.Content;
        todo.Activate();
        Keyboard.Focus(review);
        Require(review.IsKeyboardFocused, "the review button must acquire keyboard focus");
        api.SetItems("todo-bottom", [new() { Id = "review", Text = "Review 2", ToolTip = "Updated", Clickable = true }]);
        todo.UpdateLayout();
        Require(ReferenceEquals(review, VisibleButton(todoBar, "Review 2")) && ReferenceEquals(originalContent, review.Content) &&
            review.IsKeyboardFocused && (string?)review.ToolTip == "Updated" && AutomationProperties.GetName(review) == "Review 2",
            "a content refresh must update the same focused button and its accessible name in place");
        PressSpace(review);
        Require(clicked?.ActionId == "review" && clicked.Paper.Id == "todo-bottom",
            "click should route to the originating Runtime and target paper");
        zoom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, zoom));
        Require(controller.State.Papers.Single(p => p.Id == "note-bottom").TextZoom == 1 &&
            !noteBar.Children.OfType<Button>().Any(b => (b.Content as TextBlock)?.Text == "90%"),
            "text zoom should reset and hide when clicked");

        var targetChanges = 0;
        api.TargetsChanged += () => targetChanges++;
        var added = new PaperData { Id = "added", Type = PaperTypes.Note };
        controller.State.Papers.Add(added);
        controller.ReconcilePluginRuntimes();
        Require(targetChanges == 1 && api.ListTargets().Contains(new(added.Id, PaperTypes.Note)) &&
            api.GetItems(added.Id).Count == 0, "new eligible papers are targets, not mandatory contributions");
        api.SetItems(added.Id, [new() { Id = "sample", Text = "Note" }]);
        added.Type = PaperTypes.Todo;
        controller.ReconcilePluginRuntimes();
        Require(targetChanges == 2 && api.GetItems(added.Id).Count == 0 &&
            second.ListTargets().Any(p => p.PaperId == added.Id), "type changes must invalidate old contributions");
        api.SetItems(added.Id, [new() { Id = "sample", Text = "Todo" }]);
        controller.State.Papers.Remove(added);
        controller.ReconcilePluginRuntimes();
        Require(targetChanges == 3 && api.GetItems(added.Id).Count == 0, "deleted targets must release their items");

        api.Dispose();
        Require(review.Parent == null && wordCount.Parent == null &&
            ReferenceEquals(other, VisibleButton(todoBar, "Other")),
            "ending a provider must remove its items from all papers without touching another provider's same ID");
        clicked = null;
        review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, review));
        PressSpace(other);
        Require(clicked == null && otherClick?.Paper.Id == "todo-bottom" && otherClick.ActionId == "review",
            "detached actions must stop routing while the other provider remains usable");

        second.SetItems("todo-bottom", [new() { Id = "review", Text = "Read only", ToolTip = "ignored" }]);
        todo.UpdateLayout();
        var passive = todoBar.Children.OfType<Border>().Single(b => (b.Child as TextBlock)?.Text == "Read only");
        Require(other.Parent == null && !passive.IsHitTestVisible && passive.ToolTip == null,
            "switching an existing ID to passive must remove button behavior and the tooltip");

        static Panel FindBar(PaperWindow window) =>
            (Panel)typeof(PaperWindow).GetField("_bottomBar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        static Button VisibleButton(Panel panel, string text) =>
            panel.Children.OfType<Button>().Single(b => b.IsVisible && (b.Content as TextBlock)?.Text == text);
        static void PressSpace(Button button)
        {
            var source = PresentationSource.FromVisual(button)!;
            foreach (var routedEvent in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
                button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                {
                    RoutedEvent = routedEvent
                });
        }

        Console.WriteLine("PASS note and todo bottom bars apply global type declarations and route input");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
