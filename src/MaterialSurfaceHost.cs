using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

[Flags]
internal enum MaterialHostChange { None = 0, Geometry = 1, Visibility = 2, Environment = 4, Unavailable = 8, Translation = 16 }

// Observes the surface's own HwndSource (including disconnected popup roots), never
// Window.GetWindow's owner HWND. Does not move, resize, hide or intercept input.
internal sealed class MaterialSurfaceHost : IDisposable
{
    private readonly FrameworkElement _surface;
    private readonly Action<MaterialHostChange> _changed;
    private readonly List<UIElement> _opacityOwners = new();
    private HwndSource? _source;
    private DispatcherOperation? _refresh;
    private MaterialHostChange _pending;
    private bool _disposed;
    private static readonly DependencyPropertyDescriptor OpacityDescriptor =
        DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement))!;

    internal MaterialSurfaceHost(FrameworkElement surface, Action<MaterialHostChange> changed) =>
        (_surface, _changed) = (surface, changed);
    internal bool IsObserving => _source is { IsDisposed: false };
    internal bool IsVisible => IsObserving && DesktopBackgroundCapture.IsVisible(_source!.Handle) &&
        _opacityOwners.TrueForAll(element => element.Opacity >= .999 && element.IsVisible);
    internal IReadOnlyList<UIElement> OpacityOwners => _opacityOwners;

    internal void Observe(HwndSource? source)
    {
        if (_disposed || ReferenceEquals(_source, source)) return;
        Detach();
        _source = source;
        if (source == null || source.IsDisposed) return;
        source.AddHook(WindowMessage);
        source.Disposed += OnSourceDisposed;
        for (DependencyObject? node = _surface; node is Visual; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not UIElement element) continue;
            _opacityOwners.Add(element);
            OpacityDescriptor.AddValueChanged(element, OnOpacityChanged);
        }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0002 /* WM_DESTROY */)
        {
            Detach();
            _changed(MaterialHostChange.Unavailable);
        }
        else if (message == 0x0047 /* WM_WINDOWPOSCHANGED */)
        {
            // WINDOWPOS also reports Z-order-only changes. A translation does not change
            // the local capture region, DPI, opacity or material recipe. Read the native
            // flags; WPF Left/Top are not synchronized yet at this message boundary.
            Queue(lParam == IntPtr.Zero ? MaterialHostChange.Geometry | MaterialHostChange.Visibility
                : ClassifyWindowPosition(Marshal.PtrToStructure<WindowPosition>(lParam).Flags));
        }
        else if (message == 0x0018 /* WM_SHOWWINDOW */) Queue(MaterialHostChange.Visibility);
        else if (message == 0x02e0 /* WM_DPICHANGED */)
            Queue(MaterialHostChange.Geometry | MaterialHostChange.Visibility);
        else if (message is 0x007e /* DISPLAYCHANGE */ or 0x031e /* DWMCOMPOSITIONCHANGED */ or
                 0x001a /* SETTINGCHANGE */ or 0x031a /* THEMECHANGED */ or 0x0320 /* DWMCOLORIZATIONCOLORCHANGED */)
        {
            DwmMicaApi.Instance.InvalidateEnvironment();
            Queue(MaterialHostChange.Environment | MaterialHostChange.Geometry);
        }
        return IntPtr.Zero;
    }

    internal static MaterialHostChange ClassifyWindowPosition(uint flags)
    {
        var change = MaterialHostChange.None;
        if ((flags & 0x0001 /* SWP_NOSIZE */) == 0) change |= MaterialHostChange.Geometry;
        if ((flags & 0x0002 /* SWP_NOMOVE */) == 0) change |= MaterialHostChange.Translation;
        if ((flags & 0x00c0 /* SWP_SHOWWINDOW | SWP_HIDEWINDOW */) != 0) change |= MaterialHostChange.Visibility;
        // Frame style changes can alter client-to-window offsets even at identical size.
        if ((flags & 0x0020 /* SWP_FRAMECHANGED */) != 0) change |= MaterialHostChange.Geometry;
        return change;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        internal IntPtr Hwnd, InsertAfter;
        internal int X, Y, Width, Height;
        internal uint Flags;
    }

    private void Queue(MaterialHostChange change)
    {
        if (change == MaterialHostChange.None) return;
        _pending |= change;
        if (_refresh != null || _disposed || _surface.Dispatcher.HasShutdownStarted) return;
        // SHOWWINDOW/WindowChrome/DPI changes settle before reading WPF ancestry/opacity.
        // One pending operation per source, rather than independent paint/lifecycle queues.
        _refresh = _surface.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _refresh = null;
            var pending = _pending;
            _pending = MaterialHostChange.None;
            if (!_disposed && _surface.IsLoaded) _changed(pending);
        }));
    }

    private void OnOpacityChanged(object? sender, EventArgs e) => Queue(MaterialHostChange.Visibility);
    private void OnSourceDisposed(object? sender, EventArgs e)
    {
        Detach();
        if (!_disposed) _changed(MaterialHostChange.Unavailable);
    }
    private void Detach()
    {
        _refresh?.Abort(); _refresh = null; _pending = MaterialHostChange.None;
        foreach (var element in _opacityOwners) OpacityDescriptor.RemoveValueChanged(element, OnOpacityChanged);
        _opacityOwners.Clear();
        if (_source != null)
        {
            _source.Disposed -= OnSourceDisposed;
            if (!_source.IsDisposed) _source.RemoveHook(WindowMessage);
            _source = null;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }

    // Preserve fractional local transforms: PointToScreen alone rounds to a Win32 POINT.
    internal static bool TryGetScreenOrigin(Visual surface, HwndSource source, out Point origin)
    {
        origin = default;
        if (source.IsDisposed || source.RootVisual is not Visual root || source.CompositionTarget is not { } target)
            return false;
        var local = ReferenceEquals(surface, root) ? new Point() : surface.TransformToAncestor(root).Transform(new Point());
        if (VisualTreeHelper.GetTransform(root) is { } transform) local = transform.Transform(local);
        local += VisualTreeHelper.GetOffset(root);
        local = target.TransformToDevice.Transform(local);
        var client = new NativePoint();
        if (!ClientToScreen(source.Handle, ref client)) return false;
        origin = new Point(client.X + local.X, client.Y + local.Y);
        return double.IsFinite(origin.X) && double.IsFinite(origin.Y);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}
