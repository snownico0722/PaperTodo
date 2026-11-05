#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Debug-only observer for the edge-capsule input wake-up chain. It never changes hover, preview,
/// capture, hit testing or layout state; it only records native/WPF input and compares the physical
/// pointer against the already-applied capsule frame.
/// </summary>
internal static class EdgeCapsuleInputDiagnosticBootstrap
{
    private static System.Threading.Timer? _bootstrapTimer;
    private static int _installQueued;

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            _bootstrapTimer = new System.Threading.Timer(
                static _ => TryQueueInstall(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Debug diagnostics must never affect process startup.
        }
    }

    private static void TryQueueInstall()
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null ||
                dispatcher.HasShutdownStarted ||
                dispatcher.HasShutdownFinished ||
                Interlocked.Exchange(ref _installQueued, 1) != 0)
            {
                return;
            }

            _ = dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    try
                    {
                        var controller = AppController.Current;
                        if (controller == null)
                        {
                            Interlocked.Exchange(ref _installQueued, 0);
                            return;
                        }

                        controller.StartEdgeCapsuleInputDiagnostics();
                        _bootstrapTimer?.Dispose();
                        _bootstrapTimer = null;
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _installQueued, 0);
                    }
                }),
                DispatcherPriority.Background);
        }
        catch
        {
            Interlocked.Exchange(ref _installQueued, 0);
        }
    }
}

internal readonly record struct EdgeCapsuleInputDiagnosticSnapshot(
    DeviceScreenRect Bounds,
    DeviceScreenRect InteractiveBounds,
    bool Visible,
    bool HitTestVisible,
    EdgeCapsuleSurfaceKind Surface,
    bool PointerOver,
    bool Eligible,
    IntPtr Hwnd,
    string OwnerRole);

public sealed partial class PaperWindow
{
    internal EdgeCapsuleInputDiagnosticSnapshot CaptureEdgeCapsuleInputDiagnosticSnapshot()
    {
        var proxyOwned = _controller.TryGetEdgeCapsuleQueueProxyPresentation(
            this,
            out var proxyFrame);
        var frame = proxyOwned
            ? proxyFrame
            : _edgeCapsule.AppliedPresentation;
        var hwnd = _edgeCapsuleHost?.EdgeCapsuleInputDiagnosticHandle ?? IntPtr.Zero;
        if (proxyOwned &&
            _controller.TryGetEdgeCapsuleQueueProxyDiagnosticHandle(
                this,
                out var proxyHandle))
        {
            hwnd = proxyHandle;
        }
        return new EdgeCapsuleInputDiagnosticSnapshot(
            frame.Bounds,
            frame.InteractiveBounds,
            frame.Visible,
            frame.IsHitTestVisible,
            frame.Surface,
            IsEdgeCapsulePointerOver,
            CanEnterEdgeCapsulePreview,
            hwnd,
            proxyOwned ? "proxy" : "real");
    }

    internal void EnsureEdgeCapsuleInputDiagnostics(Action<string> trace)
    {
        _edgeCapsuleHost?.EnsureEdgeCapsuleInputDiagnostics(
            EdgeCapsulePreviewPaperId,
            trace);
    }
}

internal sealed partial class EdgeCapsuleHost
{
    private const int EdgeCapsuleInputDiagnosticWmMouseMove = 0x0200;
    private const int EdgeCapsuleInputDiagnosticWmMouseLeave = 0x02A3;
    private const int EdgeCapsuleInputDiagnosticWmNcMouseMove = 0x00A0;
    private const double EdgeCapsuleInputDiagnosticMoveTraceIntervalMilliseconds = 80;

    private bool _edgeCapsuleInputDiagnosticsAttached;
    private HwndSource? _edgeCapsuleInputDiagnosticSource;
    private Action<string>? _edgeCapsuleInputDiagnosticTrace;
    private string _edgeCapsuleInputDiagnosticTraceId = "<none>";
    private long _edgeCapsuleInputDiagnosticLastNativeHitTimestamp;
    private long _edgeCapsuleInputDiagnosticLastNativeMoveTimestamp;
    private long _edgeCapsuleInputDiagnosticLastWpfMoveTimestamp;

    internal IntPtr EdgeCapsuleInputDiagnosticHandle =>
        _disposed
            ? IntPtr.Zero
            : new WindowInteropHelper(Window).Handle;

    internal void EnsureEdgeCapsuleInputDiagnostics(
        string paperId,
        Action<string> trace)
    {
        if (_disposed)
        {
            return;
        }

        _edgeCapsuleInputDiagnosticTrace = trace;
        _edgeCapsuleInputDiagnosticTraceId = string.IsNullOrEmpty(paperId)
            ? "<none>"
            : paperId[..Math.Min(6, paperId.Length)];
        if (_edgeCapsuleInputDiagnosticsAttached)
        {
            AttachEdgeCapsuleInputDiagnosticNativeHook();
            return;
        }

        _edgeCapsuleInputDiagnosticsAttached = true;
        Shell.MouseEnter += OnEdgeCapsuleInputDiagnosticShellEnter;
        Shell.MouseLeave += OnEdgeCapsuleInputDiagnosticShellLeave;
        ContentArea.PreviewMouseMove += OnEdgeCapsuleInputDiagnosticContentMove;
        Window.SourceInitialized += OnEdgeCapsuleInputDiagnosticSourceInitialized;
        AttachEdgeCapsuleInputDiagnosticNativeHook();
    }

    private void OnEdgeCapsuleInputDiagnosticSourceInitialized(object? sender, EventArgs e) =>
        AttachEdgeCapsuleInputDiagnosticNativeHook();

    private void AttachEdgeCapsuleInputDiagnosticNativeHook()
    {
        if (_disposed || _edgeCapsuleInputDiagnosticSource != null)
        {
            return;
        }

        if (PresentationSource.FromVisual(Window) is not HwndSource source)
        {
            return;
        }

        source.AddHook(OnEdgeCapsuleInputDiagnosticNativeMessage);
        _edgeCapsuleInputDiagnosticSource = source;
    }

    private IntPtr OnEdgeCapsuleInputDiagnosticNativeMessage(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (_disposed || EdgeCapsuleInputDiagnosticProbe.Active)
        {
            return IntPtr.Zero;
        }

        if (msg == WmNcHitTest)
        {
            var packed = lParam.ToInt64();
            var pointer = new DeviceScreenPoint(
                unchecked((short)(packed & 0xffff)),
                unchecked((short)((packed >> 16) & 0xffff)));
            if (ContainsScreenPoint(pointer) &&
                ShouldTraceEdgeCapsuleInputDiagnosticMove(
                    ref _edgeCapsuleInputDiagnosticLastNativeHitTimestamp))
            {
                TraceEdgeCapsuleInputDiagnostic(
                    "native-hit",
                    pointer,
                    $"inside=True hitTest={_appliedFrame.IsHitTestVisible} surface={_appliedFrame.Surface}");
            }
            return IntPtr.Zero;
        }

        if (msg is EdgeCapsuleInputDiagnosticWmMouseMove or
            EdgeCapsuleInputDiagnosticWmNcMouseMove)
        {
            if (WindowNative.TryGetCursorScreenPosition(out var pointer) &&
                ShouldTraceEdgeCapsuleInputDiagnosticMove(
                    ref _edgeCapsuleInputDiagnosticLastNativeMoveTimestamp))
            {
                TraceEdgeCapsuleInputDiagnostic(
                    "native-move",
                    pointer,
                    $"inside={ContainsScreenPoint(pointer)} hitTest={_appliedFrame.IsHitTestVisible} surface={_appliedFrame.Surface}");
            }
            return IntPtr.Zero;
        }

        if (msg == EdgeCapsuleInputDiagnosticWmMouseLeave)
        {
            _edgeCapsuleInputDiagnosticTrace?.Invoke(
                $"input native-leave target={_edgeCapsuleInputDiagnosticTraceId} " +
                $"surface={_appliedFrame.Surface}");
        }

        return IntPtr.Zero;
    }

    private void OnEdgeCapsuleInputDiagnosticShellEnter(object sender, MouseEventArgs e)
    {
        var pointer = PointerScreenPosition(e);
        TraceEdgeCapsuleInputDiagnostic(
            "wpf-shell-enter",
            pointer,
            $"inside={ContainsScreenPoint(pointer)} hitTest={_appliedFrame.IsHitTestVisible} surface={_appliedFrame.Surface}");
    }

    private void OnEdgeCapsuleInputDiagnosticShellLeave(object sender, MouseEventArgs e)
    {
        var pointer = PointerScreenPosition(e);
        TraceEdgeCapsuleInputDiagnostic(
            "wpf-shell-leave",
            pointer,
            $"inside={ContainsScreenPoint(pointer)} hitTest={_appliedFrame.IsHitTestVisible} surface={_appliedFrame.Surface}");
    }

    private void OnEdgeCapsuleInputDiagnosticContentMove(object sender, MouseEventArgs e)
    {
        if (!ShouldTraceEdgeCapsuleInputDiagnosticMove(
                ref _edgeCapsuleInputDiagnosticLastWpfMoveTimestamp))
        {
            return;
        }

        var pointer = PointerScreenPosition(e);
        TraceEdgeCapsuleInputDiagnostic(
            "wpf-content-move",
            pointer,
            $"inside={ContainsScreenPoint(pointer)} captured={ContentArea.IsMouseCaptured} " +
            $"hitTest={_appliedFrame.IsHitTestVisible} surface={_appliedFrame.Surface}");
    }

    private void TraceEdgeCapsuleInputDiagnostic(
        string stage,
        DeviceScreenPoint pointer,
        string suffix)
    {
        _edgeCapsuleInputDiagnosticTrace?.Invoke(
            $"input {stage} target={_edgeCapsuleInputDiagnosticTraceId} " +
            $"pointer={pointer.X},{pointer.Y} {suffix}");
    }

    private static bool ShouldTraceEdgeCapsuleInputDiagnosticMove(ref long previousTimestamp)
    {
        var now = Stopwatch.GetTimestamp();
        if (previousTimestamp != 0 &&
            Stopwatch.GetElapsedTime(previousTimestamp, now).TotalMilliseconds <
                EdgeCapsuleInputDiagnosticMoveTraceIntervalMilliseconds)
        {
            return false;
        }

        previousTimestamp = now;
        return true;
    }
}

public sealed partial class AppController
{
    private const double EdgeCapsuleInputDiagnosticSampleMilliseconds = 24;
    private const double EdgeCapsuleInputDiagnosticMismatchMilliseconds = 40;
    private const double EdgeCapsuleInputDiagnosticControllerGapMilliseconds = 60;
    private static readonly Action<string> EdgeCapsuleInputDiagnosticTrace =
        TraceEdgeCapsuleInputDiagnosticMessage;

    private DispatcherTimer? _edgeCapsuleInputDiagnosticTimer;
    private string? _edgeCapsuleInputDiagnosticPhysicalTargetPaperId;
    private long _edgeCapsuleInputDiagnosticPhysicalTargetSinceTimestamp;
    private bool _edgeCapsuleInputDiagnosticWakeMismatchLogged;
    private bool _edgeCapsuleInputDiagnosticControllerGapLogged;
    private bool _edgeCapsuleInputDiagnosticSuppressionLogged;

    internal void StartEdgeCapsuleInputDiagnostics()
    {
        if (_edgeCapsuleInputDiagnosticTimer != null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        _edgeCapsuleInputDiagnosticTimer = new DispatcherTimer(
            DispatcherPriority.Input,
            dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(
                EdgeCapsuleInputDiagnosticSampleMilliseconds)
        };
        _edgeCapsuleInputDiagnosticTimer.Tick += OnEdgeCapsuleInputDiagnosticTick;
        _edgeCapsuleInputDiagnosticTimer.Start();
        TraceEdgeCapsulePreview("input diagnostics started");
    }

    private static void TraceEdgeCapsuleInputDiagnosticMessage(string message) =>
        TraceEdgeCapsulePreview(message);

    private void OnEdgeCapsuleInputDiagnosticTick(object? sender, EventArgs e)
    {
        if (IsExiting)
        {
            _edgeCapsuleInputDiagnosticTimer?.Stop();
            return;
        }

        foreach (var window in _windows.Values)
        {
            window.EnsureEdgeCapsuleInputDiagnostics(EdgeCapsuleInputDiagnosticTrace);
        }

        if (!State.ExperimentalEdgeCapsuleHoverPreview ||
            !WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            ResetEdgeCapsuleInputDiagnosticPhysicalTarget();
            return;
        }

        PaperWindow? target = null;
        EdgeCapsuleInputDiagnosticSnapshot targetSnapshot = default;
        foreach (var window in _windows.Values)
        {
            var snapshot = window.CaptureEdgeCapsuleInputDiagnosticSnapshot();
            if (!snapshot.Visible || snapshot.InteractiveBounds.IsEmpty ||
                !EdgeCapsuleGeometry.Contains(snapshot.InteractiveBounds, pointer))
            {
                continue;
            }

            target = window;
            targetSnapshot = snapshot;
            break;
        }

        if (target == null)
        {
            ResetEdgeCapsuleInputDiagnosticPhysicalTarget();
            return;
        }

        var targetPaperId = target.EdgeCapsulePreviewPaperId;
        var now = Stopwatch.GetTimestamp();
        if (!string.Equals(
                _edgeCapsuleInputDiagnosticPhysicalTargetPaperId,
                targetPaperId,
                StringComparison.Ordinal))
        {
            if (_edgeCapsuleInputDiagnosticPhysicalTargetPaperId != null)
            {
                TraceEdgeCapsulePreview(
                    $"input physical-leave target={EdgeCapsulePreviewTraceId(_edgeCapsuleInputDiagnosticPhysicalTargetPaperId)} " +
                    $"pointer={pointer.X},{pointer.Y}");
            }

            _edgeCapsuleInputDiagnosticPhysicalTargetPaperId = targetPaperId;
            _edgeCapsuleInputDiagnosticPhysicalTargetSinceTimestamp = now;
            _edgeCapsuleInputDiagnosticWakeMismatchLogged = false;
            _edgeCapsuleInputDiagnosticControllerGapLogged = false;
            _edgeCapsuleInputDiagnosticSuppressionLogged = false;
            TraceEdgeCapsulePreview(
                $"input physical-enter target={EdgeCapsulePreviewTraceId(targetPaperId)} " +
                $"pointer={pointer.X},{pointer.Y} hwnd={FormatEdgeCapsuleInputDiagnosticHandle(targetSnapshot.Hwnd)} " +
                $"ownerRole={targetSnapshot.OwnerRole} " +
                $"hitTest={targetSnapshot.HitTestVisible} wpfOver={targetSnapshot.PointerOver} " +
                $"eligible={targetSnapshot.Eligible} surface={targetSnapshot.Surface} " +
                $"owner={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)} " +
                $"queued={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewQueuedTransferPaperId)} " +
                $"intent={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewActivationIntent?.TargetPaperId)}");
        }

        var insideElapsed = Stopwatch.GetElapsedTime(
            _edgeCapsuleInputDiagnosticPhysicalTargetSinceTimestamp,
            now).TotalMilliseconds;
        if (!targetSnapshot.PointerOver &&
            !_edgeCapsuleInputDiagnosticWakeMismatchLogged &&
            insideElapsed >= EdgeCapsuleInputDiagnosticMismatchMilliseconds)
        {
            _edgeCapsuleInputDiagnosticWakeMismatchLogged = true;
            var under = ProbeEdgeCapsuleInputDiagnosticWindow(pointer);
            var underRoot = under == IntPtr.Zero
                ? IntPtr.Zero
                : GetAncestor(under, EdgeCapsuleInputDiagnosticGaRoot);
            var underTarget = targetSnapshot.Hwnd != IntPtr.Zero &&
                (under == targetSnapshot.Hwnd || underRoot == targetSnapshot.Hwnd);
            TraceEdgeCapsulePreview(
                $"input wake-mismatch target={EdgeCapsulePreviewTraceId(targetPaperId)} " +
                $"pointer={pointer.X},{pointer.Y} nativeInside=True " +
                $"hitTest={targetSnapshot.HitTestVisible} wpfOver=False " +
                $"eligible={targetSnapshot.Eligible} surface={targetSnapshot.Surface} " +
                $"hwnd={FormatEdgeCapsuleInputDiagnosticHandle(targetSnapshot.Hwnd)} " +
                $"ownerRole={targetSnapshot.OwnerRole} " +
                $"hwndUnder={FormatEdgeCapsuleInputDiagnosticHandle(under)} " +
                $"rootUnder={FormatEdgeCapsuleInputDiagnosticHandle(underRoot)} " +
                $"underTarget={underTarget} " +
                $"owner={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)} " +
                $"queued={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewQueuedTransferPaperId)} " +
                $"intent={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewActivationIntent?.TargetPaperId)} " +
                $"eligibility={target.EdgeCapsulePreviewEligibilityTrace()}");
            return;
        }

        if (!targetSnapshot.PointerOver ||
            !targetSnapshot.Eligible ||
            string.Equals(
                _edgeCapsulePreviewSession?.OwnerPaperId,
                targetPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        var suppressed = IsEdgeCapsulePreviewLayoutSuppressedFor(target);
        if (suppressed &&
            !_edgeCapsuleInputDiagnosticSuppressionLogged &&
            insideElapsed >= EdgeCapsuleInputDiagnosticControllerGapMilliseconds)
        {
            _edgeCapsuleInputDiagnosticSuppressionLogged = true;
            TraceEdgeCapsulePreview(
                $"input controller-suppressed target={EdgeCapsulePreviewTraceId(targetPaperId)} " +
                $"pointer={pointer.X},{pointer.Y} wpfOver=True " +
                $"owner={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)}");
            return;
        }

        var hasQueuedTransfer = string.Equals(
            _edgeCapsulePreviewQueuedTransferPaperId,
            targetPaperId,
            StringComparison.Ordinal);
        var hasActivationIntent = _edgeCapsulePreviewActivationIntent is { } intent &&
            string.Equals(intent.TargetPaperId, targetPaperId, StringComparison.Ordinal);
        if (!suppressed &&
            !hasQueuedTransfer &&
            !hasActivationIntent &&
            !_edgeCapsuleInputDiagnosticControllerGapLogged &&
            insideElapsed >= EdgeCapsuleInputDiagnosticControllerGapMilliseconds)
        {
            _edgeCapsuleInputDiagnosticControllerGapLogged = true;
            TraceEdgeCapsulePreview(
                $"input controller-gap target={EdgeCapsulePreviewTraceId(targetPaperId)} " +
                $"pointer={pointer.X},{pointer.Y} wpfOver=True eligible=True " +
                $"owner={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewSession?.OwnerPaperId)} " +
                $"queued={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewQueuedTransferPaperId)} " +
                $"intent={EdgeCapsulePreviewTraceId(_edgeCapsulePreviewActivationIntent?.TargetPaperId)}");
        }
    }

    private void ResetEdgeCapsuleInputDiagnosticPhysicalTarget()
    {
        if (_edgeCapsuleInputDiagnosticPhysicalTargetPaperId != null &&
            WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            TraceEdgeCapsulePreview(
                $"input physical-leave target={EdgeCapsulePreviewTraceId(_edgeCapsuleInputDiagnosticPhysicalTargetPaperId)} " +
                $"pointer={pointer.X},{pointer.Y}");
        }

        _edgeCapsuleInputDiagnosticPhysicalTargetPaperId = null;
        _edgeCapsuleInputDiagnosticPhysicalTargetSinceTimestamp = 0;
        _edgeCapsuleInputDiagnosticWakeMismatchLogged = false;
        _edgeCapsuleInputDiagnosticControllerGapLogged = false;
        _edgeCapsuleInputDiagnosticSuppressionLogged = false;
    }

    private static IntPtr ProbeEdgeCapsuleInputDiagnosticWindow(DeviceScreenPoint pointer)
    {
        EdgeCapsuleInputDiagnosticProbe.Enter();
        try
        {
            return WindowFromPoint(new EdgeCapsuleInputDiagnosticNativePoint(
                (int)Math.Round(pointer.X),
                (int)Math.Round(pointer.Y)));
        }
        finally
        {
            EdgeCapsuleInputDiagnosticProbe.Exit();
        }
    }

    private static string FormatEdgeCapsuleInputDiagnosticHandle(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? "<none>" : $"0x{hwnd.ToInt64():X}";

    private const uint EdgeCapsuleInputDiagnosticGaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct EdgeCapsuleInputDiagnosticNativePoint
    {
        public EdgeCapsuleInputDiagnosticNativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(
        EdgeCapsuleInputDiagnosticNativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}

internal static class EdgeCapsuleInputDiagnosticProbe
{
    [ThreadStatic]
    private static int _depth;

    internal static bool Active => _depth > 0;

    internal static void Enter() => _depth++;

    internal static void Exit()
    {
        if (_depth > 0)
        {
            _depth--;
        }
    }
}
#endif
