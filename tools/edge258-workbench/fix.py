from pathlib import Path
import re
CORE=Path('/mnt/data/edge258-core'); FULL=Path('/mnt/data/edge258-full')
def put(root,path,data):
 p=root/path;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(data)
def edit(root,path,old,new,count=1):
 s=(root/path).read_text();assert s.count(old)==count,(path,old[:60],s.count(old));put(root,path,s.replace(old,new))
def method(root,path,name,new):
 s=(root/path).read_text();m=re.search(r'^    (?:private|internal|public) [^\n]*'+re.escape(name)+r'\(',s,re.M);assert m,(path,name)
 a=m.start();b=s.index('{',m.end());i=b+1;d=1
 while d:d+=(s[i]=='{')-(s[i]=='}');i+=1
 put(root,path,s[:a]+new+s[i:])
input_code='''namespace PaperTodo;

// The position and modifier state belong to the original native message, not to the later
// handoff callback. ScreenPoint is converted once, while the output HWND is still unchanged.
internal readonly record struct EdgeCapsulePointerDown(
    DeviceScreenPoint ScreenPoint, int Message, IntPtr KeyState);
'''
handoff_code='''using System.Diagnostics;

namespace PaperTodo;

// Pending native presses belong to one proxy generation. This is not a mouse/gesture engine:
// it only retains the existing press transfer across that generation's verified handoff retry.
internal sealed class EdgeCapsuleInputHandoff
{
    private sealed record Pending(Func<bool> IsCurrent, Action Deliver, long Created);
    private readonly List<Pending> _pending = new();
    private readonly TimeProvider _clock;
    internal EdgeCapsuleInputHandoff(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    internal int Count => _pending.Count;

    internal void Enqueue(Func<bool> isCurrent, Action deliver)
    {
        Prune();
        _pending.Add(new Pending(isCurrent, deliver, _clock.GetTimestamp()));
    }

    // Called only once native/WPF authority has actually returned, not when a retry is scheduled.
    internal void Complete()
    {
        var pending = _pending.ToArray();
        _pending.Clear(); // Remove ownership BEFORE callbacks can re-enter completion.
        foreach (var item in pending)
        {
            try
            {
                if (Current(item)) item.Deliver();
            }
            catch (Exception error) { Trace.TraceWarning("Edge input transfer failed: {0}", error); }
        }
    }

    internal void Prune() => _pending.RemoveAll(item => !Current(item));
    internal void Cancel() => _pending.Clear();
    private bool Current(Pending item) =>
        _clock.GetElapsedTime(item.Created) <= TimeSpan.FromSeconds(1) && item.IsCurrent();
}

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private EdgeCapsuleInputHandoff? _inputHandoff;
    internal void DeferPointerDown(Func<bool> isCurrent, Action deliver) =>
        (_inputHandoff ??= new()).Enqueue(isCurrent, deliver);
    internal void CompleteDeferredPointerInput() => _inputHandoff?.Complete();
}
'''
for root in (CORE,FULL):
 put(root,'src/EdgeCapsulePointerDown.cs',input_code)
 put(root,'src/EdgeCapsuleInputHandoff.cs',handoff_code)
 for path in ['src/EdgeCapsuleQueueProxyWindow.cs','src/EdgeCapsuleQueueCompositionProxy.Core.cs','tests/PaperTodo.EdgeTitleChecks/ProxyInputReadinessChecks.cs']:
  s=(root/path).read_text().replace('Action<DeviceScreenPoint, int>','Action<EdgeCapsulePointerDown>')
  if 'Checks' in path:
   s=s.replace('((_, message) => received.Add(message))','(input => received.Add(input.Message))').replace('false, _ => true, route','false, _ => true, route').replace('_ => false, (_, _) => { }','_ => false, _ => { }')
  put(root,path,s)
 edit(root,'src/EdgeCapsuleQueueProxyWindow.cs','''                    if (GetCursorPos(out var cursor))
                    {
                        _interactionRequested(
                            new DeviceScreenPoint(cursor.X, cursor.Y),
                            message);
                    }''','''                    var packedPoint = lParam.ToInt64();
                    var cursor = new CursorPoint
                    {
                        X = unchecked((short)(packedPoint & 0xFFFF)),
                        Y = unchecked((short)((packedPoint >> 16) & 0xFFFF))
                    };
                    if (ClientToScreen(hwnd, ref cursor))
                    {
                        _interactionRequested(new EdgeCapsulePointerDown(
                            new DeviceScreenPoint(cursor.X, cursor.Y), message, wParam));
                    }''')
 edit(root,'src/EdgeCapsuleQueueProxyWindow.cs','private static extern bool GetCursorPos(out CursorPoint point);','private static extern bool ClientToScreen(IntPtr hwnd, ref CursorPoint point);')
 edit(root,'src/EdgeCapsuleQueueCompositionProxy.Runtime.cs','''(point, message) =>
                        host?.Current?.HandleInteractionRequested(point, message)''','''input => host?.Current?.HandleInteractionRequested(input)''')
 edit(root,'src/EdgeCapsuleQueueCompositionProxy.Routing.cs','''    private void HandleInteractionRequested(
        DeviceScreenPoint point,
        int message)''','''    private void HandleInteractionRequested(EdgeCapsulePointerDown input)''')
 edit(root,'src/EdgeCapsuleQueueCompositionProxy.Routing.cs','_interactionRequested(point, message);','_interactionRequested(input);')
 edit(root,'src/EdgeCapsuleQueueCompositionProxy.Routing.cs','''        if (_disposed ||
            _starting ||
            _finishing ||''','''        if (_disposed ||
            _inputHandoff is { Count: > 0 } ||
            _starting ||
            _finishing ||''')
 edit(root,'src/EdgeCapsuleQueueCompositionProxy.Routing.cs','''    public void ScheduleCompletionRetry(bool success)
    {
''','''    public void ScheduleCompletionRetry(bool success)
    {
        _inputHandoff?.Prune();
''')
 edit(root,'src/EdgeCapsuleQueueCompositionProxy.Handoff.cs','''    public void ForceDisposeForShutdown()
    {
''','''    public void ForceDisposeForShutdown()
    {
        _inputHandoff?.Cancel();
''')
 path='src/PaperWindow.EdgeCapsuleQueueProxy.cs';s=(root/path).read_text();anchor='    internal IntPtr EdgeCapsuleQueueProxySourceHandle =>'
 a=s.index(anchor);s=s[:a]+'''    internal Func<bool> CaptureEdgeCapsulePointerInputValidity()
    {
        var source = EdgeCapsuleQueueProxySourceHandle;
        var body = _bodySessionGeneration;
        var preview = _edgeCapsulePreviewRequest;
        return () => CanRouteEdgeCapsuleQueueProxyInput && !IsClosed &&
            source != IntPtr.Zero && EdgeCapsuleQueueProxySourceHandle == source &&
            _bodySessionGeneration == body && ReferenceEquals(_edgeCapsulePreviewRequest, preview);
    }

'''+s[a:];put(root,path,s)
 path='src/AppController.EdgeCapsuleQueueProxy.cs'
 edit(root,path,'''interactionRequested: (point, message) =>
                CompleteAndRouteEdgeCapsuleQueueProxyInput(
                    plan.QueueKey,
                    point,
                    message),''','''interactionRequested: input =>
                CompleteAndRouteEdgeCapsuleQueueProxyInput(plan.QueueKey, input),''')
 mutation='        _edgePrewarm?.Cancel(queueKey);\n        _edgePrewarm?.NotifyInteraction();\n' if root==FULL else ''
 method(root,path,'CompleteAndRouteEdgeCapsuleQueueProxyInput','''    private void CompleteAndRouteEdgeCapsuleQueueProxyInput(
        string queueKey, EdgeCapsulePointerDown input)
    {
'''+mutation+'''        if (!_edgeCapsuleQueueCompositionProxies.TryGetValue(queueKey, out var proxy)) return;
        if (proxy.TryResolveInputTarget(input.ScreenPoint, out var handle, out var endpoint))
        {
            var target = proxy.Members.FirstOrDefault(member => member.SourceHandle == handle)?.Window;
            if (target != null)
            {
                var paperId = target.EdgeCapsulePreviewPaperId;
                var valid = target.CaptureEdgeCapsulePointerInputValidity();
                proxy.DeferPointerDown(
                    () => !IsExiting && _windows.TryGetValue(paperId, out var current) &&
                        ReferenceEquals(current, target) && valid(),
                    () =>
                    {
                        if (!WindowNative.TryPostMouseButtonDown(handle, input, endpoint))
                            Trace.TraceWarning("Edge input could not be posted after handoff: {0}", paperId);
                    });
            }
        }
        proxy.CompleteNow(success: true);
    }''')
 edit(root,path,'''            try
            {
                current.Dispose();''','''            current.CompleteDeferredPointerInput();
            try
            {
                current.Dispose();''')
 edit(root,path,'''        TraceEdgeCapsuleQueueVisibility(current, "released");
        try''','''        TraceEdgeCapsuleQueueVisibility(current, "released");
        current.CompleteDeferredPointerInput();
        try''')
 path='src/WindowNative.cs'
 method(root,path,'TryPostMouseButtonDown','''    public static bool TryPostMouseButtonDown(
        IntPtr handle, EdgeCapsulePointerDown input, DeviceScreenPoint screenPoint)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle) ||
            input.Message is not (0x0201 or 0x0204 or 0x0207)) return false;
        var clientPoint = new CursorPoint
        {
            X = (int)Math.Round(screenPoint.X, MidpointRounding.AwayFromZero),
            Y = (int)Math.Round(screenPoint.Y, MidpointRounding.AwayFromZero)
        };
        if (!ScreenToClient(handle, ref clientPoint)) return false;
        return PostMessage(handle, input.Message, input.KeyState,
            PackScreenPoint(clientPoint.X, clientPoint.Y));
    }''')
 path='src/EdgeCapsuleFrameScheduler.cs'
 edit(root,path,'    private readonly HashSet<EdgeCapsuleNativeBatchGroup> _renderDemandReady = new();','    private readonly HashSet<EdgeCapsuleNativeBatchGroup> _renderDemandReady = new();\n    private readonly HashSet<EdgeCapsuleNativeBatchGroup> _renderDemandBlocked = new();')
 old='''        _renderDemandReady.Clear();
        foreach (var presenter in _presenters)
            if (IsRenderDemandGroupReady(presenter.NativeBatchGroup))
                _renderDemandReady.Add(presenter.NativeBatchGroup);'''
 edit(root,path,old,'        CollectRenderDemandReadyGroups();')
 edit(root,path,'        if (!_presenters.Any(p => IsRenderDemandGroupReady(p.NativeBatchGroup))) return;','        CollectRenderDemandReadyGroups();\n        if (_renderDemandReady.Count == 0) return;')
 s=(root/path).read_text();a=s.index('    private void OnDispatcherShutdown(')
 s=s[:a]+'''    private void CollectRenderDemandReadyGroups()
    {
        _renderDemandReady.Clear();
        _renderDemandBlocked.Clear();
        if (_shutdown || _isTicking || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        foreach (var owner in _pendingReconcileOwners.Keys)
            _renderDemandBlocked.Add(owner.NativeBatchGroup);
        foreach (var presenter in _presenters)
        {
            if (EdgeCapsuleNativeTransactionPolicy.ShouldDeferSharedFrameForNativeApply(presenter.NativeBatchApplyActive))
            {
                _renderDemandReady.Clear();
                return;
            }
            if (presenter.HasActiveTransition && !_renderDemandBlocked.Contains(presenter.NativeBatchGroup))
                _renderDemandReady.Add(presenter.NativeBatchGroup);
        }
    }

'''+s[a:];put(root,path,s)
edit(CORE,'doc/CHANGELOG.en.md','Browsable queues prepare display resources during idle time after startup and display changes. Repeated browsing within a queue, including returning after retraction, reuses those resources to reduce first-use and repeated waits; interaction pauses background preparation, and settled queues avoid unnecessary UI updates. ','')
print('Shared input and readiness fixes applied')
