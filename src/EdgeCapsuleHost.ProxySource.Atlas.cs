using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    internal sealed partial class ProxySource
    {
        private const int Gutter = 2;
        private const long MaximumAtlasPixels = 4 * 1024 * 1024;
        private int _atlasWidth, _atlasHeight, _compactTop, _previewTop, _closeTop;
        private int _compactHeight, _shellWidth, _shellHeight;
        private FrameworkElement? _compactVisual, _previewVisual;
        private Rectangle? _compactImage, _previewImage, _closeImage;
        private readonly List<VisualBrush> _brushes = new();
        private EdgeCapsuleCompositionStyle? _style;
        private EdgeCapsuleCompositionShellPlane? _chromePlane, _outlinePlane, _contentBackgroundPlane, _closeBackgroundPlane;
        private Border? _contentBackground, _closeBackground;
        private Size _previewSize, _previewLayoutSize;
        private bool _previewSlotChanged;
        internal bool AtlasDeferred { get; private set; }
        internal FrameworkElement? PreviewVisual => _previewVisual;
        internal Size PreviewSize => _previewLayoutSize;
        private bool PreviewBindingIsCurrent => _owner._previewContent == null ||
            (ReferenceEquals(_previewVisual, _owner._previewContent) && _previewVisual!.RenderSize == _previewSize &&
             _owner._previewContentLayer is { } layer && layer.Width == _previewLayoutSize.Width &&
             layer.Height == _previewLayoutSize.Height);

        private bool PrepareAtlas()
        {
            if (!TryCaptureStyle(out _style)) return false;
            _compactVisual = _owner._pluginContentLayer?.Child as FrameworkElement ?? _owner.ContentGrid;
            if (!UsableVisual(_compactVisual) || ContainsNativeChild(_compactVisual)) return false;
            var chromeInsets = Insets(_style!.ChromeMargin, _style.ChromeCorners, Dpi);
            var outlineInsets = Insets(_style.OutlineMargin, _style.OutlineCorners, Dpi);
            _shellWidth = Math.Max(DeviceCeiling(64, Dpi.DpiScaleX),
                Math.Max(chromeInsets.Left + chromeInsets.Right, outlineInsets.Left + outlineInsets.Right) + 1);
            _shellHeight = Math.Max(DeviceCeiling(_owner._options.BodyHeight + _owner._options.WindowChromeMargin * 2, Dpi.DpiScaleY),
                Math.Max(chromeInsets.Top + chromeInsets.Bottom, outlineInsets.Top + outlineInsets.Bottom) + 1);
            _compactHeight = DeviceCeiling(_owner._options.BodyHeight + _owner._options.WindowChromeMargin * 2, Dpi.DpiScaleY);
            _atlasWidth = Math.Max(_capacity.Width, _shellWidth) + Gutter * 2;
            _compactTop = Gutter;
            _previewTop = _compactTop + _compactHeight + Gutter;
            _closeTop = _previewTop + _capacity.Height + Gutter;
            var shellTop = _closeTop + _compactHeight + Gutter;
            _atlasHeight = shellTop + (_shellHeight + Gutter) * 4;
            if (_atlasWidth <= 0 || _atlasHeight <= 0 || (long)_atlasWidth * _atlasHeight > MaximumAtlasPixels)
                return false;
            _canvas = new Canvas
            {
                Width = _atlasWidth / Dpi.DpiScaleX, Height = _atlasHeight / Dpi.DpiScaleY,
                IsHitTestVisible = false, ClipToBounds = true, UseLayoutRounding = true
            };
            _compactImage = AddImage(_compactVisual, _compactTop);
            if (!ReferenceEquals(_compactVisual, _owner.ContentGrid))
                BindingOperations.SetBinding(_compactImage, UIElement.OpacityProperty,
                    new Binding(nameof(UIElement.Opacity)) { Source = _owner._pluginContentLayer });
            _closeImage = AddImage(_owner.CloseGlyph, _closeTop);
            _chromePlane = AddShell(shellTop, _style.ChromeMargin, _style.ChromeCorners,
                _style.ChromeBorder, _style.PaperBrush, _style.PaperBorderBrush, chromeInsets, true, out _);
            shellTop += _shellHeight + Gutter;
            _outlinePlane = AddShell(shellTop, _style.OutlineMargin, _style.OutlineCorners,
                _style.OutlineBorder, Brushes.Transparent, _style.OutlineBrush, outlineInsets, false, out _);
            shellTop += _shellHeight + Gutter;
            _contentBackgroundPlane = AddShell(shellTop, new Thickness(), new CornerRadius(),
                new Thickness(), _owner.ContentArea.Background, Brushes.Transparent,
                default, false, out _contentBackground);
            shellTop += _shellHeight + Gutter;
            _closeBackgroundPlane = AddShell(shellTop, new Thickness(), new CornerRadius(),
                new Thickness(), _owner.CloseArea.Background, Brushes.Transparent,
                default, false, out _closeBackground);
            BindingOperations.SetBinding(_contentBackground, Border.BackgroundProperty,
                new Binding(nameof(Border.Background)) { Source = _owner.ContentArea });
            BindingOperations.SetBinding(_closeBackground, Border.BackgroundProperty,
                new Binding(nameof(Border.Background)) { Source = _owner.CloseArea });
            return TryFillPreview();
        }

        private bool TryFillPreview()
        {
            var content = _owner._previewContent;
            if (content == null) return true;
            if (!content.IsMeasureValid || !content.IsArrangeValid ||
                _owner._previewContentLayer is not { IsMeasureValid: true, IsArrangeValid: true } layer)
            { AtlasDeferred = true; return false; }
            var replacing = _previewVisual != null;
            if (replacing && PreviewBindingIsCurrent)
                return true;
            if (replacing && !CanReplacePreview()) return false;
            // The slot is fixed before any preview exists. Bind only the real final-size visual,
            // below the changing viewport and content-layer parents; its own readiness alpha stays.
            if (!UsableVisual(content)) return false;
            if (ContainsNativeChild(content) || content.RenderSize.Width > layer.Width + .51 ||
                content.RenderSize.Height > layer.Height + .51) return false;
            if (DeviceCeiling(content.RenderSize.Width, Dpi.DpiScaleX) > _capacity.Width ||
                DeviceCeiling(content.RenderSize.Height, Dpi.DpiScaleY) > _capacity.Height) return false;
            _previewVisual = content;
            _previewSize = content.RenderSize;
            _previewLayoutSize = new Size(layer.Width, layer.Height);
            if (_previewImage == null) _previewImage = AddImage(content, _previewTop);
            else ((VisualBrush)_previewImage.Fill).Visual = content;
            _previewSlotChanged = true;
            return true;
        }

        private bool SynchronizeAtlas()
        {
            AtlasDeferred = false;
            if (!AtlasIsCompatible()) return false;
            if (HasPendingAtlasLayout())
            { AtlasDeferred = true; return false; }
            if (!TryFillPreview() || !UsableVisual(_compactVisual!)) return false;
            var compact = UpdateImage(_compactVisual!, _compactImage!, _compactTop, _compactHeight);
            var close = UsableVisual(_owner.CloseGlyph)
                ? UpdateImage(_owner.CloseGlyph, _closeImage!, _closeTop, _compactHeight) : Description?.Close;
            var preview = _previewVisual == null ? null : _owner._previewContent == null
                ? Description?.Preview
                : UpdateImage(_previewVisual, _previewImage!, _previewTop, _capacity.Height);
            if (compact == null || (_previewVisual != null && preview == null)) return false;
            if (!UpdateBackground(_contentBackground!, _owner.ContentArea) ||
                !UpdateBackground(_closeBackground!, _owner.CloseArea)) return false;
            // Description is a canonical mapping for the compositor's existing transition, not a
            // second applied frame stream. Brush-local viewboxes can follow WPF layout every Apply
            // without requiring a native rebind/Commit. Only filling a new slot changes Revision.
            if (Description != null && !_previewSlotChanged &&
                (Description.Close != null || close == null)) return true;
            compact = compact with { SourceBounds = new DeviceScreenRect(Gutter, _compactTop,
                _atlasWidth - Gutter, _compactTop + _compactHeight) };
            var frame = _owner._appliedFrame;
            var next = new EdgeCapsuleProxySourceDescription(OwnerHandle, SourceHandle, Generation,
                Description?.Revision ?? 0, Dpi, SourceBounds, _edge, compact, preview, close,
                _chromePlane!, _outlinePlane!, _style!, frame,
                BodyHeightDevice(_owner._options.BodyHeight),
                _owner._previewContentLayer is { } layer && double.IsFinite(layer.Height)
                    ? BodyHeightDevice(layer.Height) : BodyHeightDevice(_owner._options.BodyHeight),
                _owner.ContentArea.CornerRadius, _contentBackgroundPlane!, _closeBackgroundPlane!,
                LocalBounds(_owner.ContentArea), LocalBounds(_owner.CloseArea),
                _owner.ContentArea.CornerRadius, _owner.CloseArea.CornerRadius);
            // A different live Visual can occupy exactly the same slot/size/offset. Value equality
            // of the descriptor cannot hide that content replacement from native consumers.
            if (_previewSlotChanged || Description != next)
            {
                Description = next with { Revision = next.Revision + 1 };
                _updatedPending = true;
                QueueNotification();
            }
            _previewSlotChanged = false;
            return true;
        }

        private bool HasPendingAtlasLayout()
        {
            // A child can retain its previous RenderSize while an ancestor is waiting to arrange
            // its new offset. Never turn that temporarily stale crop into source invalidation.
            static bool Pending(FrameworkElement? visual) => visual != null &&
                visual.Visibility != Visibility.Collapsed && (!visual.IsMeasureValid || !visual.IsArrangeValid);
            return Pending(_owner.Root) || Pending(_owner.VisualSurface) ||
                Pending(_owner.ContentArea) || Pending(_owner.ContentHost) || Pending(_owner.CloseArea) ||
                Pending(_compactVisual) || Pending(_owner.CloseGlyph) ||
                (_owner._pluginContentLayer?.Child != null && Pending(_owner._pluginContentLayer)) ||
                (_owner._previewContent != null &&
                 (Pending(_owner._previewViewportLayer) || Pending(_owner._previewContentLayer) ||
                  Pending(_owner._previewContent)));
        }

        private bool AtlasIsCompatible()
        {
            if (_style == null || !StyleMatches() || !ReferenceEquals(_compactVisual,
                    _owner._pluginContentLayer?.Child as FrameworkElement ?? _owner.ContentGrid)) return false;
            // A detached preview is deliberately retained by its brush. A new visual or size is
            // admitted in TryFillPreview, after all leases prove that this native plane is hidden.
            return true;
        }

        private Rectangle AddImage(FrameworkElement visual, int top)
        {
            var brush = new VisualBrush(visual)
            {
                AutoLayoutContent = false, Stretch = Stretch.None, TileMode = TileMode.None,
                ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
                AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
            };
            _brushes.Add(brush);
            var image = new Rectangle { Fill = brush, IsHitTestVisible = false, SnapsToDevicePixels = true };
            Canvas.SetLeft(image, Gutter / Dpi.DpiScaleX);
            Canvas.SetTop(image, top / Dpi.DpiScaleY);
            _canvas!.Children.Add(image);
            return image;
        }

        private EdgeCapsuleCompositionPlane? UpdateImage(FrameworkElement visual, Rectangle image, int top, int capacityHeight)
        {
            if (!UsableVisual(visual) || !TryOuterBounds(visual, out var outer)) return null;
            var size = visual.RenderSize;
            var width = DeviceCeiling(size.Width, Dpi.DpiScaleX);
            var height = DeviceCeiling(size.Height, Dpi.DpiScaleY);
            if (width > _atlasWidth - Gutter * 2 || height > capacityHeight) return null;
            var brush = (VisualBrush)image.Fill;
            // VisualBrush includes its root's transform/offset (WPF GetContentBounds uses outer
            // space). Crop that exact root rectangle, then draw at 1:1 in the fixed atlas slot.
            if (brush.Viewbox != outer) brush.Viewbox = outer;
            var viewport = new Rect(size);
            if (brush.Viewport != viewport) brush.Viewport = viewport;
            if (image.Width != size.Width) image.Width = size.Width;
            if (image.Height != size.Height) image.Height = size.Height;
            var offset = visual.TransformToAncestor(_owner.Root).Transform(new Point());
            return new(new DeviceScreenRect(Gutter, top, Gutter + width, top + height),
                new DeviceScreenPoint(offset.X * Dpi.DpiScaleX, offset.Y * Dpi.DpiScaleY), size);
        }

        private static bool TryOuterBounds(FrameworkElement visual, out Rect outer)
        {
            outer = Rect.Empty;
            if (VisualTreeHelper.GetParent(visual) is not Visual parent) return false;
            var transform = visual.TransformToAncestor(parent);
            var zero = transform.Transform(new Point());
            var x = transform.Transform(new Point(1, 0));
            var y = transform.Transform(new Point(0, 1));
            // A content-owned scale/rotation is not safe to silently flatten or resize as glyphs.
            if (Math.Abs(x.X - zero.X - 1) > .001 || Math.Abs(x.Y - zero.Y) > .001 ||
                Math.Abs(y.Y - zero.Y - 1) > .001 || Math.Abs(y.X - zero.X) > .001) return false;
            outer = new Rect(zero, visual.RenderSize);
            return true;
        }

        private DeviceScreenRect LocalBounds(FrameworkElement visual)
        {
            var rect = visual.TransformToAncestor(_owner.Root).TransformBounds(new Rect(visual.RenderSize));
            return new((int)Math.Round(rect.Left * Dpi.DpiScaleX), (int)Math.Round(rect.Top * Dpi.DpiScaleY),
                (int)Math.Round(rect.Right * Dpi.DpiScaleX), (int)Math.Round(rect.Bottom * Dpi.DpiScaleY));
        }

        private EdgeCapsuleCompositionShellPlane AddShell(int top, Thickness margin, CornerRadius corners,
            Thickness thickness, Brush? background, Brush borderBrush, EdgeCapsuleCompositionInsets insets,
            bool shadow, out Border border)
        {
            var size = new Size(_shellWidth / Dpi.DpiScaleX, _shellHeight / Dpi.DpiScaleY);
            var grid = new Grid { Width = size.Width, Height = size.Height, IsHitTestVisible = false };
            border = new Border
            {
                Margin = margin, CornerRadius = corners, BorderThickness = thickness,
                Background = background, BorderBrush = borderBrush, SnapsToDevicePixels = true,
                Effect = shadow ? new DropShadowEffect { BlurRadius = _style!.ShadowBlurRadius,
                    ShadowDepth = _style.ShadowDepth, Opacity = _style.ShadowOpacity, Color = _style.ShadowColor } : null
            };
            grid.Children.Add(border);
            Canvas.SetLeft(grid, Gutter / Dpi.DpiScaleX); Canvas.SetTop(grid, top / Dpi.DpiScaleY);
            _canvas!.Children.Add(grid);
            return new(new DeviceScreenRect(Gutter, top, Gutter + _shellWidth, top + _shellHeight), size, insets);
        }

        private bool TryCaptureStyle(out EdgeCapsuleCompositionStyle? style)
        {
            style = null;
            if (_owner.Chrome.Background is not SolidColorBrush paper ||
                _owner.Chrome.BorderBrush is not SolidColorBrush border ||
                _owner.Outline.BorderBrush is not SolidColorBrush outline ||
                _owner.Chrome.Effect is not DropShadowEffect shadow || shadow.ShadowDepth != 0 ||
                !SupportedBackground(_owner.ContentArea.Background) || !SupportedBackground(_owner.CloseArea.Background)) return false;
            style = new(paper.CloneCurrentValue(), border.CloneCurrentValue(), outline.CloneCurrentValue(),
                _owner.Chrome.CornerRadius, _owner.Outline.CornerRadius, _owner.Chrome.BorderThickness,
                _owner.Outline.BorderThickness, _owner.Chrome.Margin, _owner.Outline.Margin,
                shadow.BlurRadius, shadow.ShadowDepth, shadow.Opacity, shadow.Color);
            return true;
        }

        internal void ThemeChanging(Brush paper, Brush border, Brush outline)
        {
            // Queue placement refreshes the Host theme even when every value is unchanged.
            // Only the atlas's copied shell samples require retirement. Text, icons and preview
            // resources remain live through their VisualBrush; replacing equal brush instances
            // must not recreate every atlas HWND at each successor transaction.
            if (_style != null && (!SameSolid(paper, _style.PaperBrush) ||
                !SameSolid(border, _style.PaperBorderBrush) || !SameSolid(outline, _style.OutlineBrush)))
                Invalidate();
        }

        private bool StyleMatches() => _owner.Chrome.CornerRadius == _style!.ChromeCorners &&
            _owner.Outline.CornerRadius == _style.OutlineCorners && _owner.Chrome.Margin == _style.ChromeMargin &&
            _owner.Outline.Margin == _style.OutlineMargin && _owner.Chrome.BorderThickness == _style.ChromeBorder &&
            _owner.Outline.BorderThickness == _style.OutlineBorder && SameSolid(_owner.Chrome.Background, _style.PaperBrush) &&
            SameSolid(_owner.Chrome.BorderBrush, _style.PaperBorderBrush) && SameSolid(_owner.Outline.BorderBrush, _style.OutlineBrush) &&
            _owner.Chrome.Effect is DropShadowEffect shadow && shadow.BlurRadius == _style.ShadowBlurRadius &&
            shadow.ShadowDepth == _style.ShadowDepth && shadow.Opacity == _style.ShadowOpacity && shadow.Color == _style.ShadowColor;

        private static bool UpdateBackground(Border sample, Border actual)
        {
            if (!SupportedBackground(actual.Background)) return false;
            // Binding also tracks hover changes which do not require a new presentation frame.
            return true;
        }
        private static bool SupportedBackground(Brush? brush) => brush == null || brush is SolidColorBrush;
        private static bool SameSolid(Brush? a, Brush? b) => a is SolidColorBrush x && b is SolidColorBrush y &&
            x.Color == y.Color && x.Opacity == y.Opacity && x.Transform.Value.IsIdentity && y.Transform.Value.IsIdentity;
        private static bool UsableVisual(FrameworkElement visual) => visual.IsArrangeValid &&
            double.IsFinite(visual.ActualWidth) && double.IsFinite(visual.ActualHeight) &&
            visual.ActualWidth > 0 && visual.ActualHeight > 0;
        private static bool ContainsNativeChild(DependencyObject visual)
        {
            if (visual is HwndHost) return true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
                if (ContainsNativeChild(VisualTreeHelper.GetChild(visual, i))) return true;
            return false;
        }
        private static int DeviceCeiling(double value, double scale) => checked((int)Math.Ceiling(value * scale));
        private int BodyHeightDevice(double bodyHeight)
        {
            var margin = Math.Max(0, (int)Math.Round(_owner._options.WindowChromeMargin * Dpi.DpiScaleY,
                MidpointRounding.AwayFromZero));
            var bounds = Math.Max(1, (int)Math.Round((bodyHeight + _owner._options.WindowChromeMargin * 2) * Dpi.DpiScaleY,
                MidpointRounding.AwayFromZero));
            return Math.Max(1, bounds - margin * 2);
        }
        private static EdgeCapsuleCompositionInsets Insets(Thickness margin, CornerRadius corners, DpiScale dpi) => new(
            DeviceCeiling(margin.Left + Math.Max(corners.TopLeft, corners.BottomLeft), dpi.DpiScaleX),
            DeviceCeiling(margin.Top + Math.Max(corners.TopLeft, corners.TopRight), dpi.DpiScaleY),
            DeviceCeiling(margin.Right + Math.Max(corners.TopRight, corners.BottomRight), dpi.DpiScaleX),
            DeviceCeiling(margin.Bottom + Math.Max(corners.BottomLeft, corners.BottomRight), dpi.DpiScaleY));

        private void ClearAtlas()
        {
            foreach (var brush in _brushes) brush.Visual = null;
            _brushes.Clear();
            if (_compactImage != null) BindingOperations.ClearAllBindings(_compactImage);
            if (_contentBackground != null) BindingOperations.ClearAllBindings(_contentBackground);
            if (_closeBackground != null) BindingOperations.ClearAllBindings(_closeBackground);
            _canvas?.Children.Clear(); _canvas = null;
            _compactImage = _previewImage = _closeImage = null;
            _compactVisual = _previewVisual = null;
        }
    }
}
