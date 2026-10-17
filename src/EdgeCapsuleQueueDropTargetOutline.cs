using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PaperTodo;

/// <summary>
/// Disposable, input-transparent outline marking which edge queue a free-queue drag would join.
///
/// Deliberately independent of <see cref="ExperimentalAttachmentPreviewWindow"/>. That window is
/// gated on "not a docked edge capsule" and describes snapping a folded capsule onto a window or
/// screen edge; sharing it would couple the experimental magnet feature to free queues for no gain.
///
/// Owns no paper geometry and never participates in the edge-capsule presenter.
/// </summary>
internal sealed class EdgeCapsuleQueueDropTargetOutline : Window
{
    private const int OutlineMarginDevice = 3;
    private const double RestingOpacity = 0.86;
    private const double PlaceholderRestingOpacity = 0.9;
    private const double PlaceholderSlideFadeFrom = 0.6;
    private const double PlaceholderGeometryEpsilon = 0.1;

    private bool _closed;
    private string _queueKey = "";
    private readonly Canvas _placeholderLayer;
    private readonly Border _placeholder;
    private int _placeholderIndex = -1;
    private int _placeholderCarriedCount;
    private double _placeholderLeftDip = double.NaN;
    private double _placeholderTopDip = double.NaN;
    private double _placeholderWidthDip = double.NaN;
    private double _placeholderHeightDip = double.NaN;

    /// <summary>测试用：幽灵占位框真正启动滑动动画的次数。</summary>
    internal int PlaceholderSlideCount { get; private set; }

    /// <summary>测试用：幽灵占位框启动高度展开动画的次数。</summary>
    internal int PlaceholderHeightAnimationCount { get; private set; }

    /// <summary>测试用：当前占位框对应的插入槽下标。</summary>
    internal int PlaceholderIndex => _placeholderIndex;

    /// <summary>测试用：占位框当前的目标 top（相对描边窗口的 DIP）。</summary>
    internal double PlaceholderTopTargetDip => _placeholderTopDip;

    /// <summary>测试用：占位框当前的目标高度（DIP）。</summary>
    internal double PlaceholderTargetHeightDip => _placeholderHeightDip;

    public EdgeCapsuleQueueDropTargetOutline()
    {
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Opacity = 0;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var activeColor = Theme.ActiveBrush is SolidColorBrush activeBrush
            ? activeBrush.Color
            : Colors.Gray;

        // 幽灵占位框：半透明 accent 圆角胶囊，标出真正要插入的槽位。
        _placeholder = new Border
        {
            CornerRadius = new CornerRadius(EdgeCapsuleLayout.CornerRadius),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Theme.ActiveBrush,
            Background = Theme.Tint((byte)(Theme.IsDark ? 44 : 30)),
            IsHitTestVisible = false,
            Opacity = PlaceholderRestingOpacity,
            Visibility = Visibility.Collapsed
        };
        // Canvas 与外框 Border 是兄弟节点，坐标原点就是窗口内容区左上角，
        // 不会额外受到外框 Margin(1) / BorderThickness(2) 的内缩影响。
        _placeholderLayer = new Canvas { IsHitTestVisible = false };
        _placeholderLayer.Children.Add(_placeholder);

        var root = new Grid();
        root.Children.Add(new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(2),
            BorderBrush = Theme.ActiveBrush,
            Background = Theme.Tint((byte)(Theme.IsDark ? 34 : 22)),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = activeColor,
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = Theme.IsDark ? 0.58 : 0.34
            }
        });
        root.Children.Add(_placeholderLayer);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            // The outline is feedback only: a click must reach whatever is underneath it.
            WindowNative.SetInputPassthrough(this, enabled: true);
        };
        Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// Move the outline onto the target queue and place the ghost insertion slot inside it.
    /// Returns whether this call changed the target queue, which the caller uses to decide
    /// between a resting show and an emphasized attention pulse.
    /// </summary>
    public bool ShowAt(
        EdgeCapsuleQueueDropTargetLayout layout,
        string queueKey,
        IntPtr fullscreenAvoidanceWindow,
        bool animate)
    {
        if (_closed)
        {
            return false;
        }

        // A different queue is a genuinely different target and deserves the pulse; sliding within
        // the same queue must not re-pulse, or it would strobe on every pointer move.
        var targetChanged = !string.Equals(_queueKey, queueKey, StringComparison.Ordinal);
        _queueKey = queueKey;

        var bounds = layout.QueueBounds;
        var outlineBounds = bounds.IsEmpty
            ? bounds
            : new DeviceScreenRect(
                bounds.Left - OutlineMarginDevice,
                bounds.Top - OutlineMarginDevice,
                bounds.Right + OutlineMarginDevice,
                bounds.Bottom + OutlineMarginDevice);
        var firstShow = !IsVisible;
        if (firstShow)
        {
            try
            {
                Show();
            }
            catch (Exception)
            {
                return targetChanged;
            }
        }

        if (outlineBounds.IsEmpty)
        {
            return targetChanged;
        }

        if (!WindowNative.TrySetWindowDeviceBounds(this, outlineBounds))
        {
            return targetChanged;
        }

        ApplyPlaceholder(
            EdgeCapsuleQueueDropTargetGeometry.RelativeDip(
                layout.PlaceholderBounds,
                outlineBounds,
                layout.DpiScaleX,
                layout.DpiScaleY),
            layout.TargetIndex,
            layout.CarriedCount,
            firstShow,
            targetChanged,
            animate);

        var topmost = fullscreenAvoidanceWindow == IntPtr.Zero;
        Topmost = topmost;
        WindowNative.ApplyTopmostZOrder(this, topmost, fullscreenAvoidanceWindow);

        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = RestingOpacity;
            return targetChanged;
        }

        if (firstShow || targetChanged)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = targetChanged ? 0.42 : 0;
            AnimationHelper.FadeTo(
                this,
                RestingOpacity,
                duration: targetChanged ? 90 : 120,
                easing: AnimationHelper.QuickEase);
        }

        return targetChanged;
    }

    /// <summary>
    /// 更新幽灵占位框。只有真正换了槽位/尺寸时才重新下发；同一 (队列, index, carriedCount,
    /// 几何) 的 32ms 重复调用会直接返回，避免把 200ms 滑动每帧重启。
    /// </summary>
    private void ApplyPlaceholder(
        Rect target,
        int targetIndex,
        int carriedCount,
        bool firstShow,
        bool targetChanged,
        bool animate)
    {
        if (target.Width <= 0 || target.Height <= 0)
        {
            _placeholder.Visibility = Visibility.Collapsed;
            return;
        }

        var sameSlot = !targetChanged &&
                       targetIndex == _placeholderIndex &&
                       carriedCount == _placeholderCarriedCount;
        var sameTop = NearlyEqual(_placeholderTopDip, target.Top);
        var sameHeight = NearlyEqual(_placeholderHeightDip, target.Height);
        var sameGeometry =
            NearlyEqual(_placeholderLeftDip, target.Left) &&
            sameTop &&
            NearlyEqual(_placeholderWidthDip, target.Width) &&
            sameHeight;
        if (sameSlot && sameGeometry)
        {
            return;
        }

        // 同一队列内 index 变化、高度不变：只滑动 top。
        // 首次出现/换队列/高度变化：高度从 0（或当前高度）展开，像 mini-view 一样。
        var canSlide = animate &&
                       !firstShow &&
                       !targetChanged &&
                       _placeholderIndex >= 0 &&
                       sameHeight &&
                       NearlyEqual(_placeholderLeftDip, target.Left) &&
                       NearlyEqual(_placeholderWidthDip, target.Width) &&
                       !sameTop;
        var canGrow = animate && (firstShow || targetChanged || !sameHeight);

        _placeholder.Visibility = Visibility.Visible;
        if (!NearlyEqual(_placeholderLeftDip, target.Left))
        {
            Canvas.SetLeft(_placeholder, target.Left);
        }
        if (!NearlyEqual(_placeholderWidthDip, target.Width))
        {
            _placeholder.Width = target.Width;
        }

        if (canGrow)
        {
            // 新的插入目标从头长出来；同一目标高度变化时从当前高度改道。
            var from = firstShow || targetChanged ? 0 : _placeholder.Height;
            AnimationHelper.AnimateHeightFrom(
                _placeholder,
                from,
                target.Height,
                EdgeCapsuleLayout.SlotMoveMilliseconds);
            PlaceholderHeightAnimationCount++;

            if (!firstShow && !targetChanged)
            {
                AnimationHelper.FadeToFrom(
                    _placeholder,
                    PlaceholderSlideFadeFrom,
                    PlaceholderRestingOpacity,
                    EdgeCapsuleLayout.SlotMoveMilliseconds);
            }
        }
        else if (!sameHeight)
        {
            _placeholder.BeginAnimation(FrameworkElement.HeightProperty, null);
            _placeholder.Height = target.Height;
        }

        if (canSlide)
        {
            AnimationHelper.AnimateCanvasTop(
                _placeholder,
                target.Top,
                EdgeCapsuleLayout.SlotMoveMilliseconds);
            AnimationHelper.FadeToFrom(
                _placeholder,
                PlaceholderSlideFadeFrom,
                PlaceholderRestingOpacity,
                EdgeCapsuleLayout.SlotMoveMilliseconds);
            PlaceholderSlideCount++;
        }
        else
        {
            _placeholder.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(_placeholder, target.Top);
            if (firstShow || targetChanged || !animate)
            {
                _placeholder.BeginAnimation(OpacityProperty, null);
                _placeholder.Opacity = PlaceholderRestingOpacity;
            }
        }

        _placeholderIndex = targetIndex;
        _placeholderCarriedCount = carriedCount;
        _placeholderLeftDip = target.Left;
        _placeholderTopDip = target.Top;
        _placeholderWidthDip = target.Width;
        _placeholderHeightDip = target.Height;
    }

    private static bool NearlyEqual(double left, double right) =>
        double.IsFinite(left) &&
        double.IsFinite(right) &&
        Math.Abs(left - right) < PlaceholderGeometryEpsilon;

    public void CloseForOwner()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _queueKey = "";
        BeginAnimation(OpacityProperty, null);
        Close();
    }
}
