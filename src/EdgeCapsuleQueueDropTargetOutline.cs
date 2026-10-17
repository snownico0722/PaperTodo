using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

/// <summary>
/// Disposable, input-transparent overlay marking where a free-queue drag would land.
///
/// Deliberately independent of <see cref="ExperimentalAttachmentPreviewWindow"/>. The overlay
/// only draws two lightweight markers: a thin accent rail on the queue's inner edge (which queue
/// would be joined) and a short accent line at the insertion point (where in it). The previous
/// queue-sized outline and ghost placeholder filled as much visual weight as the capsules
/// themselves, so they were removed.
///
/// Owns no paper geometry and never participates in the edge-capsule presenter.
/// </summary>
internal sealed class EdgeCapsuleQueueDropTargetOutline : Window
{
    private const int OutlineMarginDevice = 3;
    private const double RestingOpacity = 1.0;
    private const double InsertionLineRestingOpacity = 1;
    private const double InsertionLineSlideFadeFrom = 0.6;
    private const double MarkerGeometryEpsilon = 0.1;

    private bool _closed;
    private string _queueKey = "";
    private readonly Canvas _markerLayer;
    private readonly Border _queueEdgeLine;
    private readonly Border _insertionLine;
    private int _insertionLineIndex = -1;
    private int _insertionLineCarriedCount;
    private double _queueEdgeLineLeftDip = double.NaN;
    private double _queueEdgeLineTopDip = double.NaN;
    private double _queueEdgeLineWidthDip = double.NaN;
    private double _queueEdgeLineHeightDip = double.NaN;
    private double _insertionLineLeftDip = double.NaN;
    private double _insertionLineTopDip = double.NaN;
    private double _insertionLineWidthDip = double.NaN;
    private double _insertionLineHeightDip = double.NaN;
    private bool? _insertionLineFeasible;

    /// <summary>窗口是否已经关闭；关闭后控制器需要重建实例。</summary>
    internal bool IsClosed => _closed;

    /// <summary>测试用：插入短线真正启动滑动动画的次数。</summary>
    internal int InsertionLineSlideCount { get; private set; }

    /// <summary>测试用：当前插入短线对应的插入槽下标。</summary>
    internal int InsertionLineIndex => _insertionLineIndex;

    /// <summary>测试用：插入短线当前的目标 top（相对描边窗口的 DIP）。</summary>
    internal double InsertionLineTopTargetDip => _insertionLineTopDip;

    /// <summary>测试用：当前插入是否可行；不可行时插入短线使用警示色。</summary>
    internal bool InsertionLineIsWarning => _insertionLineFeasible == false;

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

        // 目标队列内侧的轻量竖线：只标识“将并入哪条队列”，不再画整队外框。
        _queueEdgeLine = new Border
        {
            Width = EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineWidthDip,
            CornerRadius = new CornerRadius(1),
            Background = Theme.ActiveBrush,
            Opacity = EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineOpacity,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        // 插入位置的短横线：默认暖色主题下即金色，跟随主题强调色。
        _insertionLine = new Border
        {
            Height = EdgeCapsuleQueueDropTargetGeometry.InsertionLineThicknessDip,
            CornerRadius = new CornerRadius(
                EdgeCapsuleQueueDropTargetGeometry.InsertionLineThicknessDip / 2.0),
            Background = Theme.ActiveBrush,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        _markerLayer = new Canvas { IsHitTestVisible = false };
        _markerLayer.Children.Add(_queueEdgeLine);
        _markerLayer.Children.Add(_insertionLine);
        Content = _markerLayer;

        SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(this);
            // The overlay is feedback only: a click must reach whatever is underneath it.
            WindowNative.SetInputPassthrough(this, enabled: true);
        };
        Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// Move the overlay onto the target queue and place both insertion markers.
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

        // A different queue is a genuinely different target and deserves the pulse; moving within
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

        ApplyMarkers(
            EdgeCapsuleQueueDropTargetGeometry.RelativeDip(
                layout.InsertionLineBounds,
                outlineBounds,
                layout.DpiScaleX,
                layout.DpiScaleY),
            EdgeCapsuleQueueDropTargetGeometry.RelativeDip(
                layout.QueueEdgeLineBounds,
                outlineBounds,
                layout.DpiScaleX,
                layout.DpiScaleY),
            layout.TargetIndex,
            layout.CarriedCount,
            layout.IsFeasible,
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
    /// 更新两条指示线。竖线只在几何变化时重新下发；插入线在同一队列内换槽时沿 Y 滑动，
    /// 避免高频调用把 200ms 动画每帧重启。
    /// </summary>
    private void ApplyMarkers(
        Rect insertionLine,
        Rect queueEdgeLine,
        int targetIndex,
        int carriedCount,
        bool isFeasible,
        bool firstShow,
        bool targetChanged,
        bool animate)
    {
        ApplyQueueEdgeLine(queueEdgeLine);

        if (_insertionLineFeasible != isFeasible)
        {
            _insertionLine.Background = isFeasible
                ? Theme.ActiveBrush
                : Theme.DangerBrush;
            _insertionLineFeasible = isFeasible;
        }

        if (insertionLine.Width <= 0.01 || insertionLine.Height <= 0.01)
        {
            _insertionLine.Visibility = Visibility.Collapsed;
            return;
        }

        _insertionLine.Visibility = Visibility.Visible;

        var sameSlot = !targetChanged &&
                       targetIndex == _insertionLineIndex &&
                       carriedCount == _insertionLineCarriedCount;
        var sameLeft = NearlyEqual(_insertionLineLeftDip, insertionLine.Left);
        var sameTop = NearlyEqual(_insertionLineTopDip, insertionLine.Top);
        var sameWidth = NearlyEqual(_insertionLineWidthDip, insertionLine.Width);
        var sameHeight = NearlyEqual(_insertionLineHeightDip, insertionLine.Height);
        if (sameSlot && sameLeft && sameTop && sameWidth && sameHeight)
        {
            return;
        }

        if (!sameLeft || !sameWidth)
        {
            Canvas.SetLeft(_insertionLine, insertionLine.Left);
            _insertionLine.Width = insertionLine.Width;
        }
        if (!sameHeight)
        {
            _insertionLine.BeginAnimation(FrameworkElement.HeightProperty, null);
            _insertionLine.Height = insertionLine.Height;
        }

        var canSlide = animate &&
                       !firstShow &&
                       !targetChanged &&
                       _insertionLineIndex >= 0 &&
                       sameLeft &&
                       sameWidth &&
                       sameHeight &&
                       !sameTop;
        if (canSlide)
        {
            AnimationHelper.AnimateCanvasTop(
                _insertionLine,
                insertionLine.Top,
                EdgeCapsuleLayout.SlotMoveMilliseconds);
            AnimationHelper.FadeToFrom(
                _insertionLine,
                InsertionLineSlideFadeFrom,
                InsertionLineRestingOpacity,
                EdgeCapsuleLayout.SlotMoveMilliseconds);
            InsertionLineSlideCount++;
        }
        else
        {
            _insertionLine.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(_insertionLine, insertionLine.Top);
            if (firstShow || targetChanged || !animate)
            {
                _insertionLine.BeginAnimation(OpacityProperty, null);
                _insertionLine.Opacity = InsertionLineRestingOpacity;
            }
        }

        _insertionLineIndex = targetIndex;
        _insertionLineCarriedCount = carriedCount;
        _insertionLineLeftDip = insertionLine.Left;
        _insertionLineTopDip = insertionLine.Top;
        _insertionLineWidthDip = insertionLine.Width;
        _insertionLineHeightDip = insertionLine.Height;
    }

    private void ApplyQueueEdgeLine(Rect target)
    {
        if (target.Width <= 0.01 || target.Height <= 0.01)
        {
            _queueEdgeLine.Visibility = Visibility.Collapsed;
            return;
        }

        _queueEdgeLine.Visibility = Visibility.Visible;
        if (!NearlyEqual(_queueEdgeLineLeftDip, target.Left))
        {
            Canvas.SetLeft(_queueEdgeLine, target.Left);
        }
        if (!NearlyEqual(_queueEdgeLineTopDip, target.Top))
        {
            Canvas.SetTop(_queueEdgeLine, target.Top);
        }
        if (!NearlyEqual(_queueEdgeLineWidthDip, target.Width))
        {
            _queueEdgeLine.Width = target.Width;
        }
        if (!NearlyEqual(_queueEdgeLineHeightDip, target.Height))
        {
            _queueEdgeLine.Height = target.Height;
        }

        _queueEdgeLineLeftDip = target.Left;
        _queueEdgeLineTopDip = target.Top;
        _queueEdgeLineWidthDip = target.Width;
        _queueEdgeLineHeightDip = target.Height;
    }

    private static bool NearlyEqual(double left, double right) =>
        double.IsFinite(left) &&
        double.IsFinite(right) &&
        Math.Abs(left - right) < MarkerGeometryEpsilon;

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
