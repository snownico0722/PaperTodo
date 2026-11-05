using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static async Task CheckToolbarBounds(WorkbenchWindow desk)
    {
        var host = (Border)Member(desk, "_toolbarHost");
        var search = (TextBox)Member(desk, "_search");
        var left = (Panel)Member(desk, "_layoutCommands");
        var right = (Panel)Member(desk, "_rightCommands");
        var parent = VisualTreeHelper.GetParent(host) as Panel
            ?? throw new InvalidOperationException("Toolbar has no panel parent");
        var index = parent.Children.IndexOf(host);
        var fixture = new Grid
        {
            Background = desk.Background, Language = host.Language, FlowDirection = host.FlowDirection,
            UseLayoutRounding = host.UseLayoutRounding, SnapsToDevicePixels = host.SnapsToDevicePixels
        };
        TextElement.SetFontFamily(fixture, TextElement.GetFontFamily(host));
        TextElement.SetFontSize(fixture, TextElement.GetFontSize(host));
        TextElement.SetFontWeight(fixture, TextElement.GetFontWeight(host));
        TextElement.SetFontStyle(fixture, TextElement.GetFontStyle(host));
        TextElement.SetFontStretch(fixture, TextElement.GetFontStretch(host));
        TextElement.SetForeground(fixture, TextElement.GetForeground(host));
        try
        {
            // Measure the actual controls outside an HWND: CI's 1024-pixel desktop
            // limits native window widths, but must not limit the wide-layout test.
            parent.Children.Remove(host);
            fixture.Children.Add(host);
            foreach (var width in new[] { 1600, 1024, 640 })
            {
                fixture.Width = width;
                fixture.Measure(new Size(width, 600));
                fixture.Arrange(new Rect(0, 0, width, 600));
                fixture.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                fixture.UpdateLayout();
                Assert(Math.Abs(fixture.ActualWidth - width) < 1,
                    $"Toolbar visual was not arranged at {width} DIP");
                var controls = new List<FrameworkElement> { search, (FrameworkElement)Member(desk, "_zoom") };
                controls.AddRange(left.Children.OfType<Button>().Where(IsPresented));
                controls.AddRange(right.Children.OfType<Button>().Where(IsPresented));
                foreach (var key in new[] { "WorkbenchNewNote", "WorkbenchNewTodo", "WorkbenchNewNode", "WorkbenchClose" })
                    Assert(right.Children.OfType<Button>().Any(b => Equals(b.Content, Strings.Get(key)) && IsPresented(b)),
                        $"Toolbar hid the {key} command at {width} DIP");

                var bounds = controls.Select(control => (Control: control,
                    Bounds: control.TransformToAncestor(host).TransformBounds(new Rect(control.RenderSize)))).ToArray();
                foreach (var item in bounds)
                    Assert(IsPresented(item.Control) && item.Bounds.Width > 0 && item.Bounds.Height > 0 &&
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
                if (width is 1024 or 640)
                    SaveToolbarImage(fixture, width, host.ActualHeight + host.Margin.Top + 14,
                        $"workbench-toolbar-{width}.png");
            }
        }
        finally
        {
            fixture.Children.Remove(host);
            if (!parent.Children.Contains(host)) parent.Children.Insert(index, host);
            desk.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        static string ControlLabel(FrameworkElement control) => control is Button button
            ? button.Content?.ToString() ?? "button" : control.GetType().Name;

        static bool IsPresented(FrameworkElement control)
        {
            for (DependencyObject? current = control; current != null; current = VisualTreeHelper.GetParent(current))
                if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
            return true;
        }
    }

    private static void SaveToolbarImage(Visual visual, double width, double height, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("WORKBENCH_ARTIFACT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, filename));
        encoder.Save(stream);
    }
}
