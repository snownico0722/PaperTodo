using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

// Temporary A/B harness. Copy this identical file to both revisions. The production
// transition runs normally in timed samples; exact-progress geometry probes run separately.
internal static class FormTransitionBenchmarks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal const string FixtureMarker = ".papertodo-form-fixture";
    private static readonly Func<PaperWindow, bool> IsTransitioning = typeof(PaperWindow)
        .GetProperty("IsPaperFormTransitioning", Private)!.GetMethod!
        .CreateDelegate<Func<PaperWindow, bool>>();
    private static readonly FieldInfo ChromeField = typeof(PaperWindow).GetField("_paperChrome", Private)!;

    internal static int RunIsolated(string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo.FormMeasurements", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // AppController writes beside its executable. Copy runtime files only, never
            // an existing data.json, plugin state, or files from a previous measurement.
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is not (".exe" or ".dll" or ".pdb") &&
                    !file.EndsWith(".deps.json") && !file.EndsWith(".runtimeconfig.json")) continue;
                var destination = Path.Combine(directory, Path.GetRelativePath(AppContext.BaseDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            File.WriteAllText(Path.Combine(directory, FixtureMarker), "isolated form measurements");
            var start = new ProcessStartInfo(Path.Combine(directory, "PaperTodo.MaterialBenchmarks.exe"))
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--form-fixture");
            start.ArgumentList.Add(Path.GetFullPath(output));
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(240_000))
            {
                child.Kill(entireProcessTree: true); child.WaitForExit();
                throw new TimeoutException("Isolated form measurements did not finish.");
            }
            Console.Write(stdout.GetAwaiter().GetResult());
            Console.Error.Write(stderr.GetAwaiter().GetResult());
            return child.ExitCode;
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    internal static void Run(AppController controller, string output)
    {
        typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, true);
        controller.State.Theme = "light";
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.UseCapsuleMode = true;
        controller.State.UseDeepCapsuleMode = false;
        controller.State.EnableAnimations = true;
        controller.State.ExperimentalInactivePaperOpacity = false;
        controller.State.ExperimentalRestingCapsuleOpacity = false;
        controller.State.MicaAlwaysActive = true;
        var results = new List<object>();
        var exactFrames = new List<object>();
        string? failure = null;
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            foreach (var skin in new[] { PaperSkins.Acrylic, PaperSkins.Mica, PaperSkins.ClearAcrylic,
                PaperSkins.TracingPaper, PaperSkins.Aero })
            foreach (var full in new[] { false, true })
            {
                controller.State.PaperSkin = skin;
                controller.State.MatchAuxiliaryMaterialStrength = full;
                Theme.Invalidate();
                var paper = new PaperData
                {
                    Type = PaperTypes.Todo, Title = "Material form measurement", X = 180, Y = 140,
                    Width = 432, Height = 370, AlwaysOnTop = true,
                    Items = Enumerable.Range(0, 12).Select(i => new PaperItem
                    {
                        Text = $"Item {i + 1}: production paper content", Order = i, Done = i % 4 == 0
                    }).ToList()
                };
                controller.State.Papers.Add(paper);
                var window = new PaperWindow(paper, controller);
                try
                {
                    window.Show(); window.Activate();
                    PumpFor(250);
                    var chrome = (SkinBorder)ChromeField.GetValue(window)!;
                    Program.Assert(window.IsNativeMicaEffective,
                        "Native material must be active before the form measurement: " + skin);
                    // One full production cycle warms JIT and the material transition path.
                    var rounds = skin == PaperSkins.Acrylic ? 5 : 2;
                    for (var round = -1; round < rounds; round++)
                    foreach (var collapsed in new[] { true, false })
                    {
                        var sample = Measure(window, chrome, skin, full, round, collapsed);
                        if (round >= 0 || !sample.Passed) results.Add(sample.Values);
                        Program.Assert(sample.Passed, sample.Failure ?? "Form measurement failed.");
                    }
                    if (skin == PaperSkins.Acrylic)
                    {
                        exactFrames.Add(MeasureExactFrames(window, chrome, full, collapsed: true));
                        exactFrames.Add(MeasureExactFrames(window, chrome, full, collapsed: false));
                    }
                }
                finally
                {
                    window.CloseForReal();
                    controller.State.Papers.Remove(paper);
                    PumpFor(60);
                }
                WriteResults();
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally { WriteResults(); }

        void WriteResults() => File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Revision = Environment.GetEnvironmentVariable("PAPER_BENCH_REVISION"),
            OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
            Processors = Environment.ProcessorCount, RenderTier = RenderCapability.Tier >> 16,
            NativeSupported = NativeMicaBackdrop.IsSupported,
            DwmComposition = DwmMicaApi.Instance.CompositionEnabled,
            TransparencyEnabled = DwmMicaApi.Instance.TransparencyEnabled,
            Scope = "Ordinary native-session PaperWindow.SetCollapsedState animations. No deep edge capsules. " +
                "Rendering intervals are WPF event intervals, not scanout FPS. CPU and allocation include identical observation overhead. " +
                "Process CPU includes capture/render worker threads, not the external DWM process. " +
                "Capture objects are observed at UI dispatcher operation boundaries, WM_SIZE/WM_WINDOWPOSCHANGED and Rendering; " +
                "creation and cancellation entirely inside one synchronous callback can escape observation. " +
                "Untimed endpoint settling and manual 0..1 probes are excluded from animation timing.",
            Failure = failure, Results = results, ExactFrames = exactFrames
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record MeasuredTransition(object Values, bool Passed, string? Failure);

    private static MeasuredTransition Measure(PaperWindow window, SkinBorder chrome, string skin, bool full, int round, bool collapsed)
    {
        var before = ReadEndpoint(window, chrome);
        using var observer = new TransitionObserver(window, chrome);
        observer.Start();
        window.SetCollapsedState(collapsed, animate: true, saveGeometry: false);
        observer.AfterStart();
        observer.WaitForCompletion();
        var atCompletion = ReadEndpoint(window, chrome);
        var settled = WaitForEndpoint(window, chrome, skin, full, collapsed);
        observer.ObserveCapture();
        var after = ReadEndpoint(window, chrome);
        var sample = new
        {
            Skin = skin, FullMaterial = full, Round = round,
            Direction = collapsed ? "collapse" : "expand",
            NominalDurationMilliseconds = 220, // collapse: 70ms delay + 150ms; expand: 220ms
            observer.Completed, observer.CompletionMilliseconds, observer.UiAllocatedBytes,
            observer.UiCpuMilliseconds, observer.ProcessCpuMilliseconds,
            observer.LayoutPasses, observer.SizeMessages, observer.SizeMessagesDuringTransition,
            observer.RenderingTimesMilliseconds, observer.RenderingIntervalsMilliseconds,
            CapturesObserved = observer.Captures.Count,
            CapturesDuringTransition = observer.Captures.Count(c => c.DuringTransition),
            CaptureObservations = observer.Captures,
            BackgroundFrames = chrome.BackgroundFrameCount - observer.InitialBackgroundFrames,
            Before = before, AtAnimationCompletion = atCompletion, EndpointReady = settled, After = after,
            NativeMaterialRestored = collapsed ? !window.IsNativeMicaEffective : window.IsNativeMicaEffective,
            FinalAffinityRestored = after.DisplayAffinity == before.DisplayAffinity
        };
        Console.WriteLine($"FORM {skin} full={full} round={round} {(collapsed ? "collapse" : "expand")}: " +
            $"{observer.CompletionMilliseconds:F2}ms, WM_SIZE={observer.SizeMessages}, " +
            $"captures={observer.Captures.Count} during={observer.Captures.Count(c => c.DuringTransition)}, endpoint={settled}");
        var failure = !observer.Completed || !settled ? "Production form transition or endpoint did not complete."
            : after.DisplayAffinity != before.DisplayAffinity ? "Final capture affinity was not restored."
            : collapsed == window.IsNativeMicaEffective ? "Native material did not match the final form."
            : null;
        return new(sample, failure == null, failure);
    }

    private static object MeasureExactFrames(PaperWindow window, SkinBorder chrome, bool full, bool collapsed)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        var steps = new List<object>();
        var messages = new List<NativeResize>(4);
        var collecting = false;
        var phase = "progress";
        HwndSourceHook hook = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (collecting && msg == 0x0005)
            {
                var packed = unchecked((uint)lp.ToInt64());
                messages.Add(new(phase, (int)(packed & 0xffff), (int)(packed >> 16)));
            }
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        try
        {
            window.SetCollapsedState(collapsed, animate: true, saveGeometry: false);
            window.BeginAnimation(PaperWindow.TransitionProgressProperty, null);
            window.UpdateLayout();
            // No dispatcher pumping, artificial Settle, or forced rendering inside this
            // probe. One UpdateLayout per exact sample matches the existing form-frame check.
            for (var i = 0; i <= 20; i++)
            {
                messages.Clear(); phase = "progress"; collecting = true;
                var progress = i / 20.0;
                window.TransitionProgress = progress;
                var immediate = messages.Count;
                phase = "layout";
                window.UpdateLayout();
                collecting = false;
                steps.Add(new
                {
                    Progress = progress, ImmediateSizeMessages = immediate,
                    TotalSizeMessages = messages.Count, NativeSizes = messages.ToArray(),
                    window.Width, window.Height, window.ActualWidth, window.ActualHeight,
                    ChromeWidth = chrome.Width, ChromeHeight = chrome.Height,
                    Margin = chrome.Margin.Left, window.MinWidth, window.MinHeight,
                    InnerOuterWidthDifference = chrome.Width + chrome.Margin.Left + chrome.Margin.Right - window.Width,
                    InnerOuterHeightDifference = chrome.Height + chrome.Margin.Top + chrome.Margin.Bottom - window.Height
                });
            }
        }
        finally
        {
            collecting = false;
            source.RemoveHook(hook);
            window.SettleAnimationsForDisabledSetting();
            window.UpdateLayout();
        }
        var settled = WaitForEndpoint(window, chrome, PaperSkins.Acrylic, full, collapsed);
        Program.Assert(settled, "Manual progress probe did not restore its endpoint.");
        return new { Skin = PaperSkins.Acrylic, FullMaterial = full,
            Direction = collapsed ? "collapse" : "expand", Steps = steps, After = ReadEndpoint(window, chrome) };
    }

    private sealed record NativeResize(string Phase, int Width, int Height);
    private sealed record CaptureObservation(double Milliseconds, bool DuringTransition);
    private sealed record Endpoint(double Width, double Height, double ActualWidth, double ActualHeight,
        int NativeWidth, int NativeHeight, double DpiX, double DpiY, string ResizeMode,
        bool NativeMaterial, bool Layered, uint? DisplayAffinity, bool BackgroundActive,
        bool CaptureActive, string? BackgroundFailure, bool Transitioning);

    private static Endpoint ReadEndpoint(PaperWindow window, SkinBorder chrome)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var rect);
        var dpi = VisualTreeHelper.GetDpi(window);
        return new(window.Width, window.Height, window.ActualWidth, window.ActualHeight,
            rect.Right - rect.Left, rect.Bottom - rect.Top, dpi.DpiScaleX, dpi.DpiScaleY,
            window.ResizeMode.ToString(), window.IsNativeMicaEffective, DwmMicaApi.Instance.IsLayered(hwnd),
            GetWindowDisplayAffinity(hwnd, out var affinity) ? affinity : null,
            chrome.IsBackgroundActive, chrome.HasBackgroundCapture, chrome.BackgroundFailure, IsTransitioning(window));
    }

    private static bool WaitForEndpoint(PaperWindow window, SkinBorder chrome, string skin, bool full, bool collapsed)
    {
        // Outside the timed interval: let pending material/geometry notifications run, then
        // require the real capture/native state. The delay is not treated as proof of completion.
        PumpFor(80);
        return PumpUntil(() => !IsTransitioning(window) && !chrome.HasBackgroundCapture &&
            (collapsed || window.IsNativeMicaEffective) &&
            (!collapsed || !full || !PaperSkins.UsesSampledAuxiliary(skin) ||
                chrome.IsBackgroundActive || chrome.BackgroundFailure != null), 4_000);
    }

    private sealed class TransitionObserver : IDisposable
    {
        private readonly PaperWindow _window;
        private readonly SkinBorder _chrome;
        private readonly HwndSource _source;
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly Stopwatch _watch = new();
        private readonly HashSet<DesktopBackgroundCapture> _seen = new();
        private readonly HwndSourceHook _hook;
        private long _allocatedStart;
        private double? _threadCpuStart;
        private TimeSpan _processCpuStart;
        private TimeSpan? _lastRenderingTime;
        private DispatcherFrame? _frame;
        private bool _started, _timing, _sawTransition;
        internal bool Completed { get; private set; }
        internal double CompletionMilliseconds { get; private set; }
        internal long UiAllocatedBytes { get; private set; }
        internal double? UiCpuMilliseconds { get; private set; }
        internal double ProcessCpuMilliseconds { get; private set; }
        internal int LayoutPasses { get; private set; }
        internal int SizeMessages { get; private set; }
        internal int SizeMessagesDuringTransition { get; private set; }
        internal int InitialBackgroundFrames { get; }
        internal List<double> RenderingTimesMilliseconds { get; } = new(64);
        internal List<double> RenderingIntervalsMilliseconds { get; } = new(64);
        internal List<CaptureObservation> Captures { get; } = new(32);

        internal TransitionObserver(PaperWindow window, SkinBorder chrome)
        {
            _window = window; _chrome = chrome;
            _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
            InitialBackgroundFrames = chrome.BackgroundFrameCount;
            if (chrome.BackgroundSessionState?.Capture is { } capture) _seen.Add(capture);
            _hook = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (_timing && msg == 0x0005)
                {
                    SizeMessages++;
                    if (IsTransitioning(_window)) SizeMessagesDuringTransition++;
                }
                if (msg is 0x0005 or 0x0047) ObserveCapture();
                return IntPtr.Zero;
            };
            _source.AddHook(_hook);
            window.LayoutUpdated += OnLayout;
            CompositionTarget.Rendering += OnRendering;
            window.Dispatcher.Hooks.OperationStarted += OnOperation;
            window.Dispatcher.Hooks.OperationCompleted += OnOperation;
        }

        internal void Start()
        {
            _allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            _threadCpuStart = ThreadCpuMilliseconds();
            _processCpuStart = _process.TotalProcessorTime;
            _started = _timing = true;
            _watch.Start();
        }
        internal void AfterStart()
        {
            ObserveCapture();
            _sawTransition |= IsTransitioning(_window);
            Program.Assert(_sawTransition, "SetCollapsedState must start the real production animation.");
        }
        internal void ObserveCapture()
        {
            if (!_started) return;
            var transitioning = IsTransitioning(_window);
            _sawTransition |= transitioning;
            if (_chrome.BackgroundSessionState?.Capture is { } capture && _seen.Add(capture))
                Captures.Add(new(_watch.Elapsed.TotalMilliseconds, transitioning));
        }
        private void OnOperation(object? sender, DispatcherHookEventArgs e)
        {
            ObserveCapture();
            if (_timing && _sawTransition && !IsTransitioning(_window)) FinishTiming(completed: true);
        }
        private void OnLayout(object? sender, EventArgs e) { if (_timing) LayoutPasses++; }
        private void OnRendering(object? sender, EventArgs e)
        {
            ObserveCapture();
            if (!_timing || e is not RenderingEventArgs rendering || _lastRenderingTime == rendering.RenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
            var time = _watch.Elapsed.TotalMilliseconds;
            if (RenderingTimesMilliseconds.Count > 0)
                RenderingIntervalsMilliseconds.Add(time - RenderingTimesMilliseconds[^1]);
            RenderingTimesMilliseconds.Add(time);
        }
        private void FinishTiming(bool completed)
        {
            if (!_timing) return;
            CompletionMilliseconds = _watch.Elapsed.TotalMilliseconds;
            UiAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _allocatedStart;
            UiCpuMilliseconds = ThreadCpuMilliseconds() - _threadCpuStart;
            ProcessCpuMilliseconds = (_process.TotalProcessorTime - _processCpuStart).TotalMilliseconds;
            Completed = completed; _timing = false;
            if (_frame != null) _frame.Continue = false;
        }
        internal void WaitForCompletion()
        {
            if (!_timing) return;
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(5) };
            timeout.Tick += (_, _) => FinishTiming(completed: false);
            _frame = new DispatcherFrame();
            timeout.Start();
            try { Dispatcher.PushFrame(_frame); }
            finally { timeout.Stop(); _frame = null; }
        }
        public void Dispose()
        {
            _started = _timing = false;
            _source.RemoveHook(_hook);
            _window.LayoutUpdated -= OnLayout;
            CompositionTarget.Rendering -= OnRendering;
            _window.Dispatcher.Hooks.OperationStarted -= OnOperation;
            _window.Dispatcher.Hooks.OperationCompleted -= OnOperation;
            _process.Dispose();
        }
    }

    private static void PumpFor(int milliseconds) => PumpUntil(() => false, milliseconds);
    private static bool PumpUntil(Func<bool> ready, int milliseconds)
    {
        if (ready()) return true;
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || watch.ElapsedMilliseconds >= milliseconds) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        return ready();
    }
    private static double? ThreadCpuMilliseconds() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? (kernel + user) / 10_000.0 : null;

    [StructLayout(LayoutKind.Sequential)] private struct RectI { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread,
        out long creation, out long exit, out long kernel, out long user);
}
