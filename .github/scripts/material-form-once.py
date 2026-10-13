from pathlib import Path
import subprocess

BASE = '0e37677489fdac59939b9abdc5a60112d93a3534'
allowed_setup = {'.github/scripts/material-form-once.py', '.github/workflows/material-form-once.yml'}
existing = set(subprocess.check_output(['git', 'diff', '--name-only', BASE, 'HEAD'], text=True).splitlines())
if not existing <= allowed_setup:
    raise RuntimeError(f'Unexpected changes since reviewed base: {sorted(existing - allowed_setup)}')
changes = {}
def edit(path, old, new):
    text = changes.get(path)
    if text is None:
        text = Path(path).read_text(encoding='utf-8-sig')
    if text.count(old) != 1:
        raise RuntimeError(f'{path}: expected exactly one edit anchor, got {text.count(old)}: {old[:120]!r}')
    changes[path] = text.replace(old, new, 1)
def add(path, text):
    if Path(path).exists():
        raise RuntimeError(f'{path}: new file already exists')
    changes[path] = text

add('src/PaperFormContentHost.cs', '''using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

// A form transition changes the viewport, not the editor's layout slot. Giving a
// full-size child a shrinking Grid slot creates an implicit WPF layout clip BEFORE
// RenderTransform. Arrange the existing child at its fixed source size instead.
internal sealed class PaperFormContentHost : Decorator
{
    private Size? _expandedSize;
    internal Size? ExpandedSize
    {
        get => _expandedSize;
        set
        {
            if (_expandedSize == value) return;
            _expandedSize = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (_expandedSize is not { } sourceSize) return base.MeasureOverride(constraint);
        Child?.Measure(sourceSize);
        return new Size(Math.Min(constraint.Width, sourceSize.Width),
            Math.Min(constraint.Height, sourceSize.Height));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (_expandedSize is not { } sourceSize) return base.ArrangeOverride(arrangeSize);
        Child?.Arrange(new Rect(sourceSize));
        return arrangeSize;
    }
}
''')

add('src/PaperChromeBorder.MaterialShadow.cs', '''using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal partial class PaperChromeBorder
{
    private static readonly Pen[] MaterialShadowPens = CreateMaterialShadowPens();
    private readonly ScaleTransform _materialShadowScale = new();
    private double _materialShadowMargin;

    internal void SetMaterialShadowMargin(double margin)
    {
        margin = Math.Max(0, margin);
        if (_materialShadowMargin == margin) return;
        _materialShadowMargin = margin;
        InvalidateVisual();
    }

    protected void DrawMaterialShadow(DrawingContext dc)
    {
        if (_materialShadowMargin <= 0 || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Leave one physical pixel for antialiasing/layout rounding. In particular,
        // the zero-gutter native start frame must not grow a clipped capsule shadow.
        var guard = 1 / Math.Min(dpi.DpiScaleX, dpi.DpiScaleY);
        var extent = Math.Min(7, Math.Max(0, _materialShadowMargin - guard));
        if (extent <= 0) return;
        var scale = extent / 7;
        _materialShadowScale.ScaleX = _materialShadowScale.ScaleY = scale;
        dc.PushTransform(_materialShadowScale);
        var radius = CornerRadius.TopLeft / scale;
        for (var i = 0; i < MaterialShadowPens.Length; i++)
        {
            var expansion = (i + .5) * 1.4;
            var rect = new Rect(0, 0, RenderSize.Width / scale, RenderSize.Height / scale);
            rect.Inflate(expansion, expansion);
            dc.DrawRoundedRectangle(null, MaterialShadowPens[i], rect,
                radius + expansion, radius + expansion);
        }
        dc.Pop();
    }

    private static Pen[] CreateMaterialShadowPens()
    {
        var alpha = new byte[] { 27, 17, 9, 4, 1 };
        var pens = new Pen[alpha.Length];
        for (var i = 0; i < alpha.Length; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha[i], 0, 0, 0));
            brush.Freeze();
            pens[i] = new Pen(brush, 1.4);
            pens[i].Freeze();
        }
        return pens;
    }
}
''')

add('src/MaterialTransitionRelief.cs', '''using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

// Eight reusable lighting tiles for the short paper form morph. Corners follow the
// current rim band; straight sides stretch along their length only. The full exact
// relief is restored at the endpoint. No capture, worker, timer or persistent pool.
internal sealed class MaterialTransitionRelief
{
    private readonly DrawingBrush[] _tiles;

    internal MaterialTransitionRelief(Drawing drawing, Size size, CornerRadius corners)
    {
        var band = Band(size, corners);
        var rects = Tiles(size, band);
        _tiles = new DrawingBrush[rects.Length];
        for (var i = 0; i < rects.Length; i++)
        {
            _tiles[i] = new DrawingBrush(drawing)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = rects[i],
                Stretch = Stretch.Fill
            };
            _tiles[i].Freeze();
        }
    }

    internal void Draw(DrawingContext dc, Size size, CornerRadius corners)
    {
        var band = Band(size, corners);
        var w = size.Width; var h = size.Height;
        Draw(0, 0, 0, band, band);
        Draw(1, band, 0, w - 2 * band, band);
        Draw(2, w - band, 0, band, band);
        Draw(3, 0, band, band, h - 2 * band);
        Draw(4, w - band, band, band, h - 2 * band);
        Draw(5, 0, h - band, band, band);
        Draw(6, band, h - band, w - 2 * band, band);
        Draw(7, w - band, h - band, band, band);
        void Draw(int index, double x, double y, double width, double height)
        {
            if (width > 0 && height > 0)
                dc.DrawRectangle(_tiles[index], null, new Rect(x, y, width, height));
        }
    }

    private static double Band(Size size, CornerRadius corners) => Math.Min(
        Math.Max(4, Math.Max(Math.Max(corners.TopLeft, corners.TopRight),
            Math.Max(corners.BottomLeft, corners.BottomRight))) + 1,
        Math.Min(size.Width, size.Height) / 2);

    private static Rect[] Tiles(Size size, double b)
    {
        // A resting pill can have no straight vertical segment. Sample its rim
        // center, not an empty Viewbox, so expansion still has side illumination.
        var x = Math.Min(b, Math.Max(0, (size.Width - 1) / 2));
        var y = Math.Min(b, Math.Max(0, (size.Height - 1) / 2));
        var width = Math.Max(1, size.Width - 2 * b);
        var height = Math.Max(1, size.Height - 2 * b);
        return
        [
            new(0, 0, b, b), new(x, 0, width, b),
            new(size.Width - b, 0, b, b),
            new(0, y, b, height), new(size.Width - b, y, b, height),
            new(0, size.Height - b, b, b),
            new(x, size.Height - b, width, b),
            new(size.Width - b, size.Height - b, b, b)
        ];
    }
}
''')

edit('src/PaperChromeBorder.cs', 'internal class PaperChromeBorder : Border',
     'internal partial class PaperChromeBorder : Border')
edit('src/PaperChromeBorder.cs', '''    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_surface.Visibility''', '''    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawMaterialShadow(drawingContext);
        if (_surface.Visibility''')

edit('src/SkinBorder.cs', '    private int _surfaceVersion;', '''    private int _surfaceVersion;
    internal bool IsFormTransition { get; private set; }
    internal Action? FormFrameRendered { get; set; }
    private MaterialTransitionRelief? _formRelief;

    internal void SetFormTransition(bool active)
    {
        if (IsFormTransition == active) return;
        IsFormTransition = active;
        _formRelief = null;
        _brushKey = null;
        _surfaceVersion++;
        if (!active) FormFrameRendered = null;
        InvalidateVisual();
    }
''')
edit('src/SkinBorder.cs', '''            _appearance = appearance;
            // Cache keys''', '''            _appearance = appearance;
            _formRelief = null;
            // Cache keys''')
edit('src/SkinBorder.cs', '''    protected override void OnRender(DrawingContext dc)
    {
        PresentPreparedMenuBackground();''', '''    protected override void OnRender(DrawingContext dc)
    {
        RenderSurface(dc);
        // Notify only after THIS surface recorded an opaque fallback drawing. A
        // generic Rendering event can belong to another HWND. The adapter queues
        // its native handoff after this WPF render operation, never inside paint.
        if (IsFormTransition && ActualWidth > 0 && ActualHeight > 0 &&
            Background is SolidColorBrush { Color.A: 255 } && FormFrameRendered is { } ready)
        {
            FormFrameRendered = null;
            ready();
        }
    }

    private void RenderSurface(DrawingContext dc)
    {
        PresentPreparedMenuBackground();''')
edit('src/SkinBorder.cs', '''        if (!decorated && !(systemMaterial && IsAuxiliary))
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            EnsureGeometry();''', '''        if (!decorated && !(systemMaterial && IsAuxiliary))
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            DrawMaterialShadow(dc);
            EnsureGeometry();''')
edit('src/SkinBorder.cs', '''        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        EnsureGeometry();
        var background =''', '''        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        DrawMaterialShadow(dc);
        EnsureGeometry();
        var background =''')
edit('src/SkinBorder.cs', '''        PaintMaterialBase(dc);
        dc.PushOpacity(MaterialStrength);''', '''        PaintMaterialBase(dc);
        dc.PushOpacity(IsFormTransition ? 1 : MaterialStrength);''')
edit('src/SkinBorder.cs', '''            if (PaperBackingOpacity > 0)
''', '''            if (!IsFormTransition && PaperBackingOpacity > 0)
''')
edit('src/SkinBorder.Materials.cs', '''        var opaque = !PaperSkins.UsesNativeBackdrop(Skin) || background.A == 255;''',
     '''        var opaque = IsFormTransition || !PaperSkins.UsesNativeBackdrop(Skin) || background.A == 255;''')
edit('src/SkinBorder.Materials.cs', '''            var dpi = VisualTreeHelper.GetDpi(this);
            var key = (RenderSize, CornerRadius, BorderThickness, dpi.DpiScaleX, dpi.DpiScaleY, Skin, _dark);''', '''            var dpi = VisualTreeHelper.GetDpi(this);
            if (IsFormTransition)
            {
                _formRelief ??= new MaterialTransitionRelief(
                    MaterialRelief.Create(RenderSize, CornerRadius, BorderThickness, dpi, _dark),
                    RenderSize, CornerRadius);
                _formRelief.Draw(dc, RenderSize, CornerRadius);
                return;
            }
            var key = (RenderSize, CornerRadius, BorderThickness, dpi.DpiScaleX, dpi.DpiScaleY, Skin, _dark);''')

edit('src/NativeMicaBackdrop.cs', '''    private bool _contentRendered;
''', '''    private bool _contentRendered;
    private FormSurfacePreparation? _formPreparation;
    private sealed class FormSurfacePreparation(SkinBorder surface)
    {
        internal SkinBorder Surface { get; } = surface;
        internal Action? Rendered;
        internal Action? Start;
        internal DispatcherOperation? Commit;
    }

    internal void PrepareFormTransition()
    {
        CancelFormTransitionPreparation();
        if (_disposed || !IsActive || !_window.IsVisible ||
            _source is not { IsDisposed: false } || _getChrome() is not SkinBorder surface)
            return;
        var preparation = new FormSurfacePreparation(surface);
        _formPreparation = preparation;
        preparation.Rendered = () =>
        {
            if (!ReferenceEquals(_formPreparation, preparation) || _window.Dispatcher.HasShutdownStarted) return;
            preparation.Commit = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_disposed || !ReferenceEquals(_formPreparation, preparation)) return;
                _formPreparation = null;
                // This runs after the source's WPF render pass has unwound. It is
                // a submission boundary, NOT a claim about physical scanout.
                Refresh(_requested, _dark, force: true);
                preparation.Start?.Invoke();
            }));
        };
        surface.FormFrameRendered = preparation.Rendered;
        _setSurface(Theme.PaperBrush);
        surface.InvalidateVisual();
    }

    internal void StartFormAnimation(Action start)
    {
        if (_formPreparation is { } preparation) preparation.Start = start;
        else start();
    }

    internal void CancelFormTransitionPreparation()
    {
        var preparation = _formPreparation;
        _formPreparation = null;
        if (preparation == null) return;
        preparation.Commit?.Abort();
        if (ReferenceEquals(preparation.Surface.FormFrameRendered, preparation.Rendered))
            preparation.Surface.FormFrameRendered = null;
    }
''')
edit('src/NativeMicaBackdrop.cs', '''        if (_source?.CompositionTarget == null || chrome == null) return;

        // A fresh HWND''', '''        if (_source?.CompositionTarget == null || chrome == null) return;
        if (_formPreparation != null)
        {
            // Form setup can refresh paint, opacity and frame policy several times.
            // Keep one installed recipe until the replacement drawing is submitted.
            _setSurface(Theme.PaperBrush);
            return;
        }

        // A fresh HWND''')
edit('src/NativeMicaBackdrop.cs', '''        _disposed = true;
        _window.SourceInitialized''', '''        _disposed = true;
        CancelFormTransitionPreparation();
        _window.SourceInitialized''')

edit('src/PaperWindow.Lifecycle.cs', '''    private void BeginPaperFormTransition(bool collapsed)
    {
        _presentationState''', '''    private void BeginPaperFormTransition(bool collapsed, bool animate)
    {
        if (_paperChrome is SkinBorder surface) surface.SetFormTransition(true);
        if (animate) _nativeMica?.PrepareFormTransition();
        else _nativeMica?.CancelFormTransitionPreparation();
        _presentationState''')
edit('src/PaperWindow.Lifecycle.cs', '''        if (_paperChrome is SkinBorder skin) skin.SetSampledBackgroundCaptureSuspended(true);
        RefreshNativeMica();
    }

    private void CompletePaperFormTransition''', '''        if (_paperChrome is SkinBorder skin) skin.SetSampledBackgroundCaptureSuspended(true);
        // Native refresh belongs after target/frame preparation in SetCollapsedStateCore.
    }

    private void CompletePaperFormTransition''')
edit('src/PaperWindow.Lifecycle.cs', '''        _presentationState = collapsed
            ? PaperPresentationState.Collapsed
            : PaperPresentationState.Expanded;

        RefreshNativeMica();''', '''        _nativeMica?.CancelFormTransitionPreparation();
        if (_paperChrome is SkinBorder surface) surface.SetFormTransition(false);
        _presentationState = collapsed
            ? PaperPresentationState.Collapsed
            : PaperPresentationState.Expanded;

        RefreshNativeMica();''')
edit('src/PaperWindow.Lifecycle.cs', '''        _windowLifecycle = PaperWindowLifecycleState.Closing;
        _presentationState''', '''        _nativeMica?.CancelFormTransitionPreparation();
        if (_paperChrome is SkinBorder surface) surface.SetFormTransition(false);
        _windowLifecycle = PaperWindowLifecycleState.Closing;
        _presentationState''')

edit('src/PaperWindow.cs', '''    private readonly Grid _containerGrid = new();''', '''    private readonly Grid _containerGrid = new();
    private readonly PaperFormContentHost _formContentHost = new();''')
edit('src/PaperWindow.cs', '''        _paperChrome.Child = _containerGrid;''', '''        _formContentHost.Child = _containerGrid;
        _paperChrome.Child = _formContentHost;''')
edit('src/PaperWindow.cs', '''            _paperChrome.Effect = null;
            _paperChrome.BeginAnimation(Border.MarginProperty, null);''', '''            _paperChrome.Effect = null;
            _paperChrome.SetMaterialShadowMargin(0);
            _paperChrome.BeginAnimation(Border.MarginProperty, null);''')
edit('src/PaperWindow.cs', '''        _paperChrome.Effect = UsesNativePaperChrome ? null : isCapsule
            ? CreatePaperChromeShadow(blurRadius: 8, opacity: 0.12, shadowDepth: 1)
            : CreatePaperChromeShadow();''', '''        var materialShadow = Theme.UsesNativeBackdrop && (IsPaperFormTransitioning || isCapsule);
        _paperChrome.Effect = UsesNativePaperChrome || materialShadow ? null : isCapsule
            ? CreatePaperChromeShadow(blurRadius: 8, opacity: 0.12, shadowDepth: 1)
            : CreatePaperChromeShadow();
        _paperChrome.SetMaterialShadowMargin(materialShadow ? _paperChrome.Margin.Left : 0);''')
edit('src/PaperWindow.cs', '''        var borderX = nativeWindow ? _paperChrome.BorderThickness.Left + _paperChrome.BorderThickness.Right : 0;
        var borderY = nativeWindow ? _paperChrome.BorderThickness.Top + _paperChrome.BorderThickness.Bottom : 0;''', '''        var borderX = _paperChrome.BorderThickness.Left + _paperChrome.BorderThickness.Right;
        var borderY = _paperChrome.BorderThickness.Top + _paperChrome.BorderThickness.Bottom;
        if (Theme.UsesNativeBackdrop) _paperChrome.SetMaterialShadowMargin(margin);''')
edit('src/PaperWindow.cs', '''    private void ResetTransitionVisuals()
    {
        _paperChrome.Width''', '''    private void ResetTransitionVisuals()
    {
        _formContentHost.ExpandedSize = null;
        _paperChrome.Width''')

edit('src/PaperWindow.Capsule.cs', '        BeginPaperFormTransition(collapsed);',
     '        BeginPaperFormTransition(collapsed, animate);')
edit('src/PaperWindow.Capsule.cs', '''            ResizeMode = ResizeMode.NoResize;
            RefreshNativeMica(force: true);''', '''            ResizeMode = ResizeMode.NoResize;
            RefreshNativeMica();''')
edit('src/PaperWindow.Capsule.cs', '''            _shell.Width = Math.Max(0, expandedWidth - (_controller.UsesNativeMicaWindows
        ? _paperChrome.BorderThickness.Left + _paperChrome.BorderThickness.Right : WindowChromeInset));
            _shell.Height = Math.Max(0, expandedHeight - (_controller.UsesNativeMicaWindows
        ? _paperChrome.BorderThickness.Top + _paperChrome.BorderThickness.Bottom : WindowChromeInset));''', '''            var expandedInset = _controller.UsesNativeMicaWindows ? 0 : WindowChromeInset;
            _shell.Width = Math.Max(0, expandedWidth - expandedInset -
                _paperChrome.BorderThickness.Left - _paperChrome.BorderThickness.Right);
            _shell.Height = Math.Max(0, expandedHeight - expandedInset -
                _paperChrome.BorderThickness.Top - _paperChrome.BorderThickness.Bottom);
            _formContentHost.ExpandedSize = new Size(_shell.Width, _shell.Height);''')
edit('src/PaperWindow.Capsule.cs', '''            if (collapsed)
            {
                _shell.Opacity = 1.0;
                _capsuleShell.Opacity = 0.0;

                var fadeOutShell''', '''            System.Windows.Media.Animation.DoubleAnimation shellAnimation;
            System.Windows.Media.Animation.DoubleAnimation capsuleAnimation;
            if (collapsed)
            {
                _shell.Opacity = 1.0;
                _capsuleShell.Opacity = 0.0;

                var fadeOutShell''')
edit('src/PaperWindow.Capsule.cs', '                _shell.BeginAnimation(UIElement.OpacityProperty, fadeOutShell);',
     '                shellAnimation = fadeOutShell;')
edit('src/PaperWindow.Capsule.cs', '                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, fadeInCapsule);',
     '                capsuleAnimation = fadeInCapsule;')
edit('src/PaperWindow.Capsule.cs', '                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, fadeOutCapsule);',
     '                capsuleAnimation = fadeOutCapsule;')
edit('src/PaperWindow.Capsule.cs', '                _shell.BeginAnimation(UIElement.OpacityProperty, fadeInShell);',
     '                shellAnimation = fadeInShell;')
edit('src/PaperWindow.Capsule.cs', '''            BeginAnimation(TransitionProgressProperty, progressAnim);
        }
        else''', '''            void StartAnimation()
            {
                if (transitionGeneration != _collapseTransitionGeneration ||
                    _windowLifecycle != PaperWindowLifecycleState.Alive || !IsPaperFormTransitioning)
                    return;
                _shell.BeginAnimation(UIElement.OpacityProperty, shellAnimation);
                _capsuleShell.BeginAnimation(UIElement.OpacityProperty, capsuleAnimation);
                BeginAnimation(TransitionProgressProperty, progressAnim);
            }
            if (_nativeMica != null) _nativeMica.StartFormAnimation(StartAnimation);
            else StartAnimation();
        }
        else''')
edit('src/SkinBorder.BackgroundSession.cs', '            dc.PushOpacity(_owner.MaterialStrength);',
     '            dc.PushOpacity(_owner.IsFormTransition ? 1 : _owner.MaterialStrength);')

add('tests/PaperTodo.MicaChecks/FormTransitionRenderingChecks.cs', '''using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            foreach (var material in new[] { MicaBackdropTypes.Acrylic,
                MicaBackdropTypes.ClearAcrylic, NativeMicaBackdrop.AeroGlassMaterial })
                CheckNativePreparation(material);
        }
        finally
        {
            (state.PaperSkin, state.Theme, state.MatchAuxiliaryMaterialStrength, state.HideSurfaceOutline) = saved;
            Theme.Invalidate();
        }
        Console.WriteLine("FORM RENDERING: scaled far-edge pixels, gutter-bounded shadow, " +
            "reusable rim tiles and staged/cancelled native fallback passed.");
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
''')
edit('tests/PaperTodo.MicaChecks/Program.cs',
     '                Check("paper form animations defer material capture until the endpoint", () => PaperFormMaterialChecks.Run(controller));',
     '                Check("paper form rendering and native fallback publication", () => FormTransitionRenderingChecks.Run(controller));\n                Check("paper form animations defer material capture until the endpoint", () => PaperFormMaterialChecks.Run(controller));')
edit('tests/PaperTodo.MicaChecks/PaperFormMaterialChecks.cs',
     '            !_fixture.Surface.HasBackgroundCapture &&\n            _fixture.Surface.BackgroundFrameCount == _initialFrames &&',
     '            !_fixture.Surface.HasBackgroundCapture &&\n            _fixture.Surface.Effect == null &&\n            _fixture.Surface.BackgroundFrameCount == _initialFrames &&')
edit('CHANGELOG.md',
     'Paper collapse/expand commits both size axes together and defers auxiliary snapshots until the final capsule layout, reducing material-transition stutter.',
     'Paper collapse/expand stages an opaque replacement before removing the native backdrop and starts its animation clocks together, avoiding the extra transparent startup flash. The existing content keeps its full layout while scaling, shadows fit the current gutter without full-content blur, and Aero reuses its rim-lighting tiles during the morph. Both size axes still commit together, and auxiliary snapshots wait for the final capsule layout.')
edit('CHANGELOG.zh.md',
     '纸片收起／展开时同步提交宽高，完整辅助材质等胶囊最终布局稳定后再采样，减少材质收展卡顿。',
     '纸片收起／展开时先绘制实色替代底，再撤下原生材质并同步启动动画，避免开头额外闪一下透明。正文按完整布局缩放，阴影限制在当前留白内且不再对整块内容做模糊，Aero 在过渡中复用边缘光照素材。仍同步提交宽高，完整辅助材质等胶囊最终布局稳定后再采样。')
edit('doc/ARCHITECTURE.md',
     '每帧先更新内层纸面，再一次提交 HWND 的宽高，不通过两次 WPF 尺寸属性写入制造中间矩形。',
     '每帧先更新内层纸面，再一次提交 HWND 的宽高，不通过两次 WPF 尺寸属性写入制造中间矩形。`PaperFormContentHost` 在形态过渡中按展开尺寸测量和排列现有正文，避免缩小的布局槽在 RenderTransform 之前产生隐式裁切；终点恢复普通伸展，不重建或迁移正文。原生材质退出时先在现有 `SkinBorder` 记录不透明替代绘制，再于该 WPF 渲染操作之后执行一次原生回退并同时启动各动画时钟；准备、取消与关闭由原形态生命周期收束，不新增补帧定时器，也不把此提交边界称为物理显示确认。材质形态动画和普通材质胶囊使用受当前留白限制的缓存软边绘制，避免整块内容阴影；Aero 过渡只复用现有光照绘制的八块素材，终点恢复完整光照，不截图或建立长期素材池。')

for path, text in changes.items():
    dest = Path(path)
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(text, encoding='utf-8', newline='\n')
print('Applied source changes:')
print('\n'.join(changes))
subprocess.run(['git', 'diff', '--check'], check=True)
subprocess.run(['git', 'add', '--', *changes.keys()], check=True)
Path('.github/scripts/material-form-once.py').unlink()
subprocess.run(['git', 'add', '--', '.github/scripts/material-form-once.py'], check=True)
