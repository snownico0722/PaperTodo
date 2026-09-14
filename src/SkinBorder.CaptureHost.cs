using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed partial class SkinBorder
{
    private HwndSource? _materialSource;
    private readonly List<UIElement> _opacityOwners = new();
    private bool _hostRefreshQueued, _materialEnvironmentDirty;
    private static readonly DependencyPropertyDescriptor OpacityDescriptor =
        DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement))!;

    // Popup menus have their OWN HWND. Window.GetWindow may return their owner (or null)
    // and must never be used to exclude/sample the wrong surface. No new HWND or input layer.
    private void ObserveMaterialHost(HwndSource? source)
    {
        if (ReferenceEquals(source, _materialSource)) return;
        DetachMaterialHost();
        _materialSource = source;
        if (source == null || source.IsDisposed) return;
        source.AddHook(MaterialHostMessage);
        source.Disposed += OnMaterialSourceDisposed;
        for (DependencyObject? node = this; node is Visual; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not UIElement element) continue;
            _opacityOwners.Add(element);
            OpacityDescriptor.AddValueChanged(element, OnMaterialOpacityChanged);
        }
    }
    private bool IsMaterialHostVisible => _materialSource is { IsDisposed: false } &&
        DesktopLensCapture.IsVisible(_materialSource.Handle) &&
        _opacityOwners.TrueForAll(element => element.Opacity >= .999 && element.IsVisible);

    private IntPtr MaterialHostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0002 /* WM_DESTROY */) { StopRefraction(); return IntPtr.Zero; }
        if (message == 0x0047 /* WM_WINDOWPOSCHANGED */ && lParam != IntPtr.Zero &&
            _capture != null && _scene?.Layout != null && Skin == PaperSkins.LiquidGlass)
        {
            var position = Marshal.PtrToStructure<MaterialWindowPos>(lParam);
            // Native caption dragging moves the HWND outside WPF's Rendering cadence.
            // Feed the matching crop into this window-position transaction, rather than
            // leaving its previous crop attached until a later dispatcher/render pass.
            // Resize/show/hide/frame changes still use the full lifecycle below.
            const uint sizeOrVisibility = 0x0020 /* FRAMECHANGED */ | 0x0040 /* SHOWWINDOW */ | 0x0080 /* HIDEWINDOW */;
            if ((position.Flags & 0x0001 /* NOSIZE */) != 0 && (position.Flags & sizeOrVisibility) == 0)
            {
                if ((position.Flags & 0x0002 /* NOMOVE */) == 0)
                {
                    _capture.MarkMoving();
                    _cropDirty = true;
                    try { UpdateRefractionCrop(); if (_cropDirty) RequestRefractionRender(); }
                    catch (Exception ex) when (ex is InvalidOperationException or ExternalException or ArgumentException)
                    { FailRefraction(ex); }
                }
                // Do not consume the message: HwndTarget must still synchronize its
                // render target, and DefWindowProc must still send WM_MOVE/WM_SIZE.
                return IntPtr.Zero;
            }
        }
        if (message is 0x0047 /* WINDOWPOSCHANGED */ or 0x0018 /* SHOWWINDOW */ or 0x02e0 /* DPICHANGED */
            or 0x007e /* DISPLAYCHANGE */ or 0x031e /* DWMCOMPOSITIONCHANGED */ or 0x001a /* SETTINGCHANGE */)
        {
            if (message is 0x007e or 0x031e or 0x001a)
            { _refractionFailed = false; _materialEnvironmentDirty = true; }
            _cropDirty = true;
            _capture?.MarkMoving();
            RequestRefractionRender(); // reproject this move, not only the next captured frame
            if (!_hostRefreshQueued && !Dispatcher.HasShutdownStarted)
            {
                _hostRefreshQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    _hostRefreshQueued = false;
                    if (IsLoaded)
                    {
                        if (_materialEnvironmentDirty) { _materialEnvironmentDirty = false; RefreshSkin(); }
                        else RefreshRefraction();
                        if (Skin == PaperSkins.Aero) OnAeroLocation(null, EventArgs.Empty);
                    }
                }));
            }
        }
        return IntPtr.Zero;
    }
    // PointToScreen rounds through a Win32 POINT. Keep the local visual's fractional
    // offset separate from the integer client origin so 125/150% DPI and translated
    // shells do not change sampling phase when their bounds are re-evaluated.
    private bool TryGetMaterialScreenOrigin(HwndSource source, out Point origin)
    {
        origin = default;
        if (source.IsDisposed || source.RootVisual is not Visual root || source.CompositionTarget is not { } target)
            return false;
        var local = ReferenceEquals(this, root) ? new Point() : TransformToAncestor(root).Transform(new Point());
        if (VisualTreeHelper.GetTransform(root) is { } transform) local = transform.Transform(local);
        local += VisualTreeHelper.GetOffset(root);
        local = target.TransformToDevice.Transform(local);
        var client = new MaterialPoint();
        if (!MaterialClientToScreen(source.Handle, ref client)) return false;
        origin = new Point(client.X + local.X, client.Y + local.Y);
        return double.IsFinite(origin.X) && double.IsFinite(origin.Y);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialWindowPos
    {
        internal IntPtr Hwnd, InsertAfter;
        internal int X, Y, Width, Height;
        internal uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialPoint { internal int X, Y; }
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    private static extern bool MaterialClientToScreen(IntPtr hwnd, ref MaterialPoint point);

    private void OnMaterialOpacityChanged(object? sender, EventArgs e) => RefreshRefraction();
    private void OnMaterialSourceDisposed(object? sender, EventArgs e)
    { StopRefraction(); DetachMaterialHost(); }
    private void DetachMaterialHost()
    {
        foreach (var element in _opacityOwners) OpacityDescriptor.RemoveValueChanged(element, OnMaterialOpacityChanged);
        _opacityOwners.Clear();
        if (_materialSource != null)
        {
            _materialSource.Disposed -= OnMaterialSourceDisposed;
            if (!_materialSource.IsDisposed) _materialSource.RemoveHook(MaterialHostMessage);
            _materialSource = null;
        }
    }

    // Aero needs only the existing layered window's alpha, not a capture worker.
    // Other native materials use a bounded software backdrop on layered auxiliaries;
    // the main window retains its original DWM Mica/Acrylic implementation.
    private bool HasAuxiliaryTransmission => IsAuxiliary && Skin == PaperSkins.Aero &&
        IsLoaded && IsVisible && !_highContrast && IsMaterialHostVisible &&
        DwmMicaApi.Instance.CompositionEnabled && DwmMicaApi.Instance.TransparencyEnabled;
    private bool RequestsLiveBackground => !UseLightweightMaterial && !SuppressLiveBackgroundForOpening && (Skin == PaperSkins.LiquidGlass ||
        IsAuxiliary && Skin is PaperSkins.Mica or PaperSkins.Acrylic or PaperSkins.ClearAcrylic or PaperSkins.TracingPaper);
}
