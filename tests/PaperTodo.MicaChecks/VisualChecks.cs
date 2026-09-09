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
        controller.State.PaperSkin = null; // Material permutations below cover legacy settings.
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
            CheckMaterialActivation(controller, window, other, white, black);
            other.Close(); other = null;
            CheckClearAcrylicBackground(controller, window, chrome, white, black);
            window.Activate();
            controller.State.MicaAlwaysActive = true;
            window.RefreshNativeMica(force: true);

            // Sample exact progress values independently of dispatcher/CI timing, then also
            // exercise the real animation completion callbacks below.
            CheckFormFrames(window, chrome, collapsed: true);
            CheckFormFrames(window, chrome, collapsed: false);
            CheckReversedExpansion(window, chrome);
            window.SetCollapsedState(true, animate: true, saveGeometry: false);
            WaitUntil(() => double.IsNaN(chrome.Width));
            Program.Assert(!window.IsNativeMicaEffective && window.ResizeMode == ResizeMode.NoResize,
                "completed capsule has no native resize frame");
            swatches.Visibility = Visibility.Collapsed;
            Capture(window, "03-collapsed", null, null, dark: null);
            window.SetCollapsedState(false, animate: true, saveGeometry: false);
            WaitUntil(() => double.IsNaN(chrome.Width));
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
            controller.State.MicaAlwaysActive = false;
            window.RefreshNativeMica(force: true);

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
            controller.State.MicaAlwaysActive = false;
            controller.State.MicaBackdropType = MicaBackdropTypes.Mica;
            if (!string.IsNullOrWhiteSpace(_output))
                File.WriteAllText(Path.Combine(_output, "samples.json"), JsonSerializer.Serialize(Samples, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void CheckMaterialActivation(AppController controller, PaperWindow window, Window other,
        FrameworkElement white, FrameworkElement black)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        foreach (var dark in new[] { false, true })
        foreach (var material in MicaBackdropTypes.All)
        {
            var name = $"material-{material}-{(dark ? "dark" : "light")}";
            controller.State.Theme = dark ? "dark" : "light";
            controller.State.MicaBackdropType = material;
            controller.State.MicaAlwaysActive = false;
            Theme.Invalidate(); window.UpdateTheme();
            window.Activate(); Wait();
            Program.Assert(DwmMicaApi.DwmGetWindowAttribute(hwnd, 38, out var type, 4) >= 0 &&
                type == MicaBackdropTypes.ToDwmBackdrop(material) && window.IsNativeMicaEffective,
                "selected material is active with the matching system backdrop policy");
            AssertNoWindowRegion(hwnd);
            // Border/caption colors are documented for DwmSetWindowAttribute only; querying
            // them is not a supported readback. Check the setter result and desktop image.
            var adapter = (NativeMicaBackdrop)typeof(PaperWindow).GetField("_nativeMica", Program.Private)!.GetValue(window)!;
            Program.Assert(adapter.LastFrameHResult >= 0,
                $"native corner and palette settings succeed: 0x{adapter.LastFrameHResult:X8}");
            var active = Capture(window, name + "-active", white, black, dark: null);
            other.Activate(); Wait();
            var inactive = Capture(window, name + "-inactive", white, black, dark: null);
            controller.State.MicaAlwaysActive = true;
            window.RefreshNativeMica(); Wait();
            AssertOtherHasFocus();
            SameMaterial(active, Capture(window, name + "-forced", white, black, dark: null));
            window.Activate(); Wait(); other.Activate(); Wait();
            AssertOtherHasFocus();
            SameMaterial(active, Capture(window, name + "-forced-after-focus", white, black, dark: null));
            controller.State.MicaAlwaysActive = false;
            window.RefreshNativeMica(); Wait();
            AssertOtherHasFocus();
            SameMaterial(inactive, Capture(window, name + "-unforced", white, black, dark: null));
        }
        controller.State.Theme = "light";
        controller.State.MicaBackdropType = MicaBackdropTypes.Mica;
        Theme.Invalidate(); window.UpdateTheme(); Wait();

        void AssertOtherHasFocus() => Program.Assert(!window.IsActive && other.IsActive &&
            GetForegroundWindow() == new WindowInteropHelper(other).Handle,
            "material override must preserve both WPF and OS focus on the other window");
        static void SameMaterial(Drawing.Color? expected, Drawing.Color? actual)
        {
            if (expected is not { } a || actual is not { } b) return;
            Program.Assert(Math.Abs(a.R - b.R) <= 8 && Math.Abs(a.G - b.G) <= 8 && Math.Abs(a.B - b.B) <= 8,
                $"activation override restores the expected desktop material: expected={a}, actual={b}");
        }
    }

    private static void CheckClearAcrylicBackground(AppController controller, PaperWindow window, Border chrome,
        FrameworkElement white, FrameworkElement black)
    {
        var backdrop = new Window
        {
            Left = window.Left - 20, Top = window.Top - 20, Width = window.Width + 40, Height = window.Height + 40,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Background = Brushes.White
        };
        try
        {
            backdrop.Show();
            // Showing a non-activating HWND can still put it above an already active paper;
            // Activate() alone then does nothing. Place the test background explicitly below it.
            Program.Assert(SetWindowPos(new WindowInteropHelper(backdrop).Handle,
                new WindowInteropHelper(window).Handle, 0, 0, 0, 0, 0x13 /* NOSIZE | NOMOVE | NOACTIVATE */),
                "test background is placed behind the paper");
            window.Activate();
            controller.State.Theme = "light";
            controller.State.MicaBackdropType = MicaBackdropTypes.ClearAcrylic;
            Theme.Invalidate(); window.UpdateTheme(); Wait();
            var whiteSample = Capture(window, "clear-white-background", white, black, dark: null);
            if (whiteSample is { } w)
                Program.Assert(w.R >= 245 && w.G >= 245 && w.B >= 245,
                    $"Clear Acrylic must not gray a white background: {w}");
            controller.State.MicaBackdropType = MicaBackdropTypes.Acrylic;
            window.RefreshNativeMica(); Wait();
            var standardWhite = Capture(window, "standard-white-background", white, black, dark: null);
            controller.State.MicaBackdropType = MicaBackdropTypes.ClearAcrylic;
            window.RefreshNativeMica(); Wait();
            backdrop.Background = new SolidColorBrush(Color.FromRgb(80, 160, 240)); Wait();
            Capture(backdrop, "color-background-placement", null, null, dark: null);
            var clear = Capture(window, "clear-color-background", white, black, dark: null);
            controller.State.MicaBackdropType = MicaBackdropTypes.Acrylic;
            window.RefreshNativeMica(); Wait();
            var standard = Capture(window, "standard-color-background", white, black, dark: null);
            if (clear is { } c && standard is { } s && standardWhite is { } sw)
            {
                // Windows Server/remote compositors can force both Acrylic paths to a solid
                // tint. Confirm that with the unchanged system-Acrylic control, not an OS
                // name or the custom effect's result. This is missing coverage, not a pass.
                if (Math.Abs(s.R - sw.R) <= 2 && Math.Abs(s.G - sw.G) <= 2 && Math.Abs(s.B - sw.B) <= 2)
                    Console.WriteLine($"SKIP Clear Acrylic transparency comparison: system Acrylic control ignores background color " +
                        $"(white={sw}, blue={s}); real Windows 11 transparency remains unverified.");
                else
                    Program.Assert(c.B - c.R > s.B - s.R + 20,
                        $"Clear Acrylic must pass through substantially more background color: clear={c}, standard={s}");
            }
            controller.State.MicaBackdropType = MicaBackdropTypes.ClearAcrylic;
            window.RefreshNativeMica(); Wait();
            CheckFormFrames(window, chrome, collapsed: true, prefix: "clear-");
            CheckFormFrames(window, chrome, collapsed: false, prefix: "clear-");
            Program.Assert(window.IsNativeMicaEffective, "Clear Acrylic returns after the capsule transition");
        }
        finally
        {
            backdrop.Close();
            controller.State.MicaBackdropType = MicaBackdropTypes.Mica;
            window.RefreshNativeMica(); Wait();
        }
    }

    private static void CheckFormFrames(PaperWindow window, Border chrome, bool collapsed, string prefix = "")
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        window.SetCollapsedState(collapsed, animate: true, saveGeometry: false);
        window.BeginAnimation(PaperWindow.TransitionProgressProperty, null);
        foreach (var progress in new[] { 0.0, 0.5, 1.0 })
        {
            window.TransitionProgress = progress;
            window.UpdateLayout();
            Program.Assert(!window.IsNativeMicaEffective && window.ResizeMode == ResizeMode.NoResize,
                "form transition suspends the native material and resize frame");
            Program.Assert((GetWindowLong(hwnd, -16) & 0x00C40000) == 0,
                "no native caption/border/THICKFRAME may outline the transparent animation gutter");
            Program.Assert(DwmMicaApi.DwmGetWindowAttribute(hwnd, 38, out var type, 4) >= 0 && type == 1,
                "form transition clears the full-window system backdrop");
            Program.Assert(Math.Abs(chrome.Width + chrome.Margin.Left + chrome.Margin.Right - window.Width) < 0.01 &&
                Math.Abs(chrome.Height + chrome.Margin.Top + chrome.Margin.Bottom - window.Height) < 0.01,
                $"form bounds: collapsed={collapsed}, progress={progress}, window={window.Width}x{window.Height}, " +
                $"actual={window.ActualWidth}x{window.ActualHeight}, chrome={chrome.Width}x{chrome.Height}, margin={chrome.Margin}");
            Program.Assert(Math.Abs(window.ActualWidth - window.Width) <= 1 && Math.Abs(window.ActualHeight - window.Height) <= 1,
                "native HWND follows the presented size without minimum-size clamping");
            if (progress == 0.5)
            {
                Program.Pump();
                Capture(window, prefix + (collapsed ? "03a-collapse-mid" : "04a-expand-mid"), null, null, dark: null);
            }
        }
        var width = window.Width;
        var height = window.Height;
        var paperWidth = chrome.Width;
        var paperHeight = chrome.Height;
        window.SettleAnimationsForDisabledSetting();
        window.UpdateLayout();
        Program.Assert(Math.Abs(window.Width - width) < 0.01 && Math.Abs(window.Height - height) < 0.01 &&
            Math.Abs(chrome.ActualWidth - paperWidth) <= 1 && Math.Abs(chrome.ActualHeight - paperHeight) <= 1,
            "clearing the final animation frame must not introduce the old 16 DIP size jump");
        Program.Assert(window.ResizeMode == (collapsed ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip),
            "settling an interrupted animation restores its final resize policy");
    }

    private static void CheckReversedExpansion(PaperWindow window, Border chrome)
    {
        var expandedWidth = window.Width;
        var expandedHeight = window.Height;
        window.SetCollapsedState(true, animate: true, saveGeometry: false);
        window.BeginAnimation(PaperWindow.TransitionProgressProperty, null);
        window.TransitionProgress = 0.5;
        var width = window.Width;
        var height = window.Height;
        var margin = chrome.Margin;
        window.SetCollapsedState(false, animate: true, saveGeometry: false);
        window.BeginAnimation(PaperWindow.TransitionProgressProperty, null);
        Program.Assert(Math.Abs(window.Width - width) < 0.01 && Math.Abs(window.Height - height) < 0.01 && chrome.Margin == margin,
            "reversing a native transition starts from its presented bounds and margin");
        window.TransitionProgress = 0.5;
        window.SettleAnimationsForDisabledSetting();
        Program.Assert(window.Width == expandedWidth && window.Height == expandedHeight &&
            window.ResizeMode == ResizeMode.CanResizeWithGrip,
            "interrupting reversed expansion restores full paper bounds and resizing");
    }

    private static void CheckSettings(AppController controller)
    {
        var pageType = typeof(AppController).GetField("_settingsPage", Program.Private)!.FieldType;
        var show = typeof(AppController).GetMethod("ShowSettingsWindow", Program.Private, null, new[] { pageType }, null)!;
        var refresh = typeof(AppController).GetMethod("RefreshSettingsWindowContent", Program.Private)!;
        show.Invoke(controller, new[] { Enum.Parse(pageType, "Visual") }); Wait();
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
            var checkbox = Descendants(window).OfType<CheckBox>().Single(c =>
                Equals(c.Content, Strings.Get("SettingsMicaAlwaysActive")));
            checkbox.IsChecked = true;
            checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Program.Assert(controller.State.MicaAlwaysActive, "settings checkbox updates the shared preference");
            typeof(AppController).GetMethod("SetPaperSkin", Program.Private)!
                .Invoke(controller, new object[] { PaperSkins.ClearAcrylic });
            Wait();
            Program.Assert(window.Content is Border { Background: SolidColorBrush clear } && clear.Color.A == 0,
                "settings receives Clear Acrylic after rebuilding its root");
            Program.Assert(Descendants(window).OfType<CheckBox>().Single(c =>
                Equals(c.Content, Strings.Get("SettingsMicaAlwaysActive"))).IsChecked == true,
                "material switching retains the checkbox state");
            Capture(window, "12-settings-clear-acrylic", null, null, dark: null);
        }
        finally
        {
            window.Close(); controller.State.Theme = "light";
            controller.State.PaperSkin = null; // Restore the legacy fixture for the following checks.
            controller.State.MicaAlwaysActive = false;
            controller.State.MicaBackdropType = MicaBackdropTypes.Mica;
            Theme.Invalidate();
        }

        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }

    private static void AssertExpanded(PaperWindow window, Border chrome, IntPtr hwnd, object? body)
    {
        AssertNoSystemCaption(hwnd);
        Program.Assert(window.ResizeMode == ResizeMode.CanResizeWithGrip && (GetWindowLong(hwnd, -16) & 0x00040000) != 0,
            "expanded paper restores the native resize frame");
        Program.Assert(window.IsNativeMicaEffective, "real DWM must be active, not silently fallback");
        Program.Assert(!window.AllowsTransparency && !DwmMicaApi.Instance.IsLayered(hwnd), "opaque endpoint uses non-layered HWND");
        Program.Assert(window.Opacity == 1 && chrome.Opacity == 1, "no accidental whole-UI translucency");
        Program.Assert(chrome.Margin == new Thickness(0) && chrome.Effect == null, "no inset or WPF outer shadow");
        Program.Assert(chrome.BorderBrush is SolidColorBrush { Color.A: 0 }, "no second WPF outline inside the native corners");
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
        AssertNoWindowRegion(hwnd);
    }

    private static void AssertNoWindowRegion(IntPtr hwnd)
    {
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
        if (window is PaperWindow { IsNativeMicaEffective: true, HasExpandedPaperSurface: true } paper && paper.Opacity == 1)
        {
            var header = (Border)typeof(PaperWindow).GetField("_topBarHost", Program.Private)!.GetValue(paper)!;
            var p = header.PointToScreen(new Point(header.ActualWidth / 2, 2));
            var actual = bitmap.GetPixel((int)Math.Round(p.X) - bounds.Left, (int)Math.Round(p.Y) - bounds.Top);
            var expected = ((SolidColorBrush)Theme.TitleBarBrush(opaque: true)).Color;
            Program.Assert(Math.Abs(actual.R - expected.R) <= 3 && Math.Abs(actual.G - expected.G) <= 3 &&
                Math.Abs(actual.B - expected.B) <= 3, $"{name}: desktop header must not acquire a system selection/activation tint ({actual})");
        }
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

    private static void WaitUntil(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!complete() && DateTime.UtcNow < deadline) Wait(16);
        Program.Assert(complete(), "form animation reaches its completed state before the deadline");
    }

    private static void Wait(int milliseconds = 650)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
