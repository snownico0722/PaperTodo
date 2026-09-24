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

// Issue #36 diagnostic only. The production default-paper path is left unchanged;
// each measured run only toggles the existing chrome Effect between its real value and null.
internal static class MaterialResizeBenchmarks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private sealed record SizeSample(double Milliseconds, int Width, int Height);

    private sealed record Measurement(
        bool ShadowEnabled,
        int Round,
        double DurationMs,
        double ProcessCpuMs,
        double? DwmCpuMs,
        long UiAllocatedBytes,
        long ProcessAllocatedBytes,
        int NativeSizeMessages,
        long Layouts,
        int GeometryBuilds,
        double DriverIntervalMedianMs,
        double ResizeIntervalMedianMs,
        double ResizeIntervalP95Ms,
        int FinalWidth,
        int FinalHeight,
        double Dpi,
        List<SizeSample> Sizes);

    internal static int RunIsolated(string output) =>
        MaterialDragBenchmarks.RunIsolatedCore("--resize-shadow-fixture", output);

    internal static void Run(AppController controller, string output)
    {
        // MaterialDragBenchmarks forces native HWNDs for its material matrix. That would make
        // the "paper" case unlike the real default paper. Force the actual legacy/default path.
        typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, false);
        controller.State.Theme = "light";
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.PaperSkin = PaperSkins.Paper;
        controller.State.EnableAnimations = false;
        controller.State.UseCapsuleMode = false;
        controller.State.ResizeGripMode = ResizeGripModes.Standard;
        Theme.Invalidate();

        var timerChanged = timeBeginPeriod(1) == 0;
        var results = new List<Measurement>();
        var paper = new PaperData
        {
            Type = PaperTypes.Todo,
            Title = "Resize shadow measurement",
            X = 100,
            Y = 80,
            Width = 420,
            Height = 320,
            AlwaysOnTop = true
        };
        controller.State.Papers.Add(paper);
        var window = new PaperWindow(paper, controller);

        try
        {
            window.Show();
            window.Activate();
            Wait(300);
            ResetWindow(window);

            Program.Assert(window.AllowsTransparency,
                "default paper benchmark must use the transparent WPF window path");
            Program.Assert(!window.IsNativeMicaEffective,
                "default paper benchmark must not activate a native backdrop");

            var chrome = (UIElement)typeof(PaperWindow)
                .GetField("_paperChrome", Private)!.GetValue(window)!;
            var productionShadow = chrome.Effect;
            Program.Assert(productionShadow is DropShadowEffect,
                "default paper chrome has the production DropShadowEffect");

            var surfaces = Descendants(window).OfType<SkinBorder>().ToArray();
            Program.Assert(surfaces.Length > 0, "production skin surface exists");

            // Warm both variants once. Measured rounds alternate order so later/hotter samples
            // do not consistently favor either side.
            Measure(window, chrome, productionShadow, surfaces, true, -1);
            Measure(window, chrome, productionShadow, surfaces, false, -1);

            var orders = new[]
            {
                new[] { true, false },
                new[] { false, true },
                new[] { true, false }
            };
            for (var round = 0; round < orders.Length; round++)
                foreach (var shadowEnabled in orders[round])
                    results.Add(Measure(
                        window, chrome, productionShadow, surfaces, shadowEnabled, round));

            WriteResults();
        }
        finally
        {
            if (timerChanged) timeEndPeriod(1);
            window.CloseForReal();
            controller.State.Papers.Remove(paper);
        }

        void WriteResults()
        {
            var withShadow = results.Where(x => x.ShadowEnabled).ToArray();
            var withoutShadow = results.Where(x => !x.ShadowEnabled).ToArray();
            var shadowCpu = Median(withShadow.Select(x => x.ProcessCpuMs));
            var noShadowCpu = Median(withoutShadow.Select(x => x.ProcessCpuMs));
            var shadowP95 = Median(withShadow.Select(x => x.ResizeIntervalP95Ms));
            var noShadowP95 = Median(withoutShadow.Select(x => x.ResizeIntervalP95Ms));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                Revision = Environment.GetEnvironmentVariable("PAPER_BENCH_REVISION"),
                OS = RuntimeInformation.OSDescription,
                Runtime = RuntimeInformation.FrameworkDescription,
                Processors = Environment.ProcessorCount,
                RenderTier = RenderCapability.Tier >> 16,
                DragFullWindows = SystemParameters.DragFullWindows,
                Scope = "Issue #36. One empty production default-paper HWND receives the same 160-step native SetWindowPos size trajectory. Same HWND/content/timing; only _paperChrome.Effect alternates between the production DropShadowEffect and null. This bypasses hosted Windows' outline-only interactive resize while retaining real HWND resize, WPF layout/render and DWM work. Diagnostic only, not a performance gate.",
                Summary = new
                {
                    ShadowProcessCpuMedianMs = shadowCpu,
                    NoShadowProcessCpuMedianMs = noShadowCpu,
                    ProcessCpuSavedPercentWhenShadowDisabled = PercentSaved(shadowCpu, noShadowCpu),
                    ShadowDwmCpuMedianMs = NullableMedian(withShadow.Select(x => x.DwmCpuMs)),
                    NoShadowDwmCpuMedianMs = NullableMedian(withoutShadow.Select(x => x.DwmCpuMs)),
                    ShadowResizeIntervalP95MedianMs = shadowP95,
                    NoShadowResizeIntervalP95MedianMs = noShadowP95,
                    ResizeP95SavedPercentWhenShadowDisabled = PercentSaved(shadowP95, noShadowP95)
                },
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static Measurement Measure(
        PaperWindow window,
        UIElement chrome,
        Effect productionShadow,
        SkinBorder[] surfaces,
        bool shadowEnabled,
        int round)
    {
        ResetWindow(window);
        chrome.Effect = shadowEnabled ? productionShadow : null;
        Wait(160);

        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd)!;
        var content = window.Content;
        Program.Assert(GetWindowRect(hwnd, out var initial), "read initial resize bounds");

        long layouts = 0;
        EventHandler layout = (_, _) => layouts++;
        window.LayoutUpdated += layout;
        var samples = new List<SizeSample>(200);
        var nativeSizeMessages = 0;
        var previous = initial;
        var watch = Stopwatch.StartNew();

        HwndSourceHook hook = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == 0x0047 && GetWindowRect(h, out var bounds))
            {
                var width = bounds.Right - bounds.Left;
                var height = bounds.Bottom - bounds.Top;
                if (width != previous.Right - previous.Left ||
                    height != previous.Bottom - previous.Top)
                {
                    nativeSizeMessages++;
                    samples.Add(new(watch.Elapsed.TotalMilliseconds, width, height));
                }
                previous = bounds;
            }
            return IntPtr.Zero;
        };
        source.AddHook(hook);

        var initialWidth = initial.Right - initial.Left;
        var initialHeight = initial.Bottom - initial.Top;
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var uiAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var geometryBefore = surfaces.Sum(x => x.GeometryBuildCount);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var dwmBefore = DwmCpu();

        samples.Clear();
        watch.Restart();
        var driver = Task.Run(() =>
        {
            var times = new double[160];
            for (var i = 0; i < times.Length; i++)
            {
                var t = (i + 1d) / times.Length;
                var width = initialWidth + (int)Math.Round(220 * t);
                var height = initialHeight + (int)Math.Round(140 * t);
                if (!SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    0,
                    0,
                    width,
                    height,
                    SwpNoMove | SwpNoZOrder | SwpNoActivate))
                {
                    throw new InvalidOperationException("SetWindowPos failed during resize benchmark");
                }
                times[i] = watch.Elapsed.TotalMilliseconds;
                Thread.Sleep(8);
            }
            return times;
        });

        try
        {
            while (!driver.IsCompleted && watch.Elapsed.TotalSeconds < 20)
                Wait(10);

            Program.Assert(driver.IsCompleted, "native resize driver completes");
            var driverTimes = driver.GetAwaiter().GetResult();
            Wait(100);

            var elapsed = watch.Elapsed.TotalMilliseconds;
            var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var dwmAfter = DwmCpu();
            var uiBytes = GC.GetAllocatedBytesForCurrentThread() - uiAllocatedBefore;
            var processBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            var geometryBuilds = surfaces.Sum(x => x.GeometryBuildCount) - geometryBefore;

            Program.Assert(GetWindowRect(hwnd, out var final), "read final resize bounds");
            var finalWidth = final.Right - final.Left;
            var finalHeight = final.Bottom - final.Top;
            Program.Assert(samples.Count >= 120,
                $"native resize changed HWND repeatedly: shadow={shadowEnabled}, samples={samples.Count}");
            Program.Assert(
                finalWidth >= initialWidth + 210 &&
                finalHeight >= initialHeight + 130,
                $"native resize reached target extent: shadow={shadowEnabled}, final={finalWidth}x{finalHeight}");
            Program.Assert(
                ReferenceEquals(content, window.Content) &&
                new WindowInteropHelper(window).Handle == hwnd,
                "resize preserves window and content identity");

            var resizeIntervals = samples.Zip(
                samples.Skip(1), (a, b) => b.Milliseconds - a.Milliseconds).ToArray();
            var driverIntervals = driverTimes.Zip(
                driverTimes.Skip(1), (a, b) => b - a).ToArray();

            var measurement = new Measurement(
                shadowEnabled,
                round,
                elapsed,
                cpuMs,
                dwmBefore.HasValue && dwmAfter.HasValue ? dwmAfter - dwmBefore : null,
                uiBytes,
                processBytes,
                nativeSizeMessages,
                layouts,
                geometryBuilds,
                Percentile(driverIntervals, .5),
                Percentile(resizeIntervals, .5),
                Percentile(resizeIntervals, .95),
                finalWidth,
                finalHeight,
                VisualTreeHelper.GetDpi(window).DpiScaleX,
                samples);

            Console.WriteLine(
                $"RESIZE shadow={shadowEnabled}/r={round}: sizes={samples.Count}, " +
                $"cpu={cpuMs:F1}ms, p95={measurement.ResizeIntervalP95Ms:F2}ms, " +
                $"layouts={layouts}, geometry={geometryBuilds}");
            return measurement;
        }
        finally
        {
            source.RemoveHook(hook);
            window.LayoutUpdated -= layout;
        }
    }

    private static void ResetWindow(PaperWindow window)
    {
        window.Left = 100;
        window.Top = 80;
        window.Width = 420;
        window.Height = 320;
        window.UpdateLayout();
        Wait(180);
    }

    private static double PercentSaved(double baseline, double candidate) =>
        baseline <= 0 ? 0 : (baseline - candidate) / baseline * 100;

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return 0;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static double? NullableMedian(IEnumerable<double?> values)
    {
        var present = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return present.Length == 0 ? null : Median(present);
    }

    private static double Percentile(double[] values, double percentile) =>
        values.Length == 0 ? 0 :
        values.Order().ElementAt(Math.Min(
            values.Length - 1,
            (int)Math.Floor((values.Length - 1) * percentile)));

    private static double? DwmCpu()
    {
        try
        {
            return Process.GetProcessesByName("dwm")
                .Sum(p => { using (p) return p.TotalProcessorTime.TotalMilliseconds; });
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return item;
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint value);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint value);
}
