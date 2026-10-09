using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    // Exercise the real PaperWindow checkbox handler and WPF ScrollViewer, not a duplicate
    // of the ordering algorithm. All test data lives in the test executable's output folder.
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Run();
                result = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL #331 Todo scroll: " + error);
            }
            finally
            {
                app.Shutdown();
            }
        });
        app.Run();
        return result;
    }

    private static async Task Run()
    {
        using var controller = new AppController();
        controller.State.EnableAnimations = false;
        controller.State.UseCapsuleMode = false;
        controller.State.UseDeepCapsuleMode = false;
        controller.State.AutoClearCompletedTodos = false;
        controller.State.ShowTodoBottomBar = true;

        var area = SystemParameters.WorkArea;
        var paper = new PaperData
        {
            Id = "issue-331-fixture",
            Type = PaperTypes.Todo,
            IsVisible = true,
            IsCollapsed = false,
            X = area.Left + 30,
            Y = area.Top + 30,
            Width = 500,
            Height = 340,
            Items = Enumerable.Range(0, 80)
                .Select(i => new PaperItem
                {
                    Id = "row-" + i,
                    Order = i,
                    Text = "Issue 331 Todo #" + i,
                    Done = i >= 40
                }).ToList()
        };
        controller.State.Papers.Add(paper);
        var window = new PaperWindow(paper, controller);
        try
        {
            window.Show();
            await Idle();
            window.UpdateLayout();

            var panel = Field<StackPanel>(window, "_todoPanel");
            var scroll = FindScrollHost(panel);
            Require(scroll.ViewportHeight > 80 && scroll.ScrollableHeight > 600,
                $"The real WPF Todo list was not scrollable: viewport={scroll.ViewportHeight:F1}, extent={scroll.ExtentHeight:F1}");

            // Negative control: ordinary completion without automatic reordering must not
            // move the viewport. Restore that checkbox before the second scenario.
            controller.State.AutoMoveCompletedTodosToBottom = false;
            await Check(window, panel, scroll, paper, "row-18", expectMove: false);
            CheckBox(window, "row-18").IsChecked = false;
            await Idle();

            // Original report: many Todo rows and many completed rows, with the user editing
            // the row that is about to be checked. CheckBox is intentionally non-focusable.
            controller.State.AutoMoveCompletedTodosToBottom = true;
            await Check(window, panel, scroll, paper, "row-20", expectMove: true);
            Console.WriteLine("PASS #331: checkbox completion preserves the viewed region");
        }
        finally
        {
            window.CloseForReal();
        }
    }

    private static async Task Check(
        PaperWindow window, StackPanel panel, ScrollViewer scroll, PaperData paper,
        string id, bool expectMove)
    {
        var row = Row(window, id);
        var editor = Editors(window)[id];
        Require(row.ActualHeight > 0, $"Row {id} was not arranged");
        // Ensure CurrentFocusedTodoItemId sees the same edit field that a user had active
        // immediately before clicking its non-focusable checkbox.
        FocusManager.SetFocusedElement(window, editor);
        await Idle();
        Require(ReferenceEquals(FocusManager.GetFocusedElement(window), editor),
            $"Could not establish logical WPF focus for {id}");

        // Put the checked row in the middle of the visible viewport, far from both ends.
        var rowY = row.TransformToAncestor(panel).Transform(new Point()).Y;
        scroll.ScrollToVerticalOffset(Math.Clamp(
            rowY - scroll.ViewportHeight / 2,
            0,
            scroll.ScrollableHeight));
        await Idle();
        var before = scroll.VerticalOffset;
        var beforeMax = scroll.ScrollableHeight;
        Require(before > 60 && before < beforeMax - 60,
            $"Fixture did not start in the middle: offset={before:F1}, max={beforeMax:F1}");

        // Changing IsChecked raises the genuine Checked handler from PaperWindow.Todo.cs.
        CheckBox(window, id).IsChecked = true;
        await Idle();
        await Idle();

        var after = scroll.VerticalOffset;
        var afterMax = scroll.ScrollableHeight;
        var isLast = paper.Items[^1].Id == id;
        Console.WriteLine(
            $"#331 {(expectMove ? "auto-move" : "no-move")}: row={id}, " +
            $"offset={before:F1}->{after:F1}, max={beforeMax:F1}->{afterMax:F1}, " +
            $"last={isLast}, keyboardFocus={editor.IsKeyboardFocused}");

        Require(paper.Items.Single(item => item.Id == id).Done,
            $"The Checked event did not update the real model for {id}");
        Require(isLast == expectMove,
            $"Unexpected completed-row ordering for {id}");

        // One moved row can legitimately change the position of nearby content slightly.
        // A large jump (especially to the completed group at the bottom) is the reported bug.
        var tolerance = Math.Max(40, row.ActualHeight * 2);
        Require(Math.Abs(after - before) <= tolerance,
            $"#331 reproduced: completing {id} shifted the viewport by {after - before:F1} DIP " +
            $"(allowed {tolerance:F1}); autoMove={expectMove}");
    }

    private static ScrollViewer FindScrollHost(DependencyObject element)
    {
        for (DependencyObject? parent = element; parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scroll) return scroll;
        }
        throw new InvalidOperationException("The real Todo ScrollViewer was not found");
    }

    private static Dictionary<string, TodoTextBox> Editors(PaperWindow window) =>
        Field<Dictionary<string, TodoTextBox>>(window, "_todoEditors");

    private static Border Row(PaperWindow window, string id) =>
        Field<List<Border>>(window, "_todoRows").Single(r => (string)r.Tag == id);

    private static CheckBox CheckBox(PaperWindow window, string id) =>
        ((Grid)Row(window, id).Child).Children.OfType<CheckBox>().Single();

    private static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, PrivateInstance)?.GetValue(owner)
            ?? throw new InvalidOperationException("Missing fixture field " + name));

    private static Task Idle() =>
        Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void Require(bool ok, string error)
    {
        if (!ok) throw new InvalidOperationException(error);
    }
}
