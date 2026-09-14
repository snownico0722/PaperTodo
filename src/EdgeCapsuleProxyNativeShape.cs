using System.Diagnostics;
using System.Windows;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

/// <summary>
/// Native commands for a live WPF atlas. The caller owns publication, Commit and retirement.
/// There is no clock source, input policy, layout pass or presentation state machine here.
/// Only text-free shell/background planes may be scaled; content is cropped 1:1.
/// </summary>
internal sealed class EdgeCapsuleProxyNativeShape : IDisposable
{
    private readonly IDCompositionDesktopDevice _device;
    private readonly IDCompositionVisual _parent;
    private readonly IUnknown _source;
    private readonly List<IDisposable> _resources = new();
    private readonly List<IDCompositionAnimation> _animations = new();
    private IDCompositionVisual? _root;
    private IDCompositionEffectGroup? _overallOpacity;
    private IDCompositionEffectGroup? _contentOpacity;
    private NineSlice? _chrome, _outline;
    private SolidBackground? _contentBackground, _closeBackground;
    private Plane? _compact, _preview, _close;
    private IDCompositionEffectGroup? _outlineOpacity;
    private EdgeCapsuleTransition? _transition;
    private EdgeCapsulePresentationFrame _frame;
    private List<CommandKey>? _lastCommands;
    private bool _disposed;
    private bool _faulted;
    private CommandKey? _submittedPreviewAlpha;

    internal EdgeCapsuleProxySourceDescription Description { get; private set; }
    internal int SubmissionCount { get; private set; }

    internal EdgeCapsuleProxyNativeShape(IDCompositionDesktopDevice device,
        IDCompositionVisual parent, IUnknown source, EdgeCapsuleProxySourceDescription description)
    {
        _device = device; _parent = parent; _source = source;
        Description = description ?? throw new ArgumentNullException(nameof(description));
        try { BuildTree(description); }
        catch { Dispose(); throw; }
    }

    internal bool Update(EdgeCapsulePresentationFrame frame, EdgeCapsuleTransition? transition,
        EdgeCapsuleProxySourceDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new InvalidOperationException("A failed native shape must be retired by its queue owner.");
        try { return UpdateCore(frame, transition, description); }
        catch { _faulted = true; throw; }
    }

    private bool UpdateCore(EdgeCapsulePresentationFrame frame, EdgeCapsuleTransition? transition,
        EdgeCapsuleProxySourceDescription description)
    {
        Validate(frame, description);
        var active = transition is { } candidate && candidate.DurationTimestampTicks > 0
            ? candidate : (EdgeCapsuleTransition?)null;
        var from = active?.Start ?? frame;
        var to = active?.Target.ToFrame() ?? frame;
        Validate(from, description); Validate(to, description);
        if (from.HostBounds.Width != to.HostBounds.Width || from.HostBounds.Height != to.HostBounds.Height ||
            from.Edge != to.Edge || from.DpiScaleX != to.DpiScaleX || from.DpiScaleY != to.DpiScaleY)
            throw new InvalidOperationException("A native shape cannot animate a source capacity, edge or DPI change.");
        if (_lastCommands != null && ReferenceEquals(Description, description) && _transition == active &&
            (active != null || _frame == frame))
        { _frame = frame; return false; }

        if (!SameAtlasLayout(Description, description))
        {
            // Source pixels may be replaced in place, but a changed crop needs an explicit new tree.
            // No Commit occurs between detachment and replacement. A failed update is handed back
            // to the queue's existing rollback, never published as a partially valid new frame.
            ReleaseTree(); BuildTree(description); _lastCommands = null;
        }
        var commands = new List<Command>();
        void Bind(double a, double b, Func<float, Result> value,
            Func<IDCompositionAnimation, Result> animation, bool unit = false) =>
            commands.Add(new(new((float)a, (float)b, unit ? 0 : float.NegativeInfinity,
                unit ? 1 : float.PositiveInfinity), value, animation));
        void BindLimit(double a, double b, double maximum, Func<float, Result> value,
            Func<IDCompositionAnimation, Result> animation,
            double otherFrom = double.PositiveInfinity, double otherTo = double.PositiveInfinity) =>
            commands.Add(new(new((float)a, (float)b, 0, (float)maximum,
                (float)otherFrom, (float)otherTo), value, animation));

        var overallOpacity = _overallOpacity ?? throw new InvalidOperationException("Native shape opacity tree is absent.");
        var contentOpacity = _contentOpacity ?? throw new InvalidOperationException("Native shape content tree is absent.");
        var outlineOpacity = _outlineOpacity ?? throw new InvalidOperationException("Native shape outline tree is absent.");
        Bind(from.Opacity, to.Opacity, overallOpacity.SetOpacity, overallOpacity.SetOpacity, true);
        Bind(from.ContentOpacity, to.ContentOpacity, contentOpacity.SetOpacity, contentOpacity.SetOpacity, true);
        Bind(to.OutlineVisible ? 1 : 0, to.OutlineVisible ? 1 : 0,
            outlineOpacity.SetOpacity, outlineOpacity.SetOpacity, true);
        var a = GeometryFor(from, description); var b = GeometryFor(to, description);
        if (active != null)
        {
            // These are structural target fields in TransitionPolicy.Sample, not interpolated
            // presentation channels. Preserve their immediate target semantics at t=0 as well.
            a = a with { CloseOpacity = CloseAlpha(from.Bounds.Width - from.BodyWindowWidthDevice, to) };
            b = b with { CloseOpacity = CloseAlpha(to.Bounds.Width - to.BodyWindowWidthDevice, to) };
        }
        _chrome!.Bind(a.Shape, b.Shape, Bind);
        _outline!.Bind(a.Shape, b.Shape, Bind);
        _contentBackground!.Bind(a.Content, b.Content, description.ContentBackgroundCorners,
            description.Dpi, Bind, BindLimit);
        _closeBackground!.Bind(a.Close, b.Close, description.CloseBackgroundCorners,
            description.Dpi, Bind, BindLimit, a.CloseOpacity, b.CloseOpacity);

        var basis = GeometryFor(description.LayoutFrame, description);
        var previewTransition = from.Surface == EdgeCapsuleSurfaceKind.DockedPreview ||
            to.Surface == EdgeCapsuleSurfaceKind.DockedPreview ||
            a.PreviewProgress > 0 || b.PreviewProgress > 0;
        // WPF owns the compact tree's existing 35 ms fade. While preview is involved its source
        // anchor is already pinned by Host.Preview; applying the body's expansion again would drift text.
        var compactA = description.Compact.HostOffset;
        var compactB = compactA;
        if (!previewTransition)
        {
            compactA = OffsetFromBasis(compactA, a.Content, basis.Content, rightAligned: false);
            compactB = OffsetFromBasis(compactB, b.Content, basis.Content, rightAligned: false);
        }
        var hostClip = new Rect(0, 0, from.HostBounds.Width, from.HostBounds.Height);
        // These WPF parents do not ClipToBounds. Do not silently add a body-width crop to a
        // compact anchor or to a fading close glyph; the native HWND capacity is their boundary.
        _compact!.Bind(compactA, compactB, hostClip, hostClip, default, 1, 1, Bind, BindLimit);
        if (_preview != null && description.Preview is { } preview)
        {
            var right = from.Edge == EdgeCapsuleEdge.Right;
            var pa = OffsetFromBasis(preview.HostOffset, a.Content, basis.Content, right);
            var pb = OffsetFromBasis(preview.HostOffset, b.Content, basis.Content, right);
            _preview.Bind(pa, pb, a.Content, b.Content, description.PreviewViewportCorners,
                a.PreviewProgress, b.PreviewProgress, Bind, BindLimit, description.Dpi);
        }
        if (_close != null && description.Close is { } close)
        {
            // Glyph/child size remains fixed. Centering follows the same expanding close segment.
            var ca = CenterFromBasis(close.HostOffset, a.Close, basis.Close);
            var cb = CenterFromBasis(close.HostOffset, b.Close, basis.Close);
            _close.Bind(ca, cb, hostClip, hostClip, default, a.CloseOpacity, b.CloseOpacity, Bind, BindLimit);
        }

        var keys = commands.Select(command => command.Key).ToList();
        var unchanged = _lastCommands != null && _lastCommands.SequenceEqual(keys) && _transition == active;
        if (unchanged) { _frame = frame; return false; }
        var previous = _animations.ToArray();
        _animations.Clear();
        try
        {
            foreach (var command in commands) Submit(command, active);
        }
        finally
        {
            // Assigned DComp properties retain their own COM references. The new references stay
            // owned until retirement even if a later property fails and the caller rolls back.
            foreach (var animation in previous) animation.Dispose();
        }
        _frame = frame; _transition = active; _lastCommands = keys; Description = description;
        _submittedPreviewAlpha = _preview == null ? null : new((float)a.PreviewProgress,
            (float)b.PreviewProgress, 0, 1);
        SubmissionCount++;
        return true;
    }

    internal bool SetStatic(EdgeCapsulePresentationFrame frame,
        EdgeCapsuleProxySourceDescription description) => Update(frame, null, description);

    internal EdgeCapsulePresentationFrame Sample(long timestamp) => _transition is { } transition
        ? EdgeCapsuleTransitionPolicy.Sample(transition, timestamp).Frame : _frame;

    internal bool CanReplacePreview(long timestamp)
    {
        if (_disposed || _faulted || SubmissionCount == 0) return false;
        if (_transition is { } transition &&
            (timestamp < transition.StartedAtTimestamp ||
             timestamp - transition.StartedAtTimestamp < transition.DurationTimestampTicks)) return false;
        // Check the actual native alpha channel, not the early device-pixel Surface identity flip.
        return _submittedPreviewAlpha is not { } alpha || Evaluate(alpha, 1) == 0;
    }

    private static DeviceScreenPoint OffsetFromBasis(DeviceScreenPoint point, Rect current, Rect basis, bool rightAligned) =>
        new(point.X + (rightAligned ? current.Right - basis.Right : current.Left - basis.Left),
            point.Y + current.Top - basis.Top);

    private static DeviceScreenPoint CenterFromBasis(DeviceScreenPoint point, Rect current, Rect basis) =>
        new(point.X + current.Left + current.Width / 2 - basis.Left - basis.Width / 2,
            point.Y + current.Top + current.Height / 2 - basis.Top - basis.Height / 2);

    private readonly record struct Geometry(Rect Shape, Rect Content, Rect Close,
        double PreviewProgress, double CloseOpacity);

    private static Geometry GeometryFor(EdgeCapsulePresentationFrame frame,
        EdgeCapsuleProxySourceDescription description)
    {
        var geometry = RawGeometryFor(frame, description);
        var basis = RawGeometryFor(description.LayoutFrame, description);
        // Preserve WPF's actual fractional-DPI arrangement at the capture point; only the
        // canonical width/height/segmentation deltas are supplied by the native transition.
        static Rect Rebase(Rect value, Rect ideal, DeviceScreenRect actual) =>
            new(value.Left + actual.Left - ideal.Left, value.Top + actual.Top - ideal.Top,
                Math.Max(0, value.Width + actual.Width - ideal.Width),
                Math.Max(0, value.Height + actual.Height - ideal.Height));
        return geometry with
        {
            Content = Rebase(geometry.Content, basis.Content, description.ContentBackgroundBounds),
            Close = Rebase(geometry.Close, basis.Close, description.CloseBackgroundBounds)
        };
    }

    private static Geometry RawGeometryFor(EdgeCapsulePresentationFrame frame,
        EdgeCapsuleProxySourceDescription description)
    {
        var scaleX = Math.Max(1, frame.DpiScaleX); var scaleY = Math.Max(1, frame.DpiScaleY);
        var marginX = (frame.Edge == EdgeCapsuleEdge.Left
            ? description.Style.ChromeMargin.Right : description.Style.ChromeMargin.Left) * scaleX;
        var marginY = Math.Round(description.Style.ChromeMargin.Top * scaleY, MidpointRounding.AwayFromZero);
        var left = frame.Edge == EdgeCapsuleEdge.Left ? 0 : frame.HostBounds.Width - frame.Bounds.Width;
        var bodyHeight = Math.Max(1, frame.Bounds.Height - 2 * marginY);
        var contentWidth = Math.Max(scaleX, frame.BodyWindowWidthDevice - marginX);
        var closeWidth = Math.Max(0, frame.Bounds.Width - frame.BodyWindowWidthDevice);
        var contentLeft = left + (frame.Edge == EdgeCapsuleEdge.Left ? closeWidth : marginX);
        var closeLeft = left + (frame.Edge == EdgeCapsuleEdge.Left ? 0 : frame.BodyWindowWidthDevice);
        var progress = (bodyHeight - description.CompactBodyHeightDevice) /
            Math.Max(scaleY, description.PreviewBodyHeightDevice - description.CompactBodyHeightDevice);
        if (description.Preview == null) progress = 0;
        var closeOpacity = CloseAlpha(closeWidth, frame);
        return new(new Rect(left, 0, frame.Bounds.Width, frame.Bounds.Height),
            new Rect(contentLeft, marginY, contentWidth, bodyHeight),
            new Rect(closeLeft, marginY, closeWidth, bodyHeight), progress, closeOpacity);
    }

    private static double CloseAlpha(double closeWidth, EdgeCapsulePresentationFrame structuralFrame) =>
        structuralFrame.CloseSegmentActsAsContent ? 1 : structuralFrame.MaximumCloseWidthDip <= 0 ? 0 :
            Math.Max(0, closeWidth) / (structuralFrame.MaximumCloseWidthDip * Math.Max(1, structuralFrame.DpiScaleX));

    private void Validate(EdgeCapsulePresentationFrame frame, EdgeCapsuleProxySourceDescription description)
    {
        if (description.SourceHandle == IntPtr.Zero || description.SourceHandle != Description.SourceHandle ||
            description.OwnerHandle != Description.OwnerHandle || description.Generation != Description.Generation ||
            description.NativeBounds != Description.NativeBounds || description.Edge != Description.Edge ||
            !frame.Visible || !frame.IsUsable || frame.Edge != description.Edge ||
            Math.Abs(frame.DpiScaleX - description.Dpi.DpiScaleX) > .001 ||
            Math.Abs(frame.DpiScaleY - description.Dpi.DpiScaleY) > .001 ||
            !double.IsFinite(frame.ContentOpacity) || !double.IsFinite(frame.Opacity))
            throw new InvalidOperationException("The native shape's live source identity is no longer compatible.");
    }

    private static bool SameAtlasLayout(EdgeCapsuleProxySourceDescription a, EdgeCapsuleProxySourceDescription b) =>
        a.Compact.SourceBounds == b.Compact.SourceBounds && a.Preview?.SourceBounds == b.Preview?.SourceBounds &&
        a.Close?.SourceBounds == b.Close?.SourceBounds && a.ShellChrome == b.ShellChrome &&
        a.ShellOutline == b.ShellOutline && a.ContentBackground == b.ContentBackground &&
        a.CloseBackground == b.CloseBackground;

    private IDCompositionVisual Visual(IDCompositionVisual? parent = null)
    {
        _device.CreateVisual(out IDCompositionVisual2 visual).CheckError(); _resources.Add(visual);
        visual.SetBorderMode(BorderMode.Hard).CheckError();
        // DComp's NULL-reference rule is counterintuitive: TRUE means below all siblings,
        // FALSE means above all. Later layers (text/preview/outline) must cover earlier Chrome.
        // https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositionvisual-addvisual
        if (parent != null) parent.AddVisual(visual, false, null!).CheckError();
        return visual;
    }

    private IDCompositionEffectGroup Effect(IDCompositionVisual visual)
    {
        _device.CreateEffectGroup(out var effect).CheckError(); _resources.Add(effect);
        effect.SetOpacity(1).CheckError(); visual.SetEffect(effect).CheckError(); return effect;
    }

    private IDCompositionRectangleClip Clip(IDCompositionVisual visual)
    {
        _device.CreateRectangleClip(out var clip).CheckError(); _resources.Add(clip);
        visual.SetClip(clip).CheckError(); return clip;
    }

    private void BuildTree(EdgeCapsuleProxySourceDescription description)
    {
        _root = Visual(); _overallOpacity = Effect(_root); _overallOpacity.SetOpacity(0).CheckError();
        var content = Visual(_root); _contentOpacity = Effect(content);
        _chrome = new NineSlice(this, content, description.ShellChrome);
        _contentBackground = new SolidBackground(this, content, description.ContentBackground);
        _closeBackground = new SolidBackground(this, content, description.CloseBackground);
        _compact = new Plane(this, content, description.Compact);
        _close = description.Close is { } close ? new Plane(this, content, close) : null;
        _preview = description.Preview is { } preview ? new Plane(this, content, preview) : null;
        var outline = Visual(content); _outlineOpacity = Effect(outline);
        _outline = new NineSlice(this, outline, description.ShellOutline);
        // The caller's parent retains its existing screen translation; it must not blit the atlas.
        _parent.SetContent(null!).CheckError();
        _parent.AddVisual(_root, false, null!).CheckError();
    }

    private readonly record struct CommandKey(float From, float To,
        float Minimum = float.NegativeInfinity, float Maximum = float.PositiveInfinity,
        float OtherFrom = float.PositiveInfinity, float OtherTo = float.PositiveInfinity);
    private sealed record Command(CommandKey Key, Func<float, Result> Value,
        Func<IDCompositionAnimation, Result> Animation);

    private void Submit(Command command, EdgeCapsuleTransition? transition)
    {
        var key = command.Key;
        if (!float.IsFinite(key.From) || !float.IsFinite(key.To))
            throw new InvalidOperationException("A composition property is not finite.");
        var cuts = CurveCuts(key);
        var to = Evaluate(key, 1);
        if (transition is not { } active || cuts.All(cut => Math.Abs(Evaluate(key, cut) - to) < 0.000001f))
        { command.Value(to).CheckError(); return; }
        var duration = active.DurationTimestampTicks * 1000.0 / Stopwatch.Frequency;
        var animation = float.IsFinite(key.Minimum) || float.IsFinite(key.Maximum) || float.IsFinite(key.OtherFrom)
            ? CreateBoundedAnimation(key, cuts, active)
            : EdgeCapsuleQueueCompositionProxy.CreateEaseOutCubicAnimation(
                _device, key.From, key.To, active.StartedAtTimestamp, duration);
        _animations.Add(animation);
        command.Animation(animation).CheckError();
    }

    private static float Evaluate(CommandKey key, double eased)
    {
        var value = key.From + (key.To - key.From) * eased;
        if (float.IsFinite(key.OtherFrom))
            value = Math.Min(value, key.OtherFrom + (key.OtherTo - key.OtherFrom) * eased);
        return (float)Math.Clamp(value, key.Minimum, key.Maximum);
    }

    // Every property is an affine function of the one eased progress, optionally min'ed with
    // another affine function and bounded. Only their analytical crossings produce segments.
    private static List<double> CurveCuts(CommandKey key)
    {
        var cuts = new List<double> { 0, 1 };
        void AddCrossing(double a, double b, double otherA, double otherB)
        {
            var denominator = b - a - otherB + otherA;
            if (!double.IsFinite(denominator) || denominator == 0) return;
            var p = (otherA - a) / denominator;
            if (p > 0 && p < 1) cuts.Add(p);
        }
        foreach (var bound in new[] { key.Minimum, key.Maximum })
        {
            if (!float.IsFinite(bound)) continue;
            AddCrossing(key.From, key.To, bound, bound);
            if (float.IsFinite(key.OtherFrom)) AddCrossing(key.OtherFrom, key.OtherTo, bound, bound);
        }
        if (float.IsFinite(key.OtherFrom)) AddCrossing(key.From, key.To, key.OtherFrom, key.OtherTo);
        return cuts.Distinct().OrderBy(value => value).ToList();
    }

    private IDCompositionAnimation CreateBoundedAnimation(CommandKey key, List<double> easedCuts,
        EdgeCapsuleTransition transition)
    {
        var seconds = Math.Max(.001, transition.DurationTimestampTicks / (double)Stopwatch.Frequency);
        var cuts = easedCuts.Select(eased => 1 - Math.Cbrt(1 - eased)).ToArray();
        var animation = _device.CreateAnimation();
        try
        {
            animation.SetAbsoluteBeginTime(transition.StartedAtTimestamp).CheckError();
            for (var i = 0; i + 1 < cuts.Length; i++)
            {
                var t = cuts[i]; var middle = (t + cuts[i + 1]) / 2;
                var easedMiddle = 1 - Math.Pow(1 - middle, 3);
                var from = key.From; var to = key.To;
                if (float.IsFinite(key.OtherFrom) &&
                    key.OtherFrom + (key.OtherTo - key.OtherFrom) * easedMiddle < from + (to - from) * easedMiddle)
                { from = key.OtherFrom; to = key.OtherTo; }
                var delta = to - from;
                var m = from + delta * easedMiddle;
                var value = from + delta * (1 - Math.Pow(1 - t, 3));
                if (m <= key.Minimum || m >= key.Maximum)
                    animation.AddCubic(t * seconds, (float)Math.Clamp(m, key.Minimum, key.Maximum), 0, 0, 0).CheckError();
                else
                    animation.AddCubic(t * seconds, (float)Math.Clamp(value, key.Minimum, key.Maximum),
                        (float)(3 * delta * (1 - t) * (1 - t) / seconds),
                        (float)(-3 * delta * (1 - t) / (seconds * seconds)),
                        (float)(delta / (seconds * seconds * seconds))).CheckError();
            }
            animation.End(seconds, Evaluate(key, 1)).CheckError(); return animation;
        }
        catch { animation.Dispose(); throw; }
    }

    private delegate void BindChannel(double from, double to, Func<float, Result> value,
        Func<IDCompositionAnimation, Result> animation, bool unit = false);
    private delegate void BindLimitedChannel(double from, double to, double maximum, Func<float, Result> value,
        Func<IDCompositionAnimation, Result> animation,
        double otherFrom = double.PositiveInfinity, double otherTo = double.PositiveInfinity);

    private sealed class Plane
    {
        private readonly IDCompositionVisual _viewport, _position;
        private readonly IDCompositionRectangleClip _viewportClip;
        private readonly IDCompositionEffectGroup _opacity;
        internal Plane(EdgeCapsuleProxyNativeShape owner, IDCompositionVisual parent,
            EdgeCapsuleCompositionPlane plane)
        {
            _viewport = owner.Visual(parent); _viewportClip = owner.Clip(_viewport); _opacity = owner.Effect(_viewport);
            _position = owner.Visual(_viewport);
            var source = owner.Visual(_position); var clip = owner.Clip(source);
            source.SetContent(owner._source).CheckError();
            SetRect(clip, new Rect(plane.SourceBounds.Left, plane.SourceBounds.Top,
                plane.SourceBounds.Width, plane.SourceBounds.Height));
            source.SetOffsetX(-plane.SourceBounds.Left).CheckError();
            source.SetOffsetY(-plane.SourceBounds.Top).CheckError();
        }

        internal void Bind(DeviceScreenPoint from, DeviceScreenPoint to, Rect a, Rect b,
            CornerRadius corners, double alphaA, double alphaB, BindChannel bind,
            BindLimitedChannel limited, DpiScale? dpi = null)
        {
            // Viewport coordinates and the plane's unscaled destination share Host-local pixels.
            bind(from.X, to.X, _position.SetOffsetX, _position.SetOffsetX);
            bind(from.Y, to.Y, _position.SetOffsetY, _position.SetOffsetY);
            bind(a.Left, b.Left, _viewportClip.SetLeft, _viewportClip.SetLeft);
            bind(a.Top, b.Top, _viewportClip.SetTop, _viewportClip.SetTop);
            bind(a.Right, b.Right, _viewportClip.SetRight, _viewportClip.SetRight);
            bind(a.Bottom, b.Bottom, _viewportClip.SetBottom, _viewportClip.SetBottom);
            bind(alphaA, alphaB, _opacity.SetOpacity, _opacity.SetOpacity, true);
            BindPreviewCorners(_viewportClip, a, b, corners, dpi ?? new DpiScale(1, 1), limited);
        }
    }

    private static void BindPreviewCorners(IDCompositionRectangleClip clip, Rect a, Rect b,
        CornerRadius corners, DpiScale dpi, BindLimitedChannel limited)
    {
        // This is Host.Preview's custom clip rule, which differs from Border's pairwise rule.
        // Each radius = min(style radius, DIP width / 2, DIP height / 2), on the same cubic.
        var x = dpi.DpiScaleX; var y = dpi.DpiScaleY;
        void X(double radius, Func<float, Result> value, Func<IDCompositionAnimation, Result> animation) =>
            limited(a.Width / 2, b.Width / 2, radius * x, value, animation,
                a.Height * x / y / 2, b.Height * x / y / 2);
        void Y(double radius, Func<float, Result> value, Func<IDCompositionAnimation, Result> animation) =>
            limited(a.Height / 2, b.Height / 2, radius * y, value, animation,
                a.Width * y / x / 2, b.Width * y / x / 2);
        X(corners.TopLeft, clip.SetTopLeftRadiusX, clip.SetTopLeftRadiusX);
        Y(corners.TopLeft, clip.SetTopLeftRadiusY, clip.SetTopLeftRadiusY);
        X(corners.TopRight, clip.SetTopRightRadiusX, clip.SetTopRightRadiusX);
        Y(corners.TopRight, clip.SetTopRightRadiusY, clip.SetTopRightRadiusY);
        X(corners.BottomRight, clip.SetBottomRightRadiusX, clip.SetBottomRightRadiusX);
        Y(corners.BottomRight, clip.SetBottomRightRadiusY, clip.SetBottomRightRadiusY);
        X(corners.BottomLeft, clip.SetBottomLeftRadiusX, clip.SetBottomLeftRadiusX);
        Y(corners.BottomLeft, clip.SetBottomLeftRadiusY, clip.SetBottomLeftRadiusY);
    }

    private sealed class SolidBackground
    {
        private readonly IDCompositionVisual _position;
        private readonly IDCompositionRectangleClip _clip;
        private readonly IDCompositionScaleTransform _scale;
        private readonly IDCompositionEffectGroup _opacity;
        private readonly double _width, _height;
        internal SolidBackground(EdgeCapsuleProxyNativeShape owner, IDCompositionVisual parent,
            EdgeCapsuleCompositionShellPlane plane)
        {
            if (plane.SourceBounds.IsEmpty || plane.Insets != default)
                throw new InvalidOperationException("A background requires a neutral solid tile without corner/border/shadow pixels.");
            // Sample only interior texels. Stretching a tile's outer antialias/transparent edge
            // would turn one boundary texel into a wide translucent strip at a large preview.
            var cropWidth = Math.Min(2, plane.SourceBounds.Width);
            var cropHeight = Math.Min(2, plane.SourceBounds.Height);
            var cropLeft = plane.SourceBounds.Left + (plane.SourceBounds.Width - cropWidth) / 2;
            var cropTop = plane.SourceBounds.Top + (plane.SourceBounds.Height - cropHeight) / 2;
            _width = cropWidth; _height = cropHeight;
            var viewport = owner.Visual(parent); _clip = owner.Clip(viewport); _opacity = owner.Effect(viewport);
            _position = owner.Visual(viewport);
            var scaled = owner.Visual(_position);
            owner._device.CreateScaleTransform(out _scale).CheckError(); owner._resources.Add(_scale);
            // DComp applies Transform after Offset on the same visual. Keep destination
            // translation on its parent so scaling the solid tile cannot scale its position.
            scaled.SetTransform(_scale).CheckError();
            var content = owner.Visual(scaled); var crop = owner.Clip(content);
            content.SetContent(owner._source).CheckError();
            SetRect(crop, new Rect(cropLeft, cropTop, _width, _height));
            content.SetOffsetX(-cropLeft).CheckError();
            content.SetOffsetY(-cropTop).CheckError();
        }

        internal void Bind(Rect a, Rect b, CornerRadius corners, DpiScale dpi,
            BindChannel bind, BindLimitedChannel limited, double alphaA = 1, double alphaB = 1)
        {
            bind(a.Left, b.Left, _position.SetOffsetX, _position.SetOffsetX);
            bind(a.Top, b.Top, _position.SetOffsetY, _position.SetOffsetY);
            bind(a.Width / _width, b.Width / _width, _scale.SetScaleX, _scale.SetScaleX);
            bind(a.Height / _height, b.Height / _height, _scale.SetScaleY, _scale.SetScaleY);
            bind(a.Left, b.Left, _clip.SetLeft, _clip.SetLeft);
            bind(a.Top, b.Top, _clip.SetTop, _clip.SetTop);
            bind(a.Right, b.Right, _clip.SetRight, _clip.SetRight);
            bind(a.Bottom, b.Bottom, _clip.SetBottom, _clip.SetBottom);
            bind(alphaA, alphaB, _opacity.SetOpacity, _opacity.SetOpacity, true);
            // WPF Border.GenerateGeometry resolves overlaps independently along each edge,
            // proportional to the adjacent radii. Zero width therefore yields zero horizontal
            // radii/fill without rejecting a normal closed segment.
            // https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Border.cs#L598-L643
            void X(double r, double adjacent, Func<float, Result> value, Func<IDCompositionAnimation, Result> animation)
            {
                var ratio = r + adjacent > 0 ? r / (r + adjacent) : 0;
                limited(a.Width * ratio, b.Width * ratio, r * dpi.DpiScaleX, value, animation);
            }
            void Y(double r, double adjacent, Func<float, Result> value, Func<IDCompositionAnimation, Result> animation)
            {
                var ratio = r + adjacent > 0 ? r / (r + adjacent) : 0;
                limited(a.Height * ratio, b.Height * ratio, r * dpi.DpiScaleY, value, animation);
            }
            X(corners.TopLeft, corners.TopRight, _clip.SetTopLeftRadiusX, _clip.SetTopLeftRadiusX);
            X(corners.TopRight, corners.TopLeft, _clip.SetTopRightRadiusX, _clip.SetTopRightRadiusX);
            X(corners.BottomLeft, corners.BottomRight, _clip.SetBottomLeftRadiusX, _clip.SetBottomLeftRadiusX);
            X(corners.BottomRight, corners.BottomLeft, _clip.SetBottomRightRadiusX, _clip.SetBottomRightRadiusX);
            Y(corners.TopLeft, corners.BottomLeft, _clip.SetTopLeftRadiusY, _clip.SetTopLeftRadiusY);
            Y(corners.BottomLeft, corners.TopLeft, _clip.SetBottomLeftRadiusY, _clip.SetBottomLeftRadiusY);
            Y(corners.TopRight, corners.BottomRight, _clip.SetTopRightRadiusY, _clip.SetTopRightRadiusY);
            Y(corners.BottomRight, corners.TopRight, _clip.SetBottomRightRadiusY, _clip.SetBottomRightRadiusY);
        }
    }

    private sealed class NineSlice
    {
        private readonly record struct Slice(int Column, int Row, double Width, double Height,
            IDCompositionVisual Position, IDCompositionScaleTransform Scale);
        private readonly List<Slice> _slices = new();
        private readonly EdgeCapsuleCompositionInsets _insets;

        internal NineSlice(EdgeCapsuleProxyNativeShape owner, IDCompositionVisual parent,
            EdgeCapsuleCompositionShellPlane plane)
        {
            // Preserve an existing wall-side texel column 1:1 even when WPF has no wall margin
            // or border. Stretching this boundary magnifies transparent atlas-neighbour sampling;
            // this copies source pixels, it does not introduce a new drawn border.
            _insets = plane.Insets with
            {
                Left = Math.Max(1, plane.Insets.Left),
                Right = Math.Max(1, plane.Insets.Right)
            };
            if (!Fits(plane.SourceBounds.Width, plane.SourceBounds.Height, _insets, strict: true))
                throw new InvalidOperationException("An atlas shell must leave a nonempty text-free middle strip.");
            var xs = new double[] { plane.SourceBounds.Left, plane.SourceBounds.Left + _insets.Left,
                plane.SourceBounds.Right - _insets.Right, plane.SourceBounds.Right };
            var ys = new double[] { plane.SourceBounds.Top, plane.SourceBounds.Top + _insets.Top,
                plane.SourceBounds.Bottom - _insets.Bottom, plane.SourceBounds.Bottom };
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                var width = xs[column + 1] - xs[column]; var height = ys[row + 1] - ys[row];
                if (width <= 0 || height <= 0) continue;
                var position = owner.Visual(parent); var scaled = owner.Visual(position);
                var content = owner.Visual(scaled);
                var clip = owner.Clip(content);
                owner._device.CreateScaleTransform(out var scale).CheckError(); owner._resources.Add(scale);
                content.SetContent(owner._source).CheckError();
                SetRect(clip, new Rect(xs[column], ys[row], width, height));
                content.SetOffsetX((float)-xs[column]).CheckError();
                content.SetOffsetY((float)-ys[row]).CheckError();
                // Offset belongs to the unscaled destination parent, not to this scale node.
                scaled.SetTransform(scale).CheckError();
                _slices.Add(new(column, row, width, height, position, scale));
            }
        }

        internal void Bind(Rect from, Rect to, BindChannel bind)
        {
            if (!Fits(from.Width, from.Height, _insets, false) || !Fits(to.Width, to.Height, _insets, false))
                throw new InvalidOperationException("A destination cannot compress the source shell's corner pixels.");
            foreach (var slice in _slices)
            {
                var a = Destination(from, slice.Column, slice.Row, _insets);
                var b = Destination(to, slice.Column, slice.Row, _insets);
                bind(a.X, b.X, slice.Position.SetOffsetX, slice.Position.SetOffsetX);
                bind(a.Y, b.Y, slice.Position.SetOffsetY, slice.Position.SetOffsetY);
                bind(a.Width / slice.Width, b.Width / slice.Width, slice.Scale.SetScaleX, slice.Scale.SetScaleX);
                bind(a.Height / slice.Height, b.Height / slice.Height, slice.Scale.SetScaleY, slice.Scale.SetScaleY);
            }
        }

        private static bool Fits(double width, double height, EdgeCapsuleCompositionInsets i, bool strict) =>
            double.IsFinite(width) && double.IsFinite(height) && i.Left >= 0 && i.Top >= 0 && i.Right >= 0 && i.Bottom >= 0 &&
            (strict ? width > i.Left + i.Right && height > i.Top + i.Bottom :
                width >= i.Left + i.Right && height >= i.Top + i.Bottom);

        private static Rect Destination(Rect r, int column, int row, EdgeCapsuleCompositionInsets i)
        {
            var xs = new[] { r.Left, r.Left + i.Left, r.Right - i.Right, r.Right };
            var ys = new[] { r.Top, r.Top + i.Top, r.Bottom - i.Bottom, r.Bottom };
            return new(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]);
        }
    }

    private static void SetRect(IDCompositionRectangleClip clip, Rect r)
    {
        clip.SetLeft((float)r.Left).CheckError(); clip.SetTop((float)r.Top).CheckError();
        clip.SetRight((float)r.Right).CheckError(); clip.SetBottom((float)r.Bottom).CheckError();
    }

    private void ReleaseTree()
    {
        // Relinquish ownership before entering native cleanup. A failed or reentrant release
        // must not retry an already attempted COM reference or strand the remaining resources.
        var root = _root; _root = null;
        var animations = _animations.ToArray(); _animations.Clear();
        var resources = _resources.ToArray(); _resources.Clear();
        if (root != null) TryCleanup(() => { _parent.RemoveVisual(root); });
        foreach (var animation in animations) TryCleanup(animation.Dispose);
        for (var i = resources.Length - 1; i >= 0; i--) TryCleanup(resources[i].Dispose);
    }

    private static void TryCleanup(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception ex)
        {
            // Cleanup must also preserve an exception already in flight from construction or
            // property submission. Even a failing diagnostic listener cannot stop retirement.
            try { Trace.TraceWarning("Edge native shape cleanup failed: {0}", ex); }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _transition = null; _lastCommands = null; _submittedPreviewAlpha = null;
        ReleaseTree();
    }
}
