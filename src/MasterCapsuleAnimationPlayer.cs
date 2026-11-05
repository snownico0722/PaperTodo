using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

/// <summary>
/// A visual-only playhead can be shared by a docked master and its floating cover during handoff.
/// It does not own gesture, queue, geometry, visibility or any paper model.
/// </summary>
internal sealed class MasterCapsulePlayback
{
    private double _position;
    private long _epoch = Stopwatch.GetTimestamp();
    private int _framesPerSecond;
    internal MasterCapsulePlayback(MasterCapsuleVisualClip clip, int framesPerSecond)
    {
        Clip = clip;
        _framesPerSecond = MasterCapsuleAnimationRates.Normalize(framesPerSecond);
    }
    internal MasterCapsuleVisualClip Clip { get; }
    internal int FramesPerSecond
    {
        get => _framesPerSecond;
        set
        {
            value = MasterCapsuleAnimationRates.Normalize(value);
            if (value == _framesPerSecond) return;
            _position = Position;
            _epoch = Stopwatch.GetTimestamp();
            _framesPerSecond = value;
        }
    }
    private double Position => (_position + Stopwatch.GetElapsedTime(_epoch).TotalSeconds * _framesPerSecond) % Clip.FrameCount;
    internal int FrameIndex => (int)Position;
}

/// <summary>
/// Advances an existing Image, including inside Windows' modal caption-move loop. A timer belongs
/// to the existing HWND; there is no second window, background thread or Edge geometry scheduler.
/// Hidden, static and returned-to-pool surfaces own no timer. Frames are decoded before attachment.
/// </summary>
internal sealed class MasterCapsuleAnimationPlayer : IDisposable
{
    private const int WmTimer = 0x0113;
    private const int WmNcDestroy = 0x0082;
    private static long s_nextTimerId = 0x504100;
    private readonly Window _window;
    private readonly Image _image;
    private readonly MasterCapsuleVisualClip? _standbyClip;
    private readonly nuint _timerId = (nuint)Interlocked.Increment(ref s_nextTimerId);
    private HwndSource? _source;
    private bool _timerRunning;
    private bool _disposed;
    private int _interval;

    internal MasterCapsuleAnimationPlayer(Window window, Image image, MasterCapsuleVisualClip? standbyClip = null)
    {
        _window = window;
        _image = image;
        _standbyClip = standbyClip;
        window.SourceInitialized += OnSourceInitialized;
        window.IsVisibleChanged += OnVisibilityChanged;
        image.IsVisibleChanged += OnVisibilityChanged;
        image.SizeChanged += OnImageSizeChanged;
        window.DpiChanged += OnDpiChanged;
        window.SizeChanged += OnImageSizeChanged;
        window.Closed += OnClosed;
        window.Dispatcher.ShutdownStarted += OnClosed;
        AttachSource();
    }

    internal MasterCapsulePlayback? Playback { get; private set; }
    internal bool IsRunning => _timerRunning;
    internal long PresentedFrameChanges { get; private set; }

    internal void Play(MasterCapsulePlayback? playback)
    {
        if (_disposed) return;
        _window.VerifyAccess();
        if (Playback?.Clip != playback?.Clip)
        {
            DetachClip();
            if (playback != null) playback.Clip.FramesChanged += PresentFrame;
        }
        Playback = playback;
        PresentFrame();
        RefreshDisplaySize();
        RefreshTimer();
    }

    internal void Refresh()
    {
        if (_disposed) return;
        PresentFrame();
        RefreshTimer();
    }

    private void PresentFrame()
    {
        var playback = Playback;
        var frame = playback?.Clip.FrameAt(playback.FrameIndex);
        if (ReferenceEquals(_image.Source, frame)) return;
        _image.Source = frame;
        PresentedFrameChanges++;
    }

    private void AttachSource()
    {
        if (_disposed || _source != null) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnWindowMessage);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        AttachSource();
        Refresh();
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshDisplaySize();
        Refresh();
    }
    private void OnImageSizeChanged(object sender, SizeChangedEventArgs e) => RefreshDisplaySize();
    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => RefreshDisplaySize();

    private void RefreshDisplaySize()
    {
        if (_disposed) return;
        var dpi = VisualTreeHelper.GetDpi(_image);
        Playback?.Clip.RequestDisplaySize(this, _window.IsVisible && _image.IsVisible
            ? new Size(_image.ActualWidth * dpi.DpiScaleX, _image.ActualHeight * dpi.DpiScaleY)
            : null);
        // Prepare the independent drag artwork at the same display size before
        // the first gesture. A drag-only image borrows the ordinary shell's bounds.
        if (_standbyClip != null && _standbyClip != Playback?.Clip)
            _standbyClip.RequestDisplaySize(this, _window.IsVisible
                ? new Size((_image.IsVisible ? _image.ActualWidth : _window.ActualWidth) * dpi.DpiScaleX,
                    (_image.IsVisible ? _image.ActualHeight : _window.ActualHeight) * dpi.DpiScaleY)
                : null);
    }

    private void DetachClip()
    {
        if (Playback == null) return;
        Playback.Clip.FramesChanged -= PresentFrame;
        Playback.Clip.RequestDisplaySize(this, null);
    }
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void RefreshTimer()
    {
        var playback = Playback;
        var shouldRun = !_disposed && _window.IsVisible && _image.IsVisible &&
            _source is { IsDisposed: false } && playback is { Clip.FrameCount: > 1 };
        if (!shouldRun) { StopTimer(); return; }
        var interval = Math.Max(1, (int)Math.Round(1000.0 / playback!.FramesPerSecond));
        if (_timerRunning && interval == _interval) return;
        StopTimer();
        _interval = interval;
        _timerRunning = SetTimer(_source!.Handle, _timerId, (uint)interval, IntPtr.Zero) != 0;
        if (!_timerRunning)
            Trace.TraceWarning("Master capsule animation timer could not start; keeping the displayed frame.");
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNcDestroy) { StopTimer(); return IntPtr.Zero; }
        if (message != WmTimer || (nuint)wParam != _timerId) return IntPtr.Zero;
        handled = true;
        // KillTimer may leave an already queued WM_TIMER. Never revive a hidden/returned lease.
        if (_disposed || !_timerRunning) return IntPtr.Zero;
        if (!_window.IsVisible || !_image.IsVisible) { StopTimer(); return IntPtr.Zero; }
        PresentFrame();
        RefreshTimer();
        return IntPtr.Zero;
    }

    private void StopTimer()
    {
        if (!_timerRunning) return;
        _timerRunning = false;
        if (_source is { IsDisposed: false }) KillTimer(_source.Handle, _timerId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
        if (_source is { IsDisposed: false }) _source.RemoveHook(OnWindowMessage);
        _source = null;
        DetachClip();
        _standbyClip?.RequestDisplaySize(this, null);
        Playback = null;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _image.IsVisibleChanged -= OnVisibilityChanged;
        _image.SizeChanged -= OnImageSizeChanged;
        _window.DpiChanged -= OnDpiChanged;
        _window.SizeChanged -= OnImageSizeChanged;
        _window.Closed -= OnClosed;
        _window.Dispatcher.ShutdownStarted -= OnClosed;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(IntPtr hwnd, nuint id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hwnd, nuint id);
}
