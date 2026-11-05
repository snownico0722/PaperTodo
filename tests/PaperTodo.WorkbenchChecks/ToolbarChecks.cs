using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static async Task CheckToolbarBounds(WorkbenchWindow desk)
    {
        var originalState = desk.WindowState;
        var originalWidth = desk.Width;
        var originalHeight = desk.Height;
        var host = (Border)Member(desk, "_toolbarHost");
        var search = (TextBox)Member(desk, "_search");
        var left = (Panel)Member(desk, "_layoutCommands");
        var right = (Panel)Member(desk, "_rightCommands");
        try
        {
            desk.WindowState = WindowState.Normal;
            desk.Height = 600;
            foreach (var width in new[] { 1600, 1024, 640 })
            {
                desk.Width = width;
                desk.UpdateLayout();
                await Until(() => Math.Abs(desk.ActualWidth - width) < 1,
                    $"Toolbar fixture did not resize to {width} DIP");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                desk.UpdateLayout();
                var controls = new List<FrameworkElement> { search, (FrameworkElement)Member(desk, "_zoom") };
                controls.AddRange(left.Children.OfType<Button>().Where(b => b.IsVisible));
                controls.AddRange(right.Children.OfType<Button>().Where(b => b.IsVisible));
                foreach (var key in new[] { "WorkbenchNewNote", "WorkbenchNewTodo", "WorkbenchNewNode", "WorkbenchClose" })
                    Assert(right.Children.OfType<Button>().Any(b => Equals(b.Content, Strings.Get(key)) && b.IsVisible),
                        $"Toolbar hid the {key} command at {width} DIP");

                var bounds = controls.Select(control => (Control: control,
                    Bounds: control.TransformToAncestor(host).TransformBounds(new Rect(control.RenderSize)))).ToArray();
                foreach (var item in bounds)
                    Assert(item.Bounds.Width > 0 && item.Bounds.Height > 0 &&
                           item.Bounds.Left >= -.5 && item.Bounds.Top >= -.5 &&
                           item.Bounds.Right <= host.ActualWidth + .5 && item.Bounds.Bottom <= host.ActualHeight + .5,
                        $"Toolbar control {ControlLabel(item.Control)} overflowed its visible host at {width} DIP: {item.Bounds}");
                for (var first = 0; first < bounds.Length; first++)
                for (var second = first + 1; second < bounds.Length; second++)
                {
                    var overlap = Rect.Intersect(bounds[first].Bounds, bounds[second].Bounds);
                    Assert(overlap.IsEmpty || overlap.Width < .5 || overlap.Height < .5,
                        $"Toolbar controls {ControlLabel(bounds[first].Control)} and {ControlLabel(bounds[second].Control)} overlapped at {width} DIP");
                }
                if (width == 1600)
                {
                    var searchBounds = bounds.Single(item => ReferenceEquals(item.Control, search)).Bounds;
                    Assert(Math.Abs(searchBounds.Left + searchBounds.Width / 2 - host.ActualWidth / 2) <= 1,
                        "Wide toolbar search was not centered in the workbench");
                }
                if (width is 1024 or 640) SaveImage(desk, $"workbench-toolbar-{width}.png");
            }
        }
        finally
        {
            desk.Width = originalWidth;
            desk.Height = originalHeight;
            desk.WindowState = originalState;
            desk.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        static string ControlLabel(FrameworkElement control) => control is Button button
            ? button.Content?.ToString() ?? "button" : control.GetType().Name;
    }
}
