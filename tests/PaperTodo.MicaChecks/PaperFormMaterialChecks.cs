using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

// Exercise the real native-paper HWND, animation clocks, reversal, hide and close.
internal static class PaperFormMaterialChecks
{
    private static readonly PropertyInfo Transitioning = typeof(PaperWindow)
        .GetProperty("IsPaperFormTransitioning", Program.Private)!;
    private static readonly FieldInfo Chrome = typeof(PaperWindow)
        .GetField("_paperChrome", Program.Private)!;

    internal static void Run(AppController controller)
    {
        var nativeMode = typeof(AppController).GetProperty("UsesNativeMicaWindows", Program.Private)!;
        var savedNative = controller.UsesNativeMicaWindows;
        var state = controller.State;
        var saved = (state.PaperSkin, state.Theme, state.EnableAnimations,
            state.UseCapsuleMode,
            state.UseDeepCapsuleMode, state.ExperimentalInactivePaperOpacity);
        try
        {
            nativeMode.SetValue(controller, true);
            state.PaperSkin = PaperSkins.Acrylic;
            state.Theme = "light";
            state.EnableAnimations = true;
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
                CheckCloseDuringTransition(fixture);
            }

        }
        finally
        {
            (state.PaperSkin, state.Theme, state.EnableAnimations,
                state.UseCapsuleMode,
                state.UseDeepCapsuleMode, state.ExperimentalInactivePaperOpacity) = saved;
            nativeMode.SetValue(controller, savedNative);
            Theme.Invalidate();
        }
        Console.WriteLine("PAPER FORM MATERIAL: animated collapse/expand, reversal, setting settlement, hide and close passed.");
    }

    private static void CheckAnimatedRoundTrip(Fixture fixture)
    {
        var window = fixture.Window;
        var body = fixture.Surface.Child;
        using (var probe = new AnimationProbe(fixture, "collapse"))
        {
            window.SetCollapsedState(true, animate: true, saveGeometry: false);
            Until(() => !IsTransitioning(window), "animated collapse reaches its endpoint");
            probe.AssertAnimated();
        }
        AwaitCollapsed(fixture);

        using (var probe = new AnimationProbe(fixture, "expand"))
        {
            window.SetCollapsedState(false, animate: true, saveGeometry: false);
            Until(() => !IsTransitioning(window), "animated expansion reaches its endpoint");
            probe.AssertAnimated();
        }
        AwaitExpanded(fixture);
        Program.Assert(ReferenceEquals(body, fixture.Surface.Child) &&
            new WindowInteropHelper(window).Handle == fixture.Handle &&
            fixture.Paper.Content == Fixture.Content,
            "form animation preserves the HWND, body tree and note content");

        window.SetCollapsedState(true, animate: false, saveGeometry: false);
        AwaitCollapsed(fixture);
        window.SetCollapsedState(false, animate: false, saveGeometry: false);
        AwaitExpanded(fixture);
    }

    private static void CheckReversal(Fixture fixture)
    {
        var window = fixture.Window;
        var expandedWidth = window.ActualWidth;
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
        AwaitExpanded(fixture);
    }

    private static void CheckAnimationSettingAndQueuedCompletion(Fixture fixture)
    {
        var window = fixture.Window;
        window.SetCollapsedState(true, animate: true, saveGeometry: false);
        Program.Assert(IsTransitioning(window), "collapse starts before animations are disabled");
        fixture.Controller.State.EnableAnimations = false;
        window.SettleAnimationsForDisabledSetting();
        Program.Assert(!IsTransitioning(window) && fixture.Paper.IsCollapsed,
            "disabling animations settles the pending collapse through its normal endpoint");
        AwaitCollapsed(fixture);

        window.SetCollapsedState(false, animate: false, saveGeometry: false);
        fixture.Controller.State.EnableAnimations = true;
        using (var probe = new AnimationProbe(fixture, "new collapse after previous endpoint"))
        {
            window.SetCollapsedState(true, animate: true, saveGeometry: false);
            Until(() => !IsTransitioning(window), "newer collapse completes");
            probe.AssertAnimated();
        }
        AwaitCollapsed(fixture);
        window.SetCollapsedState(false, animate: false, saveGeometry: false);
        AwaitExpanded(fixture);
    }

    private static void CheckHide(Fixture fixture)
    {
        fixture.Window.SetCollapsedState(true, animate: true, saveGeometry: false);
        fixture.Paper.IsVisible = false;
        fixture.Window.PrepareForHide();
        fixture.Window.HideWithoutGeometrySave();
        Program.Pump();
        Program.Assert(!fixture.Window.IsVisible && !IsTransitioning(fixture.Window),
            "hide stops the form transition without leaving an invisible animation");

        fixture.Paper.IsVisible = true;
        fixture.Window.PrepareForShow();
        fixture.Window.Show();
        Until(() => fixture.Window.IsVisible, "show restores the paper surface");
    }

    private static void CheckCloseDuringTransition(Fixture fixture)
    {
        fixture.Window.SetCollapsedState(false, animate: false, saveGeometry: false);
        fixture.Window.SetCollapsedState(true, animate: true, saveGeometry: false);
        fixture.Window.CloseForReal();
        Program.Pump();
        Program.Assert(fixture.Window.IsClosed && !IsTransitioning(fixture.Window),
            "closing the paper stops its form animation without reopening a window");
    }

    private static void AwaitCollapsed(Fixture fixture)
    {
        Until(() => !IsTransitioning(fixture.Window) && fixture.Paper.IsCollapsed &&
            !fixture.Window.IsNativeMicaEffective,
            "collapsed paper has a lightweight WPF surface rather than native full-window material");
    }

    private static void AwaitExpanded(Fixture fixture)
    {
        Until(() => !IsTransitioning(fixture.Window) && !fixture.Paper.IsCollapsed &&
            fixture.Window.IsNativeMicaEffective,
            "expanded paper restores the native Acrylic backdrop");
    }

    private static bool IsTransitioning(PaperWindow window) => (bool)Transitioning.GetValue(window)!;

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
        private readonly Size _initialSize;
        private int _rendered;
        private bool _changedSize;

        internal AnimationProbe(Fixture fixture, string context)
        {
            _fixture = fixture;
            _context = context;
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
            _fixture.Surface.Effect == null && !_fixture.Window.IsClosed,
            _context + ": transition keeps the live lightweight WPF chrome");

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
                Program.Assert(Window.IsNativeMicaEffective,
                    "expanded fixture starts with native Acrylic");
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
