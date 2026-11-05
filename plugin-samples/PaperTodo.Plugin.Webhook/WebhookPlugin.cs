using System.Windows;
using System.Windows.Threading;
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
        private readonly Dispatcher _ui = Application.Current.Dispatcher;
        private readonly WebhookSubscriptions _subscriptions;
        private readonly IDisposable _settingsSubscription;
        private bool _disposed;

        public Runtime(PaperPluginRuntimeContext context)
        {
            _subscriptions = new WebhookSubscriptions(context.Workspace.Subscribe, new WebhookDispatcher());
            try
            {
                _subscriptions.Update(context.Settings.Json);
                _settingsSubscription = context.Settings.Subscribe(UpdateEndpoints);
            }
            catch
            {
                _subscriptions.Dispose();
                throw;
            }
        }

        private void UpdateEndpoints(string json)
        {
            if (!_ui.CheckAccess())
            {
                _ui.Invoke(() => UpdateEndpoints(json));
                return;
            }
            if (!_disposed) _subscriptions.Update(json);
        }

        public void Dispose()
        {
            if (!_ui.CheckAccess())
            {
                _ui.Invoke(Dispose);
                return;
            }
            if (_disposed) return;
            _disposed = true;
            _settingsSubscription.Dispose();
            _subscriptions.Dispose();
        }
    }
}
