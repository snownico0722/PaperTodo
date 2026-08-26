using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class PaperPluginRuntimeGlobalShortcutApi : IPaperGlobalShortcutApi, IDisposable
{
    private readonly AppController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly Guid _runtimeId;
    private readonly string _providerId;
    private readonly Func<bool> _isActive;
    private Action<PaperShortcutActionInvocation>? _handler;
    private bool _disposed;

    public PaperPluginRuntimeGlobalShortcutApi(
        AppController controller,
        Guid runtimeId,
        string providerId,
        Func<bool> isActive)
    {
        _controller = controller;
        _dispatcher = Application.Current.Dispatcher;
        _runtimeId = runtimeId;
        _providerId = providerId;
        _isActive = isActive;
    }

    public void SetActionHandler(Action<PaperShortcutActionInvocation>? handler) =>
        OnUi(() =>
        {
            EnsureUsable();
            _handler = handler;
            if (handler == null)
            {
                _controller.RemovePluginGlobalShortcutRuntime(_runtimeId, _providerId);
                return;
            }

            _controller.SetPluginGlobalShortcutRuntime(
                _runtimeId,
                _providerId,
                () => !_disposed && _isActive(),
                Dispatch);
        });

    public void Clear() =>
        OnUi(() =>
        {
            EnsureUsable();
            _handler = null;
            _controller.RemovePluginGlobalShortcutRuntime(_runtimeId, _providerId);
        });

    private void Dispatch(PaperShortcutActionInvocation invocation)
    {
        if (_disposed || !_isActive())
        {
            return;
        }
        _handler?.Invoke(invocation);
    }

    private void EnsureUsable()
    {
        if (_disposed || !_isActive())
        {
            throw new PaperTodoPluginException(
                "runtime_closed",
                "The plugin runtime is no longer active.");
        }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _handler = null;
        try
        {
            if (_dispatcher.CheckAccess())
            {
                _controller.RemovePluginGlobalShortcutRuntime(_runtimeId, _providerId);
            }
            else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _dispatcher.Invoke(() =>
                    _controller.RemovePluginGlobalShortcutRuntime(_runtimeId, _providerId));
            }
        }
        catch
        {
        }
    }
}
