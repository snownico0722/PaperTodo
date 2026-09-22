using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

// Opt-in real mouse input against production title/capsule handlers. Do not run while
// using the desktop. This measures application work and HWND movement, NOT scanout FPS.
internal static class MaterialDragBenchmarks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Case(string Skin, bool Capsule, bool Live, bool Animations = true);
    private sealed record PositionSample(double Milliseconds, int X, int Y, int CursorX, int CursorY);
    private sealed record Counters(long Draws, long Refreshes, long RegionQueries, long Layouts,
        long Projections, long SceneDraws, long Frames, long Captures, long Pixels, long BusyFrames);

    internal static void Run(AppController controller, string output)
    {
        typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, true);
        controller.State.Theme = "light";
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.UseCapsuleMode = true;
        controller.State.MatchAuxiliaryMaterialStrength = true;
        controller.State.ExperimentalInactivePaperOpacity = false;
        controller.State.ExperimentalRestingCapsuleOpacity = false;
        var cases = new[] {
            new Case(PaperSkins.Paper, false, false), new Case(PaperSkins.Mica, false, true),
            new Case(PaperSkins.Acrylic, false, true), new Case(PaperSkins.ClearAcrylic, false, true),
            new Case(PaperSkins.TracingPaper, false, true), new Case(PaperSkins.Aero, false, true),
            new Case(PaperSkins.Paper, true, false), new Case(PaperSkins.Acrylic, true, false),
            new Case(PaperSkins.Acrylic, true, true), new Case(PaperSkins.Aero, true, true),
            new Case(PaperSkins.Aero, false, true, false)
        };
        GetCursorPos(out var oldCursor);
        var timerChanged = timeBeginPeriod(1) == 0;
        var results = new List<object>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        try
        {
            foreach (var c in cases)
            {
                controller.State.PaperSkin = c.Skin;
                controller.State.EnableAnimations = c.Animations;
                controller.State.LiveBackgroundProcessing = c.Live;
                Theme.Invalidate();
                var paper = new PaperData { Type = PaperTypes.Todo, Title = "Drag measurement", X = 240,
                    Y = 180, Width = 400, Height = 300, AlwaysOnTop = true };
                controller.State.Papers.Add(paper);
                var window = new PaperWindow(paper, controller);
                try
                {
                    window.Show(); window.Activate(); Wait(250);
                    if (c.Capsule) window.SetCollapsedState(true, animate: false, saveGeometry: false);
                    window.Left = 240; window.Top = 180; Wait(500);
                    if (!c.Capsule && PaperSkins.UsesNativeBackdrop(c.Skin))
                        Program.Assert(window.IsNativeMicaEffective, "native backdrop active for " + c.Skin);
                    var surfaces = Descendants(window).OfType<SkinBorder>().ToArray();
                    Program.Assert(surfaces.Length > 0, "production skin surface exists");
                    if (c.Capsule && c.Skin == PaperSkins.Acrylic && c.Live)
                        Program.Assert(surfaces.Any(s => s.IsBackgroundActive), "capsule background capture active before dragging");
                    var target = (FrameworkElement)typeof(PaperWindow).GetField(
                        c.Capsule ? "_capsuleLeftArea" : "_topBar", Private)!.GetValue(window)!;
                    // First gesture warms the real native move path; two subsequent gestures
                    // are measured without recreating the HWND, editor or capture session.
                    for (var round = -1; round < 2; round++)
                    {
                        window.Left = 240; window.Top = 180; Wait(180);
                        var sample = Drag(window, target, surfaces, c, round);
                        if (round >= 0) results.Add(sample);
                    }
                }
                finally
                {
                    window.CloseForReal(); controller.State.Papers.Remove(paper); Wait(60);
                }
                WriteResults();
            }
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(oldCursor.X, oldCursor.Y);
            if (timerChanged) timeEndPeriod(1);
            WriteResults();
        }
        void WriteResults() => File.WriteAllText(output, JsonSerializer.Serialize(new {
            Revision = Environment.GetEnvironmentVariable("PAPER_BENCH_REVISION"),
            OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
            Processors = Environment.ProcessorCount, RenderTier = RenderCapability.Tier >> 16,
            ScreenWidth = SystemParameters.PrimaryScreenWidth, ScreenHeight = SystemParameters.PrimaryScreenHeight,
            Scope = "Real mouse title/capsule dragging. Process CPU includes the input driver; DWM is whole-session CPU. Position events are not displayed frames. Instrumentation is identical and test-only.",
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Drag(PaperWindow window, FrameworkElement target, SkinBorder[] surfaces, Case c, int round)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd)!;
        var content = window.Content;
        var captures = surfaces.Select(s => s.BackgroundSessionState?.Capture).OfType<DesktopBackgroundCapture>().ToArray();
        long layouts = 0;
        EventHandler layout = (_, _) => layouts++;
        window.LayoutUpdated += layout;
        var positions = new List<PositionSample>(200);
        var nativeSizeMessages = 0; var enters = 0; var exits = 0;
        var watch = Stopwatch.StartNew();
        GetWindowRect(hwnd, out var previous);
        HwndSourceHook hook = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) => {
            if (msg == 0x0231) enters++;
            if (msg == 0x0232) exits++;
            if (msg == 0x0047 && GetWindowRect(h, out var bounds))
            {
                if (bounds.Right - bounds.Left != previous.Right - previous.Left ||
                    bounds.Bottom - bounds.Top != previous.Bottom - previous.Top) nativeSizeMessages++;
                if (bounds.Left != previous.Left || bounds.Top != previous.Top)
                {
                    GetCursorPos(out var cursor);
                    positions.Add(new(watch.Elapsed.TotalMilliseconds, bounds.Left, bounds.Top, cursor.X, cursor.Y));
                }
                previous = bounds;
            }
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        var start = target.PointToScreen(new Point(Math.Min(100, target.ActualWidth * .55), target.ActualHeight * .5));
        Program.Assert(start.X > 100 && start.Y > 80, "input trajectory has room without screen docking");
        SetCursorPos((int)start.X, (int)start.Y); Wait(60);
        var before = ReadCounters();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var dwm = DwmCpu();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var uiAllocated = GC.GetAllocatedBytesForCurrentThread();
        positions.Clear(); watch.Restart();
        var driver = Task.Run(() => {
            var times = new double[160];
            try
            {
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30);
                SetCursorPos((int)start.X + 12, (int)start.Y + 4);
                Thread.Sleep(40);
                for (var i = 0; i < times.Length; i++)
                {
                    var t = (i + 1d) / times.Length;
                    var x = (int)Math.Round(start.X + 12 + 140 * Math.Sin(2 * Math.PI * t) + 60 * t);
                    var y = (int)Math.Round(start.Y + 4 + 35 * Math.Sin(Math.PI * t) + 24 * t);
                    if (!SetCursorPos(x, y)) throw new InvalidOperationException("SetCursorPos failed");
                    times[i] = watch.Elapsed.TotalMilliseconds;
                    Thread.Sleep(8);
                }
                Thread.Sleep(30);
                return times;
            }
            finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
        });
        try
        {
            while (!driver.IsCompleted && watch.Elapsed.TotalSeconds < 15) Wait(10);
            Program.Assert(driver.IsCompleted, "mouse drag ends after release");
            var times = driver.GetAwaiter().GetResult();
            Wait(40);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
            var endDwm = DwmCpu();
            var uiBytes = GC.GetAllocatedBytesForCurrentThread() - uiAllocated;
            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            var after = ReadCounters();
            Program.Assert(positions.Count >= 35, $"actual drag moved HWND: {c}, moves={positions.Count}");
            Program.Assert(ReferenceEquals(content, window.Content) && new WindowInteropHelper(window).Handle == hwnd,
                "drag preserves window and content identity");
            Program.Assert(surfaces.All(s => s.BackgroundFailure == null), "no background capture failure while dragging");
            Program.Assert(captures.All(capture => !capture.IsStopped), "drag does not restart capture worker");
            var intervals = positions.Zip(positions.Skip(1), (a, b) => b.Milliseconds - a.Milliseconds).ToArray();
            var inputIntervals = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
            var changes = new Counters(after.Draws-before.Draws, after.Refreshes-before.Refreshes,
                after.RegionQueries-before.RegionQueries, after.Layouts-before.Layouts,
                after.Projections-before.Projections, after.SceneDraws-before.SceneDraws,
                after.Frames-before.Frames, after.Captures-before.Captures, after.Pixels-before.Pixels,
                after.BusyFrames-before.BusyFrames);
            Console.WriteLine($"DRAG {c.Skin}/{(c.Capsule ? "capsule" : "paper")}/live={c.Live}/anim={c.Animations}/r={round}: moves={positions.Count}, cpu={cpuMs:F1}ms, draws={changes.Draws}, refresh={changes.Refreshes}, regions={changes.RegionQueries}, captures={changes.Captures}");
            return new { c.Skin, c.Capsule, c.Live, c.Animations, Round = round, DurationMs = elapsed,
                ProcessCpuMs = cpuMs, DwmCpuMs = dwm.HasValue && endDwm.HasValue ? endDwm-dwm : null,
                UiAllocatedBytes = uiBytes, ProcessAllocatedBytes = bytes, NativeMoves = positions.Count,
                NativeSizeMessages = nativeSizeMessages, EnterSizeMove = enters, ExitSizeMove = exits,
                InputIntervalMedianMs = Percentile(inputIntervals, .5), MovementIntervalMedianMs = Percentile(intervals, .5),
                MovementIntervalP95Ms = Percentile(intervals, .95), Counters = changes,
                SurfaceCount = surfaces.Length, ActiveBackgrounds = surfaces.Count(s => s.IsBackgroundActive),
                NativeBackdropActive = window.IsNativeMicaEffective, Dpi = VisualTreeHelper.GetDpi(target).DpiScaleX,
                Positions = positions };
        }
        finally { source.RemoveHook(hook); window.LayoutUpdated -= layout; }

        Counters ReadCounters() => new(
            surfaces.Sum(s => TestCounter(s, "_dragDraws")), surfaces.Sum(s => TestCounter(s, "_dragRefreshes")),
            surfaces.Sum(s => TestCounter(s.BackgroundSessionState, "_dragRegions")), layouts,
            surfaces.Sum(s => s.BackgroundProjectionCount), surfaces.Sum(s => s.BackgroundSceneDrawCount),
            surfaces.Sum(s => s.BackgroundFrameCount), captures.Sum(s => s.CaptureCount),
            captures.Sum(s => s.SampledPixels), surfaces.Sum(s => s.BackgroundSessionState?.BackgroundBusyFrames ?? 0));
    }
    private static long TestCounter(object? value, string name) => value == null ? 0 :
        Convert.ToInt64(value.GetType().GetField(name, Private | BindingFlags.Public)?.GetValue(value) ?? 0);
    private static double Percentile(double[] values, double percentile) => values.Length == 0 ? 0 :
        values.Order().ElementAt(Math.Min(values.Length - 1, (int)Math.Floor((values.Length - 1) * percentile)));
    private static double? DwmCpu()
    {
        try { return Process.GetProcessesByName("dwm").Sum(p => { using (p) return p.TotalProcessorTime.TotalMilliseconds; }); }
        catch { return null; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint value);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint value);
}
