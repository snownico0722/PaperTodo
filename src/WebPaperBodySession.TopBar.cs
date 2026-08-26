using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private CoreWebView2? _topBarHookedCore;
    private WebView2CompositionControl? _topBarHookedWebView;

    private object SetPaperTopBarActionsFromWeb(JsonElement parameters)
    {
        var documentGeneration = RequireCurrentBodyTopBarDocument();
        var actions = ReadTopBarActions(parameters);
        var hidden = PaperHostTopBarActions.None;
        if (ReadStringSet(parameters, "hiddenHostActions") is { } hiddenValues)
        {
            foreach (var value in hiddenValues)
            {
                hidden |= value switch
                {
                    "newTodoPaper" => PaperHostTopBarActions.NewTodoPaper,
                    "newNotePaper" => PaperHostTopBarActions.NewNotePaper,
                    _ => throw new PaperTodoPluginException(
                        "invalid_topbar_host_action",
                        $"Unknown host top-bar action: {value}")
                };
            }
        }

        _context.TopBar.SetActionHandler(
            invocation => HandleTopBarActionInvocation(
                documentGeneration,
                invocation));
        _context.TopBar.SetPaperActions(actions, hidden);
        return new { updated = actions.Length };
    }

    private static object SetGlobalTopBarActionsFromWeb(JsonElement parameters) =>
        throw new PaperTodoPluginException(
            "global_topbar_app_runtime_only",
            "Global top-bar actions must be registered by the plugin runtime.");

    private int RequireCurrentBodyTopBarDocument()
    {
        if (!_documentReady ||
            !_pluginDocumentReady ||
            _webView.CoreWebView2 is not { } core)
        {
            throw new PaperTodoPluginException(
                "topbar_body_unavailable",
                "Web top-bar contribution requires the current ready body document.");
        }

        EnsureTopBarDocumentLifecycleHook(core, _webView);
        return _documentGeneration;
    }

    private void EnsureTopBarDocumentLifecycleHook(
        CoreWebView2 core,
        WebView2CompositionControl webView)
    {
        if (ReferenceEquals(_topBarHookedCore, core) &&
            ReferenceEquals(_topBarHookedWebView, webView))
        {
            return;
        }

        DetachTopBarDocumentLifecycleHook();
        _topBarHookedCore = core;
        _topBarHookedWebView = webView;
        core.NavigationStarting += OnTopBarDocumentNavigationStarting;
        core.ProcessFailed += OnTopBarDocumentProcessFailed;
        webView.Unloaded += OnTopBarBodyWebViewUnloaded;
    }

    private void OnTopBarDocumentNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _topBarHookedCore))
        {
            return;
        }
        ClearWebTopBarContribution();
    }

    private void OnTopBarDocumentProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, _topBarHookedCore))
        {
            return;
        }
        ClearWebTopBarContribution();
    }

    private void OnTopBarBodyWebViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _topBarHookedWebView))
        {
            return;
        }
        ClearWebTopBarContribution();
    }

    private void ClearWebTopBarContribution()
    {
        DetachTopBarDocumentLifecycleHook();
        try
        {
            _context.TopBar.Clear();
        }
        catch
        {
            // Session teardown can race navigation/process failure. HostApi.Dispose owns the final
            // cleanup, so a stale document never needs to keep retrying from renderer callbacks.
        }
    }

    private void DetachTopBarDocumentLifecycleHook()
    {
        var core = _topBarHookedCore;
        var webView = _topBarHookedWebView;
        _topBarHookedCore = null;
        _topBarHookedWebView = null;

        if (core != null)
        {
            try { core.NavigationStarting -= OnTopBarDocumentNavigationStarting; } catch { }
            try { core.ProcessFailed -= OnTopBarDocumentProcessFailed; } catch { }
        }
        if (webView != null)
        {
            try { webView.Unloaded -= OnTopBarBodyWebViewUnloaded; } catch { }
        }
    }

    private static PaperTopBarAction[] ReadTopBarActions(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("actions", out var actionsValue) ||
            actionsValue.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (actionsValue.ValueKind != JsonValueKind.Array)
        {
            throw new PaperTodoPluginException(
                "invalid_params",
                "actions must be an array.");
        }
        return DeserializePayload<PaperTopBarAction[]>(actionsValue);
    }

    private void HandleTopBarActionInvocation(
        int documentGeneration,
        PaperTopBarActionInvocation invocation)
    {
        if (documentGeneration != _documentGeneration ||
            !_documentReady ||
            !_pluginDocumentReady)
        {
            return;
        }

        Send(new
        {
            type = "topBarActionInvoked",
            action = invocation
        });
    }
}
