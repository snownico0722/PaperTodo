using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;
using Drawing = System.Drawing;

// These checks exercise the real ShowPaper startup fade, not just a hand-built HWND or
// successful DWM HRESULTs. Desktop capture is opt-in so running checks never changes
// wallpaper, registry preferences, focus policies, or the user's transparency setting.
internal static class VisualChecks
{
    private static readonly List<object> Samples = new();
    private static string? _output;

    internal static void Run(AppController controller)
    {
        _output = Environment.GetEnvironmentVariable("PAPER_MICA_CAPTURE");
        if (!string.IsNullOrWhiteSpace(_output)) Directory.CreateDirectory(_output);
        if (!NativeMicaBackdrop.IsSupported || !DwmMicaApi.Instance.CompositionEnabled ||
            !DwmMicaApi.Instance.TransparencyEnabled || SystemParameters.HighContrast)
        {
            Console.WriteLine("SKIP real DWM desktop checks: system Mica is unavailable; fake-API fallback checks still run.");
            return;
        }
        controller.State.ColorScheme = ColorSchemes.Mica;
        controller.State.Theme = "light";
        controller.State.ExperimentalInactivePaperOpacity = false;
        controller.State.EnableAnimations = true;
        controller.State.HidePapersFromTaskbar = false;
        Theme.Invalidate();
        var paper = new PaperData { Type = PaperTypes.Todo, Title = "原生云母验证", X = 40, Y = 40, Width = 380, Height = 350 };
        controller.State.Papers.Add(paper);
        PaperWindow? window = null;
        Window? other = null;
        try
        {
            controller.ShowPaper(paper);
            Wait();
            var windows = (Dictionary<string, PaperWindow>)typeof(AppController).GetField("_windows", Program.Private)!.GetValue(controller)!;
            window = windows[paper.Id];
            var hwnd = new WindowInteropHelper(window).Handle;
            var chrome = (Border)typeof(PaperWindow).GetField("_paperChrome", Program.Private)!.GetValue(window)!;
            var host = (Grid)chrome.Child; // swatches follow the same opacity/effects as real content
            var body = chrome.Child;
            var swatches = new StackPanel
            {
                Orientation = Orientation.Horizontal, Width = 80, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(24, 0, 0, 24), IsHitTestVisible = false
            };
            var white = new Border { Width = 40, Height = 20, Background = Brushes.White };
            var black = new Border { Width = 40, Height = 20, Background = Brushes.Black };
            swatches.Children.Add(white); swatches.Children.Add(black);
            Panel.SetZIndex(swatches, 10000); host.Children.Add(swatches);
            window.Activate(); Wait();
            AssertExpanded(window, chrome, hwnd, body);
            Program.Assert(!window.HasAnimatedProperties, "startup opacity clock must settle, not hold over Opacity=0");
            var active = Capture(window, "01-startup-active", white, black, dark: false);

            // Place the focus target below the main paper, including on a small CI desktop.
            other = new Window { Left = 40, Top = 500, Width = 180, Height = 100, Content = "Focus target" };
            other.Show(); other.Activate(); Wait();
            Program.Assert(!window.IsActive, "the activation test must actually deactivate the paper");
            AssertExpanded(window, chrome, hwnd, body);
            var inactive = Capture(window, "02-inactive", white, black, dark: false);
            if (active.HasValue && inactive.HasValue)
            {
                // The material may legitimately change to a neutral inactive color. It must
                // not become the reported dark gray (~160) or dim the opaque WPF swatches.
                Program.Assert(inactive.Value.R > 200 && inactive.Value.G > 200 && inactive.Value.B > 200,
                    "light inactive Mica must not acquire a dark overlay in the desktop fixture");
            }
            other.Close(); other = null;
            window.Activate();

            window.SetCollapsedState(true, animate: true, saveGeometry: false); Wait();
            Program.Assert(!window.IsNativeMicaEffective, "capsule stays on WPF alpha fallback");
            swatches.Visibility = Visibility.Collapsed;
            Capture(window, "03-collapsed", null, null, dark: null);
            window.SetCollapsedState(false, animate: true, saveGeometry: false); Wait();
            swatches.Visibility = Visibility.Visible; Wait();
            AssertExpanded(window, chrome, hwnd, body);
            Capture(window, "04-expanded-after-animation", white, black, dark: false);

            controller.State.Theme = "dark"; Theme.Invalidate(); window.UpdateTheme(); Wait();
            AssertExpanded(window, chrome, hwnd, body);
            Capture(window, "05-dark", white, black, dark: true);
            controller.State.Theme = "light"; Theme.Invalidate(); window.UpdateTheme(); Wait();
            window.Width += 40; window.Height += 25; Wait();
            AssertExpanded(window, chrome, hwnd, body);
            Capture(window, "06-light-resized", white, black, dark: false);

            controller.HidePaper(paper); Wait();
            controller.ShowPaper(paper); Wait();
            AssertExpanded(window, chrome, hwnd, body);
            Capture(window, "07-hide-show", white, black, dark: false);
            window.WindowState = WindowState.Minimized; Wait();
            window.WindowState = WindowState.Normal; window.Activate(); Wait();
            AssertExpanded(window, chrome, hwnd, body);
            Capture(window, "08-restored", white, black, dark: false);

            // Existing explicit transparency remains a real fallback, not a hidden setting
            // change to make a screenshot pass. Restoring opacity must restore native Mica.
            chrome.Opacity = 0.7; Wait();
            Program.Assert(!window.IsNativeMicaEffective, "explicit partial opacity uses fallback");
            chrome.Opacity = 1; Wait();
            AssertExpanded(window, chrome, hwnd, body);
            Capture(window, "09-opacity-restored", white, black, dark: false);

            host.Children.Remove(swatches);
            controller.HidePaper(paper); Wait();
            CheckSettings(controller);
        }
        finally
        {
            other?.Close();
            window?.CloseForReal();
            controller.State.Papers.Remove(paper);
            controller.State.EnableAnimations = false;
            if (!string.IsNullOrWhiteSpace(_output))
                File.WriteAllText(Path.Combine(_output, "samples.json"), JsonSerializer.Serialize(Samples, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void CheckSettings(AppController controller)
    {
        var show = typeof(AppController).GetMethod("ShowSettingsWindow", Program.Private, null, Type.EmptyTypes, null)!;
        var refresh = typeof(AppController).GetMethod("RefreshSettingsWindowContent", Program.Private)!;
        show.Invoke(controller, null); Wait();
        var window = (Window)typeof(AppController).GetField("_settingsWindow", Program.Private)!.GetValue(controller)!;
        try
        {
            window.Left = 20; window.Top = 20; Wait();
            var hwnd = new WindowInteropHelper(window).Handle;
            AssertNoSystemCaption(hwnd);
            var oldRoot = window.Content;
            Capture(window, "10-settings-light", null, null, dark: null);
            controller.State.Theme = "dark"; Theme.Invalidate(); refresh.Invoke(controller, null); Wait();
            Program.Assert(!ReferenceEquals(oldRoot, window.Content), "settings test actually rebuilds the chrome");
            Program.Assert(hwnd == new WindowInteropHelper(window).Handle, "settings keeps its HWND");
            Program.Assert(window.Content is Border { Background: SolidColorBrush brush } && brush.Color.A == 0,
                "rebuilt settings root exposes native material");
            Capture(window, "11-settings-dark", null, null, dark: null);
        }
        finally { window.Close(); controller.State.Theme = "light"; Theme.Invalidate(); }
    }

    private static void AssertExpanded(PaperWindow window, Border chrome, IntPtr hwnd, object? body)
    {
        AssertNoSystemCaption(hwnd);
        Program.Assert(window.IsNativeMicaEffective, "real DWM must be active, not silently fallback");
        Program.Assert(!window.AllowsTransparency && !DwmMicaApi.Instance.IsLayered(hwnd), "opaque endpoint uses non-layered HWND");
        Program.Assert(window.Opacity == 1 && chrome.Opacity == 1, "no accidental whole-UI translucency");
        Program.Assert(chrome.Margin == new Thickness(0) && chrome.Effect == null, "no inset or WPF outer shadow");
        Program.Assert(hwnd == new WindowInteropHelper(window).Handle && ReferenceEquals(body, chrome.Child), "stable HWND and editor tree");
        Program.Assert(DwmMicaApi.DwmGetWindowAttribute(hwnd, 38, out var type, 4) >= 0 && type == 2, "actual Mica, not Acrylic or a painted imitation");
        Program.Assert(GetWindowRect(hwnd, out var bounds), "window bounds");
        var clientOrigin = window.PointToScreen(new Point(0, 0));
        Program.Assert(Math.Abs(clientOrigin.X - bounds.Left) <= 1 && Math.Abs(clientOrigin.Y - bounds.Top) <= 1,
            "WPF client fills the HWND; no hidden caption/frame offset");
        static IntPtr Packed(int x, int y) => new(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        Program.Assert(SendMessage(hwnd, 0x0084, IntPtr.Zero,
            Packed(bounds.Left + 2, (bounds.Top + bounds.Bottom) / 2)).ToInt32() == 10, "left edge still resizes");
        Program.Assert(SendMessage(hwnd, 0x0084, IntPtr.Zero,
            Packed(bounds.Right - 2, (bounds.Top + bounds.Bottom) / 2)).ToInt32() == 11, "right edge still resizes");
        var region = CreateRectRgn(0, 0, 0, 0);
        try { Program.Assert(GetWindowRgn(hwnd, region) == 0, "no manual rounded-region crop over the native frame"); }
        finally { DeleteObject(region); }
    }

    private static void AssertNoSystemCaption(IntPtr hwnd) =>
        Program.Assert((GetWindowLong(hwnd, -16) & 0x00C00000) == 0,
            "system caption buttons must not overlap PaperTodo's custom title bar");

    private static Drawing.Color? Capture(Window window, string name, FrameworkElement? white, FrameworkElement? black, bool? dark)
    {
        if (string.IsNullOrWhiteSpace(_output))
        {
            Console.WriteLine("SKIP desktop pixels " + name + ": set PAPER_MICA_CAPTURE to an output directory.");
            return null;
        }
        DwmFlush();
        var hwnd = new WindowInteropHelper(window).Handle;
        Program.Assert(GetWindowRect(hwnd, out var bounds), "desktop bounds available");
        using var bitmap = new Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
        bitmap.Save(Path.Combine(_output, name + ".png"), Drawing.Imaging.ImageFormat.Png);
        var point = window.PointToScreen(new Point(window.ActualWidth / 2, Math.Max(1, window.ActualHeight - 65)));
        var sample = bitmap.GetPixel((int)Math.Round(point.X) - bounds.Left, (int)Math.Round(point.Y) - bounds.Top);
        Samples.Add(new { name, active = window.IsActive, opacity = window.Opacity, R = sample.R, G = sample.G, B = sample.B });
        Console.WriteLine($"PIXELS {name} active={window.IsActive} opacity={window.Opacity} body={sample}");
        // Save the isolated WPF channel too: native material is absent by definition. It
        // distinguishes a gray WPF layer from the final desktop compositor's background.
        var dpi = VisualTreeHelper.GetDpi(window);
        var render = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        render.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
        using (var file = File.Create(Path.Combine(_output, name + "-wpf.png"))) encoder.Save(file);
        if (white != null && black != null)
        {
            Drawing.Color Pixel(FrameworkElement element)
            {
                var p = element.PointToScreen(new Point(10, 10));
                return bitmap.GetPixel((int)Math.Round(p.X) - bounds.Left, (int)Math.Round(p.Y) - bounds.Top);
            }
            var w = Pixel(white); var b = Pixel(black);
            Program.Assert(w.R >= 250 && w.G >= 250 && w.B >= 250, $"{name}: opaque WPF white must not be darkened or covered ({w})");
            Program.Assert(b.R <= 5 && b.G <= 5 && b.B <= 5, $"{name}: opaque WPF black must remain visible ({b})");
        }
        if (dark is false) Program.Assert(sample.R > 200 && sample.G > 200 && sample.B > 200, name + ": light body is not a gray overlay");
        if (dark is true) Program.Assert(sample.R < 90 && sample.G < 90 && sample.B < 90, name + ": dark mode actually reaches the compositor");
        return sample;
    }

    private static void Wait()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
