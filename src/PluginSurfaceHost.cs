using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// A lease owns shells, not business state. There is no settings/form/menu interpretation here.
/// All factories and teardown run on the owning dispatcher. No static WPF resources are cached.
/// </summary>
internal sealed class PluginSurfaceHost : IPaperPluginSurfaces, IDisposable
{
    private readonly Guid _leaseId;
    private readonly string _providerId;
    private readonly Func<bool> _isActive;
    private readonly PluginUiAnchorStore _anchors;
    private readonly Func<PaperBodyTheme> _theme;
    private readonly Func<Window?> _defaultOwner;
    private readonly string? _hostPaperId;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, Surface> _surfaces = [];
    private bool _displaySubscribed;
    private bool _disposed;
    private int _teardownDepth;

    private sealed class Controls(Func<PaperBodyTheme> theme) : IPaperBodyControls
    {
        public void ApplySelectStyle(ComboBox comboBox, double fontSize) =>
            PaperSelectControl.ApplyPluginTheme(comboBox, theme(), fontSize);
    }

    private sealed class Surface(PluginSurfaceHost host, string id, bool popup) : IPaperPluginSurface
    {
        public string Id { get; } = id;
        public bool IsOpen => host.OnUi(() => host.IsCurrent(this));
        public void Close() => host.OnUi(() => host.CloseSurface(this));
        internal bool IsPopup { get; } = popup;
        internal bool Ready;
        internal Window? Window;
        internal Popup? Popup;
        internal Border? Frame;
        internal IPaperPluginSurfaceContent? Content;
        internal string? PaperId;
        internal readonly List<Action> Unsubscribe = [];
    }

    internal PluginSurfaceHost(Guid leaseId, string providerId, Func<bool> isActive,
        PluginUiAnchorStore anchors, Func<PaperBodyTheme> theme,
        Func<Window?> defaultOwner, string? hostPaperId = null)
    {
        _leaseId = leaseId;
        _providerId = providerId;
        _isActive = isActive;
        _anchors = anchors;
        _theme = theme;
        _defaultOwner = defaultOwner;
        _hostPaperId = hostPaperId;
        _dispatcher = Application.Current.Dispatcher;
    }

    public IPaperPluginSurface OpenWindow(PaperPluginWindowOptions options,
        Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> createContent) => OnUi(() =>
    {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createContent);
        Validate(options.Id, options.Width, options.Height);
        if (options.Title == null || options.Title.Length > 160 || options.Title.Any(char.IsControl))
            throw Error("invalid_surface_title", "Window titles must contain at most 160 characters without control characters.");
        if (_surfaces.TryGetValue(options.Id, out var existing))
        {
            if (existing.IsPopup || existing.Window == null || !existing.Ready)
                throw Error("surface_id_conflict", "That surface id is already in use or being opened.");
            if (existing.Window.WindowState == WindowState.Minimized) existing.Window.WindowState = WindowState.Normal;
            existing.Window.Activate();
            return (IPaperPluginSurface)existing;
        }
        if (_surfaces.Values.Count(item => !item.IsPopup) >= 8)
            throw Error("too_many_surfaces", "A plugin lease may open at most eight windows.");
        var resolved = options.Anchor == null ? null : _anchors.Resolve(_leaseId, options.Anchor);
        Window? owner = _defaultOwner();
        if (resolved != null && resolved.TryResolve(out _, out var anchorOwner)) owner = anchorOwner;
        var surface = Reserve(options.Id, false, resolved?.PaperId ?? _hostPaperId);
        try
        {
            var frame = CreateFrame(surface, options.Width, options.Height, isWindow: true);
            var window = new Window
            {
                Title = options.Title, Width = options.Width, Height = options.Height,
                Content = frame, Owner = owner, ShowInTaskbar = owner == null,
                WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.CanResize,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            surface.Window = window;
            BuildContent(surface, createContent);
            EventHandler closed = (_, _) => CloseSurface(surface, windowAlreadyClosed: true);
            window.Closed += closed;
            surface.Unsubscribe.Add(() => window.Closed -= closed);
            RoutedEventHandler loaded = (_, _) => ClampWindow(window);
            window.Loaded += loaded;
            surface.Unsubscribe.Add(() => window.Loaded -= loaded);
            DpiChangedEventHandler dpi = (_, _) => QueueClamp(surface);
            window.DpiChanged += dpi;
            surface.Unsubscribe.Add(() => window.DpiChanged -= dpi);
            if (owner != null) WatchOwner(surface, owner, closeOnMotion: false);
            ApplyTheme(surface);
            EnsureCurrent(surface);
            surface.Ready = true;
            window.Show();
            EnsureCurrent(surface);
            return (IPaperPluginSurface)surface;
        }
        catch { CloseSurface(surface); throw; }
    });

    public IPaperPluginSurface OpenPopup(PaperUiAnchor anchor, PaperPluginPopupOptions options,
        Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> createContent) => OnUi(() =>
    {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createContent);
        Validate(options.Id, options.Width, options.Height);
        var resolved = _anchors.Resolve(_leaseId, anchor);
        if (!resolved.TryResolve(out var target, out var owner))
            throw Error("anchor_unavailable", "The anchor is no longer visible.");
        if (_surfaces.TryGetValue(options.Id, out var existing) && !existing.IsPopup)
            throw Error("surface_id_conflict", "That id belongs to a window.");
        foreach (var current in _surfaces.Values.Where(item => item.IsPopup).ToArray()) CloseSurface(current);
        // Closing old plugin content can reenter the host. Recheck before adopting a new view.
        EnsureUsable();
        var surface = Reserve(options.Id, true, resolved.PaperId);
        try
        {
            var work = WindowWorkAreaHelper.WorkAreaFor(owner);
            var frame = CreateFrame(surface, Math.Min(options.Width, work.Width), Math.Min(options.Height, work.Height), false);
            var popup = new Popup
            {
                Child = frame, PlacementTarget = target, PlacementRectangle = resolved.Placement,
                Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None, Focusable = true
            };
            surface.Popup = popup;
            BuildContent(surface, createContent);
            EventHandler closed = (_, _) => CloseSurface(surface);
            popup.Closed += closed;
            surface.Unsubscribe.Add(() => popup.Closed -= closed);
            // Bubbling (not Preview) lets an inner ComboBox consume Escape to close its dropdown.
            KeyEventHandler key = (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseSurface(surface);
                    if (owner.IsActive && target.IsVisible && target.Focusable) target.Focus();
                }
            };
            frame.KeyDown += key;
            surface.Unsubscribe.Add(() => frame.KeyDown -= key);
            WatchOwner(surface, owner, closeOnMotion: true);
            EventHandler layout = (_, _) =>
            {
                if (!resolved.TryResolve(out _, out _)) CloseSurface(surface);
            };
            target.LayoutUpdated += layout;
            surface.Unsubscribe.Add(() => target.LayoutUpdated -= layout);
            RoutedEventHandler unloaded = (_, _) => CloseSurface(surface);
            target.Unloaded += unloaded;
            surface.Unsubscribe.Add(() => target.Unloaded -= unloaded);
            ApplyTheme(surface);
            EnsureCurrent(surface);
            if (!resolved.TryResolve(out _, out _)) throw Error("anchor_unavailable", "The source moved during content creation.");
            surface.Ready = true;
            popup.IsOpen = true;
            _ = _dispatcher.BeginInvoke((Action)(() =>
            {
                if (!IsCurrent(surface) || !popup.IsOpen || frame.IsKeyboardFocusWithin) return;
                // Read-only content has no tab stop. Give the shell keyboard focus rather than
                // leaving Escape and subsequent typing routed to the source paper.
                if (!frame.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
                    frame.Focus();
            }), DispatcherPriority.Input);
            return (IPaperPluginSurface)surface;
        }
        catch { CloseSurface(surface); throw; }
    });

    internal static void Validate(string id, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 ||
            id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw Error("invalid_surface_id", "Surface ids must contain 1-64 ASCII letters, digits, '.', '_' or '-'.");
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            width < 64 || height < 64 || width > 4096 || height > 4096)
            throw Error("invalid_surface_size", "Surface dimensions must be finite and between 64 and 4096 DIPs.");
    }

    private Surface Reserve(string id, bool popup, string? paperId)
    {
        if (_surfaces.ContainsKey(id)) throw Error("surface_id_conflict", "The id was opened by a reentrant callback.");
        var surface = new Surface(this, id, popup) { PaperId = paperId };
        _surfaces.Add(id, surface);
        if (!_displaySubscribed)
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _displaySubscribed = true;
        }
        return surface;
    }

    private Border CreateFrame(Surface surface, double width, double height, bool isWindow)
    {
        var frame = new Border
        {
            BorderThickness = isWindow ? new Thickness(0) : new Thickness(1),
            CornerRadius = isWindow ? new CornerRadius(0) : new CornerRadius(8),
            Padding = new Thickness(8), ClipToBounds = true, Focusable = !isWindow,
            Width = isWindow ? double.NaN : Math.Max(1, width),
            Height = isWindow ? double.NaN : Math.Max(1, height),
            UseLayoutRounding = true, SnapsToDevicePixels = true
        };
        // Focusable for read-only fallback, but never a tab stop before plugin controls.
        KeyboardNavigation.SetIsTabStop(frame, false);
        KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Cycle);
        surface.Frame = frame;
        return frame;
    }

    private void BuildContent(Surface surface, Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> factory)
    {
        var content = factory(new PaperPluginSurfaceContext(_theme(), new Controls(_theme), surface.Close))
            ?? throw Error("invalid_surface_content", "The content factory returned null.");
        // A plugin accidentally returning the very same live content must not have that
        // other surface's content disposed as a side effect of rejection.
        if (_surfaces.Values.Any(item => ReferenceEquals(item.Content, content)))
            throw Error("surface_content_in_use", "This content is already owned by another surface.");
        var borrowedView = false;
        try
        {
            EnsureCurrent(surface);
            var view = content.View ?? throw Error("invalid_surface_content", "The content has no WPF view.");
            if (view.Dispatcher != _dispatcher || view is Window)
                throw Error("invalid_surface_content", "Return a WPF content root created on the host dispatcher, not a Window.");
            if (view.Parent != null || VisualTreeHelper.GetParent(view) != null ||
                PresentationSource.FromVisual(view) != null)
            {
                borrowedView = true;
                throw Error("surface_content_in_use", "A surface requires a fresh, unparented content root.");
            }
            surface.Content = content;
            surface.Frame!.Child = view;
        }
        catch
        {
            if (surface.Content == null && !borrowedView) SafeDispose(content);
            throw;
        }
    }

    private void WatchOwner(Surface surface, Window owner, bool closeOnMotion)
    {
        EventHandler closed = (_, _) => CloseSurface(surface);
        owner.Closed += closed;
        surface.Unsubscribe.Add(() => owner.Closed -= closed);
        if (!closeOnMotion) return;
        owner.Deactivated += closed;
        owner.LocationChanged += closed;
        SizeChangedEventHandler size = (_, _) => CloseSurface(surface);
        DependencyPropertyChangedEventHandler visible = (_, _) => { if (!owner.IsVisible) CloseSurface(surface); };
        DpiChangedEventHandler dpi = (_, _) => CloseSurface(surface);
        owner.SizeChanged += size;
        owner.IsVisibleChanged += visible;
        owner.DpiChanged += dpi;
        surface.Unsubscribe.Add(() =>
        {
            owner.Deactivated -= closed; owner.LocationChanged -= closed;
            owner.SizeChanged -= size; owner.IsVisibleChanged -= visible; owner.DpiChanged -= dpi;
        });
    }

    public void Close(string id) => OnUi(() =>
    {
        EnsureUsable();
        if (_surfaces.TryGetValue(id, out var surface)) CloseSurface(surface);
    });
    public void CloseAll() => OnUi(() =>
    {
        EnsureUsable();
        foreach (var surface in _surfaces.Values.ToArray()) CloseSurface(surface);
    });
    internal void CloseForPaper(string paperId) => OnUi(() =>
    {
        foreach (var surface in _surfaces.Values.Where(item => item.PaperId == paperId).ToArray()) CloseSurface(surface);
    });

    private void CloseSurface(Surface surface, bool windowAlreadyClosed = false)
    {
        if (!IsCurrent(surface)) return;
        _surfaces.Remove(surface.Id); // Revoke before any plugin callbacks or native close events.
        _teardownDepth++;
        try
        {
            foreach (var unsubscribe in surface.Unsubscribe) unsubscribe();
            surface.Unsubscribe.Clear();
            if (surface.Popup != null) { surface.Popup.IsOpen = false; surface.Popup.Child = null; }
            if (surface.Frame != null) surface.Frame.Child = null;
            if (surface.Window != null)
            {
                surface.Window.Content = null;
                if (!windowAlreadyClosed) surface.Window.Close();
            }
        }
        finally
        {
            surface.Popup = null; surface.Window = null; surface.Frame = null;
            if (_surfaces.Count == 0 && _displaySubscribed)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _displaySubscribed = false;
            }
            var content = surface.Content;
            surface.Content = null;
            try { if (content != null) SafeDispose(content); }
            finally { _teardownDepth--; }
        }
    }

    internal void RefreshTheme() => OnUi(() =>
    {
        foreach (var surface in _surfaces.Values.ToArray())
        {
            try { ApplyTheme(surface); }
            catch (Exception ex)
            {
                Trace.TraceWarning("Plugin surface theme callback failed: {0}", ex.GetBaseException());
                CloseSurface(surface);
            }
        }
    });
    private void ApplyTheme(Surface surface)
    {
        if (!IsCurrent(surface) || surface.Frame == null) return;
        var theme = _theme();
        static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        surface.Frame.Background = Brush(theme.PaperColor);
        surface.Frame.BorderBrush = Brush(theme.BorderColor);
        TextElement.SetForeground(surface.Frame, Brush(theme.TextColor));
        TextElement.SetFontFamily(surface.Frame, new FontFamily(theme.FontFamily));
        TextElement.SetFontSize(surface.Frame, 12 * theme.FontScale);
        if (surface.Window != null) surface.Window.Background = surface.Frame.Background;
        surface.Content?.OnThemeChanged(theme);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.BeginInvoke((Action)(() =>
        {
            if (_disposed) return;
            foreach (var surface in _surfaces.Values.ToArray())
                if (surface.IsPopup) CloseSurface(surface); else QueueClamp(surface);
        }));
    }
    private void QueueClamp(Surface surface) => _dispatcher.BeginInvoke((Action)(() =>
    {
        if (IsCurrent(surface) && surface.Window != null) ClampWindow(surface.Window);
    }), DispatcherPriority.Loaded);

    private static void ClampWindow(Window window)
    {
        if (!window.IsVisible || window.WindowState != WindowState.Normal) return;
        var area = WindowWorkAreaHelper.WorkAreaFor(window);
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return;
        window.Width = Math.Min(window.ActualWidth, area.Width);
        window.Height = Math.Min(window.ActualHeight, area.Height);
        window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - window.Width));
        window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
    }
    private bool IsCurrent(Surface surface) =>
        _surfaces.TryGetValue(surface.Id, out var current) && ReferenceEquals(current, surface);
    private void EnsureCurrent(Surface surface)
    {
        EnsureUsable();
        if (!IsCurrent(surface)) throw Error("surface_closed", "The surface closed while its content was being created.");
    }
    private void EnsureUsable()
    {
        if (_disposed || !_isActive()) throw Error("surface_owner_closed", "The owning plugin lease has ended.");
        if (_teardownDepth > 0) throw Error("surface_busy", "Do not reopen a surface from its disposal callback.");
    }
    private static PaperTodoPluginException Error(string code, string message) => new(code, message);
    private void SafeDispose(IPaperPluginSurfaceContent content)
    {
        try { content.Dispose(); }
        catch (Exception ex) { Trace.TraceWarning("Plugin surface dispose failed. Provider={0}; Error={1}", _providerId, ex.GetBaseException()); }
    }
    private T OnUi<T>(Func<T> callback) => _dispatcher.CheckAccess() ? callback() : _dispatcher.Invoke(callback);
    private void OnUi(Action callback)
    {
        if (_dispatcher.CheckAccess()) callback(); else if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(callback);
    }
    public void Dispose() => OnUi(() =>
    {
        if (_disposed) return;
        _disposed = true;
        _anchors.RemoveOwner(_leaseId);
        foreach (var surface in _surfaces.Values.ToArray()) CloseSurface(surface);
    });
}
