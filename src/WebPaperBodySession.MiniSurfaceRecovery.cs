using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    // WebView2CompositionControl mirrors WPF IsVisible into CoreWebView2Controller.IsVisible.
    // A warm mini can retain its document while the capture-backed presentation loses its last
    // frame after a real preview detach/reattach. Keep recovery entirely host-side: only a physical
    // detach (no remaining WPF parent) arms the next Load, then briefly drive the WebView through a
    // Hidden -> Visible transition so WebView2 restarts its own presentation path. No plugin script,
    // DOM/state mutation, reload or refresh-rate-dependent delay is involved.
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

        var webView = FindMiniWebView(host);
        RestoreMiniSurfaceVisibility(webView, state);

        // Loaded/Unloaded may also occur while WPF reconnects an otherwise intact tree (for
        // example around template/theme work). Only removing the mini from its parent is the Edge
        // Preview lifetime boundary that can invalidate the capture-backed presentation surface.
        state.NeedsRecovery =
            host.Parent == null &&
            webView?.CoreWebView2 != null &&
            webView.Source != null;

#if DEBUG
        if (state.NeedsRecovery)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=surface-detached " +
                $"host={RuntimeHelpers.GetHashCode(host):x}");
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
        var webView = FindMiniWebView(host);
        if (!state.NeedsRecovery ||
            state.VisibilityCycleActive ||
            !host.IsLoaded ||
            webView?.CoreWebView2 == null ||
            webView.Source == null)
        {
            return;
        }

        state.NeedsRecovery = false;
        int generation;
        unchecked
        {
            generation = ++state.Generation;
        }

        state.RestoreVisibility = webView.Visibility;
        if (state.RestoreVisibility != Visibility.Visible)
        {
            // The mini host normally leaves local Visibility at Visible and controls presentation
            // with opacity/hit testing. Respect any unexpected local visibility owner instead of
            // overriding it as a recovery side effect.
            return;
        }

        try
        {
            state.VisibilityCycleActive = true;
            webView.Visibility = Visibility.Hidden;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=surface-recovery-hide " +
                $"host={RuntimeHelpers.GetHashCode(host):x} generation={generation}");
#endif

            // Setting Hidden invalidates WPF layout/render while preserving the slot's size.
            // ContextIdle runs after higher-priority render work, giving WebView2 a real false ->
            // true IsVisible transition without guessing how long one or two display frames take.
            _ = host.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                (Action)(() =>
                    CompleteMiniSurfaceRecovery(host, webView, state, generation)));
        }
        catch
        {
            RestoreMiniSurfaceVisibility(webView, state);
        }
    }

    private static WebView2CompositionControl? FindMiniWebView(
        WebPluginMiniViewHost host)
    {
        foreach (UIElement child in host.Children)
        {
            if (child is WebView2CompositionControl webView)
            {
                return webView;
            }
        }
        return null;
    }

    private static void CompleteMiniSurfaceRecovery(
        WebPluginMiniViewHost host,
        WebView2CompositionControl webView,
        MiniSurfaceRecoveryState state,
        int generation)
    {
        if (!state.VisibilityCycleActive || generation != state.Generation)
        {
            return;
        }

        if (!host.IsLoaded ||
            webView.CoreWebView2 == null ||
            webView.Source == null)
        {
            RestoreMiniSurfaceVisibility(webView, state);
            return;
        }

        try
        {
            webView.Visibility = state.RestoreVisibility;
            state.VisibilityCycleActive = false;
            webView.InvalidateVisual();
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"preview.webmini phase=surface-recovery-complete " +
                $"host={RuntimeHelpers.GetHashCode(host):x} generation={generation}");
#endif
        }
        catch
        {
            RestoreMiniSurfaceVisibility(webView, state);
        }
    }

    private static void RestoreMiniSurfaceVisibility(
        WebView2CompositionControl? webView,
        MiniSurfaceRecoveryState state)
    {
        if (!state.VisibilityCycleActive)
        {
            return;
        }

        if (webView != null)
        {
            try
            {
                webView.Visibility = state.RestoreVisibility;
            }
            catch
            {
                // Browser/process failure is handled by the existing Web mini ProcessFailed and
                // fallback paths. Recovery must not turn a presentation glitch into an app failure.
            }
        }
        state.VisibilityCycleActive = false;
    }
}
