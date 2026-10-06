using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

// Exercise the main paper's real HWND and animation clocks. A stopped sampler on an
// unrelated Edge host, or a no-animation collapse, cannot satisfy the animated checks.
internal static class PaperFormMaterialChecks
{
    private static readonly PropertyInfo Transitioning = typeof(PaperWindow)
        .GetProperty("IsPaperFormTransitioning", Program.Private)!;
    private static readonly FieldInfo Chrome = typeof(PaperWindow)
        .GetField("_paperChrome", Program.Private)!;

    internal static void Run(AppController controller)
    {
        if (!NativeMicaBackdrop.IsSupported || !DwmMicaApi.Instance.EffectsEnabled ||
            SystemParameters.HighContrast)
        {
            Console.WriteLine("SKIP animated paper material: native composition and transparency required.");
            return;
        }

        var nativeMode = typeof(AppController).GetProperty("UsesNativeMicaWindows", Program.Private)!;
        var savedNative = controller.UsesNativeMicaWindows;
        var state = controller.State;
        var saved = (state.PaperSkin, state.Theme, state.EnableAnimations,
            state.MatchAuxiliaryMaterialStrength, state.UseCapsuleMode,
            state.UseDeepCapsuleMode, state.ExperimentalInactivePaperOpacity);
        try
        {
            nativeMode.SetValue(controller, true);
            state.PaperSkin = PaperSkins.Acrylic;
            state.Theme = "light";
            state.EnableAnimations = true;
            state.MatchAuxiliaryMaterialStrength = true;
            state.UseCapsuleMode = true;
            state.UseDeepCapsuleMode = false;
            state.ExperimentalInactivePaperOpacity = false;
            Theme.Invalidate();

            using (var fixture = new Fixture(controller))
            {
                CheckAnimatedRoundTrip(fixture);
                CheckReversal(fixture);
                CheckAnimationSettingAndQueuedCompletion(fixture);
                CheckHide(fixture);
                CheckCloseCancelsActiveCapture(fixture);
            }

            // This completion is already queued when the HWND closes. Pump afterwards
            // to prove it cannot restart a sampler or clear the closing suspension.
            using (var fixture = new Fixture(controller))
            {
                fixture.Window.SetCollapsedState(true, animate: false, saveGeometry: false);
                Program.Assert(fixture.Surface.SampledBackgroundCaptureSuspended &&
                    !fixture.Surface.HasBackgroundCapture,
                    "non-animated endpoint waits for settled layout before acquiring material");
                fixture.Window.CloseForReal();
                Program.Pump();
                Program.Assert(fixture.Surface.SampledBackgroundCaptureSuspended &&
                    !fixture.Surface.HasBackgroundCapture &&
                    !fixture.Surface.HasMaterialHostSubscription &&
                    fixture.Surface.BackgroundFrameCount == 0,
                    "a queued endpoint callback cannot acquire material after close");
            }
        }
        finally
        {
            (state.PaperSkin, state.Theme, state.EnableAnimations,
                state.MatchAuxiliaryMaterialStrength, state.UseCapsuleMode,
                state.UseDeepCapsuleMode, state.ExperimentalInactivePaperOpacity) = saved;
            nativeMode.SetValue(controller, savedNative);
            Theme.Invalidate();
        }
        Console.WriteLine("PAPER FORM MATERIAL: animated collapse/expand, reversal, setting settlement, queued endpoints, hide and close passed.");
    }

    private static void CheckAnimatedRoundTrip(Fixture fixture)
    {
        var window = fixture.Window;
        var surface = fixture.Surface;
        var body = surface.Child;
        var frames = surface.BackgroundFrameCount;
        using (var probe = new AnimationProbe(fixture, "collapse"))
        {
            window.SetCollapsedState(true, animate: true, saveGeometry: false);
            Until(() => !IsTransitioning(window), "animated collapse reaches its endpoint");
            probe.AssertAnimated();
        }
        AwaitSnapshot(fixture, frames, "animated collapse");
        var firstSnapshot = surface.BackgroundSessionState!.Bitmap;

        frames = surface.BackgroundFrameCount;
        using (var probe = new AnimationProbe(fixture, "expand"))
        {
            window.SetCollapsedState(false, animate: true, saveGeometry: false);
            Until(() => !IsTransitioning(window), "animated expansion reaches its endpoint");
            probe.AssertAnimated();
        }
        AwaitExpanded(fixture, frames);
        Program.Assert(ReferenceEquals(body, surface.Child) &&
            new WindowInteropHelper(window).Handle == fixture.Handle &&
            fixture.Paper.Content == Fixture.Content,
            "material form changes keep the HWND, body tree and note content");

        frames = surface.BackgroundFrameCount;
        window.SetCollapsedState(true, animate: false, saveGeometry: false);
        AwaitSnapshot(fixture, frames, "non-animated collapse");
        Program.Assert(!ReferenceEquals(firstSnapshot, surface.BackgroundSessionState!.Bitmap),
            "a later collapse acquires a new endpoint snapshot instead of retaining an old scene");
        window.SetCollapsedState(false, animate: false, saveGeometry: false);
        AwaitExpanded(fixture, surface.BackgroundFrameCount);
    }

    private static void CheckReversal(Fixture fixture)
    {
        var window = fixture.Window;
        var expandedWidth = window.ActualWidth;
        var frames = fixture.Surface.BackgroundFrameCount;
        using var probe = new AnimationProbe(fixture, "collapse reversed to expand");
        var intermediate = false;
        DispatcherFrame? waiting = null;
        void OnIntermediateSize(object sender, SizeChangedEventArgs e)
        {
            if (!IsTransitioning(window) || window.TransitionProgress is <= 0 or >= 1 ||
                e.NewSize.Width >= expandedWidth - 8) return;
            intermediate = true;
            if (waiting != null) waiting.Continue = false;
        }
        window.SizeChanged += OnIntermediateSize;
        try
        {
            window.SetCollapsedState(true, animate: true, saveGeometry: false);
            var timeout = Stopwatch.StartNew();
            while (!intermediate && timeout.ElapsedMilliseconds < 5000)
            {
                // ApplicationIdle can be postponed until the whole animation finishes.
                // Leave this pump as soon as a real intermediate layout is observed,
                // but reverse only after the current WPF operation has unwound.
                var frame = new DispatcherFrame();
                waiting = frame;
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                waiting = null;
                if (!intermediate) Thread.Sleep(2);
            }
            Program.Assert(intermediate && IsTransitioning(window) && window.TransitionProgress < 1 &&
                window.ActualWidth < expandedWidth - 8,
                "reversal begins after a real intermediate size, before collapse completion");
        }
        finally { window.SizeChanged -= OnIntermediateSize; }
        window.SetCollapsedState(false, animate: true, saveGeometry: false);
        Until(() => !IsTransitioning(window), "reversed transition reaches the expanded endpoint");
        probe.AssertAnimated();
        AwaitExpanded(fixture, frames);
    }

    private static void CheckAnimationSettingAndQueuedCompletion(Fixture fixture)
    {
        var window = fixture.Window;
        var surface = fixture.Surface;
        var frames = surface.BackgroundFrameCount;
        window.SetCollapsedState(true, animate: true, saveGeometry: false);
        Program.Assert(IsTransitioning(window), "collapse really starts before animations are disabled");
        fixture.Controller.State.EnableAnimations = false;
        window.SettleAnimationsForDisabledSetting();
        Program.Assert(!IsTransitioning(window) && fixture.Paper.IsCollapsed,
            "disabling animations settles the pending collapse through the normal endpoint");
        AwaitSnapshot(fixture, frames, "animation-setting settlement");

        frames = surface.BackgroundFrameCount;
        window.SetCollapsedState(false, animate: false, saveGeometry: false);
        // Do not pump: the old expanded endpoint's Loaded callback is still pending.
        fixture.Controller.State.EnableAnimations = true;
        using (var probe = new AnimationProbe(fixture, "new collapse with an old endpoint queued"))
        {
            window.SetCollapsedState(true, animate: true, saveGeometry: false);
            // Check in the same FIFO priority immediately after the old completion,
            // rather than sampling after an unbounded ApplicationIdle pump returns.
            var inspected = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                Program.Assert(IsTransitioning(window) && surface.SampledBackgroundCaptureSuspended &&
                    !surface.HasBackgroundCapture && surface.BackgroundFrameCount == frames,
                    "the previous endpoint callback cannot resume capture during a newer transition");
                inspected.Continue = false;
            }));
            Dispatcher.PushFrame(inspected);
            Until(() => !IsTransitioning(window), "newer collapse completes");
            probe.AssertAnimated();
        }
        AwaitSnapshot(fixture, frames, "newer collapse after stale completion");
        window.SetCollapsedState(false, animate: false, saveGeometry: false);
        AwaitExpanded(fixture, surface.BackgroundFrameCount);
    }

    private static void CheckHide(Fixture fixture)
    {
        var surface = fixture.Surface;
        var frames = surface.BackgroundFrameCount;
        fixture.Window.SetCollapsedState(true, animate: true, saveGeometry: false);
        fixture.Paper.IsVisible = false;
        fixture.Window.PrepareForHide();
        fixture.Window.HideWithoutGeometrySave();
        Program.Pump();
        Program.Assert(!fixture.Window.IsVisible && !IsTransitioning(fixture.Window) &&
            !surface.HasBackgroundCapture && !surface.IsBackgroundActive &&
            surface.BackgroundFrameCount == frames &&
            DesktopBackgroundCapture.ReadAffinity(fixture.Handle) == fixture.Affinity,
            "hide settles the form without an invisible endpoint capture or retained exclusion");

        fixture.Paper.IsVisible = true;
        fixture.Window.PrepareForShow();
        fixture.Window.Show();
        AwaitSnapshot(fixture, frames, "show after hidden collapse");
    }

    private static void CheckCloseCancelsActiveCapture(Fixture fixture)
    {
        var surface = fixture.Surface;
        surface.BackgroundSessionState!.GeometryChanged();
        var capture = surface.BackgroundSessionState.Capture;
        Program.Assert(capture != null, "close test starts a real outstanding material capture");
        var observedClosing = false;
        fixture.Window.Closing += (_, _) =>
        {
            observedClosing = true;
            Program.Assert(!surface.HasBackgroundCapture && capture!.IsStopped &&
                DesktopBackgroundCapture.ReadAffinity(fixture.Handle) == fixture.Affinity,
                "close cancels material capture and restores affinity before destroying the HWND");
        };
        fixture.Window.CloseForReal();
        Program.Pump();
        Program.Assert(observedClosing && !surface.HasBackgroundCapture && !surface.HasMaterialHostSubscription,
            "closed paper releases material capture and host subscriptions");
    }

    private static void AwaitSnapshot(Fixture fixture, int previousFrames, string context)
    {
        var surface = fixture.Surface;
        Until(() => !IsTransitioning(fixture.Window) && !surface.SampledBackgroundCaptureSuspended &&
            surface.IsBackgroundActive && !surface.HasBackgroundCapture, context + " publishes its static material");
        DrainRendering();
        Program.Assert(surface.BackgroundFrameCount == previousFrames + 1 &&
            DesktopBackgroundCapture.ReadAffinity(fixture.Handle) == fixture.Affinity,
            context + " acquires exactly one endpoint snapshot and releases exclusion");
    }

    private static void AwaitExpanded(Fixture fixture, int frames)
    {
        Until(() => !IsTransitioning(fixture.Window) && !fixture.Surface.SampledBackgroundCaptureSuspended &&
            fixture.Window.IsNativeMicaEffective, "expanded paper restores its native Acrylic backdrop");
        Program.Assert(!fixture.Surface.IsBackgroundActive && !fixture.Surface.HasBackgroundCapture &&
            fixture.Surface.BackgroundFrameCount == frames &&
            DesktopBackgroundCapture.ReadAffinity(fixture.Handle) == fixture.Affinity,
            "expanded paper has no software scene, sampler, extra frame or capture exclusion");
    }

    private static bool IsTransitioning(PaperWindow window) => (bool)Transitioning.GetValue(window)!;

    private static void DrainRendering()
    {
        var frames = 0;
        EventHandler rendered = (_, _) => frames++;
        CompositionTarget.Rendering += rendered;
        try { Until(() => frames >= 2, "settled endpoint presents on successive render ticks"); }
        finally { CompositionTarget.Rendering -= rendered; }
    }

    private static void Until(Func<bool> condition, string context)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.ElapsedMilliseconds < 5000)
        {
            Program.Pump();
            Thread.Sleep(2);
        }
        Program.Assert(condition(), context);
    }

    private sealed class AnimationProbe : IDisposable
    {
        private readonly Fixture _fixture;
        private readonly string _context;
        private readonly int _initialFrames;
        private readonly Size _initialSize;
        private int _rendered;
        private bool _changedSize;

        internal AnimationProbe(Fixture fixture, string context)
        {
            _fixture = fixture;
            _context = context;
            _initialFrames = fixture.Surface.BackgroundFrameCount;
            _initialSize = fixture.Window.RenderSize;
            CompositionTarget.Rendering += OnRendering;
            fixture.Window.SizeChanged += OnSizeChanged;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!IsTransitioning(_fixture.Window)) return;
            _rendered++;
            Check();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsTransitioning(_fixture.Window)) return;
            _changedSize |= Math.Abs(e.NewSize.Width - _initialSize.Width) > 1 ||
                Math.Abs(e.NewSize.Height - _initialSize.Height) > 1;
            Check();
        }

        private void Check() => Program.Assert(
            !_fixture.Surface.HasBackgroundCapture &&
            _fixture.Surface.BackgroundFrameCount == _initialFrames &&
            DesktopBackgroundCapture.ReadAffinity(_fixture.Handle) == _fixture.Affinity,
            _context + ": changing geometry must not start or publish background captures");

        internal void AssertAnimated() => Program.Assert(_rendered > 0 && _changedSize,
            _context + ": observed actual render ticks and changing window geometry");

        public void Dispose()
        {
            CompositionTarget.Rendering -= OnRendering;
            _fixture.Window.SizeChanged -= OnSizeChanged;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Content = "# Acrylic form transition\n保留正文和编辑器";
        internal AppController Controller { get; }
        internal PaperData Paper { get; }
        internal PaperWindow Window { get; }
        internal SkinBorder Surface { get; }
        internal IntPtr Handle { get; }
        internal uint Affinity { get; }

        internal Fixture(AppController controller)
        {
            Controller = controller;
            Paper = new PaperData
            {
                Type = PaperTypes.Note, Title = "Acrylic form transition", Content = Content,
                IsVisible = true, Width = 420, Height = 320, X = 80, Y = 80, AlwaysOnTop = true
            };
            controller.State.Papers.Add(Paper);
            Window = new PaperWindow(Paper, controller) { ShowInTaskbar = false };
            Surface = (SkinBorder)Chrome.GetValue(Window)!;
            try
            {
                Window.Show();
                Window.Activate();
                Until(() => Window.IsNativeMicaEffective && Surface.ActualWidth > 200,
                    "fixture starts with native Acrylic on an expanded paper");
                Program.Pump();
                Handle = new WindowInteropHelper(Window).Handle;
                Affinity = DesktopBackgroundCapture.ReadAffinity(Handle);
                Program.Assert(!Surface.HasBackgroundCapture && !Surface.IsBackgroundActive &&
                    Affinity != uint.MaxValue && Affinity != 0x11,
                    "expanded native fixture starts without software capture or exclusion");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (!Window.IsClosed) Window.CloseForReal();
            Controller.State.Papers.Remove(Paper);
        }
    }
}
