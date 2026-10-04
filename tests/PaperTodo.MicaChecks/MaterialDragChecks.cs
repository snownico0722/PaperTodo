using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static class MaterialDragChecks
{
    internal static void Run(AppController controller)
    {
        Program.Assert(MaterialSurfaceHost.ClassifyWindowPosition(0x0015) == MaterialHostChange.Translation,
            "position-only notification is translation, not geometry");
        Program.Assert(MaterialSurfaceHost.ClassifyWindowPosition(0x0017) == MaterialHostChange.None,
            "Z-order-only notification does not schedule material work");
        Program.Assert(MaterialSurfaceHost.ClassifyWindowPosition(0x0016) == MaterialHostChange.Geometry,
            "resize retains geometry refresh");
        Program.Assert((MaterialSurfaceHost.ClassifyWindowPosition(0x0037) & MaterialHostChange.Geometry) != 0,
            "frame change retains local coordinate refresh");

        var saved = (controller.State.PaperSkin, controller.State.EnableAnimations,
            controller.State.MatchAuxiliaryMaterialStrength);
        var content = new TextBlock { Text = "Retained foreground", Margin = new Thickness(8) };
        controller.State.PaperSkin = PaperSkins.Acrylic;
        controller.State.EnableAnimations = true;
        controller.State.MatchAuxiliaryMaterialStrength = true;
        Theme.Invalidate();

        var surface = new SkinBorder
        {
            IsCapsule = true,
            CornerRadius = new CornerRadius(8),
            Child = content
        };
        var window = new Window
        {
            Left = 220,
            Top = 220,
            Width = 220,
            Height = 100,
            Content = surface,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize
        };

        try
        {
            window.Show();
            Until(() => surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "initial static capsule snapshot");
            var hwnd = new WindowInteropHelper(window).Handle;
            var session = surface.BackgroundSessionState!;
            var localBitmap = session.Bitmap;
            var localFrames = surface.BackgroundFrameCount;
            var before = session.SceneVisual!.Offset;
            var projected = surface.BackgroundProjectionCount;

            GetWindowRect(hwnd, out var bounds);
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, bounds.Left + 12, bounds.Top + 8, 0, 0, 0x0015),
                "native translation");
            Until(() => surface.BackgroundProjectionCount > projected,
                "world-space static background follows translation");
            var dpi = VisualTreeHelper.GetDpi(surface);
            var after = session.SceneVisual!.Offset;
            Program.Assert(ReferenceEquals(localBitmap, session.Bitmap) &&
                surface.BackgroundFrameCount == localFrames &&
                Math.Abs(after.X - before.X + 12 / dpi.DpiScaleX) < .1 &&
                Math.Abs(after.Y - before.Y + 8 / dpi.DpiScaleY) < .1,
                "translation only reprojects the retained local snapshot");

            using var rejectedCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var rejected = DesktopBackgroundCapture.PrepareDragAsync(new IntPtr(-12345), rejectedCts.Token)
                .GetAwaiter().GetResult();
            Program.Assert(rejected == null,
                "drag capture fails closed when the requested owner cannot be excluded");

            using var dragCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var drag = DesktopBackgroundCapture.PrepareDragAsync(hwnd, dragCts.Token)
                .GetAwaiter().GetResult();
            Program.Assert(drag is { PreBlurred: true } &&
                drag.Layout.Bounds == DesktopBackgroundCapture.DesktopBounds &&
                drag.Layout.PixelWidth == Math.Max(1, (drag.Layout.Bounds.Width + 1) / 2) &&
                drag.Layout.PixelHeight == Math.Max(1, (drag.Layout.Bounds.Height + 1) / 2) &&
                drag.Bitmap.IsFrozen,
                "drag uses one frozen 50%-resolution virtual-desktop snapshot with baked blur");

            surface.UseDragBackground(drag!);
            var dragBitmap = session.Bitmap;
            var dragFrames = surface.BackgroundFrameCount;
            GetWindowRect(hwnd, out bounds);
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, bounds.Left + 16, bounds.Top + 5, 0, 0, 0x0015),
                "drag translation");
            Wait(80);
            Program.Assert(ReferenceEquals(dragBitmap, session.Bitmap) &&
                surface.BackgroundFrameCount == dragFrames &&
                !surface.HasBackgroundCapture,
                "drag movement changes only crop coordinates and never recaptures or reblurs");

            surface.EndDragBackground();
            Until(() => surface.BackgroundFrameCount > dragFrames && !surface.HasBackgroundCapture,
                "drag end replaces the desktop snapshot with one final local snapshot");
            Program.Assert(!ReferenceEquals(dragBitmap, session.Bitmap) && ReferenceEquals(content, surface.Child),
                "final local snapshot replaces only the material background");

            surface.Opacity = .6;
            Wait(80);
            Program.Assert(!surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "opacity fallback releases the static snapshot");
            surface.Opacity = 1;
            Until(() => surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "opacity restore takes one fresh snapshot");
            window.Hide();
            Wait(80);
            Program.Assert(!surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "hide releases the static snapshot");
            window.Show();
            Until(() => surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "show takes one fresh snapshot");
        }
        finally
        {
            window.Close();
            (controller.State.PaperSkin, controller.State.EnableAnimations,
                controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }

        Program.Assert(!surface.HasBackgroundCapture && !surface.HasMaterialHostSubscription,
            "closed static surface releases all background ownership");
        CheckMasterDrag(controller);
        CheckAtomicReflection(controller);
        Console.WriteLine("PASS material translation: static projection, one drag snapshot, final recapture, Aero reflection and teardown.");
    }

    private static void CheckMasterDrag(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.EnableAnimations,
            controller.State.MatchAuxiliaryMaterialStrength);
        controller.State.PaperSkin = PaperSkins.Acrylic;
        controller.State.EnableAnimations = false;
        controller.State.MatchAuxiliaryMaterialStrength = true;
        Theme.Invalidate();

        var paper = new PaperWindow(new PaperData { Type = PaperTypes.Note }, controller);
        var master = new MasterCapsuleWindow(controller, EdgeCapsuleEdge.Right, "");
        var hostField = typeof(MasterCapsuleWindow).GetField("_floatingDragHost", Program.Private)!;
        var captureField = typeof(MasterCapsuleWindow).GetField("_floatingDragBackgroundCapture", Program.Private)!;
        var prepare = typeof(MasterCapsuleWindow).GetMethod("PrepareFloatingDragBackgroundAsync", Program.Private)!;
        var release = typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Program.Private)!;
        var shape = new EdgeCapsuleFloatingShape(true, EdgeCapsuleSurfaceKind.FloatingFree,
            160, 40, 32, 16, true);
        var options = (EdgeCapsuleDragWindowOptions)typeof(PaperWindow)
            .GetMethod("CreateDeepCapsuleFloatingDragHostOptions", Program.Private)!
            .Invoke(paper, [shape])!;
        EdgeCapsuleDragWindow? host = null;
        Task Prepare() => master.Dispatcher.Invoke(() => (Task)prepare.Invoke(master, [host])!);
        try
        {
            host = EdgeCapsuleDragWindow.Rent(options with { Icon = "▾", Label = "5" });
            hostField.SetValue(master, host);
            host.ShowWithEntrance(new DeviceScreenPoint(320, 240), false, 1, 0);
            var surface = (SkinBorder)typeof(EdgeCapsuleDragWindow)
                .GetField("_paperBackground", Program.Private)!.GetValue(host)!;
            var ready = Prepare();
            Until(() => ready.IsCompleted, "master drag snapshot preparation");
            ready.GetAwaiter().GetResult();
            var bitmap = surface.BackgroundSessionState?.Bitmap as BitmapSource;
            var desktop = DesktopBackgroundCapture.DesktopBounds;
            Program.Assert(bitmap is { IsFrozen: true } &&
                bitmap.PixelWidth == Math.Max(1, (desktop.Width + 1) / 2) &&
                bitmap.PixelHeight == Math.Max(1, (desktop.Height + 1) / 2),
                "master floating drag receives the ordinary half-resolution virtual-desktop snapshot");

            var frames = surface.BackgroundFrameCount;
            var projections = surface.BackgroundProjectionCount;
            var hwnd = new WindowInteropHelper(host).Handle;
            GetWindowRect(hwnd, out var bounds);
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero,
                    desktop.X + desktop.Width - (bounds.Right - bounds.Left) - 40,
                    bounds.Top + 5, 0, 0, 0x0015),
                "master drag moves beyond its initial bounded local snapshot");
            Until(() => surface.BackgroundProjectionCount > projections, "master drag crop follows movement");
            Program.Assert(ReferenceEquals(bitmap, surface.BackgroundSessionState?.Bitmap) &&
                surface.BackgroundFrameCount == frames && !surface.HasBackgroundCapture,
                "master movement only reprojects the same texture without another capture");

            release.Invoke(master, null);
            host.ReturnToPool();
            Program.Assert(captureField.GetValue(master) == null &&
                !surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "master release cancels the capture lease and clears the pooled background");

            var reused = EdgeCapsuleDragWindow.Rent(options);
            Program.Assert(ReferenceEquals(host, reused), "master background teardown preserves the shared host");
            host = reused;
            hostField.SetValue(master, host);
            host.ShowWithEntrance(new DeviceScreenPoint(320, 240), false, 1, 0);
            // Do not pump the Dispatcher between starting capture and returning the host: even a
            // completed worker must not publish its queued continuation into the next lease.
            var pending = Prepare();
            var canceled = ((CancellationTokenSource)captureField.GetValue(master)!).Token;
            release.Invoke(master, null);
            host.ReturnToPool();
            host = EdgeCapsuleDragWindow.Rent(options);
            hostField.SetValue(master, host);
            Until(() => pending.IsCompleted, "cancelled master background completion");
            pending.GetAwaiter().GetResult();
            Program.Assert(canceled.IsCancellationRequested && !surface.IsBackgroundActive &&
                captureField.GetValue(master) == null,
                "late master capture cannot repopulate a returned and re-leased drag host");

            foreach (var skin in new[] { PaperSkins.Acrylic, PaperSkins.Aero, PaperSkins.Paper })
            {
                controller.State.PaperSkin = skin;
                controller.State.MatchAuxiliaryMaterialStrength = skin != PaperSkins.Acrylic;
                Theme.Invalidate();
                var skipped = Prepare();
                Program.Assert(skipped.IsCompletedSuccessfully && captureField.GetValue(master) == null,
                    skin + ": master drag respects auxiliary-material eligibility");
            }
        }
        finally
        {
            release.Invoke(master, null);
            host?.ReturnToPool();
            master.CloseForReal();
            paper.CloseForReal();
            (controller.State.PaperSkin, controller.State.EnableAnimations,
                controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
        Console.WriteLine("PASS master drag material: desktop texture, crop-only motion, release, late cancellation and pool reuse.");
    }

    private static void CheckAtomicReflection(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.EnableAnimations);
        controller.State.PaperSkin = PaperSkins.Aero;
        controller.State.EnableAnimations = true;
        Theme.Invalidate();
        var surface = new SkinBorder { IsCapsule = true, CornerRadius = new CornerRadius(8) };
        var window = new Window
        {
            Left = 220,
            Top = 220,
            Width = 220,
            Height = 100,
            Content = surface,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize
        };
        try
        {
            window.Show();
            Wait(200);
            var shift = (Transform)typeof(SkinBorder).GetField("_reflectionShift", Program.Private)!.GetValue(surface)!;
            var changes = 0;
            shift.Changed += (_, _) => changes++;
            var hwnd = new WindowInteropHelper(window).Handle;
            GetWindowRect(hwnd, out var bounds);
            Program.Assert(SetWindowPos(hwnd, IntPtr.Zero, bounds.Left + 12, bounds.Top + 8, 0, 0, 0x0015),
                "Aero translation");
            Wait(100);
            Program.Assert(changes == 1, "one window movement publishes one combined reflection transform");
            Program.Assert(MaterialSurfaceHost.TryGetScreenOrigin(surface, HwndSource.FromHwnd(hwnd)!, out var origin),
                "Aero world origin");
            var dpi = VisualTreeHelper.GetDpi(surface);
            var reference = new TranslateTransform(
                -origin.X / dpi.DpiScaleX * .10,
                -origin.Y / dpi.DpiScaleY * .06).Value;
            Program.Assert(shift.Value == reference,
                "combined transform preserves exact X/Y reflection translation");
            controller.State.EnableAnimations = false;
            surface.RefreshSkin();
            Program.Assert(shift.Value.IsIdentity,
                "disabling reflection restores identity without retaining motion");
        }
        finally
        {
            window.Close();
            (controller.State.PaperSkin, controller.State.EnableAnimations) = saved;
            Theme.Invalidate();
        }
    }

    private static void Until(Func<bool> predicate, string context)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && started.Elapsed.TotalSeconds < 6) Wait(10);
        Program.Assert(predicate(), context);
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectI
    {
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
