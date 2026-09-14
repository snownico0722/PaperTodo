#if DEBUG
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Opt-in real route 3 device validation only. A pull-only, current-user pipe observes the real controller;
/// it cannot execute actions, mutate a model, force layout, settle a window or report success.
/// The external driver supplies real desktop input and decides assertions from these facts.
/// </summary>
internal sealed class EdgeRoute3HandoffProbe : IDisposable
{
    private static readonly string? ConfiguredPipe = Environment.GetEnvironmentVariable("PAPERTODO_EDGE_HANDOFF_PROBE");
    internal static bool Enabled => !string.IsNullOrWhiteSpace(ConfiguredPipe) && EdgeDiagnosticJournal.Enabled;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Dictionary<Window, string> ObservedWindows = new();
    private static readonly Dictionary<long, long> Routes = new();
    private static long _nextRoute;
    private readonly CancellationTokenSource _stop = new();
    private readonly AppController _controller;
    private readonly Dispatcher _dispatcher;
    private Task? _listener;

    private EdgeRoute3HandoffProbe(AppController controller, Dispatcher dispatcher)
    { _controller = controller; _dispatcher = dispatcher; }

    internal static EdgeRoute3HandoffProbe? Start(AppController controller, Dispatcher dispatcher)
    {
        if (!Enabled) return null;
        var probe = new EdgeRoute3HandoffProbe(controller, dispatcher);
        probe._listener = Task.Run(probe.ListenAsync);
        EdgeCapsulePerformanceDiagnostics.Event("handoff.probe.started", value1: Environment.ProcessId, detail: ConfiguredPipe);
        return probe;
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(ConfiguredPipe!, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    // Exactly one small newline-terminated request per connection, bounded before parsing.
                    var bytes = new byte[1024];
                    var count = 0;
                    while (count < bytes.Length)
                    {
                        var read = await pipe.ReadAsync(bytes.AsMemory(count, 1), deadline.Token);
                        if (read == 0 || bytes[count++] == (byte)'\n') break;
                    }
                    if (count == 0 || bytes[count - 1] != (byte)'\n') continue;
                    var request = Encoding.UTF8.GetString(bytes, 0, count).Trim();
                    object response;
                    if (request == "snapshot")
                    {
                        response = await _dispatcher.InvokeAsync(_controller.CaptureRoute3HandoffProbeSnapshot,
                            DispatcherPriority.Background, deadline.Token);
                    }
                    else if (request.StartsWith("mark ", StringComparison.Ordinal) && request.Length <= 160)
                    {
                        var marker = request[5..];
                        EdgeCapsulePerformanceDiagnostics.Event("handoff.probe.mark", detail: marker);
                        response = new { ok = true, marker, qpc = Stopwatch.GetTimestamp() };
                    }
                    else response = new { ok = false, error = "Only snapshot and mark are supported." };
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, JsonOptions) + "\n");
                    await pipe.WriteAsync(payload, deadline.Token);
                    await pipe.FlushAsync(deadline.Token);
                }
                catch (Exception ex)
                { EdgeCapsulePerformanceDiagnostics.Event("handoff.probe.request-failed", detail: ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Do not restart a failing diagnostic listener or put a retry timer in the app.
            EdgeCapsulePerformanceDiagnostics.Event("handoff.probe.failed", detail: ex.GetType().Name);
        }
    }

    internal static long BeginRoute(long session, IntPtr target, DeviceScreenPoint physical, DeviceScreenPoint endpoint, bool found,
        EdgeCapsuleQueueCompositionProxy? proxy = null)
    {
        if (!Enabled) return 0;
        var route = Interlocked.Increment(ref _nextRoute);
        EdgeCapsulePerformanceDiagnostics.Event("handoff.input.begin", route, session, target.ToInt64(),
            EdgeDiagnosticObservation.Pack((int)Math.Round(physical.X), (int)Math.Round(physical.Y)),
            EdgeDiagnosticObservation.Pack((int)Math.Round(endpoint.X), (int)Math.Round(endpoint.Y)), found ? 1 : 0);
        proxy?.RecordRoute3HandoffStart(route);
        return route;
    }

    internal static void BeforePost(long route, IntPtr target)
    {
        if (route != 0) Routes[target.ToInt64()] = route;
    }

    internal static void RecordPostResult(long route, IntPtr target, bool posted)
    {
        if (route == 0) return;
        if (!posted) Routes.Remove(target.ToInt64());
        EdgeCapsulePerformanceDiagnostics.Event("handoff.input.post", route, target.ToInt64(), posted ? 1 : 0);
    }

    internal static void EndRoute(long route, EdgeCapsuleQueueCompositionProxy proxy)
    {
        if (route != 0) proxy.RecordRoute3HandoffOutcome(route);
    }

    internal static void ObserveWindow(Window window, string paperId)
    {
        if (!Enabled || ObservedWindows.ContainsKey(window)) return;
        ObservedWindows.Add(window, paperId);
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouse), true);
        window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouse), true);
        window.Closed += OnClosed;
    }

    private static void OnClosed(object? sender, EventArgs args)
    {
        if (sender is Window window) Forget(window);
    }

    private static void Forget(Window window)
    {
        window.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouse));
        window.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouse));
        window.Closed -= OnClosed;
        ObservedWindows.Remove(window);
        Routes.Remove(new WindowInteropHelper(window).Handle.ToInt64());
    }

    private static void OnMouse(object sender, MouseButtonEventArgs args)
    {
        try { ObserveMouse(sender, args); }
        catch (Exception ex)
        { EdgeCapsulePerformanceDiagnostics.Event("handoff.wpf.observer-failed", detail: ex.GetType().Name); }
    }

    private static void ObserveMouse(object sender, MouseButtonEventArgs args)
    {
        if (sender is not Window window || !ObservedWindows.TryGetValue(window, out var paperId)) return;
        var handle = new WindowInteropHelper(window).Handle.ToInt64();
        Routes.TryGetValue(handle, out var route);
        var local = args.GetPosition(window);
        var screen = window.PointToScreen(local);
        EdgeCapsulePerformanceDiagnostics.Event(args.ButtonState == MouseButtonState.Pressed ? "handoff.wpf.down" : "handoff.wpf.up",
            route, handle, (long)args.ChangedButton, EdgeDiagnosticObservation.Pack((int)Math.Round(screen.X), (int)Math.Round(screen.Y)),
            args.Handled ? 1 : 0, local.X, local.Y, paperId + "|" + args.OriginalSource?.GetType().Name);
        if (args.ButtonState == MouseButtonState.Released) Routes.Remove(handle);
    }

    internal static object Native(IntPtr handle)
    {
        var hasBounds = GetWindowRect(handle, out var rect);
        var cloakHr = DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int));
        return new { handle = handle.ToInt64(), alive = handle != IntPtr.Zero && WindowNative.IsWindowHandleAlive(handle),
            visible = IsWindowVisible(handle), hasBounds,
            bounds = new { left = rect.Left, top = rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top },
            cloakKnown = cloakHr == 0, cloaked, dpi = GetDpiForWindow(handle) };
    }

    internal static bool NativeMatches(IntPtr handle, DeviceScreenRect expected) =>
        GetWindowRect(handle, out var rect) && rect.Left == expected.Left && rect.Top == expected.Top &&
        rect.Right - rect.Left == expected.Width && rect.Bottom - rect.Top == expected.Height;

    internal static object Frame(EdgeCapsulePresentationFrame frame) => new
    {
        frame.Visible, surface = frame.Surface.ToString(), frame.Bounds, frame.HostBounds, frame.InteractiveBounds,
        edge = frame.Edge.ToString(), frame.BodyWindowWidthDevice, frame.WallDeviceX,
        frame.DpiScaleX, frame.DpiScaleY, frame.MaximumCloseWidthDip, frame.Opacity, frame.ContentOpacity,
        frame.OutlineVisible, frame.IsHitTestVisible, frame.CloseSegmentActsAsContent, frame.TitleVisible
    };

    public void Dispose()
    {
        _stop.Cancel(); // Async pipe is canceled; never block the UI shutdown waiting for its dispatcher request.
        foreach (var window in ObservedWindows.Keys.ToArray()) Forget(window);
        Routes.Clear();
        EdgeCapsulePerformanceDiagnostics.Event("handoff.probe.stopped");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}

public sealed partial class AppController
{
    internal object CaptureRoute3HandoffProbeSnapshot()
    {
        return new { ok = true, qpc = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
            processId = Environment.ProcessId, running = IsRunning, proxyShapeRequested = Environment.GetEnvironmentVariable("PAPERTODO_EDGE_PROXY_SHAPE_EXPERIMENT") == "1",
            papers = _windows.Values.Select(window => window.CaptureRoute3HandoffProbePaper(QueueKey(window.EdgeCapsulePreviewPaper))).ToArray(),
            proxies = _edgeCapsuleQueueCompositionProxies.Values.Select(proxy => proxy.CaptureRoute3HandoffProbeProxy()).ToArray() };
    }
}

public sealed partial class PaperWindow
{
    internal object CaptureRoute3HandoffProbePaper(string queueKey)
    {
        EdgeRoute3HandoffProbe.ObserveWindow(this, _paper.Id);
        _edgeCapsuleHost?.ObserveRoute3HandoffProbeInput(_paper.Id);
        return new { id = _paper.Id, title = _paper.Title,
            queueKey, _paper.IsCollapsed, _paper.IsVisible,
            closed = IsClosed, previewOpen = IsEdgeCapsulePreviewOpen, capture = EdgeCapsulePreviewPointerCaptureActive,
            gesture = EdgeCapsuleGesture.ToString(), authority = CurrentEdgeCapsuleVisualAuthority.ToString(),
            activeTransition = _edgeCapsule.HasActiveTransition, applied = EdgeRoute3HandoffProbe.Frame(_edgeCapsule.AppliedPresentation),
            paperWindow = EdgeRoute3HandoffProbe.Native(new WindowInteropHelper(this).Handle),
            host = _edgeCapsuleHost?.CaptureRoute3HandoffProbeHost() };
    }
}

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    internal void RecordRoute3HandoffStart(long route) =>
        EdgeCapsulePerformanceDiagnostics.Event("handoff.input.state-before", route,
            _completionRetryCount, _inputHandoff?.Count ?? 0, _sourcesReleased ? 1 : 0);

    internal void RecordRoute3HandoffOutcome(long route)
    {
        EdgeCapsulePerformanceDiagnostics.Event("handoff.input.end", route,
            _sourcesReleased ? 1 : 0, _completionRetryCount, _inputHandoff?.Count ?? 0,
            _coverLost ? 1 : 0, _disposed ? 1 : 0,
            detail: "sourcesReleased/retryCount/pendingPress/coverLost/disposed; not a delivery assertion");
    }

    internal object CaptureRoute3HandoffProbeProxy() => new
    {
        queueKey = QueueKey, session = SessionOrdinal, retained = IsRetainedForQueueBrowsing,
        hadPredecessor = _hadPredecessor, staticPreacquisition = _plan.IsStaticPreacquisition,
        sourcesReleased = _sourcesReleased, disposed = _disposed, pendingPress = _inputHandoff?.Count ?? 0,
        coverPublished = _coverPublished, coverLost = _coverLost, targetRootInstalled = _targetRootInstalled,
        ownsCurrentHost = ReferenceEquals(_host.Current, this), completionRetries = _completionRetryCount,
        output = EdgeRoute3HandoffProbe.Native(OutputHandle),
        shapes = _visuals.Where(state => state.Shape != null).Select(state => new {
            paperId = state.Member.Plan.PaperId,
            source = EdgeRoute3HandoffProbe.Native(state.PresentedSourceHandle),
            generation = state.Shape!.Description.Generation,
            revision = state.Shape.Description.Revision,
            atlas = state.Shape.Description.NativeBounds,
            submissions = state.Shape.SubmissionCount,
            current = EdgeRoute3HandoffProbe.Frame(state.Shape.Sample(Stopwatch.GetTimestamp()))
        }).ToArray(),
        members = _members.Select(member => new {
            paperId = member.Plan.PaperId, native = EdgeRoute3HandoffProbe.Native(member.SourceHandle),
            source = EdgeRoute3HandoffProbe.Frame(member.Plan.Source), target = EdgeRoute3HandoffProbe.Frame(member.Plan.Target),
            current = TryGetPresentation(member.Window, out var frame) ? EdgeRoute3HandoffProbe.Frame(frame) : null,
            debt = member.Plan.Source.HostBounds != member.Plan.Target.HostBounds
        }).ToArray()
    };
}

internal sealed partial class EdgeCapsuleHost
{
    internal void ObserveRoute3HandoffProbeInput(string paperId) => EdgeRoute3HandoffProbe.ObserveWindow(Window, paperId);

    internal object CaptureRoute3HandoffProbeHost()
    {
        var controls = new List<object>();
        var dragAreas = new List<object>();
        void AddCapsuleDragArea(FrameworkElement element, string role)
        {
            if (!element.IsVisible || element.Opacity <= 0 || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var topLeft = element.TranslatePoint(default, Window);
            var bottomRight = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), Window);
            var center = new Point((topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2);
            var hit = Window.InputHitTest(center) as DependencyObject;
            if (hit == null || IsPreviewInteractiveSource(hit)) return;
            // Only actual compact title/icon regions are offered. A preview body has different
            // input semantics and is never silently substituted when the title is suppressed.
            dragAreas.Add(new { role, local = new { left = topLeft.X, top = topLeft.Y,
                width = bottomRight.X - topLeft.X, height = bottomRight.Y - topLeft.Y }, hit = hit.GetType().Name });
        }
        if (!_disposed && !_previewVisible && _appliedFrame.Surface is
            EdgeCapsuleSurfaceKind.DockedResting or EdgeCapsuleSurfaceKind.DockedHovered or EdgeCapsuleSurfaceKind.DockedActive)
        {
            AddCapsuleDragArea(Label, "capsule-title");
            AddCapsuleDragArea(Icon, "capsule-icon");
        }
        // Read existing arranged visuals only, with a strict bound; no ApplyTemplate/UpdateLayout.
        var remaining = 2048;
        void Walk(DependencyObject node)
        {
            if (--remaining < 0) return;
            if (node is FrameworkElement element && element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0 &&
                node is ButtonBase)
            {
                var topLeft = element.TranslatePoint(default, Window);
                var bottomRight = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), Window);
                controls.Add(new { kind = node.GetType().Name, element.Name, element.IsEnabled, element.IsHitTestVisible,
                    local = new { left = topLeft.X, top = topLeft.Y, width = bottomRight.X - topLeft.X, height = bottomRight.Y - topLeft.Y },
                    isChecked = node is ToggleButton toggle ? toggle.IsChecked : null });
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node) && remaining > 0; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        if (!_disposed) Walk(Root);
        return new { native = EdgeRoute3HandoffProbe.Native(Handle), applied = EdgeRoute3HandoffProbe.Frame(_appliedFrame),
            rootOpacity = Root.Opacity, windowOpacity = Window.Opacity,
            previewVisible = _previewVisible, mouseCaptured = Window.IsMouseCaptureWithin,
            controls, dragAreas, controlsTruncated = remaining <= 0 };
    }
}
#endif
