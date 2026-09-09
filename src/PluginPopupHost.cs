using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>One transient, activating window; no source tracking, id registry or anchor lifetime.</summary>
internal sealed class PluginPopupHost(Func<bool> isActive, Func<PaperBodyTheme>? theme = null)
    : IPaperPluginPopups, IDisposable
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly Func<PaperBodyTheme> _theme = theme ?? CurrentTheme;
    private Popup? _current;
    private bool _disposed;
    private bool _closing;

    private sealed class Controls(Func<PaperBodyTheme> current) : IPaperBodyControls
    {
        public void ApplySelectStyle(ComboBox comboBox, double fontSize) =>
            PaperSelectControl.ApplyPluginTheme(comboBox, current(), fontSize);
    }

    private sealed class Popup(PluginPopupHost host) : IPaperPluginPopup
    {
        private PaperPluginPopupFailure? _openFailure;
        internal Window? Window;
        internal Border? Frame;
        internal IPaperPluginPopupContent? Content;
        public bool IsOpen => host.OnUi(() => ReferenceEquals(host._current, this));
        public PaperPluginPopupFailure? OpenFailure => host.OnUi(() => _openFailure);
        public event Action<PaperPluginPopupFailure>? OpenFailed;
        public void Close() => host.OnUi(() => host.ClosePopup(this));

        internal void FailOpen(Exception exception)
        {
            if (_openFailure != null)
            {
                return;
            }

            var root = exception.GetBaseException();
            _openFailure = root is PaperTodoPluginException plugin
                ? new PaperPluginPopupFailure(plugin.Code, plugin.Message)
                : new PaperPluginPopupFailure(
                    "popup_open_failed",
                    "The popup could not be opened.");
            var failure = _openFailure;

            // Revoke and dispose the shell before notifying plugin code. A callback is therefore
            // free to inspect IsOpen/OpenFailure or open a replacement without reentering teardown.
            host.ClosePopup(this);
            try
            {
                OpenFailed?.Invoke(failure);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Plugin popup open-failure callback failed: {0}", ex.GetBaseException());
            }
        }
    }

    internal static PaperPopupPosition CapturePosition()
    {
        if (!WindowNative.TryGetCursorScreenPosition(out var point))
            throw Error("popup_position_unavailable", "The click position is unavailable.");
        return new(point.X, point.Y);
    }

    public IPaperPluginPopup Open(PaperPopupPosition position, PaperPluginPopupOptions options,
        Func<PaperPluginPopupContext, IPaperPluginPopupContent> createContent) => OnUi(() =>
    {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(createContent);
        Validate(position, options);
        Close();
        EnsureUsable();
        var popup = new Popup(this);
        _current = popup;
        try
        {
            var frame = new Border
            {
                BorderThickness = new Thickness(1), Padding = new Thickness(8),
                ClipToBounds = true, Focusable = true,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            KeyboardNavigation.SetIsTabStop(frame, false);
            KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Cycle);
            popup.Frame = frame;
            var point = WindowWorkAreaHelper.DeviceScreenPointToDip(new(position.X, position.Y));
            var window = new Window
            {
                Content = frame, Width = options.Width, Height = options.Height,
                Left = point.X, Top = point.Y, WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = true, Topmost = true,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            popup.Window = window;
            window.Deactivated += (_, _) => ClosePopup(popup);
            window.Closed += (_, _) => ClosePopup(popup, alreadyClosed: true);
            var content = createContent(new(_theme(), new Controls(_theme), popup.Close))
                ?? throw Error("invalid_popup_content", "The content factory returned null.");
            var borrowed = false;
            try
            {
                var view = content.View ?? throw Error("invalid_popup_content", "Content has no view.");
                // Do not inspect or dispose UI owned by another dispatcher.
                borrowed = view.Dispatcher != _dispatcher;
                if (borrowed) throw Error("invalid_popup_content", "Create the content on the host UI dispatcher.");
                // Reject borrowed views without disposing another live interface's content.
                borrowed = view.Parent != null || VisualTreeHelper.GetParent(view) != null ||
                    PresentationSource.FromVisual(view) != null;
                if (borrowed) throw Error("popup_content_in_use", "Return a fresh, unparented view.");
                if (view is Window)
                    throw Error("invalid_popup_content", "Return a content root, not a Window.");
                if (!ReferenceEquals(_current, popup))
                {
                    SafeDispose(content);
                    return (IPaperPluginPopup)popup; // Close was requested during the factory.
                }
                popup.Content = content;
                frame.Child = view;
            }
            catch
            {
                if (popup.Content == null && !borrowed) SafeDispose(content);
                throw;
            }
            RefreshTheme();
            // ContextMenu finishes dismissal/activation before this turn. No timer or source
            // subscription is required, and the saved position is never resampled here.
            _dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!ReferenceEquals(_current, popup)) return;
                try
                {
                    EnsureUsable();
                    _ = new WindowInteropHelper(window).EnsureHandle();
                    if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                        new(position.X, position.Y), out var monitor) ||
                        !WindowNative.TrySetWindowDeviceBounds(window, Place(position, options, monitor)))
                        throw Error("popup_position_unavailable", "The popup work area is unavailable.");
                    window.Show();
                    // Do not leave a topmost, unfocused popup behind when Windows rejects activation.
                    if (!window.IsActive && !window.Activate())
                        throw Error("popup_activation_failed", "The popup could not be activated.");
                    if (!frame.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) frame.Focus();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Plugin popup failed to open: {0}", ex.GetBaseException());
                    popup.FailOpen(ex);
                }
            }));
            return (IPaperPluginPopup)popup;
        }
        catch { ClosePopup(popup); throw; }
    });

    internal static void Validate(PaperPopupPosition position, PaperPluginPopupOptions options)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
            position.X < int.MinValue || position.X > int.MaxValue ||
            position.Y < int.MinValue || position.Y > int.MaxValue)
            throw Error("invalid_popup_position", "Position must be finite screen-pixel coordinates.");
        if (!double.IsFinite(options.Width) || !double.IsFinite(options.Height) ||
            options.Width < 64 || options.Height < 64 || options.Width > 4096 || options.Height > 4096)
            throw Error("invalid_popup_size", "Popup dimensions must be between 64 and 4096 DIPs.");
    }

    internal static DeviceScreenRect Place(PaperPopupPosition position, PaperPluginPopupOptions options,
        MonitorGeometry monitor)
    {
        var work = monitor.WorkArea;
        var width = Math.Min(work.Width, (int)Math.Ceiling(options.Width * monitor.DpiScaleX));
        var height = Math.Min(work.Height, (int)Math.Ceiling(options.Height * monitor.DpiScaleY));
        // Flip at the right/bottom edges; always keep the entire shell inside the work area.
        var x = position.X + 8;
        var y = position.Y + 8;
        if (x + width > work.Right) x = position.X - width - 8;
        if (y + height > work.Bottom) y = position.Y - height - 8;
        var left = (int)Math.Clamp(x, work.Left, work.Right - width);
        var top = (int)Math.Clamp(y, work.Top, work.Bottom - height);
        return new(left, top, left + width, top + height);
    }

    public void Close() => OnUi(() => { if (_current is { } current) ClosePopup(current); });

    private void ClosePopup(Popup popup, bool alreadyClosed = false)
    {
        if (!ReferenceEquals(_current, popup)) return;
        _current = null; // Revoke before native close events or plugin disposal reenter.
        _closing = true;
        try
        {
            if (popup.Frame != null) popup.Frame.Child = null;
            if (popup.Window is { } window)
            {
                window.Content = null;
                if (!alreadyClosed) window.Close();
            }
        }
        finally
        {
            var content = popup.Content;
            popup.Content = null; popup.Frame = null; popup.Window = null;
            if (content != null) SafeDispose(content);
            _closing = false;
        }
    }

    internal void RefreshTheme() => OnUi(() =>
    {
        if (_current is not { Frame: { } frame } popup) return;
        try
        {
            var current = _theme();
            static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            frame.Background = Brush(current.PaperColor);
            frame.BorderBrush = Brush(current.BorderColor);
            TextElement.SetForeground(frame, Brush(current.TextColor));
            TextElement.SetFontFamily(frame, new FontFamily(current.FontFamily));
            TextElement.SetFontSize(frame, 12 * current.FontScale);
            popup.Window!.Background = frame.Background;
            popup.Content?.OnThemeChanged(current);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Plugin popup theme failed: {0}", ex.GetBaseException());
            if (popup.Window?.IsVisible == true)
            {
                ClosePopup(popup);
            }
            else
            {
                popup.FailOpen(ex);
            }
        }
    });

    internal static PaperBodyTheme CurrentTheme()
    {
        static string Hex(Brush brush, string fallback) => brush is SolidColorBrush solid
            ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" : fallback;
        return new(Theme.IsDark, Hex(Theme.PaperBrush, "#FFF8E6"), Hex(Theme.TextBrush, "#202020"),
            Hex(Theme.WeakTextBrush, "#707070"), Hex(Theme.ActiveBrush, "#B07A31"),
            Hex(Theme.PaperBorderBrush, "#807050"), AppTypography.UiFontFamily.Source, AppTypography.ScaleFactor);
    }

    private void EnsureUsable()
    {
        if (_disposed || _closing || !isActive())
            throw Error("popup_host_closed", "The popup owner is no longer available.");
    }
    private T OnUi<T>(Func<T> action) => _dispatcher.CheckAccess() ? action() : _dispatcher.Invoke(action);
    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action(); else _dispatcher.Invoke(action);
    }
    private static PaperTodoPluginException Error(string code, string message) => new(code, message);
    private static void SafeDispose(IDisposable content)
    {
        try { content.Dispose(); }
        catch (Exception ex) { Trace.TraceWarning("Plugin popup disposal failed: {0}", ex.GetBaseException()); }
    }
    public void Dispose() => OnUi(() => { if (_disposed) return; _disposed = true; Close(); });
}
