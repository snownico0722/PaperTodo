using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using D = System.Drawing;

// These are final DWM desktop pixels, not RenderTargetBitmap's WPF-only image.
// No wallpaper/registry edits. Refraction capture is frozen for final desktop evidence.
internal static class NativeSurfaceChecks
{
    internal static void Run(AppController controller)
    {
        var output = Environment.GetEnvironmentVariable("PAPER_MICA_CAPTURE");
        if (string.IsNullOrWhiteSpace(output) || !NativeMicaBackdrop.IsSupported ||
            !DwmMicaApi.Instance.CompositionEnabled || !DwmMicaApi.Instance.TransparencyEnabled || SystemParameters.HighContrast)
        {
            Console.WriteLine("SKIP final native surface pixels: capture opt-in and native composition required.");
            return;
        }
        Directory.CreateDirectory(output);
        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations,
            controller.State.ColorScheme, controller.State.UseCapsuleMode);
        controller.State.EnableAnimations = false;
        controller.State.UseCapsuleMode = true;
        controller.State.ColorScheme = ColorSchemes.Warm;
        var board = new DrawingGroup();
        using (var dc = board.Open())
            for (var x = 0; x < 400; x += 8)
                dc.DrawRectangle(x % 16 == 0 ? Brushes.Black : Brushes.White, null, new Rect(x, 0, 8, 340));
        var rear = new Window
        {
            Left = 50, Top = 50, Width = 400, Height = 340, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = new DrawingBrush(board) { Stretch = Stretch.None }
        };
        PaperWindow? paper = null;
        try
        {
            rear.Show();
            foreach (var mode in new[] { "light", "dark" })
            foreach (var skin in new[] { PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic,
                PaperSkins.TracingPaper, PaperSkins.Aero, PaperSkins.Pixel })
            {
                controller.State.PaperSkin = skin; controller.State.Theme = mode; Theme.Invalidate();
                // The fixture is about composited pixels, not activation-dependent Z order.
                paper = new PaperWindow(new PaperData { Type = PaperTypes.Todo, Title = "实际材质 · 顶栏与包边",
                    X = 50, Y = 50, Width = 400, Height = 340, AlwaysOnTop = true }, controller);
                var readiness = AttachDesktopReadinessMarker(paper);
                paper.Show(); paper.Activate(); Wait();
                WaitForDesktopInk(paper, readiness.Marker, output, skin + "-" + mode);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
                    skin is PaperSkins.Mica or PaperSkins.Acrylic or PaperSkins.TracingPaper)
                {
                    CheckCaptionSentinel(paper, output, skin + "-" + mode);
                    WaitForDesktopInk(paper, readiness.Marker, output, skin + "-" + mode + "-after-alpha");
                }
                readiness.Host.Children.Remove(readiness.Marker);
                paper.UpdateLayout();
                Wait();
                var header = (Border)typeof(PaperWindow).GetField("_topBarHost", Program.Private)!.GetValue(paper)!;
                using (var image = Capture(paper, output, $"desktop-{skin}-{mode}"))
                {
                    if (PaperSkins.UsesNativeBackdrop(skin))
                    {
                        Program.Assert(paper.IsNativeMicaEffective, "real native recipe activated");
                        var y = header.TransformToAncestor(paper).Transform(new Point(0, 8)).Y;
                        // Rear stripes are identical at every height. Sample the empty lower
                        // paper, not y+70: that row intersects the tinted new-todo button.
                        var h = image.GetPixel(image.Width / 2, (int)Math.Round(y));
                        var b = image.GetPixel(image.Width / 2, image.Height * 7 / 10);
                        Console.WriteLine($"CONTINUITY {skin}/{mode}: header={h} body={b}");
                        if (mode == "light" && skin == PaperSkins.Mica)
                            Program.Assert(Math.Min(b.R, Math.Min(b.G, b.B)) >= 120,
                                $"light Mica must not expose an uncomposited black underlay: {b}");
                        if (skin is PaperSkins.Mica or PaperSkins.Acrylic or PaperSkins.ClearAcrylic)
                            Program.Assert(Difference(h, b) <= 4,
                                $"{skin}/{mode}: native header must use the body material, not a solid caption ({h} versus {b})");
                    }
                }
                paper.CloseForReal(); paper = null;
            }
        }
        finally
        {
            paper?.CloseForReal(); rear.Close();
            (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations,
                controller.State.ColorScheme, controller.State.UseCapsuleMode) = saved;
            Theme.Invalidate();
        }
    }
    private static (Grid Host, Border Marker) AttachDesktopReadinessMarker(PaperWindow paper)
    {
        // Install before Show(): adding the marker after the first presentation would itself
        // invalidate WPF and could hide the missing-first-present regression this check exists for.
        var chrome = (SkinBorder)typeof(PaperWindow).GetField("_paperChrome", Program.Private)!.GetValue(paper)!;
        Program.Assert(chrome.Child is Grid, "paper root grid available for desktop readiness marker");
        var host = (Grid)chrome.Child;
        var marker = new Border
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(7, 241, 19)),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Grid.SetRowSpan(marker, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(marker, Math.Max(1, host.ColumnDefinitions.Count));
        Panel.SetZIndex(marker, int.MaxValue);
        host.Children.Add(marker);
        return (host, marker);
    }

    private static void WaitForDesktopInk(PaperWindow paper, Border marker, string output, string name)
    {
        // DwmFlush/ContentRendered do not prove that WPF's redirection bitmap has reached the
        // desktop composite. Require the pre-show WPF marker in two consecutive desktop captures;
        // the actual material/caption assertions remain independent from this readiness evidence.
        var color = ((SolidColorBrush)marker.Background).Color;
        var expected = D.Color.FromArgb(color.R, color.G, color.B);
        var stableFrames = 0;
        var lastMatches = 0;
        var lastSamples = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            paper.UpdateLayout();
            // Both the calibration rear window and the paper are topmost so desktop pixels cannot
            // be contaminated by the runner shell. Do not rely on Activate() to order two windows
            // in the same topmost band: GitHub runners occasionally leave the rear window above
            // the paper. Reassert the paper at the top of that band before every evidence frame.
            WindowNative.ApplyTopmostZOrder(paper, topmost: true, insertAfter: IntPtr.Zero);
            using var image = Capture(paper, output, "ready-" + name + "-" + attempt);
            var point = marker.TransformToAncestor(paper).Transform(new Point());
            var dpi = VisualTreeHelper.GetDpi(paper);
            var left = Math.Max(0, (int)Math.Floor(point.X * dpi.DpiScaleX));
            var top = Math.Max(0, (int)Math.Floor(point.Y * dpi.DpiScaleY));
            var right = Math.Min(image.Width, (int)Math.Ceiling((point.X + marker.ActualWidth) * dpi.DpiScaleX));
            var bottom = Math.Min(image.Height, (int)Math.Ceiling((point.Y + marker.ActualHeight) * dpi.DpiScaleY));
            lastMatches = 0;
            lastSamples = Math.Max(0, right - left) * Math.Max(0, bottom - top);
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
                if (Difference(image.GetPixel(x, y), expected) <= 18) lastMatches++;

            var markerReady = lastSamples > 0 && lastMatches >= Math.Ceiling(lastSamples * 0.65);
            stableFrames = markerReady ? stableFrames + 1 : 0;
            if (stableFrames >= 2) return;
            Wait();
        }
        Program.Assert(false,
            $"{name}: pre-show WPF readiness marker never reached a stable desktop composite ({lastMatches}/{lastSamples} pixels)");
    }

    private static void CheckCaptionSentinel(PaperWindow paper, string output, string name)
    {
        var hwnd = new WindowInteropHelper(paper).Handle;
        using var baseline = Capture(paper, output, "caption-baseline-" + name);
        var sentinel = 0x00ff00ff;
        try
        {
            // These DWM attributes are documented setters, not guaranteed getters.
            // Prove the marker is effective with an old-path positive control instead.
            // Do not pump the UI dispatcher between marker installation and DwmFlush:
            // queued theme refreshes must not reset the deliberately hostile caption.
            Program.Assert(DwmMicaApi.Instance.SetRedirectionAlpha(hwnd, false) >= 0 &&
                DwmMicaApi.Instance.ExtendFrame(hwnd, -1) >= 0, "caption positive control installs full glass");
            Program.Assert(DwmSetWindowAttribute(hwnd, 35, ref sentinel, 4) >= 0, "caption sentinel is accepted");
            using var control = Capture(paper, output, "caption-old-path-" + name);
            var before = baseline.GetPixel(baseline.Width / 2, 8);
            var exposed = control.GetPixel(control.Width / 2, 8);
            // Opaque tracing-paper tint attenuates the marker. Test its magenta
            // chroma, not an arbitrary brightness jump through every different skin.
            var markerChroma = (exposed.R + exposed.B - 2 * exposed.G) -
                (before.R + before.B - 2 * before.G);
            Program.Assert(Difference(before, exposed) >= 8 && markerChroma >= 24,
                $"{name}: positive control exposes the hostile native caption ({before} / {exposed})");
            Program.Assert(DwmMicaApi.Instance.SetRedirectionAlpha(hwnd, true) >= 0 &&
                DwmMicaApi.Instance.ExtendFrame(hwnd, 0) >= 0, "restoring tested redirection alpha");
            Program.Assert(DwmSetWindowAttribute(hwnd, 35, ref sentinel, 4) >= 0, "same caption sentinel reapplied");
            using var marked = Capture(paper, output, "caption-sentinel-" + name);
            var after = marked.GetPixel(marked.Width / 2, 8);
            Program.Assert(Difference(before, after) <= 3,
                $"{name}: native caption cannot cover the material even with deliberate magenta ({before} / {after})");
            Console.WriteLine($"CAPTION SENTINEL {name}: old={exposed}, alpha={after}, original={before}");
        }
        finally { paper.RefreshNativeMica(force: true); }
    }
    private static int Difference(D.Color a, D.Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
    internal static D.Bitmap Capture(Window window, string? output, string name)
    {
        var surface = window is PaperWindow paper
            ? (SkinBorder)typeof(PaperWindow).GetField("_paperChrome", Program.Private)!.GetValue(paper)! : null;
        using var frozen = surface?.IsBackgroundActive == true ? surface.FreezeBackgroundForEvidence() : null;
        if (frozen != null) Wait();
        DwmFlush();
        Program.Assert(GetWindowRect(new WindowInteropHelper(window).Handle, out var r), "native bounds available");
        var image = new D.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = D.Graphics.FromImage(image)) g.CopyFromScreen(r.Left, r.Top, 0, 0, image.Size);
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            image.Save(Path.Combine(output, name + ".png"), D.Imaging.ImageFormat.Png);
        }
        return image;
    }
    private static void Wait()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
