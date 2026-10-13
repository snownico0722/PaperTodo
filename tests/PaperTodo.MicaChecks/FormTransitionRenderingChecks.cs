using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using PaperTodo;

internal static class FormTransitionRenderingChecks
{
    internal static void Run(AppController controller)
    {
        var state = controller.State;
        var saved = (state.PaperSkin, state.Theme, state.MatchAuxiliaryMaterialStrength, state.HideSurfaceOutline);
        try
        {
            state.PaperSkin = PaperSkins.Aero;
            state.Theme = "light";
            state.MatchAuxiliaryMaterialStrength = false;
            Theme.Invalidate();
            CheckContentViewport();
            CheckShadow(state);
            CheckReliefTiles();
            CheckQuietMaterialTransitionEndpoints(state);
            foreach (var material in new[] { MicaBackdropTypes.Acrylic,
                MicaBackdropTypes.ClearAcrylic, NativeMicaBackdrop.AeroGlassMaterial })
                CheckNativePreparation(material);
            CheckFallbackShadowSettlement(controller);
        }
        finally
        {
            (state.PaperSkin, state.Theme, state.MatchAuxiliaryMaterialStrength, state.HideSurfaceOutline) = saved;
            Theme.Invalidate();
        }
        Console.WriteLine("FORM RENDERING: scaled far-edge pixels, gutter-bounded shadow, " +
            "quiet-material endpoint continuity, forced shadow restoration, reusable rim tiles " +
            "and staged/cancelled native fallback passed.");
    }

    private static void CheckContentViewport()
    {
        var content = new Grid { Background = Brushes.Blue };
        content.Children.Add(new Border
        {
            Width = 32, Height = 32, Background = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        });
        var transform = new ScaleTransform();
        content.RenderTransform = transform;
        var host = new PaperFormContentHost { Child = content, ExpandedSize = new Size(400, 300) };
        var root = new Border { BorderThickness = new Thickness(1), Child = host };
        // Raster densities exercise rounding, not simulated native monitor DPI.
        foreach (var density in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var size in new[] { new Size(402, 302), new Size(302, 202), new Size(152, 72), new Size(82, 34) })
        {
            transform.ScaleX = (size.Width - 2) / 400;
            transform.ScaleY = (size.Height - 2) / 300;
            var pixels = Render(root, size, density);
            var corner = pixels.At(1 + (size.Width - 2) * .96, 1 + (size.Height - 2) * .96);
            Program.Assert(content.RenderSize == new Size(400, 300), "unscaled layout is retained throughout the morph");
            Program.Assert(corner.R > 200 && corner.G < 30 && corner.B < 30 && corner.A > 240,
                $"far corner is scaled, not clipped before scaling: size={size}, density={density}, pixel={corner}");
        }
        host.ExpandedSize = null;
        transform.ScaleX = transform.ScaleY = 1;
        Render(root, new Size(502, 352), 1);
        Program.Assert(ReferenceEquals(host.Child, content) && content.RenderSize == new Size(500, 350),
            "ending the morph restores normal stretch without replacing content");
    }

    private static void CheckShadow(AppState state)
    {
        foreach (var hiddenOutline in new[] { false, true })
        {
            state.HideSurfaceOutline = hiddenOutline;
            var surface = new SkinBorder
            {
                Background = Theme.PaperBrush, BorderBrush = Theme.PaperBorderBrush,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12)
            };
            surface.SetFormTransition(true);
            var root = new Grid();
            root.Children.Add(surface);
            foreach (var margin in new[] { 0.0, .25, .75, 1.0, 2.0, 4.0, 8.0 })
            {
                surface.Margin = new Thickness(margin);
                surface.SetMaterialShadowMargin(margin);
                var pixels = Render(root, new Size(260, 190), 1);
                var drawing = VisualTreeHelper.GetDrawing(surface)!;
                var guard = 1 / VisualTreeHelper.GetDpi(surface).DpiScaleX;
                var extent = Math.Min(7, Math.Max(0, margin - guard));
                var bounds = drawing.Bounds;
                Program.Assert(bounds.Left >= -extent - .01 && bounds.Top >= -extent - .01 &&
                    bounds.Right <= surface.ActualWidth + extent + .01 &&
                    bounds.Bottom <= surface.ActualHeight + extent + .01,
                    $"shadow paint fits current gutter, margin={margin}, bounds={bounds}");
                Program.Assert(surface.Effect == null, "material shadow does not process the content with an Effect");
                if (margin == 8)
                    Program.Assert(pixels.At(6, 95).A > 0 && pixels.At(253, 95).A > 0,
                        "both side shadows survive even when the outline is hidden");
            }
        }
    }

    private static void CheckReliefTiles()
    {
        foreach (var sourceSize in new[] { new Size(420, 320), new Size(76, 32) })
        {
            var corners = new CornerRadius(sourceSize.Height == 32 ? 16 : 8);
            var drawing = MaterialRelief.Create(sourceSize, corners, new Thickness(1), new DpiScale(1, 1), false);
            var relief = new MaterialTransitionRelief(drawing, sourceSize, corners);
            var surface = new ReliefSurface(relief);
            foreach (var size in new[] { new Size(420, 320), new Size(200, 100), new Size(76, 32) })
            {
                surface.Corners = new CornerRadius(size.Height == 32 ? 16 : 8);
                var pixels = Render(surface, size, 1);
                Program.Assert(pixels.AlphaIn(new Rect(0, size.Height / 3, 4, size.Height / 3)) > 0 &&
                    pixels.AlphaIn(new Rect(size.Width - 4, size.Height / 3, 4, size.Height / 3)) > 0,
                    $"cached rim retains both sides, including pill-source expansion: source={sourceSize}, target={size}");
            }
        }
    }

    private static void CheckQuietMaterialTransitionEndpoints(AppState state)
    {
        // Both ends of a quiet-material morph must match the actual resting paint.
        // Expansion changes IsCapsule to the target role before its first animation frame.
        foreach (var skin in new[] { PaperSkins.Mica, PaperSkins.Acrylic,
            PaperSkins.ClearAcrylic, PaperSkins.TracingPaper, PaperSkins.Aero })
        {
            state.PaperSkin = skin;
            Theme.Invalidate();
            var surface = new SkinBorder
            {
                IsCapsule = true, Background = Theme.PaperBrush,
                BorderBrush = Theme.PaperBorderBrush,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12)
            };
            var size = new Size(176, 64);
            Color Center() => Render(surface, size, 1).At(88, 32);
            var restingCapsule = Center();

            surface.SetFormTransition(true);
            surface.SetFormTransitionCompactness(1);
            AssertSameCenter(restingCapsule, Center(), skin + ": collapse endpoint");
            surface.IsCapsule = false;
            AssertSameCenter(restingCapsule, Center(), skin + ": expand first frame");

            surface.SetFormTransitionCompactness(0);
            var stagedExpanded = Center();
            surface.SetFormTransition(false);
            AssertSameCenter(stagedExpanded, Center(), skin + ": expand endpoint");

            surface.SetFormTransition(true);
            surface.IsCapsule = true;
            surface.SetFormTransitionCompactness(1);
            AssertSameCenter(restingCapsule, Center(), skin + ": return to capsule");
            surface.SetFormTransition(false);
            AssertSameCenter(restingCapsule, Center(), skin + ": resting after collapse");
        }
        state.PaperSkin = PaperSkins.Aero;
        Theme.Invalidate();
    }

    private static void AssertSameCenter(Color expected, Color actual, string context)
    {
        var difference = Math.Max(
            Math.Max(Math.Abs(expected.A - actual.A), Math.Abs(expected.R - actual.R)),
            Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
        Program.Assert(difference <= 1,
            $"{context}: staged and resting fallback colors differ: {expected} / {actual}");
    }

    private static void CheckFallbackShadowSettlement(AppController controller)
    {
        // Material skin without a native HWND: transition removes the full-content
        // Effect, but the expanded endpoint still owns its ordinary WPF shadow.
        var nativeMode = typeof(AppController).GetProperty("UsesNativeMicaWindows", Program.Private)!;
        var wasNative = controller.UsesNativeMicaWindows;
        var state = controller.State;
        var saved = (state.PaperSkin, state.EnableAnimations, state.UseCapsuleMode,
            state.UseDeepCapsuleMode);
        var paper = new PaperData
        {
            Type = PaperTypes.Note, Title = "Material shadow settlement",
            Content = "Retained editor", IsVisible = true,
            Width = 360, Height = 250, X = 60, Y = 60, AlwaysOnTop = true
        };
        PaperWindow? window = null;
        try
        {
            nativeMode.SetValue(controller, false);
            state.PaperSkin = PaperSkins.Acrylic;
            state.EnableAnimations = true;
            state.UseCapsuleMode = true;
            state.UseDeepCapsuleMode = false;
            Theme.Invalidate();
            state.Papers.Add(paper);
            window = new PaperWindow(paper, controller) { ShowInTaskbar = false };
            window.Show();
            var chrome = (SkinBorder)typeof(PaperWindow)
                .GetField("_paperChrome", Program.Private)!.GetValue(window)!;
            window.SetCollapsedState(true, animate: false, saveGeometry: false);
            window.SetCollapsedState(false, animate: true, saveGeometry: false);
            Program.Assert(chrome.Effect == null && chrome.IsFormTransition,
                "layered Acrylic expansion starts with its cheap form shadow");
            state.EnableAnimations = false;
            window.SettleAnimationsForDisabledSetting();
            Program.Assert(!chrome.IsFormTransition && chrome.Effect is DropShadowEffect,
                "disabled animation restores expanded paper shadow");

            state.EnableAnimations = true;
            window.SetCollapsedState(true, animate: false, saveGeometry: false);
            window.SetCollapsedState(false, animate: true, saveGeometry: false);
            window.EnsureExpandedSurfaceGeometry();
            Program.Assert(!chrome.IsFormTransition && chrome.Effect is DropShadowEffect,
                "forced expanded surface restoration also selects the endpoint shadow");
        }
        finally
        {
            window?.CloseForReal();
            state.Papers.Remove(paper);
            (state.PaperSkin, state.EnableAnimations, state.UseCapsuleMode,
                state.UseDeepCapsuleMode) = saved;
            nativeMode.SetValue(controller, wasNative);
            Theme.Invalidate();
        }
    }

    private static void CheckNativePreparation(string material)
    {
        var surface = new SkinBorder { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        var window = new Window
        {
            Content = surface, Width = 320, Height = 240, Left = 20, Top = 20,
            WindowStyle = WindowStyle.None, AllowsTransparency = false, ShowInTaskbar = false
        };
        var api = new NativeProbe();
        var eligible = true;
        using var backdrop = new NativeMicaBackdrop(window, () => surface, () => eligible,
            brush => surface.Background = brush, api);
        try
        {
            backdrop.Refresh(true, false, material);
            window.Show();
            Until(() => backdrop.IsActive && surface.ActualWidth > 0, "fixture has a native recipe");
            Program.Pump();
            var handle = new WindowInteropHelper(window).Handle;
            var started = 0;
            surface.SetFormTransition(true);
            backdrop.PrepareFormTransition();
            eligible = false;
            backdrop.Refresh(true, false, material, force: true);
            backdrop.StartFormAnimation(() => started++);
            Program.Assert(started == 0 && backdrop.IsActive &&
                surface.Background is SolidColorBrush { Color.A: 255 },
                "prepare replacement without first removing the installed background");
            Until(() => started == 1, "source drawing releases one animation start");
            Program.Assert(!backdrop.IsActive && api.Alpha && api.Backdrop == DwmMicaApi.None,
                "animation starts on the normal solid/alpha fallback recipe");
            Program.Assert(new WindowInteropHelper(window).Handle == handle && ReferenceEquals(window.Content, surface),
                "staging keeps the window and existing surface");

            // Exercise cancellation after publication is queued, without pretending
            // this manually delivered paint notification is a desktop pixel test.
            Restore();
            surface.SetFormTransition(true);
            backdrop.PrepareFormTransition();
            eligible = false;
            backdrop.StartFormAnimation(() => started++);
            surface.FormFrameRendered?.Invoke();
            backdrop.CancelFormTransitionPreparation();
            backdrop.Refresh(true, false, material);
            Program.Pump();
            Program.Assert(started == 1, "queued old preparation cannot restart a cancelled form");

            Restore();
            surface.SetFormTransition(true);
            backdrop.PrepareFormTransition();
            var stale = surface.FormFrameRendered;
            backdrop.StartFormAnimation(() => started += 100);
            backdrop.PrepareFormTransition();
            eligible = false;
            backdrop.StartFormAnimation(() => started++);
            stale?.Invoke();
            Until(() => started == 2, "replacement preparation starts only the successor");

            Restore();
            surface.SetFormTransition(true);
            backdrop.PrepareFormTransition();
            backdrop.StartFormAnimation(() => started += 100);
            surface.FormFrameRendered?.Invoke();
            backdrop.Dispose();
            window.Close();
            Program.Pump();
            Program.Assert(started == 2, "closing releases the queued paint handoff and callback");

            void Restore()
            {
                surface.SetFormTransition(false);
                eligible = true;
                backdrop.Refresh(true, false, material, force: true);
                Program.Pump();
            }
        }
        finally { window.Close(); }
    }

    private static void Until(Func<bool> predicate, string description)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate() && timeout.ElapsedMilliseconds < 3000)
        {
            Program.Pump();
            Thread.Sleep(2);
        }
        Program.Assert(predicate(), description);
    }

    private sealed class NativeProbe : INativeMicaApi
    {
        public bool IsSupported => true;
        public bool CompositionEnabled => true;
        public bool TransparencyEnabled => true;
        public bool HighContrast => false;
        public bool IsLayered(IntPtr hwnd) => false;
        internal bool Alpha;
        internal int Backdrop;
        public int ExtendFrame(IntPtr hwnd, int top) => 0;
        public int SetDarkMode(IntPtr hwnd, bool dark) => 0;
        public int SetBackdrop(IntPtr hwnd, int backdrop) { Backdrop = backdrop; return 0; }
        public int SetClearAcrylic(IntPtr hwnd, bool enabled, bool dark) => 0;
        public int EnableAlpha(IntPtr hwnd) { Alpha = true; return 0; }
        public int DisableAlpha(IntPtr hwnd) { Alpha = false; return 0; }
        public int SetRedirectionAlpha(IntPtr hwnd, bool enabled) => 0;
        public void InvalidateContent(IntPtr hwnd) { }
        public int ConfigureFrame(IntPtr hwnd, bool rounded, int borderColor, int captionColor) => 0;
        public void SetNonClientActive(IntPtr hwnd, bool active) { }
    }

    private sealed class ReliefSurface(MaterialTransitionRelief relief) : FrameworkElement
    {
        internal CornerRadius Corners;
        protected override void OnRender(DrawingContext dc) => relief.Draw(dc, RenderSize, Corners);
    }

    private static Pixels Render(FrameworkElement element, Size size, double density)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        element.InvalidateVisual();
        element.UpdateLayout();
        var width = (int)Math.Ceiling(size.Width * density);
        var height = (int)Math.Ceiling(size.Height * density);
        var bitmap = new RenderTargetBitmap(width, height, 96 * density, 96 * density, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var bytes = new byte[width * height * 4];
        bitmap.CopyPixels(bytes, width * 4, 0);
        return new Pixels(bytes, width, height, density);
    }

    private sealed record Pixels(byte[] Bytes, int Width, int Height, double Density)
    {
        internal Color At(double x, double y)
        {
            var px = Math.Clamp((int)Math.Floor(x * Density), 0, Width - 1);
            var py = Math.Clamp((int)Math.Floor(y * Density), 0, Height - 1);
            var i = (py * Width + px) * 4;
            return Color.FromArgb(Bytes[i + 3], Bytes[i + 2], Bytes[i + 1], Bytes[i]);
        }
        internal long AlphaIn(Rect rect)
        {
            long sum = 0;
            for (var y = rect.Top; y < rect.Bottom; y++)
            for (var x = rect.Left; x < rect.Right; x++) sum += At(x, y).A;
            return sum;
        }
    }
}
