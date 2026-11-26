using System.Reflection;
using System.Windows;
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
        var oneSide = PaperBottomBarLayout.Plan(entries.Take(2).Select(i => i with { Side = PaperBottomBarSide.Left }).ToArray(), 45, 12);
        Require(oneSide.Left.Length == 0 && oneSide.Right.SequenceEqual(new[] { 1 }) && oneSide.Omitted,
            "one overflowing side must also align its retained items to the right");

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
            TextZoom = 0.9, Width = 340, Height = 230, X = 140, Y = 140,
            BottomBarPluginIds = ["checks.bottom-bar"]
        });
        state.Papers.Add(new PaperData
        {
            Id = "todo-bottom", Type = PaperTypes.Todo, Title = "Todo",
            Width = 340, Height = 230, X = 530, Y = 140,
            BottomBarPluginIds = ["checks.bottom-bar"]
        });
    }

    internal static void Run(AppController controller, Dictionary<string, PaperWindow> windows)
    {
        // Drive the same Native API a provider receives, with an isolated Runtime lifetime.
        using var api = new PaperPluginRuntimeBottomBarApi(controller, "checks.bottom-bar", () => true);
        var runtimes = (Dictionary<string, PaperPluginRuntimeBottomBarApi>)typeof(AppController)
            .GetField("_pluginBottomBars", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        runtimes.Add(api.ProviderId, api);
        Require(api.ListTargets().Select(p => p.PaperId).SequenceEqual(new[] { "note-bottom", "todo-bottom" }),
            "bindings must survive persistence and expose both paper types");

        api.SetItems("note-bottom", [new() { Id = "words", Text = "4 chars", Side = PaperBottomBarSide.Left }]);
        api.SetItems("todo-bottom", [new() { Id = "review", Text = "Review", Clickable = true }]);
        PaperActionInvocation? clicked = null;
        api.SetActionHandler(action => clicked = action);

        var note = windows["note-bottom"];
        var todo = windows["todo-bottom"];
        note.UpdateLayout();
        todo.UpdateLayout();

        var noteBar = FindBar(note);
        var todoBar = FindBar(todo);
        var zoom = VisibleItem(noteBar, "90%");
        var wordCount = VisibleItem(noteBar, "4 chars");
        var review = VisibleItem(todoBar, "Review");
        Require(noteBar.Margin.Bottom == 4 && noteBar.Margin.Right == 12 &&
            zoom.TranslatePoint(new Point(), noteBar).X > wordCount.TranslatePoint(new Point(), noteBar).X,
            "note must keep passive text to the left and zoom to the right");
        Require(!wordCount.IsHitTestVisible && review.IsHitTestVisible,
            "passive text should pass through pointer input");
        Click(review);
        Require(clicked?.ActionId == "review" && clicked.Paper.Id == "todo-bottom",
            "click should route to the originating Runtime and target paper");
        Click(zoom);
        Require(controller.State.Papers.Single(p => p.Id == "note-bottom").TextZoom == 1 &&
            !noteBar.Children.OfType<Border>().Any(b => (b.Child as TextBlock)?.Text == "90%"),
            "text zoom should reset and hide when clicked");

        var targetChanges = 0;
        api.TargetsChanged += () => targetChanges++;
        controller.TogglePaperBottomBar(controller.State.Papers.Single(p => p.Id == "todo-bottom"), api.ProviderId);
        Require(targetChanges == 1 && api.ListTargets().Single().PaperId == "note-bottom" &&
            !todoBar.Children.OfType<Border>().Any(b => (b.Child as TextBlock)?.Text == "Review"),
            "detaching one paper must only withdraw its own items");

        static Panel FindBar(PaperWindow window) =>
            (Panel)typeof(PaperWindow).GetField("_bottomBar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        static Border VisibleItem(Panel panel, string text) =>
            panel.Children.OfType<Border>().Single(b => b.IsVisible && (b.Child as TextBlock)?.Text == text);
        static void Click(Border border) =>
            border.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });

        Console.WriteLine("PASS note and todo bottom bars retain per-paper bindings and route input");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
