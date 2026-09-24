using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private BackgroundSession? _background;
    private MaterialSurfaceHost? _materialHost;
    private bool _menuRendered;
    internal BackgroundSession? BackgroundSessionState => _background;
    internal bool SuppressStaticBackgroundForOpening { get; private set; }
    internal bool FirstMenuRenderUsedBackground { get; private set; }
    internal int MenuFallbackRenderCount { get; private set; }
    internal bool HasBackgroundCapture => _background?.HasBackgroundCapture == true;
    internal bool IsBackgroundActive => _background?.IsBackgroundActive == true;
    internal int BackgroundFrameCount => _background?.BackgroundFrameCount ?? 0;
    internal string? BackgroundFailure => _background?.BackgroundFailure;
    internal int BackgroundProjectionCount => _background?.BackgroundProjectionCount ?? 0;
    internal int BackgroundSceneDrawCount => _background?.BackgroundSceneDrawCount ?? 0;
    internal System.Collections.Generic.IReadOnlyList<UIElement> MaterialOpacityOwners =>
        _materialHost?.OpacityOwners ?? Array.Empty<UIElement>();
    internal bool HasMaterialHostSubscription => _materialHost?.IsObserving == true;
    private ContainerVisual? BackgroundVisual => _background?.Visual;
    private bool FullAuxiliaryMaterial =>
        AppController.Current?.State.MatchAuxiliaryMaterialStrength == true;
    private bool RequestsSampledBackground => !IsOutline && !UseLightweightMaterial &&
        !SuppressStaticBackgroundForOpening && IsAuxiliary && FullAuxiliaryMaterial &&
        PaperSkins.UsesSampledAuxiliary(Skin);
    private bool IsMaterialHostVisible => _materialHost?.IsVisible == true;
    private bool HasAuxiliaryTransmission => IsAuxiliary && FullAuxiliaryMaterial &&
        Skin == PaperSkins.Aero && !_highContrast && DwmMicaApi.Instance.EffectsEnabled &&
        (IsMenu || IsLoaded && IsVisible && IsMaterialHostVisible);

    protected override int VisualChildrenCount => base.VisualChildrenCount + (BackgroundVisual == null ? 0 : 1);
    protected override Visual GetVisualChild(int index) => BackgroundVisual is { } visual
        ? index == 0 ? visual : base.GetVisualChild(index - 1)
        : base.GetVisualChild(index);

    private void AttachBackgroundVisual(Visual visual) { AddVisualChild(visual); InvalidateVisual(); }
    private void DetachBackgroundVisual(Visual visual) { RemoveVisualChild(visual); InvalidateVisual(); }

    internal void PrepareMenuBackground(DesktopBackgroundCapture.Frame? frame, bool failed) =>
        (_background ??= new BackgroundSession(this)).PrepareMenuBackground(frame, failed);

    private void PresentPreparedMenuBackground() => _background?.PresentPreparedMenuBackground();
    internal IDisposable FreezeBackgroundForEvidence() =>
        (_background ??= new BackgroundSession(this)).FreezeBackgroundForEvidence();

    internal void RefreshBackground()
    {
        var sampled = RequestsSampledBackground;
        var observe = IsLoaded && IsVisible && !IsOutline && !UseLightweightMaterial && !_highContrast &&
            (sampled || Skin == PaperSkins.Aero && (IsAuxiliary || _animateReflection));
        if (observe)
        {
            _materialHost ??= new MaterialSurfaceHost(this, OnMaterialHostChanged);
            _materialHost.Observe(PresentationSource.FromVisual(this) as HwndSource);
        }
        else
        {
            _materialHost?.Dispose();
            _materialHost = null;
        }
        if (sampled && IsLoaded) _background ??= new BackgroundSession(this);
        _background?.RefreshBackground();
        UpdateAeroReflection();
    }

    private void OnMaterialHostChanged(MaterialHostChange change)
    {
        if ((change & MaterialHostChange.Unavailable) != 0)
        {
            ReleaseMaterialResources();
            return;
        }
        if ((change & MaterialHostChange.Geometry) != 0)
            _background?.GeometryChanged();
        if ((change & MaterialHostChange.Translation) != 0)
        {
            // Static snapshots are screen-anchored. Movement only changes the crop; a fresh local
            // snapshot is taken after an explicit drag ends, never on WM_WINDOWPOSCHANGED.
            _background?.TranslationChanged();
            UpdateAeroReflection();
            if (change == MaterialHostChange.Translation) return;
        }
        if ((change & MaterialHostChange.Environment) != 0)
        {
            _background?.ResetFailure();
            RefreshSkin();
        }
        else RefreshBackground();
        InvalidateVisual(); // e.g. Aero becomes opaque when an ancestor starts fading.
    }

    internal void UseDragBackground(DesktopBackgroundCapture.Snapshot snapshot) =>
        (_background ??= new BackgroundSession(this)).UseDragSnapshot(snapshot);

    internal void EndDragBackground() => _background?.EndDragSnapshot();

    private void ReleaseMaterialResources()
    {
        _background?.Stop();
        _materialHost?.Dispose();
        _materialHost = null;
        UpdateAeroReflection();
    }
}
