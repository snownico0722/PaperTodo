using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PaperTodo;

// One-time benchmark: actual native HWND size changes without synthesizing cursor input.
// The same 80-step trajectory and render pump are used across shadow/top-bar arms.
// NOT a mouse-to-photon, DWM present or 240-Hz display latency measurement.
internal static class ResizeNativeBenchmarks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint SwpNoMoveNoZNoActivate = 0x0002 | 0x0004 | 0x0010;
    private sealed record Arm(string Shadow, bool SkipTopbar);
    private sealed record Result(string PaperType, string Shadow, bool SkipTopbar, int Pass,
        double CpuMs, double WallMs, double SetWindowPosMedianMs, double SetWindowPosP95Ms,
        double RenderP95Ms, int RenderCallbacks, int LayoutCallbacks, int WindowChanges,
        double? DwmCpuMs, int FinalWidth, int FinalHeight);

    internal static void Run(AppController controller, string output)
    {
        typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, false);
        controller.State.PaperSkin = PaperSkins.Paper;
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.Theme = "light";
        controller.State.EnableAnimations = false;
        controller.State.UseCapsuleMode = false;
        controller.State.UseDeepCapsuleMode = false;
        controller.State.ExperimentalInactivePaperOpacity = false;
        controller.State.ExperimentalWindowTethering = false;
        Theme.Invalidate();

        var arms = new[] { new Arm("nine-slice", false), new Arm("none", false),
            new Arm("legacy-effect", false), new Arm("none", true),
            new Arm("nine-slice", true), new Arm("legacy-effect", true) };
        var results = new List<Result>();
        try
        {
            foreach (var type in new[] { PaperTypes.Todo, PaperTypes.Note })
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    foreach (var arm in pass == 0 ? arms : arms.Reverse())
                    {
                        var paper = new PaperData {
                            Type = type, X = 85, Y = 80, Width = 400, Height = 320,
                            Title = "Synthetic HWND resize probe", AlwaysOnTop = true,
                            Content = type == PaperTypes.Note
                                ? "# Resize benchmark\\n\\n**Formatted paragraph**\\n" +
                                    string.Join("\\n", Enumerable.Repeat(
                                        "Long text wraps when resized, so Note layout work remains realistic.", 24))
                                : ""
                        };
                        if (type == PaperTypes.Todo)
                            for (var n = 0; n < 16; n++)
                                paper.Items.Add(new PaperItem { Text = "Example todo with wrapping text " + n });
                        controller.State.Papers.Add(paper);
                        var window = new PaperWindow(paper, controller);
                        try
                        {
                            window.Show();
                            window.Activate();
                            Pump(110);
                            Program.Assert(window.AllowsTransparency, "layered paper HWND");
                            var chrome = (PaperChromeBorder)typeof(PaperWindow)
                                .GetField("_paperChrome", Private)!.GetValue(window)!;
                            window.ResizePerformanceSkipTopBarUpdates = arm.SkipTopbar;
                            chrome.ClearNineSliceShadow();
                            chrome.Effect = null;
                            if (arm.Shadow == "nine-slice")
                                chrome.SetNineSliceShadow(14, 2, .22, capsuleShape: false);
                            else if (arm.Shadow == "legacy-effect")
                                chrome.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = .22 };
                            Pump(95);
                            var hwnd = new WindowInteropHelper(window).Handle;
                            var content = window.Content;
                            var row = Benchmark(window, hwnd, type, arm, pass);
                            Program.Assert(ReferenceEquals(window.Content, content) &&
                                hwnd == new WindowInteropHelper(window).Handle, "native window identity unchanged");
                            Program.Assert(arm.Shadow == "nine-slice" ? chrome.HasNineSliceShadow
                                : arm.Shadow == "none" ? !chrome.HasNineSliceShadow && chrome.Effect == null
                                : chrome.Effect is DropShadowEffect, "benchmark shadow remains selected");
                            results.Add(row);
                            Console.WriteLine($"SYNTHETIC {type}/{arm.Shadow}/topbar={(arm.SkipTopbar ? "skip" : "normal")}/pass={pass}: " +
                                $"cpu={row.CpuMs:F1}ms, wall={row.WallMs:F1}ms, native-p95={row.SetWindowPosP95Ms:F2}ms, " +
                                $"render-p95={row.RenderP95Ms:F2}ms, render={row.RenderCallbacks}, layouts={row.LayoutCallbacks}, resizes={row.WindowChanges}");
                        }
                        finally
                        {
                            window.ResizePerformanceSkipTopBarUpdates = false;
                            window.CloseForReal();
                            controller.State.Papers.Remove(paper);
                            Pump(50);
                        }
                    }
                }
            }
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                Revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                RenderTier = RenderCapability.Tier >> 16,
                Scope = "160-step Native SetWindowPos sequence per arm (80 measurement + 80 reverse), genuine WPF PaperWindow, synthetic pointer-independent resize, not display scanout.",
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"SYNTHETIC SUMMARY {results.Count} valid arm runs");
            foreach (var group in results.GroupBy(x => new { x.PaperType, x.Shadow, x.SkipTopbar }))
                Console.WriteLine($"MEDIAN {group.Key.PaperType}/{group.Key.Shadow}/topbar={(group.Key.SkipTopbar ? "skip" : "normal")}: " +
                    $"cpu={Median(group.Select(x => x.CpuMs)):F1}ms, " +
                    $"wall={Median(group.Select(x => x.WallMs)):F1}ms, " +
                    $"native-p95={Median(group.Select(x => x.SetWindowPosP95Ms)):F2}ms, " +
                    $"render-p95={Median(group.Select(x => x.RenderP95Ms)):F2}ms, " +
                    $"layouts={Median(group.Select(x => (double)x.LayoutCallbacks)):F0}");
        }
    }

    private static Result Benchmark(PaperWindow window, IntPtr hwnd, string type, Arm arm, int pass)
    {
        // Same dimensions and chronological stepping in every arm. Keep UI event loop alive.
        var targets = Enumerable.Range(0, 160).Select(i => {
            var t = i < 80 ? (i + 1) / 80.0 : (159 - i) / 80.0;
            return (Width: 400 + (int)Math.Round(140 * t),
                Height: 320 + (int)Math.Round(90 * t));
        }).ToArray();
        // Warm window sizing and the first WPF layout in this HWND (excluded).
        foreach (var target in targets.Take(6))
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, 0, 0, target.Width, target.Height,
                SwpNoMoveNoZNoActivate), "warm native resize");
        Pump(75);
        var times = new double[targets.Length];
        var frameTimes = new List<double>();
        var layouts = 0; var sizeChanged = 0;
        var watch = Stopwatch.StartNew();
        EventHandler render = (_, _) => frameTimes.Add(watch.Elapsed.TotalMilliseconds);
        EventHandler layout = (_, _) => layouts++;
        SizeChangedEventHandler resized = (_, _) => sizeChanged++;
        window.LayoutUpdated += layout;
        window.SizeChanged += resized;
        CompositionTarget.Rendering += render;
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var dwm0 = DwmCpu();
        try
        {
            watch.Restart();
            for (var i = 0; i < targets.Length; i++)
            {
                var stamp = Stopwatch.GetTimestamp();
                Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, 0, 0,
                    targets[i].Width, targets[i].Height, SwpNoMoveNoZNoActivate), "native HWND resize");
                times[i] = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                Pump(2);
            }
            Pump(90);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds;
            var dwm1 = DwmCpu();
            Program.Assert(GetWindowRect(hwnd, out var bounds), "native HWND final rect");
            Program.Assert(sizeChanged >= 80, $"actual HWND resize events: {sizeChanged}");
            return new Result(type, arm.Shadow, arm.SkipTopbar, pass,
                cpu, elapsed, Percentile(times, .5), Percentile(times, .95),
                Percentile(frameTimes.Zip(frameTimes.Skip(1),(a,b)=>b-a).ToArray(), .95),
                frameTimes.Count, layouts, sizeChanged,
                dwm0.HasValue && dwm1.HasValue ? dwm1 - dwm0 : null,
                bounds.Right-bounds.Left, bounds.Bottom-bounds.Top);
        }
        finally
        {
            CompositionTarget.Rendering -= render;
            window.LayoutUpdated -= layout;
            window.SizeChanged -= resized;
        }
    }

    private static double? DwmCpu()
    {
        try { return Process.GetProcessesByName("dwm").Sum(p => { using (p) return p.TotalProcessorTime.TotalMilliseconds; }); }
        catch { return null; }
    }
    private static double Percentile(double[] v, double p) =>
        v.Length == 0 ? 0 : v.Order().ElementAt(Math.Min(v.Length - 1, (int)Math.Floor((v.Length - 1) * p)));
    private static double Median(IEnumerable<double> v) => Percentile(v.ToArray(), .5);
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
