using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

// One-time CI performance diagnosis, NOT a correctness test or display-present benchmark.
// All PaperTodo arms use real ordinary layered PaperWindow with cached nine-slice shadow.
// Isolated process/data fixture comes from MaterialDragBenchmarks.
internal static class RapidResizeBenchmarks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint SwpFlags = 0x0002 | 0x0004 | 0x0010; // NOMOVE, NOZORDER, NOACTIVATE
    private sealed record Case(string Type, string Mode, string Pattern);
    private sealed record Sample(string Type, string Mode, string Pattern, int Pass,
        double ProcessCpuMs, double WallMs, double WindowResizeP95Ms, double WindowResizeP50Ms,
        double RenderingP95Ms, int NativeUpdates, int LayoutUpdates, int RenderingCallbacks);

    internal static void Run(AppController controller, string output)
    {
        typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, false);
        var state = controller.State;
        state.PaperSkin = PaperSkins.Paper;
        state.ColorScheme = ColorSchemes.Warm;
        state.Theme = "light";
        state.EnableAnimations = false;
        state.UseCapsuleMode = false;
        state.UseDeepCapsuleMode = false;
        state.ExperimentalInactivePaperOpacity = false;
        state.ExperimentalWindowTethering = false;
        Theme.Invalidate();
        var cases = new List<Case>();
        foreach (var type in new[] { PaperTypes.Todo, PaperTypes.Note })
        {
            foreach (var mode in new[] { "normal", "body-hidden", "body-collapsed", "no-body" })
                cases.Add(new Case(type, mode, "reverse"));
        }
        cases.Add(new Case("plain-wpf", "bare-layered", "reverse"));
        var samples = new List<Sample>();

        try
        {
            for (int pass = 0; pass < 2; pass++)
            foreach (var c in pass == 0 ? cases : cases.AsEnumerable().Reverse())
            {
                Window? window = null;
                PaperData? paper = null;
                try
                {
                    if (c.Type == "plain-wpf")
                    {
                        window = new Window
                        {
                            Width = 440, Height = 340, Left = 80, Top = 70,
                            WindowStyle = WindowStyle.None,
                            AllowsTransparency = true,
                            Background = Brushes.Transparent,
                            ResizeMode = ResizeMode.CanResize,
                            ShowInTaskbar = false,
                            Content = new Border { Margin = new Thickness(8),
                                CornerRadius = new CornerRadius(16), Background = Brushes.White,
                                Child = new TextBlock { Text = "Plain WPF layered control", Margin = new Thickness(14) } }
                        };
                    }
                    else
                    {
                        paper = new PaperData {
                            Type = c.Type, X = 80, Y = 70, Width = 440, Height = 340,
                            Title = "Temporary fast-reversal benchmark", AlwaysOnTop = true,
                            Content = c.Type == PaperTypes.Note
                                ? "# Layout test\n\n" + string.Join("\n",
                                    Enumerable.Repeat("Several words force text wrapping each time width changes.", 32))
                                : ""
                        };
                        if (c.Type == PaperTypes.Todo)
                            for (int i = 0; i < 20; i++)
                                paper.Items.Add(new PaperItem { Text = "A todo item with enough characters to wrap at some widths " + i });
                        state.Papers.Add(paper);
                        var paperWindow = new PaperWindow(paper, controller);
                        window = paperWindow;
                    }

                    window.Show();
                    window.Activate();
                    Wait(100);
                    Program.Assert(window.AllowsTransparency, "the benchmark must resize a real layered HWND");
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (window is PaperWindow pw)
                    {
                        var chrome = (PaperChromeBorder)typeof(PaperWindow)
                            .GetField("_paperChrome", Private)!.GetValue(pw)!;
                        if (c.Mode == "no-shadow")
                        {
                            chrome.ClearNineSliceShadow();
                            chrome.Effect = null;
                        }
                        else Program.Assert(chrome.HasNineSliceShadow,
                            "ordinary paper baseline must use nine-slice shadow");

                        if (c.Mode is "no-geometry" or "no-both")
                            typeof(PaperWindow).GetField("_suppressGeometrySave", Private)!.SetValue(pw, true);
                        if (c.Mode is "no-topbar" or "no-both")
                            typeof(PaperWindow).GetField("_updatingTopBarResponsiveLayout", Private)!.SetValue(pw, true);
                        if (c.Mode is "no-body" or "body-hidden" or "body-collapsed")
                        {
                            var shell = (Grid)typeof(PaperWindow).GetField("_shell", Private)!.GetValue(pw)!;
                            if (c.Mode == "no-body") shell.Visibility = Visibility.Collapsed;
                            else
                            {
                                var body = shell.Children.OfType<UIElement>()
                                    .First(element => Grid.GetRow(element) == 1);
                                body.Visibility = c.Mode == "body-hidden"
                                    ? Visibility.Hidden : Visibility.Collapsed;
                            }
                        }
                        pw.UpdateLayout();
                    }
                    Wait(90);
                    var result = Measure(window, hwnd, c, pass);
                    samples.Add(result);
                    Console.WriteLine($"RAPID {c.Type}/{c.Mode}/{c.Pattern}/pass={pass}: " +
                        $"cpu={result.ProcessCpuMs:F1}ms wall={result.WallMs:F1}ms " +
                        $"resizeP95={result.WindowResizeP95Ms:F2}ms resizeP50={result.WindowResizeP50Ms:F2}ms " +
                        $"renderP95={result.RenderingP95Ms:F2}ms size={result.NativeUpdates} " +
                        $"layout={result.LayoutUpdates} rendering={result.RenderingCallbacks}");
                }
                finally
                {
                    if (window is PaperWindow pw) pw.CloseForReal();
                    else window?.Close();
                    if (paper != null) state.Papers.Remove(paper);
                    Wait(35);
                }
            }
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                RenderTier = RenderCapability.Tier >> 16,
                Scope = "Real layered PaperWindow 120 size operations, same HWND per arm; program-driven native SetWindowPos; measures Win32/WPF response not mouse-to-photon. Alternating-order duplicate passes.",
                Samples = samples
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"RAPID TOTAL valid={samples.Count}");
            foreach (var group in samples.GroupBy(s => new { s.Type, s.Mode, s.Pattern }))
                Console.WriteLine($"AGG {group.Key.Type}/{group.Key.Mode}/{group.Key.Pattern}: " +
                    $"cpu={Mean(group.Select(s => s.ProcessCpuMs)):F1}ms, " +
                    $"resizeP95={Mean(group.Select(s => s.WindowResizeP95Ms)):F2}ms, " +
                    $"wall={Mean(group.Select(s => s.WallMs)):F1}ms, " +
                    $"layouts={Mean(group.Select(s => (double)s.LayoutUpdates)):F0}, n={group.Count()}");
        }
    }

    private static Sample Measure(Window window, IntPtr hwnd, Case c, int pass)
    {
        int[] widths = new int[120];
        int[] heights = new int[120];
        for (int i=0; i<120; i++)
        {
            // Reversal: 10 small back-and-forth moves across the same size band.
            var phase = i % 12;
            var progress = c.Pattern == "reverse"
                ? (phase <= 6 ? phase : 12 - phase) / 6.0
                : (i <= 60 ? i / 60.0 : (119 - i) / 59.0);
            widths[i] = 405 + (int)Math.Round(85 * progress);
            heights[i] = 315 + (int)Math.Round(55 * progress);
        }
        Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 440, 340, SwpFlags),
            "native window warmup");
        Wait(75);
        var watch = Stopwatch.StartNew();
        var timings = new double[widths.Length];
        var rendering = new List<double>();
        int layouts = 0;
        int resizes = 0;
        EventHandler layout = (_, _) => layouts++;
        EventHandler render = (_, _) => rendering.Add(watch.Elapsed.TotalMilliseconds);
        SizeChangedEventHandler size = (_, _) => resizes++;
        window.LayoutUpdated += layout;
        window.SizeChanged += size;
        CompositionTarget.Rendering += render;
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        try
        {
            watch.Restart();
            for (int i=0; i<widths.Length; i++)
            {
                var stamp = Stopwatch.GetTimestamp();
                Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, 0, 0,
                    widths[i], heights[i], SwpFlags), "real native HWND size update");
                timings[i] = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                Wait(2);
            }
            Wait(75);
            Program.Assert(resizes >= 90,
                $"native size test invalid: {resizes} size changes for {c.Mode}/{c.Pattern}");
            return new Sample(c.Type, c.Mode, c.Pattern, pass,
                (Process.GetCurrentProcess().TotalProcessorTime-cpuStart).TotalMilliseconds,
                watch.Elapsed.TotalMilliseconds,
                Percentile(timings,.95), Percentile(timings,.5),
                Percentile(rendering.Zip(rendering.Skip(1),(a,b)=>b-a).ToArray(),.95),
                resizes, layouts, rendering.Count);
        }
        finally
        {
            window.LayoutUpdated -= layout;
            window.SizeChanged -= size;
            CompositionTarget.Rendering -= render;
        }
    }
    private static double Mean(IEnumerable<double> values) { var v=values.ToArray(); return v.Sum()/Math.Max(1,v.Length); }
    private static double Percentile(double[] values,double p) =>
        values.Length == 0 ? 0 : values.Order().ElementAt(Math.Min(values.Length-1,(int)Math.Floor((values.Length-1)*p)));
    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval=TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_,_)=>{timer.Stop();frame.Continue=false;};
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x,int y,int w,int h,uint flags);
}
