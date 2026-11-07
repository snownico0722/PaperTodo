using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PaperTodo;

// One-time Windows diagnosis. Compares the same production PaperWindow and native
// bottom-right grip with three shadow modes; this is NOT display scanout FPS.
internal static class ResizeShadowBenchmarks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Arm(string Shadow, bool SkipTopbar);
    private sealed record SizeSample(double TimeMs, int Width, int Height, int CursorX, int CursorY);
    private sealed record Result(
        string PaperType, string Shadow, bool SkipTopbar, int Pass,
        double DurationMs, double CpuMs, double? DwmCpuMs, long UiBytes,
        int NativeSizeUpdates, int WmSizing, int LayoutUpdated, int RenderCallbacks,
        double NativeIntervalP50Ms, double NativeIntervalP95Ms,
        double RenderIntervalP95Ms, double CursorGapP95Pixels,
        int FinalWidthChange, int FinalHeightChange);

    internal static void Run(AppController controller, string output)
    {
        // Exactly the ordinary layered Paper path, never the native Mica window.
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

        var arms = new[] {
            new Arm("nine-slice", false), new Arm("none", false), new Arm("legacy-effect", false),
            new Arm("none", true), new Arm("nine-slice", true), new Arm("legacy-effect", true)
        };
        var all = new List<Result>();
        GetCursorPos(out var originalCursor);
        var originalDragFullWindowsAvailable = GetDragFullWindows(0x0026, 0, out var originalDragFullWindows, 0);
        Console.WriteLine($"ENV drag-full-windows BEFORE={originalDragFullWindowsAvailable}:{originalDragFullWindows}");
        var enabledDragFullWindows = SetDragFullWindows(0x0025, 1, IntPtr.Zero, 0);
        var readAfter = GetDragFullWindows(0x0026, 0, out var afterDragFullWindows, 0);
        Console.WriteLine($"ENV drag-full-windows SET={enabledDragFullWindows}, AFTER={readAfter}:{afterDragFullWindows}");
        var timerChanged = timeBeginPeriod(1) == 0;
        try
        {
            foreach (var paperType in new[] { PaperTypes.Todo, PaperTypes.Note })
            {
                var paper = new PaperData
                {
                    Type = paperType, X = 85, Y = 80, Width = 400, Height = 320,
                    Title = "Temporary resize benchmark", AlwaysOnTop = true,
                    Content = paperType == PaperTypes.Note
                        ? "# Resize\n\n**Text** with several wrapped lines, to exercise the real editor.\n" +
                          string.Join("\n", Enumerable.Repeat("A sample paragraph containing enough words to wrap at differing widths.", 24))
                        : ""
                };
                if (paperType == PaperTypes.Todo)
                    for (var n = 0; n < 16; n++)
                        paper.Items.Add(new PaperItem { Text = "Resize item with some variable width text " + n });
                state.Papers.Add(paper);
                try
                {
                    // One fresh HWND for each arm: native resize leaves WPF-owned topmost
                    // popups/input surfaces behind on hosted CI, and can cover the next grip.
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var order = pass == 0 ? arms : arms.Reverse();
                        foreach (var arm in order)
                        {
                            paper.Width = 400;
                            paper.Height = 320;
                            paper.X = 85;
                            paper.Y = 80;
                            var window = new PaperWindow(paper, controller);
                            try
                            {
                                window.Show();
                                window.Topmost = true;
                                window.Activate();
                                Wait(150);
                                Program.Assert(window.AllowsTransparency && !window.IsNativeMicaEffective,
                                    "paper must be a real layered WPF window");
                                var chrome = (PaperChromeBorder)typeof(PaperWindow)
                                    .GetField("_paperChrome", Private)!.GetValue(window)!;
                                var hwnd = new WindowInteropHelper(window).Handle;
                                var originalChild = window.Content;
                                window.ResizePerformanceSkipTopBarUpdates = arm.SkipTopbar;
                                chrome.ClearNineSliceShadow();
                                chrome.Effect = null;
                                if (arm.Shadow == "nine-slice")
                                    chrome.SetNineSliceShadow(14, 2, .22, capsuleShape: false);
                                else if (arm.Shadow == "legacy-effect")
                                    chrome.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = .22 };
                                window.UpdateLayout();
                                Wait(170);
                                var result = DragResize(window, arm, paperType, pass);
                                all.Add(result);
                                Program.Assert(ReferenceEquals(originalChild, window.Content) &&
                                    new WindowInteropHelper(window).Handle == hwnd,
                                    "real PaperWindow and editor survive native dragging");
                                Program.Assert(arm.Shadow == "nine-slice" ? chrome.HasNineSliceShadow
                                    : arm.Shadow == "none" ? !chrome.HasNineSliceShadow && chrome.Effect == null
                                    : chrome.Effect is DropShadowEffect,
                                    "shadow test arm must not be overridden during resizing");
                                Console.WriteLine($"RESIZE {result.PaperType}/{result.Shadow}/topbar={(result.SkipTopbar ? "skipped" : "normal")}/pass={result.Pass}: " +
                                    $"cpu={result.CpuMs:F1}ms, native={result.NativeSizeUpdates}, size-p95={result.NativeIntervalP95Ms:F2}ms, " +
                                    $"render={result.RenderCallbacks}, render-p95={result.RenderIntervalP95Ms:F2}ms, " +
                                    $"cursor-gap-p95={result.CursorGapP95Pixels:F1}px, layouts={result.LayoutUpdated}");
                            }
                            finally
                            {
                                window.ResizePerformanceSkipTopBarUpdates = false;
                                window.CloseForReal();
                                Wait(90);
                            }
                        }
                    }
                }
                finally { state.Papers.Remove(paper); }
            }
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(originalCursor.X, originalCursor.Y);
            if (timerChanged) timeEndPeriod(1);
            if (originalDragFullWindowsAvailable)
                SetDragFullWindows(0x0025, (uint)originalDragFullWindows, IntPtr.Zero, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                Revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                OS = RuntimeInformation.OSDescription,
                Runtime = RuntimeInformation.FrameworkDescription,
                RenderTier = RenderCapability.Tier >> 16,
                Scope = "Actual native bottom-right mouse resizing on freshly created real PaperWindow HWNDs; same paper data and pointer path for every arm. UI callbacks/rect changes, NOT displayed frames or 240-Hz scanout. Two alternating passes.",
                Results = all
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"RESIZE SUMMARY: {all.Count} measurements written to {output}");
            foreach (var group in all.GroupBy(r => new { r.PaperType, r.Shadow, r.SkipTopbar }))
                Console.WriteLine($"MEDIAN {group.Key.PaperType}/{group.Key.Shadow}/topbar={(group.Key.SkipTopbar ? "skipped" : "normal")}: " +
                    $"cpu={Median(group.Select(r => r.CpuMs)):F1}ms, " +
                    $"size-p95={Median(group.Select(r => r.NativeIntervalP95Ms)):F2}ms, " +
                    $"render-p95={Median(group.Select(r => r.RenderIntervalP95Ms)):F2}ms, " +
                    $"native-moves={Median(group.Select(r => (double)r.NativeSizeUpdates)):F0}, " +
                    $"layouts={Median(group.Select(r => (double)r.LayoutUpdated)):F0}");
        }
    }

    private static Result DragResize(PaperWindow window, Arm arm, string paperType, int pass)
    {
        window.Activate();
        Wait(70);
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd)!;
        var grip = Descendants(window).OfType<ResizeGrip>().Single(g =>
            g.IsVisible && g.ActualWidth >= 8 && g.ActualHeight >= 8);
        var origin = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
        var x0 = (int)Math.Round(origin.X);
        var y0 = (int)Math.Round(origin.Y);
        var packed = new IntPtr(unchecked((int)((uint)(ushort)x0 | ((uint)(ushort)y0 << 16))));
        Program.Assert(SendMessage(hwnd, 0x0084, IntPtr.Zero, packed).ToInt32() == 17,
            "real WPF grip must hit HTBOTTOMRIGHT");
        Program.Assert(GetWindowRect(hwnd, out var baseline), "original HWND bounds");
        var width0 = baseline.Right - baseline.Left;
        var height0 = baseline.Bottom - baseline.Top;
        Program.Assert(width0 > 300 && height0 > 200, "benchmark uses expanded paper");

        var samples = new List<SizeSample>(200);
        var renderTimes = new List<double>(200);
        var watch = Stopwatch.StartNew();
        var layouts = 0;
        var sizingCount = 0;
        EventHandler layout = (_, _) => layouts++;
        EventHandler render = (_, _) => renderTimes.Add(watch.Elapsed.TotalMilliseconds);
        HwndSourceHook hook = (IntPtr h, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (message == 0x0214) sizingCount++;
            if (message is 0x0047 or 0x0005 && GetWindowRect(h, out var now))
            {
                var w = now.Right - now.Left;
                var ht = now.Bottom - now.Top;
                if (samples.Count == 0 || w != samples[^1].Width || ht != samples[^1].Height)
                {
                    GetCursorPos(out var cursor);
                    samples.Add(new SizeSample(watch.Elapsed.TotalMilliseconds, w, ht, cursor.X, cursor.Y));
                }
            }
            return IntPtr.Zero;
        };
        var previousCursorOk = GetCursorPos(out var previousCursor);
        source.AddHook(hook);
        window.LayoutUpdated += layout;
        CompositionTarget.Rendering += render;
        try
        {
            Program.Assert(SetCursorPos(x0, y0), "position cursor on real resize grip");
            Wait(90);
            var hitWindow = WindowFromPoint(new NativePoint { X = x0, Y = y0 });
            Console.WriteLine($"HIT {paperType}/{arm.Shadow} expected={hwnd}, actual={hitWindow}, " +
                $"point={x0},{y0}, active={window.IsActive}, topmost={window.Topmost}");
            Program.Assert(hitWindow == hwnd,
                "the real visible window must own the grip pointer before mouse-down");
            samples.Clear();
            renderTimes.Clear();
            layouts = 0;
            sizingCount = 0;
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            var dwm0 = DwmCpu();
            var allocated0 = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            var driver = Task.Run(() =>
            {
                try
                {
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(105);
                    for (var i = 1; i <= 110; i++)
                    {
                        // Mostly increasing dimensions; small oscillation exercises responsive layout.
                        var t = i / 110.0;
                        var x = x0 + (int)Math.Round(88 * t + 9 * Math.Sin(4 * Math.PI * t));
                        var y = y0 + (int)Math.Round(63 * t + 6 * Math.Sin(4 * Math.PI * t));
                        if (!SetCursorPos(x, y)) throw new InvalidOperationException("cursor injection failed");
                        Thread.Sleep(10);
                    }
                    Thread.Sleep(50);
                }
                finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
            });
            var timeout = Stopwatch.StartNew();
            while (!driver.IsCompleted && timeout.Elapsed.TotalSeconds < 13)
                Wait(8);
            Program.Assert(driver.IsCompleted, "real resize gesture completed");
            driver.GetAwaiter().GetResult();
            Wait(135);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
            var dwm1 = DwmCpu();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocated0;
            Program.Assert(GetWindowRect(hwnd, out var final), "final HWND bounds");
            Console.WriteLine($"RESIZE-END {paperType}/{arm.Shadow}: " +
                $"{width0}x{height0} -> {final.Right-final.Left}x{final.Bottom-final.Top}, " +
                $"sizing={sizingCount}, updates={samples.Count}, hwnd={hwnd}");
            Program.Assert(final.Right - final.Left >= width0 + 45 &&
                final.Bottom - final.Top >= height0 + 35,
                $"real resize must change BOTH dimensions: {width0}x{height0} -> " +
                $"{final.Right - final.Left}x{final.Bottom - final.Top} ({arm.Shadow})");
            Console.WriteLine($"DIAG {paperType}/{arm.Shadow}: updates={samples.Count}, WM_SIZING={sizingCount}, " +
                $"renders={renderTimes.Count}, layouts={layouts}, final delta={final.Right-final.Left-width0}x{final.Bottom-final.Top-height0}");
            Program.Assert(samples.Count >= 20,
                $"native resize not progressing: {samples.Count} updates; confirm drag-full-windows system option");
            var intervals = samples.Zip(samples.Skip(1), (a, b) => b.TimeMs - a.TimeMs).ToArray();
            var frameIntervals = renderTimes.Zip(renderTimes.Skip(1), (a, b) => b - a).ToArray();
            var gaps = samples.Select(s => Math.Max(0,
                Math.Max(s.CursorX - x0 - (s.Width - width0),
                         s.CursorY - y0 - (s.Height - height0)))).Select(v => (double)v).ToArray();
            return new Result(paperType, arm.Shadow, arm.SkipTopbar, pass,
                elapsed, cpu, dwm0.HasValue && dwm1.HasValue ? dwm1 - dwm0 : null,
                allocated, samples.Count, sizingCount, layouts, renderTimes.Count,
                Percentile(intervals, .5), Percentile(intervals, .95),
                Percentile(frameIntervals, .95), Percentile(gaps, .95),
                final.Right - final.Left - width0, final.Bottom - final.Top - height0);
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            if (previousCursorOk) SetCursorPos(previousCursor.X, previousCursor.Y);
            CompositionTarget.Rendering -= render;
            window.LayoutUpdated -= layout;
            source.RemoveHook(hook);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static double Percentile(double[] values, double p) =>
        values.Length == 0 ? 0 : values.Order().ElementAt(Math.Min(values.Length - 1, (int)Math.Floor((values.Length - 1) * p)));
    private static double Median(IEnumerable<double> values) => Percentile(values.ToArray(), .5);
    private static double? DwmCpu()
    {
        try { return Process.GetProcessesByName("dwm").Sum(p => { using (p) return p.TotalProcessorTime.TotalMilliseconds; }); }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
    private static extern bool SetDragFullWindows(uint action, uint value, IntPtr unused, uint flags);
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
    private static extern bool GetDragFullWindows(uint action, uint unused, out int enabled, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint value);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint value);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint value);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint value);
}
