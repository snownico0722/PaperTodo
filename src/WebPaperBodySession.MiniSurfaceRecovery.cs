using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    // WebView2CompositionControl mirrors WPF IsVisible into CoreWebView2Controller.IsVisible.
    // A warm mini can keep its document/plugin readiness while the capture-backed presentation
    // loses its last frame across preview detach/reattach. Recover only that presentation lifetime:
    // after a mini was physically unloaded while logically hidden, briefly make the WebView itself
    // Hidden and restore it after WPF has had a render opportunity. This restarts WebView2's own
    // visibility/presentation path without executing script, mutating plugin DOM/state or reloading.
    private static readonly ConditionalWeakTable<WebPluginMiniViewHost, MiniSurfaceRecoveryState>
        MiniSurfaceRecoveryStates = new();

    private sealed class MiniSurfaceRecoveryState
    {
        public int Generation;
        public bool NeedsRecovery;
        public bool VisibilityCycleActive;
        public Visibility RestoreVisibility = Visibility.Visible;
    }

    static WebPaperBodySession()
    {
        EventManager.RegisterClassHandler(
            typeof(WebPluginMiniViewHost),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWebMiniSurfaceLoaded),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(WebPluginMiniViewHost),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnWebMiniSurfaceUnloaded),
            handledEventsToo: true);
    }

    private static void OnWebMiniSurfaceUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebPluginMiniViewHost host)
        {
            return;
        }

        var state = MiniSurfaceRecoveryStates.GetOrCreateValue(host);
        unchecked
        {
            state.Generation++;
        }

        // Never leave the control locally Hidden if a rapid close/reparent invalidates an in-flight
        // visibility cycle before its queued restore callback runs.
        RestoreMiniSurfaceVisibility(host, state);

        // Loaded/Unloaded can also occur for WPF tree/template reasons. Only a physical detach that
        // follows the mini's own logical SetVisible(false) is evidence that the next attach needs
        // capture-surface recovery.
        state.NeedsRecovery =
            !host._disposed &&
            !host._visible &&
            host._documentReady &&
            host._pluginReportedReady &&
            host._webView.CoreWebView2 != null;

#if DEBUG
        if (state.NeedsRecovery)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=surface-detached " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(host._owner._context.PaperId)}");
        }
#endif
    }

    private static void OnWebMiniSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebPluginMiniViewHost host)
        {
            return;
        }

        var state = MiniSurfaceRecoveryStates.GetOrCreateValue(host);
        if (!state.NeedsRecovery ||
            state.VisibilityCycleActive ||
            host._disposed ||
            !host._documentReady ||
            !host._pluginReportedReady ||
            host._webView.CoreWebView2 == null)
        {
            return;
        }

        state.NeedsRecovery = false;
        int generation;
        unchecked
        {
            generation = ++state.Generation;
        }

        state.RestoreVisibility = host._webView.Visibility;
        if (state.RestoreVisibility != Visibility.Visible)
        {
            // PaperTodo never hides a healthy mini through the WebView's local Visibility property.
            // Respect an unexpected owner/plugin value rather than overriding it as a recovery side
            // effect. A later real detach can arm recovery again.
            return;
        }

        try
        {
            state.VisibilityCycleActive = true;
            host._webView.Visibility = Visibility.Hidden;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=surface-recovery-hide " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(host._owner._context.PaperId)} " +
                $"generation={generation}");
#endif

            // Visibility invalidation queues layout/render work. ContextIdle runs behind that work,
            // so WebView2 observes a real false -> true IsVisible transition without a wall-clock
            // delay or assumptions about 60/120/240 Hz refresh rates.
            _ = host.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                (Action)(() => CompleteMiniSurfaceRecovery(host, state, generation)));
        }
        catch
        {
            RestoreMiniSurfaceVisibility(host, state);
        }
    }

    private static void CompleteMiniSurfaceRecovery(
        WebPluginMiniViewHost host,
        MiniSurfaceRecoveryState state,
        int generation)
    {
        if (!state.VisibilityCycleActive || generation != state.Generation)
        {
            return;
        }

        if (host._disposed ||
            !host.IsLoaded ||
            !host._documentReady ||
            !host._pluginReportedReady)
        {
            RestoreMiniSurfaceVisibility(host, state);
            return;
        }

        try
        {
            host._webView.Visibility = state.RestoreVisibility;
            state.VisibilityCycleActive = false;
            host._webView.InvalidateVisual();
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=surface-recovery-complete " +
                $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(host._owner._context.PaperId)} " +
                $"generation={generation}");
#endif
        }
        catch
        {
            RestoreMiniSurfaceVisibility(host, state);
        }
    }

    private static void RestoreMiniSurfaceVisibility(
        WebPluginMiniViewHost host,
        MiniSurfaceRecoveryState state)
    {
        if (!state.VisibilityCycleActive)
        {
            return;
        }

        try
        {
            host._webView.Visibility = state.RestoreVisibility;
        }
        catch
        {
            // Browser/process failure is still handled by the existing Web mini ProcessFailed and
            // fallback paths. Recovery must never turn a presentation glitch into an app failure.
        }
        state.VisibilityCycleActive = false;
    }
}
