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
    private sealed record Case(string Skin, bool Capsule, bool Animations = true);
    private sealed record PositionSample(double Milliseconds, int X, int Y, int CursorX, int CursorY);
    private sealed record Counters(long Draws, long Refreshes, long RegionQueries, long Layouts,
        long Projections, long SceneDraws, long Frames);

    internal const string FixtureMarker = ".papertodo-drag-fixture";
    internal static int RunIsolated(string output) => RunIsolatedCore("--drag-fixture", output);
    internal static int RunSnapshotTimingIsolated(string output) =>
        RunIsolatedCore("--drag-snapshot-fixture", output);

    private static int RunIsolatedCore(string childMode, string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo.DragChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // The real controller persists beside its executable. Never load/copy user data
            // or reuse settings left by a previous benchmark process.
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is not (".exe" or ".dll" or ".pdb") &&
                    !file.EndsWith(".deps.json") && !file.EndsWith(".runtimeconfig.json")) continue;
                var destination = Path.Combine(directory, Path.GetRelativePath(AppContext.BaseDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            File.WriteAllText(Path.Combine(directory, FixtureMarker), "isolated drag checks");
            var start = new ProcessStartInfo(Path.Combine(directory, "PaperTodo.MaterialBenchmarks.exe"))
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            start.ArgumentList.Add(childMode); start.ArgumentList.Add(Path.GetFullPath(output));
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(240_000))
            {
                child.Kill(entireProcessTree: true); child.WaitForExit();
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                throw new TimeoutException("Isolated drag check did not finish.");
            }
            Console.Write(stdout.GetAwaiter().GetResult()); Console.Error.Write(stderr.GetAwaiter().GetResult());
            return child.ExitCode;
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    internal static void RunSnapshotTiming(AppController controller, string output)
    {
        typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, true);
        controller.State.Theme = "light";
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.PaperSkin = PaperSkins.Acrylic;
        controller.State.UseCapsuleMode = true;
        controller.State.UseDeepCapsuleMode = false;
        controller.State.MatchAuxiliaryMaterialStrength = true;
        controller.State.ExperimentalInactivePaperOpacity = false;
        controller.State.ExperimentalRestingCapsuleOpacity = false;
        Theme.Invalidate();

        GetCursorPos(out var oldCursor);
        var paper = new PaperData
        {
            Type = PaperTypes.Todo,
            Title = "Drag snapshot timing",
            X = 240,
            Y = 180,
            Width = 400,
            Height = 300,
            AlwaysOnTop = true
        };
        controller.State.Papers.Add(paper);
        var window = new PaperWindow(paper, controller);
        var buttonHeld = 0;
        var sawNativeMovingWhilePressed = false;
        var sawDragSnapshotWhilePressed = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };

        try
        {
            window.Show();
            window.Activate();
            Wait(250);
            window.SetCollapsedState(true, animate: false, saveGeometry: false);
            window.Left = 240;
            window.Top = 180;
            Wait(500);

            var surfaces = Descendants(window).OfType<SkinBorder>().Where(s => s.IsCapsule).ToArray();
            Program.Assert(surfaces.Length > 0, "production capsule material surface exists");
            var ready = Stopwatch.StartNew();
            while (!surfaces.Any(s => s.IsBackgroundActive) && ready.Elapsed.TotalSeconds < 6) Wait(20);
            Program.Assert(surfaces.Any(s => s.IsBackgroundActive),
                "sampled capsule has its initial static snapshot");

            var target = (FrameworkElement)typeof(PaperWindow)
                .GetField("_capsuleLeftArea", Private)!.GetValue(window)!;
            var pointerState = typeof(PaperWindow).GetField("_capsulePointerState", Private)!;
            var start = target.PointToScreen(new Point(target.ActualWidth * .55, target.ActualHeight * .5));
            Program.Assert(start.X > 40 && start.Y > 40, "capsule input target is on-screen");
            Program.Assert(SetCursorPos((int)start.X, (int)start.Y), "position capsule timing cursor");
            Wait(80);

            timer.Tick += (_, _) =>
            {
                if (Volatile.Read(ref buttonHeld) == 0) return;
                if (string.Equals(pointerState.GetValue(window)?.ToString(), "NativeMoving",
                        StringComparison.Ordinal))
                {
                    sawNativeMovingWhilePressed = true;
                }
                if (surfaces.Any(surface =>
                {
                    var session = surface.BackgroundSessionState;
                    return session?.GetType()
                        .GetField("_dragSnapshotActive", Private)?.GetValue(session) is true;
                }))
                {
                    sawDragSnapshotWhilePressed = true;
                }
            };
            timer.Start();

            var driver = Task.Run(() =>
            {
                try
                {
                    Interlocked.Exchange(ref buttonHeld, 1);
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(80);
                    if (!SetCursorPos((int)start.X + 28, (int)start.Y + 8))
                        throw new InvalidOperationException("SetCursorPos failed");
                    Thread.Sleep(1500);
                }
                finally
                {
                    Interlocked.Exchange(ref buttonHeld, 0);
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                }
            });

            var watch = Stopwatch.StartNew();
            while (!driver.IsCompleted && watch.Elapsed.TotalSeconds < 8) Wait(10);
            Program.Assert(driver.IsCompleted, "focused capsule drag input completes");
            driver.GetAwaiter().GetResult();
            Wait(80);

            Program.Assert(sawNativeMovingWhilePressed,
                "production capsule handler entered native DragMove while the button was held");
            Program.Assert(sawDragSnapshotWhilePressed,
                "drag snapshot becomes active before native DragMove returns and before button release");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                Revision = Environment.GetEnvironmentVariable("PAPER_BENCH_REVISION"),
                OS = RuntimeInformation.OSDescription,
                Runtime = RuntimeInformation.FrameworkDescription,
                SawNativeMovingWhilePressed = sawNativeMovingWhilePressed,
                SawDragSnapshotWhilePressed = sawDragSnapshotWhilePressed
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS real capsule drag publishes the shared snapshot before button release.");
        }
        finally
        {
            timer.Stop();
            Interlocked.Exchange(ref buttonHeld, 0);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(oldCursor.X, oldCursor.Y);
            window.CloseForReal();
            controller.State.Papers.Remove(paper);
            Wait(60);
        }
    }

    internal static void Run(AppController controller, string output)
    {
        typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, true);
        controller.State.Theme = "light";
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.UseCapsuleMode = true;
        controller.State.UseDeepCapsuleMode = false;
        controller.State.MatchAuxiliaryMaterialStrength = true;
        controller.State.ExperimentalInactivePaperOpacity = false;
        controller.State.ExperimentalRestingCapsuleOpacity = false;
        var cases = new[] {
            new Case(PaperSkins.Paper, false), new Case(PaperSkins.Mica, false),
            new Case(PaperSkins.Acrylic, false), new Case(PaperSkins.ClearAcrylic, false),
            new Case(PaperSkins.TracingPaper, false), new Case(PaperSkins.Aero, false),
            new Case(PaperSkins.Paper, true), new Case(PaperSkins.Acrylic, true),
            new Case(PaperSkins.Aero, true), new Case(PaperSkins.Aero, false, false)
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
                    if (c.Capsule && c.Skin == PaperSkins.Acrylic)
                        Program.Assert(surfaces.Any(s => s.IsBackgroundActive),
                            "capsule static background is ready before dragging");
                    var target = (FrameworkElement)typeof(PaperWindow).GetField(
                        c.Capsule ? "_capsuleLeftArea" : "_topBar", Private)!.GetValue(window)!;
                    // First gesture warms the real native move path; two subsequent gestures
                    // are measured without recreating the HWND or editor.
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
        long layouts = 0;
        EventHandler layout = (_, _) => layouts++;
        window.LayoutUpdated += layout;
        var positions = new List<PositionSample>(200);
        var nativeSizeMessages = 0; var enters = 0; var exits = 0;
        var sawDragSnapshotWhilePressed = false;
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
        var start = target.PointToScreen(new Point(target.ActualWidth * (c.Capsule ? .55 : .5), target.ActualHeight * .5));
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
            while (!driver.IsCompleted && watch.Elapsed.TotalSeconds < 15)
            {
                if (c.Capsule && PaperSkins.UsesSampledAuxiliary(c.Skin))
                {
                    foreach (var surface in surfaces)
                    {
                        var session = surface.BackgroundSessionState;
                        if (session?.GetType().GetField("_dragSnapshotActive", Private)?.GetValue(session) is true)
                        {
                            sawDragSnapshotWhilePressed = true;
                            break;
                        }
                    }
                }
                Wait(10);
            }
            Program.Assert(driver.IsCompleted, "mouse drag ends after release");
            var times = driver.GetAwaiter().GetResult();
            if (c.Capsule && PaperSkins.UsesSampledAuxiliary(c.Skin))
                Program.Assert(sawDragSnapshotWhilePressed,
                    "real capsule drag publishes the shared virtual-desktop snapshot before button release");
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
            var intervals = positions.Zip(positions.Skip(1), (a, b) => b.Milliseconds - a.Milliseconds).ToArray();
            var inputIntervals = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
            var changes = new Counters(after.Draws-before.Draws, after.Refreshes-before.Refreshes,
                after.RegionQueries-before.RegionQueries, after.Layouts-before.Layouts,
                after.Projections-before.Projections, after.SceneDraws-before.SceneDraws,
                after.Frames-before.Frames);
            Console.WriteLine($"DRAG {c.Skin}/{(c.Capsule ? "capsule" : "paper")}/anim={c.Animations}/r={round}: moves={positions.Count}, cpu={cpuMs:F1}ms, draws={changes.Draws}, refresh={changes.Refreshes}, regions={changes.RegionQueries}, frames={changes.Frames}");
            return new { c.Skin, c.Capsule, c.Animations, Round = round, DurationMs = elapsed,
                ProcessCpuMs = cpuMs, DwmCpuMs = dwm.HasValue && endDwm.HasValue ? endDwm-dwm : null,
                UiAllocatedBytes = uiBytes, ProcessAllocatedBytes = bytes, NativeMoves = positions.Count,
                NativeSizeMessages = nativeSizeMessages, EnterSizeMove = enters, ExitSizeMove = exits,
                InputIntervalMedianMs = Percentile(inputIntervals, .5), MovementIntervalMedianMs = Percentile(intervals, .5),
                MovementIntervalP95Ms = Percentile(intervals, .95), Counters = changes,
                SurfaceCount = surfaces.Length, ActiveBackgrounds = surfaces.Count(s => s.IsBackgroundActive),
                NativeBackdropActive = window.IsNativeMicaEffective, Dpi = VisualTreeHelper.GetDpi(target).DpiScaleX,
                SawDragSnapshotWhilePressed = sawDragSnapshotWhilePressed, Positions = positions };
        }
        finally { source.RemoveHook(hook); window.LayoutUpdated -= layout; }

        Counters ReadCounters() => new(
            surfaces.Sum(s => TestCounter(s, "_dragDraws")), surfaces.Sum(s => TestCounter(s, "_dragRefreshes")),
            surfaces.Sum(s => TestCounter(s.BackgroundSessionState, "_dragRegions")), layouts,
            surfaces.Sum(s => s.BackgroundProjectionCount), surfaces.Sum(s => s.BackgroundSceneDrawCount),
            surfaces.Sum(s => s.BackgroundFrameCount));
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
