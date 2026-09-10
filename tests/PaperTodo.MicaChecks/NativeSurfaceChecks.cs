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
                PaperSkins.TracingPaper, PaperSkins.LiquidGlass, PaperSkins.Aero, PaperSkins.Ceramic, PaperSkins.Pixel })
            {
                controller.State.PaperSkin = skin; controller.State.Theme = mode; Theme.Invalidate();
                // The fixture is about composited pixels, not activation-dependent Z order.
                paper = new PaperWindow(new PaperData { Type = PaperTypes.Todo, Title = "实际材质 · 顶栏与包边",
                    X = 50, Y = 50, Width = 400, Height = 340, AlwaysOnTop = true }, controller);
                paper.Show(); paper.Activate(); Wait();
                WaitForDesktopInk(paper, output, skin + "-" + mode);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
                    skin is PaperSkins.Mica or PaperSkins.Acrylic or PaperSkins.Aero or PaperSkins.TracingPaper)
                {
                    CheckCaptionSentinel(paper, output, skin + "-" + mode);
                    WaitForDesktopInk(paper, output, skin + "-" + mode + "-after-alpha");
                }
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
                    if (skin == PaperSkins.LiquidGlass)
                    {
                        // At 100% DPI, adjacent 8px black/white stripes must stay sharp.
                        // A frosted Acrylic wash cannot pass this high-frequency contrast check.
                        var row = image.Height - 90;
                        var values = Enumerable.Range(image.Width / 2 - 32, 64).Select(x => image.GetPixel(x, row).R).ToArray();
                        Program.Assert(values.Max() - values.Min() is >= 120 and <= 225,
                            $"{mode}: glass must retain rear detail AND a visible veil, neither opaque nor invisible ({values.Min()}..{values.Max()})");
                        Console.WriteLine($"CLEAR GLASS {mode}: sharp rear stripe range {values.Min()}..{values.Max()}");
                    }
                }
                if (skin == PaperSkins.LiquidGlass)
                {
                    var hwnd = new WindowInteropHelper(paper).Handle;
                    paper.Width += 20; paper.Height += 10; Wait();
                    paper.SetCollapsedState(true, false, false); Wait();
                    Program.Assert(!paper.IsNativeMicaEffective, "capsule retains solid safe fallback");
                    paper.SetCollapsedState(false, false, false); Wait();
                    Program.Assert(paper.IsNativeMicaEffective && new WindowInteropHelper(paper).Handle == hwnd,
                        "clear surface returns after resizing and collapse without a new HWND");
                    using var final = Capture(paper, output, $"desktop-{skin}-{mode}-restored");
                    var row = final.Height - 90;
                    var values = Enumerable.Range(final.Width / 2 - 32, 64).Select(x => final.GetPixel(x, row).R).ToArray();
                    Program.Assert(values.Max() - values.Min() is >= 120 and <= 225, "resizing/collapse preserves the visible translucent lens");
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
    private static void WaitForDesktopInk(PaperWindow paper, string output, string name)
    {
        // The compositor may expose its uniform fallback before WPF's first present.
        // A blank frame is not evidence that the actual header/body material matches.
        var pin = (Button)typeof(PaperWindow).GetField("_paperIconButton", Program.Private)!.GetValue(paper)!;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            paper.UpdateLayout();
            using var image = Capture(paper, output, "ready-" + name);
            var point = pin.TransformToAncestor(paper).Transform(new Point());
            var dpi = VisualTreeHelper.GetDpi(paper);
            var color = ((SolidColorBrush)pin.Foreground).Color;
            var expected = D.Color.FromArgb(color.R, color.G, color.B);
            var matches = 0;
            for (var y = Math.Max(0, (int)(point.Y * dpi.DpiScaleY)); y < Math.Min(image.Height, (point.Y + pin.ActualHeight) * dpi.DpiScaleY); y++)
            for (var x = Math.Max(0, (int)(point.X * dpi.DpiScaleX)); x < Math.Min(image.Width, (point.X + pin.ActualWidth) * dpi.DpiScaleX); x++)
                if (Difference(image.GetPixel(x, y), expected) <= 18) matches++;
            if (matches >= 2) return;
            Wait();
        }
        Program.Assert(false, name + ": actual WPF pin never appeared in the desktop composite");
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
            if (Theme.Skin == PaperSkins.Aero)
                Program.Assert(DwmMicaApi.Instance.SetClearAcrylic(hwnd, false, Theme.IsDark) >= 0,
                    "caption positive control removes the separate Aero accent layer");
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
            if (Theme.Skin == PaperSkins.Aero)
                Program.Assert(DwmMicaApi.Instance.SetAeroGlass(hwnd, Theme.IsDark) >= 0,
                    "restore the actual low-tint Aero recipe for the negative control");
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
        using var frozen = surface?.IsRefractionActive == true ? surface.FreezeRefractionForEvidence() : null;
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
