using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

// Copy this SAME harness into both compared revisions. Timings describe CPU command
// recording and popup-open latency on that runner, never compositor FPS or scanout.
internal static class MaterialBenchmarks
{
    private sealed record Sample(double MicrosecondsPerOperation, double AllocatedBytesPerOperation);
    private sealed record Measurement(string Name, int OperationsPerRound, Sample[] Rounds);
    private static readonly Action<SkinBorder, DrawingContext> Paint = typeof(SkinBorder)
        .GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Action<SkinBorder, DrawingContext>>();

    internal static void Run(AppController controller, string output)
    {
        controller.State.PaperSkin = PaperSkins.TracingPaper;
        controller.State.Theme = "light";
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.EnableAnimations = false;
        controller.State.LiveBackgroundProcessing = false;
        Theme.Invalidate();
        var measurements = new List<Measurement>();
        foreach (var count in new[] { 1, 10, 30 })
        {
            var surfaces = Enumerable.Range(0, count).Select(_ => new SkinBorder
            {
                Width = 240, Height = 160, CornerRadius = new CornerRadius(8), Background = Brushes.White
            }).ToArray();
            var visual = new DrawingVisual();
            foreach (var surface in surfaces) Draw(surface, visual, true);
            measurements.Add(Measure($"noop-refresh-{count}-surfaces", 100, i =>
            {
                foreach (var surface in surfaces) { surface.RefreshSkin(); Draw(surface, visual, false); }
            }));
            measurements.Add(Measure($"resize-{count}-surfaces", 60, i =>
            {
                foreach (var surface in surfaces) { surface.Width = 240 + i % 17; Draw(surface, visual, true); }
            }));
            measurements.Add(Measure($"palette-{count}-surfaces", 50, i =>
            {
                controller.State.ColorScheme = (i & 1) == 0 ? ColorSchemes.Warm : ColorSchemes.Ink;
                Theme.Invalidate();
                foreach (var surface in surfaces) { surface.RefreshSkin(); Draw(surface, visual, false); }
            }));
        }
        // Bind once, outside measured loops. The pre-refactor overload had a skin ID.
        var create = typeof(MaterialRelief).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "Create" && m.GetParameters().Length is 5 or 6);
        Func<Size, CornerRadius, Thickness, DpiScale, bool, DrawingGroup> relief;
        if (create.GetParameters().Length == 5)
            relief = create.CreateDelegate<Func<Size, CornerRadius, Thickness, DpiScale, bool, DrawingGroup>>();
        else
        {
            var old = create.CreateDelegate<Func<Size, CornerRadius, Thickness, DpiScale, string, bool, DrawingGroup>>();
            relief = (s, c, b, d, dark) => old(s, c, b, d, PaperSkins.Aero, dark);
        }
        measurements.Add(Measure("aero-relief-varying-width", 80, i => GC.KeepAlive(relief(
            new Size(432 + i % 31, 370), new CornerRadius(8), new Thickness(1), new DpiScale(1.5, 1.5), false))));
        var menu = MeasureMenu(controller);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Revision = Environment.GetEnvironmentVariable("PAPER_BENCH_REVISION"),
            OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
            Processors = Environment.ProcessorCount, RenderTier = RenderCapability.Tier >> 16,
            Scope = "CPU recording/allocation; popup Opened notification, not physical display FPS. Same harness, separate processes.",
            Measurements = measurements, MenuOpenMilliseconds = menu
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"BENCHMARK written: {output}");
    }

    private static void Draw(SkinBorder surface, DrawingVisual visual, bool layout)
    {
        if (layout)
        {
            surface.Measure(new Size(surface.Width, surface.Height));
            surface.Arrange(new Rect(0, 0, surface.Width, surface.Height));
        }
        using var dc = visual.RenderOpen();
        Paint(surface, dc);
    }
    private static Measurement Measure(string name, int operations, Action<int> action)
    {
        // Short, single-iteration warmups can measure tiered-JIT transitions instead
        // of steady work. Warm each real operation, then avoid sub-millisecond rounds.
        var warmup = Stopwatch.StartNew();
        var warmOperations = 0;
        do { action(warmOperations++); } while (warmup.ElapsedMilliseconds < 500);
        operations = Math.Max(operations, (int)Math.Min(100_000,
            Math.Ceiling(warmOperations * 30 / warmup.Elapsed.TotalMilliseconds)));
        var rounds = new Sample[7];
        for (var round = 0; round < rounds.Length; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < operations; i++) action(i);
            rounds[round] = new(Stopwatch.GetElapsedTime(start).TotalMicroseconds / operations,
                (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)operations);
        }
        Console.WriteLine($"BENCH {name}: median {rounds.Select(r => r.MicrosecondsPerOperation).Order().ElementAt(3):F3} us/op");
        return new(name, operations, rounds);
    }
    private static double[] MeasureMenu(AppController controller)
    {
        controller.State.PaperSkin = PaperSkins.Acrylic;
        controller.State.LiveBackgroundProcessing = true; Theme.Invalidate();
        var owner = new Window { Left = 60, Top = 60, Width = 200, Height = 100, ShowInTaskbar = false, Content = new Border() };
        var menu = controller.CreateTrayMenu();
        menu.PlacementTarget = (UIElement)owner.Content; menu.Placement = PlacementMode.Bottom;
        var times = new List<double>();
        long started = 0; var opened = false; double elapsed = 0;
        menu.Opened += (_, _) => { elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds; opened = true; };
        try
        {
            owner.Show(); Wait(100);
            for (var i = 0; i < 16; i++)
            {
                opened = false; started = Stopwatch.GetTimestamp();
                menu.SetCurrentValue(ContextMenu.IsOpenProperty, true);
                while (!opened && Stopwatch.GetElapsedTime(started).TotalSeconds < 4) Wait(1);
                if (!opened) throw new InvalidOperationException("Benchmark menu failed to open.");
                if (i >= 2) times.Add(elapsed);
                Wait(60); menu.IsOpen = false; Wait(60);
            }
        }
        finally { menu.IsOpen = false; owner.Close(); }
        return times.ToArray();
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
