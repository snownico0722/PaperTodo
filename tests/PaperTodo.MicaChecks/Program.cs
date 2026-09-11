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
    internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        // Read the real resource definitions, but not the Application subclass/BAML root:
        // pumping an App would start a second production controller in this test process.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using (var source = typeof(Program).Assembly.GetManifestResourceStream("PaperTodo.App.xaml")!)
        {
            var xaml = System.Xml.Linq.XDocument.Load(source);
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
                xaml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration),
                xaml.Root.Element(ns + "Application.Resources")!.Nodes());
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        }
        var temp = Path.Combine(Path.GetTempPath(), "PaperTodo.NativeMicaChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Check("skin normalization and data compatibility", () =>
            {
                foreach (var id in new[] { "warm", "ink", "forest", "rose", "mica" })
                    Assert(ColorSchemes.IsValid(id) && ColorSchemes.Normalize(id) == id, id);
                Assert(ColorSchemes.All.Distinct().Count() == 5, "unique skins");
                Assert(ColorSchemes.Normalize(null) == ColorSchemes.Warm, "null default");
                Assert(ColorSchemes.Normalize("future") == ColorSchemes.Warm, "unknown default");
                Assert(MicaBackdropTypes.Normalize(null) == MicaBackdropTypes.Mica, "null mica backdrop default");
                Assert(MicaBackdropTypes.Normalize("future") == MicaBackdropTypes.Mica, "unknown mica backdrop default");
                Assert(MicaBackdropTypes.ToDwmBackdrop(MicaBackdropTypes.Mica) == 2, "mica backdrop dwm value");
                Assert(MicaBackdropTypes.Normalize("micaAlt") == MicaBackdropTypes.Mica, "retired Mica Alt migrates to Mica");
                Assert(MicaBackdropTypes.ToDwmBackdrop(MicaBackdropTypes.Acrylic) == 3, "acrylic backdrop dwm value");
                Assert(MicaBackdropTypes.ToDwmBackdrop(MicaBackdropTypes.ClearAcrylic) == 1, "clear Acrylic disables the fixed system backdrop");
                var store = new StateStore(temp, DurableAtomicFileWriter.Shared);
                long version = 0;
                foreach (var mode in new[] { "light", "dark", "system" })
                foreach (var material in MicaBackdropTypes.All)
                foreach (var alwaysActive in new[] { false, true })
                {
                    var state = new AppState { ColorScheme = ColorSchemes.Mica, Theme = mode,
                        MicaBackdropType = material, MicaAlwaysActive = alwaysActive };
                    state.Papers.Add(new PaperData { Type = PaperTypes.Note, Content = "# Mica\n保留笔记" });
                    store.SaveJsonSync(store.SerializeState(state), ++version);
                    var restored = store.Load();
                    Assert(restored.ColorScheme == "mica" && restored.Theme == mode && restored.MicaBackdropType == material &&
                        restored.MicaAlwaysActive == alwaysActive, "saved material and activation preference");
                    Assert(restored.Papers[0].Content == state.Papers[0].Content, "preserved body");
                }
                store.SaveJsonSync("""{"colorScheme":"mica","micaBackdropType":"micaAlt","papers":[]}""", ++version);
                var legacy = store.Load();
                Assert(legacy.MicaBackdropType == MicaBackdropTypes.Mica && !legacy.MicaAlwaysActive,
                    "old data migrates retired material and defaults to real activation");
            });
            Check("layered Edge HWNDs are never accepted", () =>
            {
                var window = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true };
                try
                {
                    var rejected = false;
                    try { using var adapter = new NativeMicaBackdrop(window, () => null, () => true, _ => { }); }
                    catch (ArgumentException) { rejected = true; }
                    Assert(rejected && window.AllowsTransparency, "unchanged layered policy");
                }
                finally { window.Close(); }
            });
            Check("native surface and alpha fallback are mutually exclusive", () =>
            {
                using var f = new Fixture();
                f.Apply(true, false);
                Assert(f.Backdrop.IsActive && Transparent(f.Chrome.Background), "transparent native surface");
                Assert(!f.Api.Alpha && f.Api.Backdrop == 2 && f.Api.Rounded, "native recipe and corners");
                Assert(Transparent(f.Chrome.BorderBrush) && f.Api.BorderColor >= 0, "native frame is the only outer stroke");
                f.Backdrop.Refresh(true, false, material: MicaBackdropTypes.Acrylic, force: true);
                Assert(f.Api.Backdrop == 3, "Acrylic backdrop");
                f.Apply(true, true);
                Assert(f.Api.Dark, "explicit dark mode");
                f.Apply(false, true);
                Assert(!f.Backdrop.IsActive && !Transparent(f.Chrome.Background), "solid fallback");
                Assert(f.Api.Alpha && f.Api.Backdrop == 1 && !f.Api.Rounded, "fallback recipe");
                Assert(!Transparent(f.Chrome.BorderBrush) && f.Api.BorderColor == unchecked((int)0xfffffffe),
                    "fallback restores the WPF stroke and removes the native stroke");
                f.Apply(true, false);
                Assert(f.Backdrop.IsActive && !f.Api.Alpha, "no residual legacy blur after fallback");
                Assert(f.Api.CaptionColor == unchecked((int)0xffffffff) && f.Api.FrameTop == 0 && f.Api.RedirectionAlpha,
                    "modern system material uses explicit redirection alpha without extended caption");
                f.Api.Failure = "frame-colors"; f.Apply(true, false);
                Assert(f.Backdrop.IsActive && f.Backdrop.LastFrameHResult < 0 && !Transparent(f.Chrome.BorderBrush),
                    "rejected native frame settings retain a visible WPF outline");
            });
            Check("unsupported modern alpha retains full-glass composition", () =>
            {
                using var f = new Fixture(new FakeNative { Failure = "redirection-unsupported" });
                f.Apply(true, false);
                Assert(f.Backdrop.IsActive && f.Api.Glass && f.Api.FrameTop == -1 && !f.Api.RedirectionAlpha,
                    "old Windows must never be forced into zero-margin black output");
            });
            Check("Clear Acrylic switches native recipes without recreating content", () =>
            {
                using var f = new Fixture();
                var hwnd = new WindowInteropHelper(f.Window).Handle;
                foreach (var dark in new[] { false, true })
                {
                    f.Backdrop.Refresh(true, dark, MicaBackdropTypes.Acrylic);
                    var standard = ((SolidColorBrush)f.Chrome.Background).Color;
                    Assert(!f.Api.ClearAcrylic && f.Api.Backdrop == 3 && f.Api.FrameTop == 0 && f.Api.RedirectionAlpha, "standard system Acrylic recipe restored");
                    f.Backdrop.Refresh(true, dark, MicaBackdropTypes.ClearAcrylic);
                    var clear = ((SolidColorBrush)f.Chrome.Background).Color;
                    Assert(standard.A == (dark ? 144 : 152), "standard Acrylic preserves the current effect");
                    Assert(clear.A == 0 && f.Api.ClearAcrylic && f.Api.FrameTop == 0, "native tint without a second WPF wash or full glass");
                    Assert(f.Api.Backdrop == 1 && !f.Api.Alpha && f.Chrome.Opacity == 1 && f.Window.Opacity == 1,
                        "exclusive accent blur with opaque content");
                    f.Backdrop.Refresh(true, dark, MicaBackdropTypes.Acrylic);
                    Assert(!f.Api.ClearAcrylic && f.Api.Backdrop == 3 && f.Api.FrameTop == 0 && f.Api.RedirectionAlpha, "switching back removes the active accent");
                    f.Backdrop.Refresh(true, dark, MicaBackdropTypes.ClearAcrylic);
                    f.Eligible = false; f.Apply(true, dark);
                    Assert(!f.Api.ClearAcrylic && f.Api.Alpha, "collapse removes accent before alpha fallback");
                    f.Eligible = true; f.Apply(true, dark);
                    Assert(f.Api.ClearAcrylic && !f.Api.Alpha, "expanded endpoint restores accent");
                    f.Api.Failure = "accent"; f.Apply(true, dark);
                    Assert(!f.Backdrop.IsActive && !f.Api.ClearAcrylic && !Transparent(f.Chrome.Background) &&
                        f.Backdrop.LastHResult < 0, "failed refresh clears the previous accent and restores solid paper");
                    f.Api.Failure = null;
                }
                f.Apply(true, false);
                Assert(new WindowInteropHelper(f.Window).Handle == hwnd, "material changes retain the same HWND");
                f.Backdrop.Dispose();
                Assert(!f.Api.ClearAcrylic, "disposal removes accent");
            });
            Check("Aero uses clear alpha without inheriting Acrylic blur", () =>
            {
                using var f = new Fixture();
                f.Backdrop.Refresh(true, false, NativeMicaBackdrop.AeroGlassMaterial);
                Assert(f.Backdrop.IsActive && f.Api.Alpha && f.Api.AccentState == 0 && f.Api.Backdrop == 1 &&
                    f.Api.FrameTop == 0 && Transparent(f.Chrome.Background), "Aero transmits without either Acrylic recipe");
                f.Backdrop.Refresh(true, false, MicaBackdropTypes.Acrylic);
                Assert(!f.Api.Alpha && f.Api.AccentState == 0 && f.Api.Backdrop == 3, "Aero -> system Acrylic clears alpha first");
                f.Backdrop.Refresh(true, false, MicaBackdropTypes.ClearAcrylic);
                Assert(f.Api.AccentState == 4, "existing Clear Acrylic remains recipe 4");
                f.Backdrop.Refresh(true, false, NativeMicaBackdrop.AeroGlassMaterial);
                Assert(f.Api.Alpha && f.Api.AccentState == 0 && f.Api.Backdrop == 1, "Clear Acrylic -> Aero removes accent");
                f.Api.Failure = "alpha-disable";
                f.Backdrop.Refresh(true, false, NativeMicaBackdrop.AeroGlassMaterial, force: true);
                Assert(!f.Backdrop.IsActive && f.Api.AccentState == 0 && !Transparent(f.Chrome.Background),
                    "Aero API failure keeps a readable solid fallback");
            });

            Check("clear glass preserves alpha without enabling either blur recipe", () =>
            {
                using var f = new Fixture();
                var hwnd = new WindowInteropHelper(f.Window).Handle;
                foreach (var previous in new[] { MicaBackdropTypes.Mica, MicaBackdropTypes.Acrylic, MicaBackdropTypes.ClearAcrylic })
                {
                    f.Backdrop.Refresh(true, false, previous);
                    f.Backdrop.Refresh(true, false, NativeMicaBackdrop.ClearGlassMaterial);
                    Assert(f.Backdrop.IsActive && f.Api.Alpha && f.Api.Backdrop == 1 && !f.Api.ClearAcrylic &&
                        f.Api.FrameTop == 0 && Transparent(f.Chrome.Background), "unblurred clear composition");
                    f.Backdrop.Refresh(true, false, previous);
                    Assert(!f.Api.Alpha, "switching to a native material clears glass alpha before backdrop setup");
                }
                f.Backdrop.Refresh(true, false, NativeMicaBackdrop.ClearGlassMaterial);
                f.Api.TransparencyEnabled = false; f.Apply(true, false);
                Assert(!f.Backdrop.IsActive && !Transparent(f.Chrome.Background), "disabled transparency remains opaque");
                Assert(new WindowInteropHelper(f.Window).Handle == hwnd, "same editor HWND across recipes");
            });
            Check("active material appearance never prevents real focus changes", () =>
            {
                using var f = new Fixture();
                var other = new Window { Left = 450, Top = 40, Width = 120, Height = 120 };
                try
                {
                    f.Backdrop.Refresh(true, false, alwaysActive: true);
                    f.Window.Activate(); Pump();
                    var calls = f.Api.ActivationCalls;
                    other.Show(); other.Activate(); Pump();
                    Assert(other.IsActive && !f.Window.IsActive, "focus leaves the paper normally");
                    Assert(f.Api.ActivationCalls > calls && f.Api.NonClientActive, "inactive message preserves active material appearance");
                    f.Backdrop.Refresh(true, false, alwaysActive: false);
                    Assert(!f.Api.NonClientActive && !f.Window.IsActive, "unchecking restores real inactive appearance immediately");
                    f.Backdrop.Refresh(true, false, alwaysActive: true);
                    Assert(f.Api.NonClientActive && !f.Window.IsActive, "checking an inactive paper does not activate it");
                    f.Eligible = false; f.Apply(true, false);
                    Assert(!f.Api.NonClientActive && !f.Backdrop.IsActive, "animation fallback suspends the appearance override");
                    f.Eligible = true; f.Apply(true, false);
                    Assert(f.Api.NonClientActive && !f.Window.IsActive, "native endpoint restores the appearance override");
                    f.Backdrop.Dispose();
                    Assert(!f.Api.NonClientActive, "disposal restores actual activation appearance");
                }
                finally { other.Close(); }
            });
            Check("setup failures never publish a transparent shell", () =>
            {
                foreach (var stage in new[] { "alpha-disable", "frame", "dark", "backdrop" })
                {
                    using var f = new Fixture(new FakeNative { Failure = stage });
                    f.Apply(true, false);
                    Assert(!f.Backdrop.IsActive && !Transparent(f.Chrome.Background), stage);
                    Assert(f.Api.Backdrop == 1 && f.Backdrop.LastHResult < 0, "observable safe fallback");
                }
            });
            Check("unsupported, high contrast, transparency, composition and layered fallbacks", () =>
            {
                foreach (var reason in new[] { "old", "hc", "transparency", "composition", "layered" })
                {
                    using var f = new Fixture();
                    f.Apply(true, false);
                    switch (reason)
                    {
                        case "old": f.Api.IsSupported = false; break;
                        case "hc": f.Api.HighContrast = true; break;
                        case "transparency": f.Api.TransparencyEnabled = false; break;
                        case "composition": f.Api.CompositionEnabled = false; break;
                        case "layered": f.Api.Layered = true; break;
                    }
                    f.Apply(true, false);
                    Assert(!f.Backdrop.IsActive && !Transparent(f.Chrome.Background), reason);
                }
            });
            Check("stable layouts and resizing do not repaint native attributes", () =>
            {
                using var f = new Fixture(); f.Apply(true, false); Pump();
                var calls = f.Api.BackdropCalls;
                for (int i = 0; i < 100; i++) f.Backdrop.Refresh(true, false);
                f.Window.Width += 40; f.Window.UpdateLayout(); f.Backdrop.Refresh(true, false);
                Assert(calls == f.Api.BackdropCalls, "one full-HWND surface, no resize regions");
                f.Chrome.CornerRadius = new CornerRadius(0); f.Backdrop.Refresh(true, false);
                Assert(!f.Api.Rounded, "snapped/maximized square corner policy");
            });
            Check("form, whole-window and chrome opacity restore the same native HWND", () =>
            {
                using var f = new Fixture(); f.Apply(true, false);
                var hwnd = new WindowInteropHelper(f.Window).Handle;
                f.Eligible = false; f.Apply(true, false);
                Assert(!f.Backdrop.IsActive && f.Api.Alpha, "form fallback");
                f.Eligible = true; f.Apply(true, false);
                f.Chrome.Opacity = 0.5;
                Assert(!f.Backdrop.IsActive, "explicit chrome transparency fallback");
                f.Chrome.Opacity = 1; Pump();
                Assert(f.Backdrop.IsActive && !f.Api.Alpha, "chrome opacity restored");
                f.Window.Opacity = 0.5;
                Assert(!f.Backdrop.IsActive, "window animation fallback");
                f.Window.Opacity = 1; Pump();
                Assert(f.Backdrop.IsActive && !f.Api.Alpha, "startup animation restores clean native path");
                Assert(new WindowInteropHelper(f.Window).Handle == hwnd, "no window/editor recreation");
            });
            Check("replacement settings root and adapter disposal", () =>
            {
                using var f = new Fixture(); f.Apply(true, false);
                f.Chrome = Fixture.NewChrome(); f.Window.Content = f.Chrome; f.Window.UpdateLayout();
                f.Apply(true, true);
                Assert(Transparent(f.Chrome.Background) && f.Backdrop.IsActive, "rebuilt root");
                f.Backdrop.Dispose(); f.Backdrop.Dispose();
                var calls = f.Api.BackdropCalls;
                f.Window.Opacity = 0.6; f.Backdrop.Refresh(true, false); Pump();
                Assert(!f.Backdrop.IsActive && f.Api.BackdropCalls == calls, "detached hooks and idempotent disposal");
            });
            using (var controller = new AppController())
            {
                typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, true);
                controller.State.PaperSkin = null; // Exercise the pre-skin Mica settings format.
                controller.State.EnableAnimations = false;
                controller.State.UseCapsuleMode = true;
                controller.State.UseDeepCapsuleMode = false;
                controller.State.ExperimentalInactivePaperOpacity = false;
                Check("readable opaque semantic palette in light and dark", () =>
                {
                    controller.State.ColorScheme = "mica";
                    foreach (var mode in new[] { "light", "dark" })
                    {
                        controller.State.Theme = mode; Theme.Invalidate();
                        var paper = ((SolidColorBrush)Theme.PaperBrush).Color;
                        var text = ((SolidColorBrush)Theme.TextBrush).Color;
                        Assert(paper.A == 255 && text.A == 255, "opaque plugin/menu colors");
                        Assert(Contrast(paper, text) >= 4.5, "readable text contrast");
                    }
                });
                Check("real Todo and Note keep one editor and use edge-to-edge expanded chrome", () =>
                {
                    foreach (var type in new[] { PaperTypes.Todo, PaperTypes.Note })
                    {
                        var paper = new PaperData { Type = type, Content = "# Mica\n保留正文", X = 40, Y = 40, Width = 360, Height = 320 };
                        controller.State.Papers.Add(paper);
                        var window = new PaperWindow(paper, controller);
                        try
                        {
                            window.Show(); Pump();
                            var hwnd = new WindowInteropHelper(window).Handle;
                            var chrome = (Border)typeof(PaperWindow).GetField("_paperChrome", Private)!.GetValue(window)!;
                            var body = chrome.Child;
                            foreach (var scheme in new[] { "mica", "warm", "mica" })
                            {
                                controller.State.ColorScheme = scheme; controller.State.Theme = "light"; Theme.Invalidate(); window.UpdateTheme(); Pump();
                                Assert(chrome.Margin == new Thickness(0) && chrome.Effect == null, "no old shadow gutter, including fallback");
                                Assert(chrome.Opacity == 1 && window.Opacity == 1, "disabled inactive transparency does not dim UI");
                                window.SetCollapsedState(true, animate: false, saveGeometry: false); Pump();
                                Assert(!window.IsNativeMicaEffective, "no Mica on folded capsule");
                                window.SetCollapsedState(false, animate: false, saveGeometry: false); Pump();
                                Assert(chrome.Margin == new Thickness(0) && chrome.Effect == null, "expansion restores native shell");
                                Assert(new WindowInteropHelper(window).Handle == hwnd && ReferenceEquals(chrome.Child, body), "same HWND and editor tree");
                                Assert(paper.Content == "# Mica\n保留正文", "unchanged body");
                            }
                        }
                        finally { window.CloseForReal(); controller.State.Papers.Remove(paper); }
                    }
                });
                Check("experimental skins", () => SkinChecks.Run(controller));
                Check("curved material lighting and parallax", () => MaterialStudyChecks.Run(controller));
                Check("real background refraction and capture lifecycle", () => RefractionChecks.Run(controller));
                Check("shared live capsule and popup materials, RGB dispersion and lifecycle", () => SharedMaterialChecks.Run(controller));
                Check("material-specific colors and stable preview opacity", () => MaterialPaletteChecks.Run(controller));
                Check("stable sampling, retained settings shell and cancelled menu opening", () => MaterialPresentationChecks.Run(controller));
                Check("native activation, shape and desktop pixels", () => VisualChecks.Run(controller));
                Check("actual native header, frame and unblurred glass pixels", () => NativeSurfaceChecks.Run(controller));
                Check("non-Mica startup keeps the original layered paper", () =>
                {
                    typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, false);
                    controller.State.ColorScheme = "warm"; Theme.Invalidate();
                    var window = new PaperWindow(new PaperData { Type = PaperTypes.Todo }, controller);
                    try { Assert(window.AllowsTransparency && !window.IsNativeMicaEffective, "unchanged legacy window"); }
                    finally { window.CloseForReal(); }
                });
            }
            Console.WriteLine($"Native Mica behavior checks passed: {_passed}.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(temp, recursive: true); }
    }
    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    internal static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static bool Transparent(Brush brush) => brush is SolidColorBrush solid && solid.Color.A == 0;
    private static void Check(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte v) { var c = v / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double L(Color c) => Channel(c.R) * 0.2126 + Channel(c.G) * 0.7152 + Channel(c.B) * 0.0722;
        return (Math.Max(L(a), L(b)) + 0.05) / (Math.Min(L(a), L(b)) + 0.05);
    }
    private sealed class Fixture : IDisposable
    {
        internal Window Window { get; }
        internal Border Chrome { get; set; }
        internal FakeNative Api { get; }
        internal NativeMicaBackdrop Backdrop { get; }
        internal bool Eligible { get; set; } = true;
        internal Fixture(FakeNative? api = null)
        {
            Api = api ?? new FakeNative(); Chrome = NewChrome();
            Window = new Window { Content = Chrome, Width = 300, Height = 230, Left = 40, Top = 40, WindowStyle = WindowStyle.SingleBorderWindow, AllowsTransparency = false, ShowInTaskbar = false };
            Backdrop = new NativeMicaBackdrop(Window, () => Chrome, () => Eligible, b => Chrome.Background = b, Api);
            Window.Show(); Pump();
        }
        internal static Border NewChrome() => new() { CornerRadius = new CornerRadius(8), Background = Brushes.White };
        internal void Apply(bool requested, bool dark) => Backdrop.Refresh(requested, dark, force: true);
        public void Dispose() { Window.Close(); Backdrop.Dispose(); }
    }
    private sealed class FakeNative : INativeMicaApi
    {
        private const int Error = unchecked((int)0x80004005);
        public bool IsSupported { get; set; } = true;
        public bool CompositionEnabled { get; set; } = true;
        public bool TransparencyEnabled { get; set; } = true;
        public bool HighContrast { get; set; }
        internal bool Layered, Dark, Alpha, Rounded, NonClientActive, ClearAcrylic, Glass, RedirectionAlpha;
        internal int ActivationCalls, AccentState;
        internal string? Failure;
        internal int Backdrop = 1, BackdropCalls, BorderColor, CaptionColor, FrameTop;
        public bool IsLayered(IntPtr hwnd) => Layered;
        public int ExtendFrame(IntPtr hwnd, int top) { FrameTop = top; Glass = top < 0; return Failure == "frame" ? Error : 0; }
        public int SetDarkMode(IntPtr hwnd, bool dark) { Dark = dark; return Failure == "dark" ? Error : 0; }
        public int SetBackdrop(IntPtr hwnd, int backdrop)
        {
            BackdropCalls++;
            if (backdrop == 2 && Failure == "backdrop") return Error;
            if (backdrop is 2 or 3) Assert(!Alpha && !ClearAcrylic && (Glass || RedirectionAlpha), "system backdrop needs full glass or explicit bitmap alpha, and excludes legacy alpha/accent");
            Backdrop = backdrop; return 0;
        }
        public int SetClearAcrylic(IntPtr hwnd, bool enabled, bool dark)
        {
            if (enabled && Failure == "accent") return Error;
            if (enabled) Assert(Backdrop == 1 && !Alpha && !Glass, "accent excludes system backdrop, full glass and alpha fallback");
            ClearAcrylic = enabled; AccentState = enabled ? 4 : 0; return 0;
        }
        public int EnableAlpha(IntPtr hwnd) { Assert(!ClearAcrylic, "alpha fallback must not retain accent Acrylic"); Alpha = true; return 0; }
        public void InvalidateContent(IntPtr hwnd) { }
        public int SetRedirectionAlpha(IntPtr hwnd, bool enabled) { if (Failure == "redirection-unsupported") return Error; RedirectionAlpha = enabled; return 0; }
        public int DisableAlpha(IntPtr hwnd) { if (Failure == "alpha-disable") return Error; Alpha = false; return 0; }
        public int ConfigureFrame(IntPtr hwnd, bool rounded, int borderColor, int captionColor)
        { Rounded = rounded; BorderColor = borderColor; CaptionColor = captionColor; return Failure == "frame-colors" ? Error : 0; }
        public void SetNonClientActive(IntPtr hwnd, bool active) { NonClientActive = active; ActivationCalls++; }
    }
}
