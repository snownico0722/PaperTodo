using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.Webhook;

public sealed class WebhookPlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new Session();

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) => new Runtime(context);

    private sealed class Session : IPaperBodySession
    {
        public FrameworkElement View { get; } = new TextBlock
        {
            Text = "请在插件设置的事件路由中填写 JSON，按事件名称配置 Webhook 地址。",
            Margin = new Thickness(16),
            TextWrapping = TextWrapping.Wrap
        };

        public void Dispose() { }
    }

    private sealed class Runtime : IPaperPluginRuntime
    {
        private readonly WebhookDispatcher _dispatcher = new();
        private readonly IDisposable _settingsSubscription;
        private readonly IDisposable _eventSubscription;
        private Dictionary<string, Uri> _endpoints = new(StringComparer.OrdinalIgnoreCase);

        public Runtime(PaperPluginRuntimeContext context)
        {
            UpdateEndpoints(context.Settings.Json);
            _settingsSubscription = context.Settings.Subscribe(UpdateEndpoints);
            _eventSubscription = context.Workspace.Subscribe(
                new PaperTodoEventFilter
                {
                    Kinds = Enum.GetValues<PaperTodoEventKind>().ToHashSet(),
                    ExcludeOwnOperations = false
                },
                OnEvent);
        }

        private void OnEvent(PaperTodoEvent value)
        {
            if (_endpoints.TryGetValue(WebhookRoutes.EventName(value.Kind), out var endpoint))
            {
                _dispatcher.Enqueue(endpoint, value);
            }
        }

        private void UpdateEndpoints(string json)
        {
            if (WebhookRoutes.TryRead(json, out var routes, out var error))
            {
                _endpoints = routes;
            }
            else
            {
                _endpoints = routes;
                Trace.TraceWarning("Webhook routes are invalid; sending is disabled: {0}", error);
            }
        }

        public void Dispose()
        {
            _eventSubscription.Dispose();
            _settingsSubscription.Dispose();
            _dispatcher.Dispose();
        }
    }
}
