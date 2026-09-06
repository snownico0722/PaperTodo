using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private static int _passed;
    private static int _nativeVerified;
    private static int _skipped;

    [STAThread]
    private static int Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var temp = Path.Combine(Path.GetTempPath(), "PaperTodo.NativeMicaChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Check("skin normalization preserves existing choices and defaults", () =>
            {
                foreach (var scheme in new[] { "warm", "ink", "forest", "rose", "mica" })
                    Assert(ColorSchemes.IsValid(scheme) && ColorSchemes.Normalize(scheme) == scheme, scheme);
                Assert(ColorSchemes.All.Distinct().Count() == 5, "unique schemes");
                Assert(ColorSchemes.Normalize(null) == ColorSchemes.Warm, "null default");
                Assert(ColorSchemes.Normalize("future") == ColorSchemes.Warm, "unknown default");
                Assert(new AppState().ColorScheme == ColorSchemes.Warm, "unchanged startup default");
            });
            Check("StateStore round-trips Mica and all theme modes without losing notes", () =>
            {
                var store = new StateStore(temp, DurableAtomicFileWriter.Shared);
                var version = 0L;
                foreach (var mode in new[] { "light", "dark", "system" })
                {
                    var state = new AppState { ColorScheme = ColorSchemes.Mica, Theme = mode };
                    state.Papers.Add(new PaperData { Type = PaperTypes.Note, Content = "# Native Mica\n保留笔记" });
                    store.SaveJsonSync(store.SerializeState(state), ++version);
                    var restored = store.Load();
                    Assert(restored.ColorScheme == ColorSchemes.Mica && restored.Theme == mode, "saved selection");
                    Assert(restored.Papers[0].Content == state.Papers[0].Content, "preserved note");
                }
            });
            Check("native region follows existing WPF geometry at mixed DPI", () =>
            {
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    var region = NativeMicaRegion.FromLayout(new Point(10, 10), new Size(340, 260), 16,
                        new DpiScale(scale, scale));
                    Assert(region.Left == (int)Math.Round(10 * scale) && region.Top == region.Left, "physical origin");
                    Assert(region.Right == (int)Math.Round(350 * scale) && region.Bottom == (int)Math.Round(270 * scale), "physical bounds");
                    Assert(region.EllipseWidth == (int)Math.Round(32 * scale), "DPI-aware corners");
                }
                var square = NativeMicaRegion.FromLayout(new Point(), new Size(800, 600), 0, new DpiScale(1, 1));
                Assert(square.EllipseWidth == 0 && square.Right == 800, "snapped square shell");
                var small = NativeMicaRegion.FromLayout(new Point(), new Size(20, 10), 100, new DpiScale(1, 1));
                Assert(small.EllipseWidth == 10, "corner cannot exceed half the shorter edge");
            });
            Check("layered Edge-style windows are rejected without changing them", () =>
            {
                var window = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true };
                try
                {
                    var rejected = false;
                    try { using var adapter = new NativeMicaBackdrop(window, () => null, () => true, _ => { }, () => { }); }
                    catch (ArgumentException) { rejected = true; }
                    Assert(rejected && window.AllowsTransparency, "adapter does not convert existing layered windows");
                }
                finally { window.Close(); }
            });
            Check("native setup succeeds before exposing a transparent surface", () =>
            {
                using var fixture = new Fixture();
                fixture.Apply(true, false);
                Assert(fixture.Backdrop.IsActive && Transparent(fixture.Chrome.Background), "native surface exposed");
                Assert(fixture.Api.Backdrop == DwmMicaApi.MainWindow && !fixture.Api.Dark, "Mica rather than Acrylic/Auto");
                Assert(fixture.Api.HasRegion && fixture.Api.GlassCalls > 0, "glass and shape prepared");
                fixture.Apply(true, true);
                Assert(fixture.Api.Dark && fixture.Backdrop.IsActive, "dark theme follows selection");
                fixture.Apply(false, true);
                Assert(!fixture.Backdrop.IsActive && !Transparent(fixture.Chrome.Background), "solid surface restored");
                Assert(fixture.Api.Backdrop == DwmMicaApi.None && !fixture.Api.HasRegion, "native background and clip removed");
            });
            Check("all native setup failures retain solid content instead of transparent holes", () =>
            {
                foreach (var stage in new[] { "frame", "dark", "backdrop", "region" })
                {
                    using var fixture = new Fixture(new FakeNative { Failure = stage });
                    fixture.Apply(true, false);
                    Assert(!fixture.Backdrop.IsActive && !Transparent(fixture.Chrome.Background), "safe fallback at " + stage);
                    Assert(fixture.Api.Backdrop == DwmMicaApi.None, "backdrop removed at " + stage);
                    Assert(fixture.Backdrop.LastHResult < 0, "failure observable at " + stage);
                }
            });
            Check("unsupported OS, disabled transparency, HC, lost composition and layered styles fall back", () =>
            {
                foreach (var reason in new[] { "old", "transparency", "hc", "composition", "layered" })
                {
                    using var fixture = new Fixture();
                    fixture.Apply(true, false);
                    switch (reason)
                    {
                        case "old": fixture.Api.IsSupported = false; break;
                        case "transparency": fixture.Api.TransparencyEnabled = false; break;
                        case "hc": fixture.Api.HighContrast = true; break;
                        case "composition": fixture.Api.CompositionEnabled = false; break;
                        case "layered": fixture.Api.Layered = true; break;
                    }
                    fixture.Apply(true, false);
                    Assert(!fixture.Backdrop.IsActive && !Transparent(fixture.Chrome.Background), "fallback: " + reason);
                }
            });
            Check("layout and stable refreshes do not continually reapply DWM attributes", () =>
            {
                using var fixture = new Fixture();
                fixture.Apply(true, false);
                Pump();
                var attributes = fixture.Api.BackdropCalls;
                var regions = fixture.Api.RegionCalls;
                for (var i = 0; i < 100; i++) fixture.Backdrop.Refresh(true, false);
                Assert(fixture.Api.BackdropCalls == attributes && fixture.Api.RegionCalls == regions, "unchanged cached state");
                fixture.Window.Width += 40;
                fixture.Window.UpdateLayout();
                fixture.Backdrop.Refresh(true, false);
                Assert(fixture.Api.RegionCalls > regions, "resize updates actual clip");
                Assert(fixture.Api.BackdropCalls == attributes, "resize does not recreate backdrop");
                fixture.Api.Failure = "region";
                fixture.Window.Width += 20;
                fixture.Window.UpdateLayout();
                fixture.Backdrop.Refresh(true, false);
                Assert(!fixture.Backdrop.IsActive && !Transparent(fixture.Chrome.Background), "failed clip update uses solid fallback");
            });
            Check("form and opacity transitions suspend and restore the same native HWND", () =>
            {
                using var fixture = new Fixture();
                fixture.Apply(true, false);
                var hwnd = new WindowInteropHelper(fixture.Window).Handle;
                fixture.Eligible = false;
                fixture.Apply(true, false);
                Assert(!fixture.Backdrop.IsActive && !fixture.Api.HasRegion, "form transition has no full-HWND material");
                fixture.Eligible = true;
                fixture.Apply(true, false);
                fixture.Chrome.Opacity = 0.5;
                Assert(!fixture.Backdrop.IsActive && !Transparent(fixture.Chrome.Background), "chrome fade uses solid alpha fallback");
                fixture.Chrome.Opacity = 1;
                Pump();
                Assert(fixture.Backdrop.IsActive, "completed fade restores Mica");
                fixture.Window.Opacity = 0.75;
                Assert(!fixture.Backdrop.IsActive, "whole-window fade suspends Mica");
                fixture.Window.Opacity = 1;
                Pump();
                Assert(fixture.Backdrop.IsActive && new WindowInteropHelper(fixture.Window).Handle == hwnd, "same HWND restored");
            });
            Check("replacement Settings chrome receives native surface without replacing the window", () =>
            {
                using var fixture = new Fixture();
                fixture.Apply(true, false);
                fixture.Chrome = Fixture.NewChrome();
                fixture.Window.Content = fixture.Chrome;
                fixture.Window.UpdateLayout();
                fixture.Apply(true, true);
                Assert(fixture.Backdrop.IsActive && Transparent(fixture.Chrome.Background), "replacement chrome refreshed");
            });
            Check("failed backdrop disable retains its clip until a successful retry", () =>
            {
                using var fixture = new Fixture();
                fixture.Apply(true, false);
                fixture.Api.Failure = "disable";
                fixture.Apply(false, false);
                Assert(!Transparent(fixture.Chrome.Background) && fixture.Api.HasRegion, "no unbounded native slab");
                fixture.Api.Failure = null;
                fixture.Apply(false, false);
                Assert(!fixture.Api.HasRegion && fixture.Api.Backdrop == DwmMicaApi.None, "successful teardown releases region");
            });
            Check("closed adapters stop responding to layout and preference refresh", () =>
            {
                var fixture = new Fixture();
                fixture.Apply(true, false);
                fixture.Dispose();
                var count = fixture.Api.BackdropCalls;
                fixture.Backdrop.Refresh(true, true, force: true);
                Pump();
                Assert(fixture.Api.BackdropCalls == count, "no disposed HWND access");
            });
            Check("real native DWM attribute and window region round-trip when the runner supports them", NativeSmoke);
            using (var controller = new AppController())
            {
                controller.State.EnableAnimations = false;
                controller.State.UseCapsuleMode = true;
                controller.State.UseDeepCapsuleMode = false;
                Check("native palette keeps semantic plugin colors solid and readable", () =>
                {
                    foreach (var mode in new[] { "light", "dark" })
                    {
                        controller.State.ColorScheme = ColorSchemes.Mica;
                        controller.State.Theme = mode;
                        Theme.Invalidate();
                        Assert(Theme.PaperBrush is SolidColorBrush { IsFrozen: true, Color.A: 255 }, "solid semantic paper color");
                        foreach (var foreground in new[] { Theme.TextBrush, Theme.WeakTextBrush, Theme.LinkBrush })
                            Assert(Contrast(((SolidColorBrush)foreground).Color, ((SolidColorBrush)Theme.PaperBrush).Color) >= 4.5, "fallback text contrast");
                    }
                });
                Check("actual Todo and Note keep their HWND and data across native-skin folding", () =>
                {
                    // Test the session policy without touching production saved preferences or
                    // recreating live windows. The public behavior requires a restart on entry.
                    typeof(AppController).GetProperty("UsesNativeMicaWindows", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(controller, true);
                    foreach (var type in new[] { PaperTypes.Todo, PaperTypes.Note })
                    {
                        var paper = new PaperData { Type = type, Width = 360, Height = 280, Content = "# Mica\n正文保留" };
                        controller.State.Papers.Add(paper);
                        var window = new PaperWindow(paper, controller);
                        try
                        {
                            window.Show();
                            Pump();
                            var hwnd = new WindowInteropHelper(window).Handle;
                            Assert(!window.AllowsTransparency && hwnd != IntPtr.Zero, "non-layered native session");
                            foreach (var scheme in new[] { ColorSchemes.Mica, ColorSchemes.Warm, ColorSchemes.Mica })
                            {
                                controller.State.ColorScheme = scheme;
                                Theme.Invalidate();
                                window.UpdateTheme();
                                window.SetCollapsedState(true, animate: false, saveGeometry: false);
                                Pump();
                                Assert(!window.IsNativeMicaEffective, "folded capsule is never native");
                                Assert(!Transparent((Brush)window.Resources["PaperSurfaceBrushKey"]), "capsule has a solid surface");
                                window.SetCollapsedState(false, animate: false, saveGeometry: false);
                                Pump();
                                Assert(new WindowInteropHelper(window).Handle == hwnd, "no HWND recreation");
                                Assert(paper.Content == "# Mica\n正文保留", "body unchanged");
                            }
                        }
                        finally
                        {
                            window.CloseForReal();
                            controller.State.Papers.Remove(paper);
                        }
                    }
                });
                Check("existing non-Mica startup still uses the original layered paper window", () =>
                {
                    typeof(AppController).GetProperty("UsesNativeMicaWindows", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(controller, false);
                    controller.State.ColorScheme = ColorSchemes.Warm;
                    Theme.Invalidate();
                    var window = new PaperWindow(new PaperData { Type = PaperTypes.Todo }, controller);
                    try { Assert(window.AllowsTransparency && !window.IsNativeMicaEffective, "unchanged legacy policy"); }
                    finally { window.CloseForReal(); }
                });
            }
            Console.WriteLine($"Native Mica checks passed: {_passed}; native integration verified: {_nativeVerified}; native integration skipped: {_skipped}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static void NativeSmoke()
    {
        var native = DwmMicaApi.Instance;
        if (!native.IsSupported || !native.CompositionEnabled || !native.TransparencyEnabled || native.HighContrast)
        {
            Skip("DWM Mica unavailable under this runner's OS/session/accessibility settings");
            return;
        }
        var chrome = Fixture.NewChrome();
        var window = Fixture.NewWindow(chrome);
        using var adapter = new NativeMicaBackdrop(window, () => chrome, () => true, brush => chrome.Background = brush, () => { });
        try
        {
            adapter.Refresh(true, false);
            window.Show();
            Pump();
            adapter.Refresh(true, false, force: true);
            if (!adapter.IsActive)
            {
                Skip($"DWM rejected native setup in this runner session: HRESULT=0x{adapter.LastHResult:X8}");
                Assert(!Transparent(chrome.Background), "real API failure does not leave transparent chrome");
                return;
            }
            var hwnd = new WindowInteropHelper(window).Handle;
            foreach (var dark in new[] { false, true })
            {
                adapter.Refresh(true, dark, force: true);
                Assert(adapter.IsActive, "native remains active");
                Assert(DwmMicaApi.DwmGetWindowAttribute(hwnd, 38, out var value, sizeof(int)) >= 0 && value == 2, "actual DWMSBT_MAINWINDOW readback");
                Assert(DwmMicaApi.DwmGetWindowAttribute(hwnd, 20, out var theme, sizeof(int)) >= 0 && theme == (dark ? 1 : 0), "actual dark-mode readback");
                Assert(!native.IsLayered(hwnd), "actual native HWND is not layered");
            }
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert(GetWindowRgn(hwnd, region) > 0, "actual rounded window region applied");
                Assert(!PtInRegion(region, 0, 0) && PtInRegion(region, 150, 100), "native clip excludes transparent margin and contains body");
            }
            finally { DeleteObject(region); }
            adapter.Refresh(false, false, force: true);
            Assert(DwmMicaApi.DwmGetWindowAttribute(hwnd, 38, out var disabled, sizeof(int)) >= 0 && disabled == 1, "actual native backdrop removed");
            _nativeVerified++;
            Console.WriteLine("NATIVE VERIFIED: actual HWND Mica attribute, light/dark state, shape region and removal (not a visual screenshot assertion).");
        }
        finally { window.Close(); }
    }

    private sealed class Fixture : IDisposable
    {
        internal Window Window { get; }
        internal Border Chrome { get; set; }
        internal FakeNative Api { get; }
        internal NativeMicaBackdrop Backdrop { get; }
        internal bool Eligible { get; set; } = true;
        internal Fixture(FakeNative? native = null)
        {
            Api = native ?? new FakeNative();
            Chrome = NewChrome();
            Window = NewWindow(Chrome);
            Backdrop = new NativeMicaBackdrop(Window, () => Chrome, () => Eligible, brush => Chrome.Background = brush, () => { }, Api);
            Window.Show();
            Pump();
        }
        internal void Apply(bool requested, bool dark) => Backdrop.Refresh(requested, dark, force: true);
        internal static Border NewChrome() => new()
        {
            Margin = new Thickness(10), CornerRadius = new CornerRadius(16), Background = Brushes.White,
            Child = new TextBlock { Text = "Native Mica\n原生云母", Foreground = Brushes.Black, Margin = new Thickness(20) }
        };
        internal static Window NewWindow(Border chrome) => new()
        {
            Content = chrome, Width = 320, Height = 240, Left = 40, Top = 40,
            WindowStyle = WindowStyle.None, AllowsTransparency = false, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false
        };
        public void Dispose() { Window.Close(); Backdrop.Dispose(); }
    }
    private sealed class FakeNative : INativeMicaApi
    {
        internal const int FailureCode = unchecked((int)0x80004005);
        public bool IsSupported { get; set; } = true;
        public bool CompositionEnabled { get; set; } = true;
        public bool TransparencyEnabled { get; set; } = true;
        public bool HighContrast { get; set; }
        internal bool Layered, Dark, HasRegion;
        internal string? Failure;
        internal int Backdrop = DwmMicaApi.None, BackdropCalls, RegionCalls, GlassCalls;
        public bool IsLayered(IntPtr hwnd) => Layered;
        public int ExtendFrame(IntPtr hwnd, bool enabled) { GlassCalls++; return Failure == "frame" ? FailureCode : 0; }
        public int SetDarkMode(IntPtr hwnd, bool dark) { Dark = dark; return Failure == "dark" ? FailureCode : 0; }
        public int SetBackdrop(IntPtr hwnd, int backdrop)
        {
            BackdropCalls++;
            if ((backdrop == 2 && Failure == "backdrop") || (backdrop == 1 && Failure == "disable")) return FailureCode;
            Backdrop = backdrop;
            return 0;
        }
        public int EnableAlpha(IntPtr hwnd) => Failure == "alpha" ? FailureCode : 0;
        public bool SetRegion(IntPtr hwnd, NativeMicaRegion region)
        {
            RegionCalls++;
            if (Failure == "region") return false;
            HasRegion = true;
            return true;
        }
        public bool ClearRegion(IntPtr hwnd) { HasRegion = false; return true; }
        public void RefreshFrame(IntPtr hwnd) { }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static bool Transparent(Brush brush) => brush is SolidColorBrush solid && solid.Color.A == 0;
    private static void Check(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Skip(string reason) { _skipped++; Console.WriteLine("SKIP NATIVE: " + reason); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { var c = value / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => Channel(c.R) * 0.2126 + Channel(c.G) * 0.7152 + Channel(c.B) * 0.0722;
        var la = Luminance(a); var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
}
