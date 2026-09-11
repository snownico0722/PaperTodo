using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

// Keep WPF's actual ContextMenu/Popup placement, focus, capture and dismissal. The only
// deferred step is the first open request: prepare a local scene BEFORE that HWND exists.
// No hidden warm-up window, foreground fade, UI-thread readback or persistent screen cache.
internal sealed class MaterialContextMenu : ContextMenu
{
    private MaterialMenuOpening? _opening;
    internal bool IsOpening => _opening?.IsPending == true;
    static MaterialContextMenu() => IsOpenProperty.OverrideMetadata(typeof(MaterialContextMenu),
        new FrameworkPropertyMetadata(false, null, CoerceOpen));
    private static object CoerceOpen(DependencyObject d, object value)
    {
        var menu = (MaterialContextMenu)d;
        menu._opening ??= new MaterialMenuOpening(menu, IsOpenProperty, () => menu,
            () => menu.PlacementTarget, () => menu.Placement);
        return menu._opening.Coerce((bool)value);
    }
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (LogicalTreeHelper.GetParent(this) is Popup popup)
        {
            if (MaterialMenuOpening.NeedsBackground) popup.SetCurrentValue(Popup.PopupAnimationProperty, PopupAnimation.None);
            else popup.SetResourceReference(Popup.PopupAnimationProperty, SystemParameters.MenuPopupAnimationKey);
        }
    }
}

internal sealed class MaterialSubmenuPopup : Popup
{
    public MaterialSubmenuPopup() { }
    private MaterialMenuOpening? _opening;
    private static readonly CoerceValueCallback? BaseOpenCoercion = IsOpenProperty.GetMetadata(typeof(Popup)).CoerceValueCallback;
    static MaterialSubmenuPopup() => IsOpenProperty.OverrideMetadata(typeof(MaterialSubmenuPopup),
        new FrameworkPropertyMetadata(false, null, CoerceOpen));
    private static object CoerceOpen(DependencyObject d, object value)
    {
        var popup = (MaterialSubmenuPopup)d;
        // Retain Popup's own disconnected-tree/Loaded guard.
        var requested = (bool)(BaseOpenCoercion?.Invoke(d, value) ?? value);
        popup.SetCurrentValue(PopupAnimationProperty, MaterialMenuOpening.NeedsBackground ? PopupAnimation.None : PopupAnimation.Fade);
        popup._opening ??= new MaterialMenuOpening(popup, IsOpenProperty, () => popup.Child as FrameworkElement,
            () => popup.PlacementTarget, () => popup.Placement);
        return popup._opening.Coerce(requested);
    }
}

internal sealed class MaterialMenuOpening
{
    private readonly FrameworkElement _owner;
    private readonly DependencyProperty _isOpen;
    private readonly Func<FrameworkElement?> _content;
    private readonly Func<UIElement?> _target;
    private readonly Func<PlacementMode> _placement;
    private CancellationTokenSource? _cancel;
    private FrameworkElement? _anchor;
    private bool _pending, _ready;
    private int _generation;
    internal bool IsPending => _pending;

    internal static bool NeedsBackground => AppController.Current?.State.LiquidGlassRefraction != false &&
        PaperSkins.UsesNativeBackdrop(Theme.Skin) && Theme.Skin != PaperSkins.Aero &&
        !SystemParameters.HighContrast && DwmMicaApi.Instance.CompositionEnabled && DwmMicaApi.Instance.TransparencyEnabled;

    internal MaterialMenuOpening(FrameworkElement owner, DependencyProperty isOpen, Func<FrameworkElement?> content,
        Func<UIElement?> target, Func<PlacementMode> placement)
    {
        _owner = owner; _isOpen = isOpen; _content = content; _target = target; _placement = placement;
        owner.Unloaded += (_, _) => Cancel();
    }

    internal bool Coerce(bool requested)
    {
        if (!requested) { Cancel(); return false; }
        if (_ready) return true;
        if (!NeedsBackground || _placement() is PlacementMode.Custom or PlacementMode.Absolute or PlacementMode.AbsolutePoint)
            return true;
        if (!_pending)
        {
            _pending = true;
            var version = ++_generation;
            _cancel = new CancellationTokenSource();
            InputManager.Current.PreProcessInput += OnPendingInput;
            _anchor = _target() as FrameworkElement;
            if (_anchor != null) _anchor.Unloaded += OnAnchorUnloaded;
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Prepare(version)));
        }
        return false;
    }

    private async void Prepare(int version)
    {
        if (!_pending || version != _generation || _cancel == null) return;
        var cancel = _cancel;
        DesktopLensCapture.Frame? frame = null;
        SkinBorder? surface = null;
        var failed = false;
        try
        {
            var content = _content();
            if (content == null) { failed = true; }
            else
            {
                if (content is Control control) control.ApplyTemplate();
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                surface = FindSurface(content);
                if (surface == null) failed = true;
                else
                {
                    var requested = InitialBounds(content, _target(), _placement());
                    var capture = DesktopLensCapture.PreparePopupAsync(requested, Theme.Skin == PaperSkins.LiquidGlass, cancel.Token);
                    try { frame = await capture.WaitAsync(TimeSpan.FromMilliseconds(300), cancel.Token); }
                    catch
                    {
                        if (!cancel.IsCancellationRequested) cancel.Cancel();
                        // A slow GDI call cannot be interrupted. Dispose any late result;
                        // it must never reopen a dismissed menu or leak pooled screen data.
                        _ = capture.ContinueWith(t =>
                        {
                            if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose();
                            else if (t.IsFaulted) _ = t.Exception;
                        },
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        throw;
                    }
                    failed = frame == null;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or InvalidOperationException or
            ExternalException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        { failed = true; Debug.WriteLine("Menu background preparation: " + ex.Message); }
        finally
        {
            if (version != _generation || !_pending || _owner.Dispatcher.HasShutdownStarted)
                frame?.Dispose();
            else
            {
                DetachPendingInput();
                _pending = false; _ready = true; _cancel = null;
                // Failure gets one stable fallback for this opening, not a delayed flash
                // from fallback to glass. The next open can try again.
                if (!NeedsBackground) { frame?.Dispose(); frame = null; failed = false; }
                if (surface != null) surface.PrepareMenuBackground(frame, failed);
                else frame?.Dispose();
                // ContextMenuService opens with SetCurrentValue, not SetValue. Its
                // requested true is not the base value after we coerce it to false.
                // Re-coercing would read the default false and cancel a real right click.
                // Replay only this still-current request; preserve bindings on submenus.
                _owner.SetCurrentValue(_isOpen, true);
            }
            cancel.Dispose();
        }
    }

    private void Cancel()
    {
        ++_generation; _pending = _ready = false;
        _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null;
        DetachPendingInput();
    }
    private void DetachPendingInput()
    {
        InputManager.Current.PreProcessInput -= OnPendingInput;
        if (_anchor != null) _anchor.Unloaded -= OnAnchorUnloaded;
        _anchor = null;
    }
    private void OnAnchorUnloaded(object sender, RoutedEventArgs e) => _owner.SetCurrentValue(_isOpen, false);
    private void OnPendingInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs { Key: Key.Escape, IsDown: true } ||
            e.StagingItem.Input is MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed })
            _owner.SetCurrentValue(_isOpen, false);
    }
    private static SkinBorder? FindSurface(DependencyObject node)
    {
        if (node is SkinBorder { IsMenu: true } surface) return surface;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindSurface(VisualTreeHelper.GetChild(node, i)) is { } result) return result;
        return null;
    }
    private static Int32Rect InitialBounds(FrameworkElement content, UIElement? target, PlacementMode placement)
    {
        var scale = target == null ? 1 : Math.Max(VisualTreeHelper.GetDpi(target).DpiScaleX, VisualTreeHelper.GetDpi(target).DpiScaleY);
        // Cover both WPF placement alternatives (left/right and above/below). Actual
        // placement is still performed by Popup; SkinBorder crops this scene afterwards.
        var w = Math.Clamp((int)Math.Ceiling(Math.Max(190, content.DesiredSize.Width) * scale) + 64, 64, 8192);
        var h = Math.Clamp((int)Math.Ceiling(Math.Max(32, content.DesiredSize.Height) * scale) + 64, 64, 8192);
        GetCursorPos(out var cursor);
        var point = new Point(cursor.X, cursor.Y); var anchorSize = new Size();
        if (target != null && placement is not (PlacementMode.Mouse or PlacementMode.MousePoint) && PresentationSource.FromVisual(target) != null)
        {
            point = target.PointToScreen(new Point());
            var end = target.PointToScreen(new Point(target.RenderSize.Width, target.RenderSize.Height));
            anchorSize = new Size(Math.Abs(end.X - point.X), Math.Abs(end.Y - point.Y));
        }
        return new((int)Math.Floor(point.X) - w, (int)Math.Floor(point.Y) - h,
            Math.Min(32768, 2 * w + (int)Math.Ceiling(anchorSize.Width)),
            Math.Min(32768, 2 * h + (int)Math.Ceiling(anchorSize.Height)));
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
}
