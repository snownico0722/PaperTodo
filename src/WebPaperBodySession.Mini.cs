using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private WebPluginMiniViewHost? _miniViewHost;

    private void ReleaseIdleMiniView(WebPluginMiniViewHost host)
    {
        if (!ReferenceEquals(_miniViewHost, host) || host.IsPreviewVisible)
        {
            return;
        }
        _miniViewHost = null;
        host.Dispose();
    }

    internal bool HasMiniEntry =>
        !_disposed &&
        !string.IsNullOrWhiteSpace(_manifest.MiniEntryPath);

    internal EdgeCapsulePreviewDescriptor DescribeMiniView(
        EdgeCapsulePreviewContext context,
        Func<EdgeCapsulePreviewContext, EdgeCapsulePreviewSize, FrameworkElement>
            buildFallback)
    {
        var declared = _manifest.MiniSize;
        var size = new EdgeCapsulePreviewSize(
            declared?.Width ?? 320,
            declared?.Height ?? 220);
        return new EdgeCapsulePreviewDescriptor(
            size,
            normalized => GetOrCreateMiniView(
                normalized,
                buildFallback(context, normalized)),
            visible => _miniViewHost?.SetVisible(visible),
            DeferContentCreation: true);
    }

    private FrameworkElement GetOrCreateMiniView(
        EdgeCapsulePreviewSize size,
        FrameworkElement fallback)
    {
        if (_miniViewHost == null || !_miniViewHost.Matches(size))
        {
            _miniViewHost?.Dispose();
            _miniViewHost = new WebPluginMiniViewHost(this, size, fallback);
        }
        else
        {
            _miniViewHost.ReplaceFallback(fallback);
        }
        return _miniViewHost;
    }

    private void UpdateStateFromWebSurface(
        JsonElement payload,
        WebPluginMiniViewHost? sourceMini)
    {
        if (_paperRuntimeOwnsState)
        {
            return;
        }

        var nextStateJson = payload.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : payload.GetRawText();
        if (string.Equals(nextStateJson, _stateJson, StringComparison.Ordinal))
        {
            return;
        }

        _context.SaveStateJson(nextStateJson);
        _stateJson = nextStateJson;
        if (sourceMini != null)
        {
            SendStateChanged();
        }
        if (!ReferenceEquals(_miniViewHost, sourceMini))
        {
            _miniViewHost?.SendStateChanged();
        }
    }

    private void SendStateChanged() => Send(new
    {
        type = "stateChanged",
        state = ParseState(_stateJson),
        stateVersion = _context.TargetStateVersion
    });

    private sealed class WebPluginMiniViewHost : Grid, IDisposable
    {
        private readonly record struct InteractiveRegion(
            double Left,
            double Top,
            double Right,
            double Bottom)
        {
            public bool Contains(double x, double y) =>
                x >= Left && x < Right && y >= Top && y < Bottom;
        }

        // CoreWebView2 can front-load tens of milliseconds of UI-thread work the first time a
        // mini surface is initialized. Keep that cold bootstrap out of the same 200 ms window in
        // which the fallback card is morphing/moving; the already-rendered fallback remains the
        // visible authority until the WebView is ready.
        private const int ColdInitializationDeferralMilliseconds =
            EdgeCapsuleLayout.SlotMoveMilliseconds + 20;
        private const int MaximumInteractiveRegions = 128;
        private static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromSeconds(45);

        private readonly WebPaperBodySession _owner;
        private readonly EdgeCapsulePreviewSize _size;
        private readonly WebView2CompositionControl _webView;
        private readonly CancellationTokenSource _lifetime = new();
        private DispatcherTimer? _idleReleaseTimer;
        private FrameworkElement _fallback;
        private InteractiveRegion[] _interactiveRegions = Array.Empty<InteractiveRegion>();
        private string _expectedOrigin = "";
        private bool _visible;
        private bool _initializationQueued;
        private int _initializationDeferralGeneration;
        private bool _initializationStarted;
#if DEBUG
        private long _initializationQueuedAtTimestamp;
#endif
        private bool _documentReady;
        private bool _pluginReportedReady;
        private bool _pluginReady;
        private bool _surfaceReady;
        private bool _surfaceRefreshRequired;
        private bool _disposed;
        private int _documentGeneration;
        private int _presentationGeneration;
        private ulong _documentNavigationId;
        private bool _hasDocumentNavigation;
        private int _queuedShowGeneration = -1;
        private int _readyProbeGeneration = -1;
        private string? _readyProbeToken;
        private int _surfaceProbeGeneration = -1;
        private string? _surfaceProbeToken;
        private EventHandler? _surfaceRevealRenderingHandler;
#if DEBUG
        private long _surfaceRecoveryStartedAtTimestamp;
#endif

        public WebPluginMiniViewHost(
            WebPaperBodySession owner,
            EdgeCapsulePreviewSize size,
            FrameworkElement fallback)
        {
            _owner = owner;
            _size = size;
            _fallback = fallback;
            PrepareFallbackForFirstDisplay(_fallback);
            Background = Brushes.Transparent;
            ClipToBounds = true;

            _webView = new WebView2CompositionControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            _webView.SetValue(UIElement.OpacityProperty, 0.0);
            // Web mini surfaces are host-owned by default. The bridge mirrors only explicitly
            // declared data-papertodo-interactive rectangles; WPF evaluates the current pointer
            // against those rectangles synchronously before the next click reaches the host.
            PaperMiniViewInteraction.SetConsumesPointer(_webView, false);
            Children.Add(_fallback);
            Children.Add(_webView);
            Panel.SetZIndex(_webView, 2);

            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            _webView.PreviewMouseMove += OnWebViewPreviewMouseMove;
            _webView.MouseLeave += OnWebViewMouseLeave;
        }

        public bool IsPreviewVisible => _visible;

        public bool Matches(EdgeCapsulePreviewSize size) =>
            Math.Abs(_size.WidthDip - size.WidthDip) <= 0.001 &&
            Math.Abs(_size.HeightDip - size.HeightDip) <= 0.001;

        public void ReplaceFallback(FrameworkElement fallback)
        {
            if (ReferenceEquals(_fallback, fallback))
            {
                return;
            }
            Children.Remove(_fallback);
            _fallback = fallback;
            PrepareFallbackForFirstDisplay(_fallback);
            Children.Insert(0, fallback);
            _fallback.Visibility = _pluginReady && _surfaceReady
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private static void PrepareFallbackForFirstDisplay(FrameworkElement fallback)
        {
            if (fallback is EdgeCapsuleLivePreviewView livePreview)
            {
                livePreview.PrepareForFirstDisplay();
            }
        }

        public void SetVisible(bool visible)
        {
            if (_disposed)
            {
                return;
            }

            var presentationGeneration = AdvancePresentationGeneration();
            CancelSurfaceRecovery(restorePreviousSurface: !visible);
            _visible = visible;

            if (visible)
            {
                StopIdleReleaseTimer();
                QueueInitialization();
            }
            else
            {
                _surfaceRefreshRequired =
                    _documentReady &&
                    _pluginReportedReady &&
                    _pluginReady &&
                    _webView.CoreWebView2 != null;
                PaperMiniViewInteraction.SetConsumesPointer(_webView, false);
                _initializationQueued = false;
                _initializationDeferralGeneration++;
                Send(new { type = "commitRequested" });
                RestartIdleReleaseTimer();
            }

            // Deliver the public lifecycle event before a host-private surface probe. A plugin that
            // chooses to repaint on resume can do so naturally; a paused/static plugin still gets
            // host-owned recovery without seeing the private probe itself.
            Send(new { type = "miniVisibilityChanged", visible });
            if (visible && _pluginReportedReady)
            {
                QueuePluginPresentation(presentationGeneration);
            }
            UpdatePresentation();
        }

        private void RestartIdleReleaseTimer()
        {
            if (_disposed)
            {
                return;
            }
            _idleReleaseTimer ??= new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = IdleReleaseDelay
            };
            _idleReleaseTimer.Tick -= OnIdleReleaseTimerTick;
            _idleReleaseTimer.Tick += OnIdleReleaseTimerTick;
            _idleReleaseTimer.Stop();
            _idleReleaseTimer.Start();
        }

        private void StopIdleReleaseTimer()
        {
            _idleReleaseTimer?.Stop();
        }

        private void OnIdleReleaseTimerTick(object? sender, EventArgs e)
        {
            StopIdleReleaseTimer();
            if (!_disposed && !_visible)
            {
                _owner.ReleaseIdleMiniView(this);
            }
        }

        public void SendStateChanged() => Send(new
        {
            type = "stateChanged",
            state = ParseState(_owner._stateJson),
            stateVersion = _owner._context.TargetStateVersion
        });

        public void SendSettingsChanged() => Send(new
        {
            type = "settingsChanged",
            settings = ParseState(_owner._settingsJson)
        });

        public void SendThemeChanged(string type) => Send(new
        {
            type,
            theme = ThemePayload(_owner._theme)
        });

        private void OnLoaded(object sender, RoutedEventArgs e) =>
            QueueInitialization();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            QueueInitialization();
            RefreshPointerOwnership();
        }

        private void OnWebViewPreviewMouseMove(object sender, MouseEventArgs e) =>
            RefreshPointerOwnership(e.GetPosition(_webView));

        private void OnWebViewMouseLeave(object sender, MouseEventArgs e) =>
            PaperMiniViewInteraction.SetConsumesPointer(_webView, false);

        private void QueueInitialization()
        {
            if (_initializationQueued ||
                _initializationStarted ||
                _disposed ||
                !_visible)
            {
                return;
            }

            _initializationQueued = true;
            var generation = ++_initializationDeferralGeneration;
#if DEBUG
            _initializationQueuedAtTimestamp =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=init-deferred " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                $"delayMs={ColdInitializationDeferralMilliseconds}");
#endif
            _ = StartInitializationAfterTransitionAsync(
                generation,
                _lifetime.Token);
        }

        private async Task StartInitializationAfterTransitionAsync(
            int generation,
            CancellationToken token)
        {
            try
            {
                await Task.Delay(
                        ColdInitializationDeferralMilliseconds,
                        token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                // The delay protects the current queue transition; ApplicationIdle then keeps the
                // cold CoreWebView2 bootstrap behind any newer Render/Input work. If browsing has
                // already moved elsewhere, the visibility generation makes this callback a no-op.
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    (Action)(() =>
                    {
                        if (_disposed ||
                            generation != _initializationDeferralGeneration ||
                            !_visible)
                        {
                            return;
                        }
                        _initializationQueued = false;
#if DEBUG
                        EdgeCapsulePerformanceDiagnostics.Trace(
                            $"preview.webmini phase=init-dispatch " +
                            $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                            $"waitMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_initializationQueuedAtTimestamp):F3}");
                        _initializationQueuedAtTimestamp = 0;
#endif
                        TryStartInitialization();
                    }));
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shutdown can race a delayed cold bootstrap. The fallback needs no
                // cleanup beyond the host/session lifetime that is already ending.
            }
        }

        private void TryStartInitialization()
        {
            if (_initializationStarted ||
                _disposed ||
                !_visible ||
                !IsLoaded ||
                ActualWidth <= 0 ||
                ActualHeight <= 0)
            {
                return;
            }

            _initializationStarted = true;
            _ = InitializeAsync(_lifetime.Token);
        }

        private async Task InitializeAsync(CancellationToken token)
        {
            try
            {
                var environment = await WebPaperBodySession.GetPluginEnvironmentAsync(
                    _owner._manifest.DirectoryPath);
                token.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return;
                }

#if DEBUG
                var coreInitializationStartedAt =
                    EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                await _webView.EnsureCoreWebView2Async(environment);
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"preview.webmini phase=core-ready " +
                    $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                    $"coreMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(coreInitializationStartedAt):F3}");
#endif
                token.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return;
                }

                var core = _webView.CoreWebView2
                    ?? throw new InvalidOperationException(
                        "WebView2 initialization returned no CoreWebView2 instance.");
                core.Settings.AreDefaultContextMenusEnabled = false;
#if DEBUG
                core.Settings.AreDevToolsEnabled = true;
#else
                core.Settings.AreDevToolsEnabled = false;
#endif
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;

                var hostName = WebHostName(_owner._manifest.Id);
                _expectedOrigin = $"https://{hostName}";
                var webRoot = Path.GetDirectoryName(_owner._manifest.EntryPath)
                    ?? throw new InvalidOperationException(
                        "Web plugin entry has no containing directory.");
                var relativeEntry = Path.GetRelativePath(
                        webRoot,
                        _owner._manifest.MiniEntryPath)
                    .Replace('\\', '/');
                var miniUri = new Uri(
                    $"{_expectedOrigin}/{Uri.EscapeDataString(relativeEntry).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");

                core.WebMessageReceived += OnWebMessageReceived;
                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.ProcessFailed += OnProcessFailed;
                core.DownloadStarting += WebPaperBodySession.OnDownloadStarting;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    BuildMiniBridgeScript(
                        _expectedOrigin,
                        persistentStateWritable: !_owner._paperRuntimeOwnsState));
                token.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    return;
                }

                core.SetVirtualHostNameToFolderMapping(
                    hostName,
                    webRoot,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                _webView.Source = miniUri;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch
            {
                ShowFallback();
            }
        }

        private static string BuildMiniBridgeScript(
            string expectedOrigin,
            bool persistentStateWritable)
        {
            var originJson = JsonSerializer.Serialize(expectedOrigin);
            var stateWritableJson = persistentStateWritable ? "true" : "false";
            return $$"""
                (() => {
                  const expectedOrigin = {{originJson}};
                  const persistentStateWritable = {{stateWritableJson}};
                  if (window !== window.top || location.origin !== expectedOrigin || window.papertodo) return;
                  const listeners = new Set();
                  const pending = new Map();
                  let sequence = 0;
                  let stateProvider = null;
                  let markHostReady;
                  const hostReady = new Promise(resolve => { markHostReady = resolve; });
                  const post = (type, payload = null) => window.chrome.webview.postMessage({ type, payload });

                  const interactiveSelector = '[data-papertodo-interactive]';
                  let interactiveRegionFrame = 0;
                  let interactiveRegionSignature = '';
                  const observedInteractiveElements = new Set();
                  const interactiveResizeObserver = typeof ResizeObserver === 'function'
                    ? new ResizeObserver(() => queueInteractiveRegions())
                    : null;
                  const syncObservedInteractiveElements = elements => {
                    if (!interactiveResizeObserver) return;
                    const next = new Set(elements);
                    for (const element of [...observedInteractiveElements]) {
                      if (next.has(element)) continue;
                      interactiveResizeObserver.unobserve(element);
                      observedInteractiveElements.delete(element);
                    }
                    for (const element of elements) {
                      if (observedInteractiveElements.has(element)) continue;
                      observedInteractiveElements.add(element);
                      interactiveResizeObserver.observe(element);
                    }
                  };
                  const publishInteractiveRegions = () => {
                    const viewportWidth = Math.max(1, window.innerWidth || document.documentElement?.clientWidth || 1);
                    const viewportHeight = Math.max(1, window.innerHeight || document.documentElement?.clientHeight || 1);
                    const elements = [...document.querySelectorAll(interactiveSelector)];
                    syncObservedInteractiveElements(elements);
                    const regions = [];
                    for (const element of elements) {
                      const style = getComputedStyle(element);
                      if (style.display === 'none' || style.visibility === 'hidden' || style.pointerEvents === 'none') continue;
                      const rect = element.getBoundingClientRect();
                      const left = Math.max(0, Math.min(viewportWidth, rect.left));
                      const top = Math.max(0, Math.min(viewportHeight, rect.top));
                      const right = Math.max(0, Math.min(viewportWidth, rect.right));
                      const bottom = Math.max(0, Math.min(viewportHeight, rect.bottom));
                      if (right <= left || bottom <= top) continue;
                      regions.push({
                        left: left / viewportWidth,
                        top: top / viewportHeight,
                        right: right / viewportWidth,
                        bottom: bottom / viewportHeight
                      });
                    }
                    const signature = JSON.stringify(regions);
                    if (signature === interactiveRegionSignature) return;
                    interactiveRegionSignature = signature;
                    post('miniInteractiveRegions', { regions });
                  };
                  function queueInteractiveRegions() {
                    if (interactiveRegionFrame) return;
                    interactiveRegionFrame = requestAnimationFrame(() => {
                      interactiveRegionFrame = 0;
                      publishInteractiveRegions();
                    });
                  }
                  const startInteractiveRegionTracking = () => {
                    const root = document.documentElement;
                    if (!root) return;
                    new MutationObserver(queueInteractiveRegions).observe(root, {
                      subtree: true,
                      childList: true,
                      attributes: true,
                      characterData: true
                    });
                    publishInteractiveRegions();
                  };
                  if (document.readyState === 'loading') {
                    document.addEventListener('DOMContentLoaded', startInteractiveRegionTracking, { once: true });
                  } else {
                    startInteractiveRegionTracking();
                  }
                  document.addEventListener('scroll', queueInteractiveRegions, true);
                  window.addEventListener('resize', queueInteractiveRegions);
                  window.addEventListener('load', queueInteractiveRegions);

                  const saveState = state => {
                    if (!persistentStateWritable) {
                      throw new Error('Persistent paper state is owned by paperRuntime; send a runtime command instead.');
                    }
                    post('saveState', state ?? {});
                  };
                  const flushState = () => {
                    if (typeof stateProvider !== 'function') return;
                    try { saveState(stateProvider()); } catch { }
                  };
                  const request = async (method, params = {}) => {
                    await hostReady;
                    const requestId = `m${++sequence}`;
                    return new Promise((resolve, reject) => {
                      pending.set(requestId, { resolve, reject });
                      post('hostRequest', { requestId, method: String(method ?? ''), params: params ?? {} });
                    });
                  };
                  const paper = Object.freeze({
                    setTitle(title) { post('setTitle', String(title ?? '')); },
                    setHeaderText(text) { post('setHeaderText', String(text ?? '')); },
                    setCapsulePresentation(value) { post('setCapsulePresentation', value ?? null); }
                  });
                  const body = Object.freeze({
                    markDirty() { post('markDirty'); },
                    openExternal(url) { post('openExternal', String(url ?? '')); }
                  });
                  const runtime = Object.freeze({
                    post(message) { return request('runtime.post', { message: message ?? null }); }
                  });
                  let miniReady = false;
                  const mini = Object.freeze({
                    ready() {
                      miniReady = true;
                      post('miniReady');
                    }
                  });
                  window.papertodo = Object.freeze({
                    surface: 'mini', paper, body, mini, runtime,
                    workspace: Object.freeze({ request }),
                    post, request, saveState, flushState,
                    registerStateProvider(provider) {
                      if (!persistentStateWritable) {
                        throw new Error('Persistent paper state providers belong to paperRuntime for this plugin.');
                      }
                      stateProvider = typeof provider === 'function' ? provider : null;
                      return () => { if (stateProvider === provider) stateProvider = null; };
                    },
                    onEvent(listener) {
                      if (typeof listener !== 'function') return () => {};
                      listeners.add(listener);
                      return () => listeners.delete(listener);
                    }
                  });
                  window.chrome.webview.addEventListener('message', event => {
                    const message = event.data;
                    if (message?.type === 'miniSurfacePresentProbe') {
                      const token = String(message.token ?? '');
                      requestAnimationFrame(() => {
                        requestAnimationFrame(() => {
                          post('miniSurfacePresentProbeResult', { token });
                        });
                      });
                      // Host-private presentation bookkeeping must not leak into plugin listeners.
                      return;
                    }
                    if (message?.type === 'initialize') markHostReady();
                    if (message?.type === 'commitRequested') flushState();
                    if (message?.type === 'miniReadyProbe') {
                      post('miniReadyProbeResult', {
                        token: String(message.token ?? ''),
                        ready: miniReady
                      });
                    }
                    if (message?.type === 'hostResponse') {
                      const waiter = pending.get(message.requestId);
                      if (waiter) {
                        pending.delete(message.requestId);
                        if (message.ok) waiter.resolve(message.result);
                        else {
                          const error = new Error(message.error?.message ?? 'PaperTodo host request failed.');
                          error.code = message.error?.code ?? 'host_error';
                          waiter.reject(error);
                        }
                      }
                    }
                    for (const listener of [...listeners]) {
                      try { listener(message); } catch { }
                    }
                    window.dispatchEvent(new CustomEvent('papertodo', { detail: message }));
                  });
                  window.addEventListener('beforeunload', flushState);
                  document.addEventListener('visibilitychange', () => {
                    if (document.visibilityState === 'hidden') flushState();
                  });
                })();
                """;
        }

        private void OnNavigationStarting(
            object? sender,
            CoreWebView2NavigationStartingEventArgs e)
        {
            if (!ReferenceEquals(sender, _webView.CoreWebView2))
            {
                return;
            }
            if (!string.IsNullOrEmpty(_expectedOrigin) &&
                Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
                !string.Equals(
                    uri.GetLeftPart(UriPartial.Authority),
                    _expectedOrigin,
                    StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                try
                {
                    _owner._context.OpenExternal(uri.AbsoluteUri);
                }
                catch
                {
                }
                return;
            }

            CancelQueuedShowPlugin();
            _documentGeneration++;
            _documentNavigationId = e.NavigationId;
            _hasDocumentNavigation = true;
            _documentReady = false;
            _pluginReportedReady = false;
            _pluginReady = false;
            _surfaceReady = false;
            _surfaceRefreshRequired = false;
            ClearInteractiveRegions();
            ShowFallback();
        }

        private void OnNavigationCompleted(
            object? sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
                !_hasDocumentNavigation ||
                e.NavigationId != _documentNavigationId)
            {
                // A host-cancelled external navigation still raises NavigationCompleted. It does
                // not replace the healthy mini document and must not tear its painted surface down.
                return;
            }
            if (!e.IsSuccess)
            {
                _documentReady = false;
                ShowFallback();
                return;
            }
            _documentReady = true;
            SendInitialize();
            // mini.ready() is allowed before NavigationCompleted. The current document's bridge
            // remembers that call. A challenge sent to the currently committed document recovers
            // it without allowing an old same-origin document to authorize the new generation.
            RequestMiniReadyProbe();
        }

        private void OnProcessFailed(
            object? sender,
            CoreWebView2ProcessFailedEventArgs e)
        {
            if (!ReferenceEquals(sender, _webView.CoreWebView2))
            {
                return;
            }
            CancelQueuedShowPlugin();
            _documentGeneration++;
            _hasDocumentNavigation = false;
            _documentReady = false;
            _pluginReportedReady = false;
            _pluginReady = false;
            _surfaceReady = false;
            _surfaceRefreshRequired = false;
            ClearInteractiveRegions();
            ShowFallback();
        }

        private void OnWebMessageReceived(
            object? sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
                !IsAllowedSource(e.Source))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    return;
                }
                var type = typeElement.GetString() ?? "";
                var payload = root.TryGetProperty("payload", out var value)
                    ? value
                    : default;
                switch (type)
                {
                    case "miniSurfacePresentProbeResult":
                        if (!_documentReady ||
                            !_pluginReportedReady ||
                            !_pluginReady ||
                            !_visible ||
                            _surfaceProbeGeneration != _presentationGeneration ||
                            !string.Equals(
                                PayloadString(payload, "token"),
                                _surfaceProbeToken,
                                StringComparison.Ordinal))
                        {
                            break;
                        }
                        var surfaceGeneration = _surfaceProbeGeneration;
                        _surfaceProbeGeneration = -1;
                        _surfaceProbeToken = null;
#if DEBUG
                        EdgeCapsulePerformanceDiagnostics.Trace(
                            $"preview.webmini phase=surface-probe-ack " +
                            $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                            $"generation={surfaceGeneration}");
#endif
                        QueueSurfaceRevealAfterComposition(surfaceGeneration);
                        break;
                    case "miniReady":
                        // Do not trust the source URL alone: a retiring same-origin document can
                        // still have a queued message. Challenge the currently committed document
                        // and promote only its answer.
                        RequestMiniReadyProbe();
                        break;
                    case "miniReadyProbeResult":
                        if (!_documentReady ||
                            _readyProbeGeneration != _documentGeneration ||
                            !string.Equals(
                                PayloadString(payload, "token"),
                                _readyProbeToken,
                                StringComparison.Ordinal) ||
                            !payload.TryGetProperty("ready", out var readyValue) ||
                            readyValue.ValueKind != JsonValueKind.True)
                        {
                            break;
                        }
                        _readyProbeGeneration = -1;
                        _readyProbeToken = null;
                        _pluginReportedReady = true;
                        QueuePluginPresentation(_presentationGeneration);
                        break;
                    case "miniInteractiveRegions":
                        UpdateInteractiveRegions(payload);
                        break;
                    case "saveState":
                        if (!_owner._paperRuntimeOwnsState)
                        {
                            _owner.UpdateStateFromWebSurface(payload, this);
                        }
                        break;
                    case "setTitle":
                        _owner._context.SetTitle(ReadPayloadString(payload));
                        break;
                    case "setHeaderText":
                        _owner._context.Paper.SetHeaderText(ReadPayloadString(payload));
                        break;
                    case "setCapsulePresentation":
                        _owner._context.Paper.SetCapsulePresentation(
                            payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                                ? null
                                : JsonSerializer.Deserialize<PaperCapsulePresentation>(
                                    payload.GetRawText(),
                                    BridgeJsonOptions));
                        break;
                    case "markDirty":
                        _owner._context.MarkDirty();
                        break;
                    case "openExternal":
                        _owner._context.OpenExternal(ReadPayloadString(payload));
                        break;
                    case "hostRequest":
                        HandleHostRequest(payload);
                        break;
                }
            }
            catch
            {
                // A malformed mini message cannot affect the body session.
            }
        }

        private void UpdateInteractiveRegions(JsonElement payload)
        {
            if (_disposed ||
                payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("regions", out var regionsValue) ||
                regionsValue.ValueKind != JsonValueKind.Array)
            {
                ClearInteractiveRegions();
                return;
            }

            var regions = new List<InteractiveRegion>();
            foreach (var region in regionsValue.EnumerateArray())
            {
                if (regions.Count >= MaximumInteractiveRegions)
                {
                    break;
                }
                if (region.ValueKind != JsonValueKind.Object ||
                    !TryReadFiniteCoordinate(region, "left", out var left) ||
                    !TryReadFiniteCoordinate(region, "top", out var top) ||
                    !TryReadFiniteCoordinate(region, "right", out var right) ||
                    !TryReadFiniteCoordinate(region, "bottom", out var bottom))
                {
                    continue;
                }

                left = Math.Clamp(left, 0, 1);
                top = Math.Clamp(top, 0, 1);
                right = Math.Clamp(right, 0, 1);
                bottom = Math.Clamp(bottom, 0, 1);
                if (right <= left || bottom <= top)
                {
                    continue;
                }
                regions.Add(new InteractiveRegion(left, top, right, bottom));
            }

            _interactiveRegions = regions.ToArray();
            RefreshPointerOwnership();
        }

        private static bool TryReadFiniteCoordinate(
            JsonElement source,
            string propertyName,
            out double value)
        {
            value = 0;
            return source.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.Number &&
                property.TryGetDouble(out value) &&
                double.IsFinite(value);
        }

        private void ClearInteractiveRegions()
        {
            _interactiveRegions = Array.Empty<InteractiveRegion>();
            PaperMiniViewInteraction.SetConsumesPointer(_webView, false);
        }

        private void RefreshPointerOwnership(Point? localPoint = null)
        {
            var interactive = false;
            if (!_disposed &&
                _visible &&
                _pluginReady &&
                _surfaceReady &&
                _webView.IsHitTestVisible &&
                _webView.IsMouseOver &&
                _interactiveRegions.Length > 0 &&
                _webView.ActualWidth > 0 &&
                _webView.ActualHeight > 0)
            {
                var point = localPoint ?? Mouse.GetPosition(_webView);
                if (point.X >= 0 && point.Y >= 0 &&
                    point.X < _webView.ActualWidth &&
                    point.Y < _webView.ActualHeight)
                {
                    var x = point.X / _webView.ActualWidth;
                    var y = point.Y / _webView.ActualHeight;
                    interactive = _interactiveRegions.Any(region => region.Contains(x, y));
                }
            }
            PaperMiniViewInteraction.SetConsumesPointer(_webView, interactive);
        }

        private void HandleHostRequest(JsonElement payload)
        {
            var requestId = PayloadString(payload, "requestId");
            var generation = _documentGeneration;
            try
            {
                var method = PayloadString(payload, "method");
                var parameters = payload.ValueKind == JsonValueKind.Object &&
                                 payload.TryGetProperty("params", out var paramsValue)
                    ? paramsValue
                    : JsonSerializer.SerializeToElement(new { });
                var result = _owner.ExecuteMiniHostRequest(method, parameters);
                if (generation != _documentGeneration)
                {
                    return;
                }
                Send(new { type = "hostResponse", requestId, ok = true, result });
            }
            catch (PaperTodoPluginException ex)
            {
                if (generation == _documentGeneration)
                {
                    Send(new
                    {
                        type = "hostResponse",
                        requestId,
                        ok = false,
                        error = new { code = ex.Code, message = ex.Message }
                    });
                }
            }
            catch
            {
                if (generation == _documentGeneration)
                {
                    Send(new
                    {
                        type = "hostResponse",
                        requestId,
                        ok = false,
                        error = new
                        {
                            code = "host_error",
                            message = "PaperTodo could not complete the plugin request."
                        }
                    });
                }
            }
        }

        private void SendInitialize() => Send(new
        {
            type = "initialize",
            surface = "mini",
            paperId = _owner._context.PaperId,
            providerId = _owner._context.ProviderId,
            apiVersion = _owner._context.ApiVersion,
            state = ParseState(_owner._stateJson),
            stateVersion = _owner._context.StateVersion,
            targetStateVersion = _owner._context.TargetStateVersion,
            settings = ParseState(_owner._settingsJson),
            permissions = _owner._context.GrantedPermissions
                .OrderBy(value => value)
                .ToArray(),
            theme = ThemePayload(_owner._theme),
            visible = _visible,
            presentationVisible = _visible
        });

        private int AdvancePresentationGeneration()
        {
            unchecked
            {
                return ++_presentationGeneration;
            }
        }

        private void QueuePluginPresentation(int presentationGeneration)
        {
            if (_disposed ||
                !_visible ||
                presentationGeneration != _presentationGeneration ||
                !_documentReady ||
                !_pluginReportedReady)
            {
                return;
            }

            if (_surfaceRefreshRequired &&
                _pluginReady &&
                _webView.CoreWebView2 != null)
            {
                BeginSurfaceRecovery(presentationGeneration);
                return;
            }

            QueueShowPlugin();
        }

        private void BeginSurfaceRecovery(int presentationGeneration)
        {
            if (_disposed ||
                !_visible ||
                presentationGeneration != _presentationGeneration ||
                !_documentReady ||
                !_pluginReportedReady ||
                !_pluginReady ||
                _webView.CoreWebView2 == null)
            {
                return;
            }

            CancelQueuedShowPlugin();
            CancelSurfaceRecovery(restorePreviousSurface: false);
            _surfaceRefreshRequired = false;
            _surfaceReady = false;
            _fallback.Visibility = Visibility.Visible;
            _webView.SetValue(UIElement.OpacityProperty, 0.0);
            _webView.IsHitTestVisible = false;
            ClearInteractiveRegions();
#if DEBUG
            _surfaceRecoveryStartedAtTimestamp =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=surface-recovery-begin " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                $"generation={presentationGeneration}");
#endif

            // Drive the recovery through WebView2's own WPF visibility path. Unlike the old
            // workaround, this does not insert/remove elements from the plugin DOM. The preview is
            // still at the start of its host-owned expansion transaction, so this hidden pulse is
            // covered by the compact/fallback presentation rather than becoming a user-visible
            // flash.
            _webView.Visibility = Visibility.Hidden;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                (Action)(() =>
                {
                    if (!IsSurfaceRecoveryCurrent(presentationGeneration))
                    {
                        return;
                    }

                    _webView.Visibility = Visibility.Visible;
                    _webView.InvalidateVisual();
#if DEBUG
                    EdgeCapsulePerformanceDiagnostics.Trace(
                        $"preview.webmini phase=surface-visible " +
                        $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                        $"generation={presentationGeneration}");
#endif
                    RequestSurfacePresentProbe(presentationGeneration);
                }));
        }

        private bool IsSurfaceRecoveryCurrent(int presentationGeneration) =>
            !_disposed &&
            _visible &&
            presentationGeneration == _presentationGeneration &&
            _documentReady &&
            _pluginReportedReady &&
            _pluginReady &&
            _webView.CoreWebView2 != null;

        private void RequestSurfacePresentProbe(int presentationGeneration)
        {
            if (!IsSurfaceRecoveryCurrent(presentationGeneration))
            {
                return;
            }

            _surfaceProbeGeneration = presentationGeneration;
            _surfaceProbeToken = Guid.NewGuid().ToString("N");
            if (TrySendSurfacePresentProbe(_surfaceProbeToken))
            {
                return;
            }

#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=surface-probe-send-failed " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                $"generation={presentationGeneration}");
#endif
            _surfaceProbeGeneration = -1;
            _surfaceProbeToken = null;
            QueueSurfaceRevealAfterComposition(presentationGeneration);
        }

        private bool TrySendSurfacePresentProbe(string token)
        {
            if (!_documentReady || _disposed || _webView.CoreWebView2 == null)
            {
                return false;
            }
            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(
                        new { type = "miniSurfacePresentProbe", token },
                        BridgeJsonOptions));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void QueueSurfaceRevealAfterComposition(int presentationGeneration)
        {
            if (!IsSurfaceRecoveryCurrent(presentationGeneration))
            {
                return;
            }

            CancelSurfaceRevealRenderingHandler();
            EventHandler? renderHandler = null;
            renderHandler = (_, _) =>
            {
                if (renderHandler != null)
                {
                    CompositionTarget.Rendering -= renderHandler;
                }
                if (ReferenceEquals(_surfaceRevealRenderingHandler, renderHandler))
                {
                    _surfaceRevealRenderingHandler = null;
                }
                if (!IsSurfaceRecoveryCurrent(presentationGeneration))
                {
                    return;
                }

                _surfaceReady = true;
                _fallback.Visibility = Visibility.Collapsed;
                _webView.InvalidateVisual();
                UpdatePresentation();
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"preview.webmini phase=surface-recovery-complete " +
                    $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                    $"generation={presentationGeneration} " +
                    $"ms={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_surfaceRecoveryStartedAtTimestamp):F3}");
                _surfaceRecoveryStartedAtTimestamp = 0;
#endif
            };
            _surfaceRevealRenderingHandler = renderHandler;
            CompositionTarget.Rendering += renderHandler;
        }

        private void CancelSurfaceRevealRenderingHandler()
        {
            var handler = _surfaceRevealRenderingHandler;
            _surfaceRevealRenderingHandler = null;
            if (handler != null)
            {
                CompositionTarget.Rendering -= handler;
            }
        }

        private void CancelSurfaceRecovery(bool restorePreviousSurface)
        {
            var hadRecovery =
                _surfaceProbeGeneration >= 0 ||
                _surfaceRevealRenderingHandler != null ||
                _webView.Visibility != Visibility.Visible;
            _surfaceProbeGeneration = -1;
            _surfaceProbeToken = null;
            CancelSurfaceRevealRenderingHandler();
            if (!_disposed)
            {
                _webView.Visibility = Visibility.Visible;
            }
            if (restorePreviousSurface &&
                !_disposed &&
                _documentReady &&
                _pluginReady)
            {
                // Closing can race the hidden/restart phase. Restore the old painted surface so the
                // outgoing host-owned shrink still has content; the next open remains marked for a
                // fresh recovery below.
                _surfaceReady = true;
                _fallback.Visibility = Visibility.Collapsed;
            }
#if DEBUG
            if (hadRecovery)
            {
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"preview.webmini phase=surface-recovery-cancel " +
                    $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_owner._context.PaperId)} " +
                    $"restore={restorePreviousSurface}");
            }
            _surfaceRecoveryStartedAtTimestamp = 0;
#endif
        }

        private void QueueShowPlugin()
        {
            if (!_documentReady ||
                !_pluginReportedReady ||
                !_visible ||
                _disposed)
            {
                return;
            }
            var generation = _presentationGeneration;
            if (_queuedShowGeneration == generation)
            {
                return;
            }

            CancelQueuedShowPlugin();
            _queuedShowGeneration = generation;
            CompositionTarget.Rendering += OnCompositionRendering;
        }

        private void RequestMiniReadyProbe()
        {
            if (!_documentReady || _disposed)
            {
                return;
            }

            _readyProbeGeneration = _documentGeneration;
            _readyProbeToken = Guid.NewGuid().ToString("N");
            Send(new { type = "miniReadyProbe", token = _readyProbeToken });
        }

        private void OnCompositionRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            var generation = _queuedShowGeneration;
            _queuedShowGeneration = -1;
            if (_disposed ||
                generation != _presentationGeneration ||
                !_documentReady ||
                !_pluginReportedReady ||
                !_visible)
            {
                return;
            }

            // This is the first normal reveal for the current presentation. A warm reattach that
            // needs capture recovery bypasses this path until its private browser-frame probe and
            // the following WPF composition pass have both completed.
            _pluginReady = true;
            _surfaceReady = true;
            _surfaceRefreshRequired = false;
            _fallback.Visibility = Visibility.Collapsed;
            UpdatePresentation();
        }

        private void CancelQueuedShowPlugin()
        {
            if (_queuedShowGeneration < 0)
            {
                return;
            }
            CompositionTarget.Rendering -= OnCompositionRendering;
            _queuedShowGeneration = -1;
        }

        private bool IsAllowedSource(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(
                uri.GetLeftPart(UriPartial.Authority),
                _expectedOrigin,
                StringComparison.OrdinalIgnoreCase);

        private void UpdatePresentation()
        {
            // The edge host owns the outgoing cross-fade. Keep the last painted Web frame visible
            // after miniVisibilityChanged(false); only input stops immediately. Hiding the WebView
            // here would manufacture an empty frame while the card is still shrinking.
            var painted =
                _documentReady &&
                _pluginReady &&
                _surfaceReady &&
                !_disposed;
            _webView.SetValue(UIElement.OpacityProperty, painted ? 1.0 : 0.0);
            _webView.IsHitTestVisible = painted && _visible;
            RefreshPointerOwnership();
            if (!_pluginReady || !_surfaceReady)
            {
                _fallback.Visibility = Visibility.Visible;
            }
        }

        private void ShowFallback()
        {
            if (_disposed)
            {
                return;
            }
            AdvancePresentationGeneration();
            CancelSurfaceRecovery(restorePreviousSurface: false);
            CancelQueuedShowPlugin();
            _readyProbeGeneration = -1;
            _readyProbeToken = null;
            _pluginReady = false;
            _pluginReportedReady = false;
            _surfaceReady = false;
            _surfaceRefreshRequired = false;
            ClearInteractiveRegions();
            _fallback.Visibility = Visibility.Visible;
            _webView.SetValue(UIElement.OpacityProperty, 0.0);
            _webView.IsHitTestVisible = false;
        }

        private void Send(object value)
        {
            if (!_documentReady || _disposed || _webView.CoreWebView2 == null)
            {
                return;
            }
            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(value, BridgeJsonOptions));
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            // Best effort only; reliable durability is saveState-at-mutation time.
            Send(new { type = "commitRequested" });
            StopIdleReleaseTimer();
            if (_idleReleaseTimer != null)
            {
                _idleReleaseTimer.Tick -= OnIdleReleaseTimerTick;
                _idleReleaseTimer = null;
            }
            AdvancePresentationGeneration();
            CancelSurfaceRecovery(restorePreviousSurface: false);
            _disposed = true;
            ClearInteractiveRegions();
            CancelQueuedShowPlugin();
            _lifetime.Cancel();
            Loaded -= OnLoaded;
            SizeChanged -= OnSizeChanged;
            _webView.PreviewMouseMove -= OnWebViewPreviewMouseMove;
            _webView.MouseLeave -= OnWebViewMouseLeave;
            if (_webView.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnWebMessageReceived;
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.ProcessFailed -= OnProcessFailed;
                core.DownloadStarting -= WebPaperBodySession.OnDownloadStarting;
            }
            Children.Remove(_webView);
            try
            {
                _webView.Dispose();
            }
            catch
            {
            }
            _lifetime.Dispose();
        }
    }
}
