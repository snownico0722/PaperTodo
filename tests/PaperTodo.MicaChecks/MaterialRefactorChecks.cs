using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

// Tests observable ownership, pixels, resource identity and cancelled requests. They do
// not assert file names, class layout or a private-field arrangement.
internal static class MaterialRefactorChecks
{
    internal static void Run(AppController controller)
    {
        CheckFrameSlot();
        CheckPadding();
        CheckEnvironmentCache();
        CheckReliefReuse();
        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
            controller.State.EnableAnimations, controller.State.LiveBackgroundProcessing,
            controller.State.MatchAuxiliaryMaterialStrength);
        try
        {
            controller.State.Theme = "light";
            controller.State.ColorScheme = ColorSchemes.Neutral;
            controller.State.EnableAnimations = false;
            controller.State.LiveBackgroundProcessing = false;
            controller.State.MatchAuxiliaryMaterialStrength = false;
            CheckPaintCaches(controller);
            CheckNativeIsolation(controller);
            CheckOpeningRequests(controller);
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
                controller.State.EnableAnimations, controller.State.LiveBackgroundProcessing,
                controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
    }

    private static DesktopBackgroundCapture.Frame Frame(byte value = 37)
    {
        var pixels = ArrayPool<byte>.Shared.Rent(64 * 32 * 4);
        pixels.AsSpan(0, 64 * 32 * 4).Fill(value);
        return new(new BackgroundCaptureLayout.Scene(new Int32Rect(0, 0, 64, 32), 64, 32), pixels);
    }

    private static void CheckFrameSlot()
    {
        using (var slot = new DesktopBackgroundCapture.FrameSlot())
        {
            var first = Frame(); var latest = Frame();
            Program.Assert(slot.Publish(first) && slot.Publish(latest) && first.IsDisposed,
                "a replaced latest frame is released, not queued");
            Program.Assert(ReferenceEquals(slot.Take(), latest) && slot.Take() == null && !latest.IsDisposed,
                "taking a frame transfers sole disposal responsibility to the consumer");
            slot.Dispose();
            Program.Assert(!latest.IsDisposed, "stopping never returns the consumer's pixels prematurely");
            latest.Dispose(); latest.Dispose();
            var late = Frame();
            Program.Assert(!slot.Publish(late) && late.IsDisposed && slot.Take() == null,
                "a late GDI result cannot republish after Stop");
        }
        // Race actual Publish/Take/Stop, including stop-before-first-publication. No UI/GDI
        // work occurs inside the slot gate; every lease has exactly one surviving owner.
        for (var round = 0; round < 24; round++)
        {
            using var slot = new DesktopBackgroundCapture.FrameSlot();
            using var barrier = new Barrier(3);
            var produced = new ConcurrentBag<DesktopBackgroundCapture.Frame>();
            Parallel.Invoke(
                () => { barrier.SignalAndWait(); for (var i = 0; i < 40; i++) { var frame = Frame((byte)i); produced.Add(frame); slot.Publish(frame); } },
                () => { barrier.SignalAndWait(); for (var i = 0; i < 80; i++) { using var frame = slot.Take(); if (frame != null) Program.Assert(!frame.IsDisposed, "consumer frame stays alive until released"); } },
                () => { barrier.SignalAndWait(); slot.Dispose(); });
            slot.Dispose();
            Program.Assert(produced.All(frame => frame.IsDisposed) && slot.Take() == null,
                "concurrent stop drains all owned frames without leaving a pooled-buffer lease");
        }
        Console.WriteLine("PASS latest-frame slot: replacement, transfer, stop and concurrent late publication.");
    }

    private static void CheckPadding()
    {
        var desktop = new Int32Rect(-8192, -4096, 16384, 8192);
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            var dpi = new DpiScale(scale, scale);
            var padding = BackgroundCaptureLayout.Padding(true, dpi);
            Program.Assert(padding / 2 >= Math.Ceiling(38 * scale), "menu guard fully covers the strongest Gaussian diffusion");
            Program.Assert(BackgroundCaptureLayout.Padding(false, dpi) == (int)(256 * scale),
                "capsules retain their established drag headroom at every DPI");
            var window = new Int32Rect(-200, 100, (int)(240 * scale), (int)(160 * scale));
            var region = new DesktopBackgroundCapture.Region(0, 0, window.Width, window.Height, padding);
            var menu = BackgroundCaptureLayout.Create(window, region, desktop)!;
            var legacy = BackgroundCaptureLayout.Create(window, region with { Padding = (int)(256 * scale) }, desktop)!;
            Program.Assert((long)menu.PixelWidth * menu.PixelHeight < (long)legacy.PixelWidth * legacy.PixelHeight,
                "stationary popup samples fewer pixels without changing scene scale or blur radius");
            var moved = window; moved.X += (int)(20 * scale);
            Program.Assert(ReferenceEquals(menu, BackgroundCaptureLayout.Create(moved, region, desktop, menu)),
                "ordinary popup placement adjustments keep the primed scene and sampling phase");
        }
    }

    private static void CheckEnvironmentCache()
    {
        var api = DwmMicaApi.Instance;
        api.InvalidateEnvironment();
        var composition = api.CompositionEnabled;
        var transparency = api.TransparencyEnabled;
        var reads = api.EnvironmentReadCount;
        for (var i = 0; i < 500; i++)
            Program.Assert(api.CompositionEnabled == composition && api.TransparencyEnabled == transparency,
                "cached native environment stays stable without another system event");
        Program.Assert(api.EnvironmentReadCount == reads, "hot-path eligibility never reopens the registry");
        api.InvalidateEnvironment();
        _ = api.CompositionEnabled; _ = api.TransparencyEnabled;
        Program.Assert(api.EnvironmentReadCount == reads + 2, "a real environment invalidation refreshes both native inputs");
    }

    private static void CheckPaintCaches(AppController controller)
    {
        controller.State.PaperSkin = PaperSkins.TracingPaper; Theme.Invalidate();
        var surface = new SkinBorder { Width = 240, Height = 160, Background = Brushes.White, CornerRadius = new CornerRadius(8) };
        Render(surface);
        var geometry = surface.GeometryBuildCount; var brushes = surface.BrushBuildCount;
        for (var i = 0; i < 10; i++) { surface.RefreshSkin(); Render(surface); }
        Program.Assert(surface.GeometryBuildCount == geometry && surface.BrushBuildCount == brushes,
            "repeated refreshes reuse both geometry and brushes");
        surface.Width += 12; Render(surface);
        Program.Assert(surface.GeometryBuildCount == geometry + 1 && surface.BrushBuildCount == brushes,
            "resize rebuilds only geometry, not size-independent gradients");
        geometry = surface.GeometryBuildCount;
        controller.State.ColorScheme = ColorSchemes.Ink; Theme.Invalidate(); surface.RefreshSkin(); Render(surface);
        Program.Assert(surface.GeometryBuildCount == geometry && surface.BrushBuildCount == brushes + 1,
            "palette change rebuilds only brushes, not shape/hit geometry");
        brushes = surface.BrushBuildCount;
        controller.State.EnableAnimations = true; surface.RefreshSkin(); Render(surface);
        Program.Assert(surface.GeometryBuildCount == geometry && surface.BrushBuildCount == brushes,
            "animation subscription changes do not destroy paint caches");
        Program.Assert(surface.BackgroundSessionState == null && !surface.HasMaterialHostSubscription,
            "a detached/non-auxiliary painted surface never allocates a background session");
    }

    private static void CheckNativeIsolation(AppController controller)
    {
        controller.State.EnableAnimations = false;
        controller.State.LiveBackgroundProcessing = true;
        foreach (var skin in new[] { PaperSkins.Paper, PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic, PaperSkins.TracingPaper })
        {
            controller.State.PaperSkin = skin; Theme.Invalidate();
            var surface = new SkinBorder { Background = Brushes.White, CornerRadius = new CornerRadius(8) };
            var window = new Window { Content = surface, Width = 280, Height = 190, Left = 60, Top = 60, ShowInTaskbar = false };
            try
            {
                window.Show(); Program.Pump();
                window.Left += 10; window.Width += 12; Program.Pump();
                surface.RefreshBackground();
                Program.Assert(surface.BackgroundSessionState == null && !surface.HasMaterialHostSubscription && !surface.HasBackgroundWorker,
                    skin + ": ordinary/native main surfaces stay completely outside software capture lifetime");
            }
            finally { window.Close(); }
        }
    }

    private static void CheckReliefReuse()
    {
        var cases = 0; long evaluations = 0, reused = 0;
        foreach (var size in new[] { new Size(92, 46), new Size(280, 220), new Size(700, 240) })
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var dark in new[] { false, true })
        foreach (var border in new[] { new Thickness(1), new Thickness(0, 1, 1, 1) })
        {
            var corners = new CornerRadius(8, 17, 12, 0);
            var cached = MaterialRelief.Create(size, corners, border, new DpiScale(dpi, dpi), dark, out var metrics);
            var reference = MaterialRelief.Create(size, corners, border, new DpiScale(dpi, dpi), dark, out var baseline, reuseStraightEdges: false);
            var a = cached.Children.Cast<ImageDrawing>().ToArray();
            var b = reference.Children.Cast<ImageDrawing>().ToArray();
            Program.Assert(a.Length == b.Length && metrics.Pixels == baseline.Pixels, "relief caching does not change raster geometry");
            for (var i = 0; i < a.Length; i++)
                Program.Assert(a[i].Rect == b[i].Rect && Pixels((BitmapSource)a[i].ImageSource).SequenceEqual(Pixels((BitmapSource)b[i].ImageSource)),
                    "cached Aero lighting is byte-identical, including asymmetric corners/open edges/fractional DPI");
            evaluations += metrics.LightingEvaluations; reused += metrics.ReusedLighting; cases++;
        }
        Program.Assert(reused > evaluations, "straight-edge reuse eliminates real lighting evaluations, not just an extra object name");
        Console.WriteLine($"AERO CACHE: {cases} byte-identical cases; {evaluations} lighting evaluations, {reused} reused.");
    }

    private static void CheckOpeningRequests(AppController controller)
    {
        controller.State.PaperSkin = PaperSkins.Acrylic;
        controller.State.LiveBackgroundProcessing = true; Theme.Invalidate();
        if (!MaterialMenuOpening.NeedsBackground) throw new InvalidOperationException("Test requires composed desktop with transparency enabled.");
        var completions = new List<TaskCompletionSource<DesktopBackgroundCapture.Frame?>>();
        var probe = new OpeningProbe((_, _) =>
        {
            var completion = new TaskCompletionSource<DesktopBackgroundCapture.Frame?>(TaskCreationOptions.RunContinuationsAsynchronously);
            completions.Add(completion);
            return completion.Task; // Deliberately ignore cancellation like an in-flight GDI call.
        });
        probe.Request(true); probe.Request(false); Program.Pump();
        Program.Assert(completions.Count == 0 && !probe.Open && !probe.Opening.IsPending,
            "cancel-before-dispatch does not acquire a background or reopen");

        probe.Request(true); Until(() => completions.Count == 1, "first pending acquisition");
        probe.Request(false);
        probe.Request(true); Until(() => completions.Count == 2, "replacement acquisition");
        var stale = Frame(); completions[0].SetResult(stale);
        Until(() => stale.IsDisposed, "late cancelled frame is released");
        Program.Assert(!probe.Open && probe.Opening.IsPending, "old result cannot publish into a newer opening");
        var fresh = Frame(83); completions[1].SetResult(fresh);
        Until(() => probe.Open, "current acquisition opens with prepared material");
        Program.Assert(fresh.IsDisposed && probe.Surface.BackgroundSessionState?.Bitmap?.IsFrozen == true &&
            !probe.Surface.HasBackgroundWorker, "first pixels are immutable and pool released before any HWND exists");
        probe.Request(false);

        probe.Request(true); Until(() => completions.Count == 3, "cancel-after-completion request");
        var ready = Frame(); completions[2].SetResult(ready); probe.Request(false);
        Until(() => ready.IsDisposed, "cancel after capture but before UI continuation");
        Program.Assert(!probe.Open, "a queued completion cannot undo a dismissal");

        probe.Request(true); Until(() => completions.Count == 4, "unload pending request");
        probe.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var unloaded = Frame(); completions[3].SetResult(unloaded);
        Until(() => unloaded.IsDisposed, "unloaded owner releases late frame");
        Program.Assert(!probe.Open && !probe.Opening.IsPending, "an unloaded owner is not reopened");

        var timed = new OpeningProbe((_, _) => Task.FromException<DesktopBackgroundCapture.Frame?>(new TimeoutException("injected")));
        timed.Request(true); Until(() => timed.Open, "timeout produces a stable fallback");
        Program.Assert(timed.Surface.SuppressLiveBackgroundForOpening && timed.Surface.BackgroundSessionState?.Visual == null,
            "failed acquisition opens once with a stable fallback, not a later material flash");
        timed.Request(false);
        probe.Surface.PrepareMenuBackground(null, false);
        timed.Surface.PrepareMenuBackground(null, false);
        Console.WriteLine("PASS menu tickets: pre-dispatch cancel, replacement, late result, post-capture cancel, unload and timeout.");
    }

    private sealed class OpeningProbe : FrameworkElement
    {
        private static readonly DependencyProperty OpenProperty = DependencyProperty.Register("Open", typeof(bool), typeof(OpeningProbe),
            new FrameworkPropertyMetadata(false, null, (d, value) => ((OpeningProbe)d).Opening.Coerce((bool)value)));
        internal readonly SkinBorder Surface = new() { IsMenu = true, Width = 240, Height = 160 };
        internal readonly MaterialMenuOpening Opening;
        internal bool Open => (bool)GetValue(OpenProperty);
        internal OpeningProbe(Func<Int32Rect, CancellationToken, Task<DesktopBackgroundCapture.Frame?>> capture) =>
            Opening = new MaterialMenuOpening(this, OpenProperty, () => Surface, () => null, () => PlacementMode.MousePoint, capture);
        internal void Request(bool open) => SetCurrentValue(OpenProperty, open);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data;
    }
    private static void Render(SkinBorder surface)
    {
        surface.Measure(new Size(surface.Width, surface.Height));
        surface.Arrange(new Rect(0, 0, surface.Width, surface.Height));
        var bitmap = new RenderTargetBitmap((int)surface.Width, (int)surface.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
    }
    private static void Until(Func<bool> predicate, string reason)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate() && deadline.ElapsedMilliseconds < 6000)
        {
            Program.Pump(); Thread.Sleep(1);
        }
        Program.Assert(predicate(), reason);
    }
}
