using System.Diagnostics;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.Webhook;

internal sealed class WebhookSubscriptions : IDisposable
{
    private readonly Func<PaperTodoEventFilter, Action<PaperTodoEvent>, IDisposable> _subscribe;
    private readonly WebhookDispatcher _dispatcher;
    private IDisposable? _subscription;
    private HashSet<PaperTodoEventKind> _kinds = [];
    private Dictionary<string, Uri> _routes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public WebhookSubscriptions(
        Func<PaperTodoEventFilter, Action<PaperTodoEvent>, IDisposable> subscribe,
        WebhookDispatcher dispatcher)
    {
        _subscribe = subscribe;
        _dispatcher = dispatcher;
    }

    public void Update(string json)
    {
        if (_disposed) return;
        if (!WebhookRoutes.TryRead(json, out var routes, out var error))
            Trace.TraceWarning("Webhook routes are invalid; sending is disabled: {0}", error);

        if (!_routes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(routes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase), RouteComparer.Instance))
            _dispatcher.InvalidatePending();
        _routes = routes;

        var kinds = Enum.GetValues<PaperTodoEventKind>()
            .Where(kind => routes.ContainsKey(WebhookRoutes.EventName(kind)))
            .ToHashSet();
        if (_kinds.SetEquals(kinds)) return;

        IDisposable? next = null;
        if (kinds.Count != 0)
        {
            try
            {
                next = _subscribe(new PaperTodoEventFilter { Kinds = kinds, ExcludeOwnOperations = false }, OnEvent);
            }
            catch
            {
                _subscription?.Dispose();
                _subscription = null;
                _kinds.Clear();
                throw;
            }
        }
        var previous = _subscription;
        _subscription = next;
        _kinds = kinds;
        previous?.Dispose();
    }

    private void OnEvent(PaperTodoEvent value)
    {
        if (!_disposed && _routes.TryGetValue(WebhookRoutes.EventName(value.Kind), out var endpoint))
            _dispatcher.Enqueue(endpoint, value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscription?.Dispose();
        _dispatcher.Dispose();
    }

    private sealed class RouteComparer : IEqualityComparer<KeyValuePair<string, Uri>>
    {
        public static readonly RouteComparer Instance = new();
        public bool Equals(KeyValuePair<string, Uri> x, KeyValuePair<string, Uri> y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Key, y.Key) && x.Value.Equals(y.Value);
        public int GetHashCode(KeyValuePair<string, Uri> value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Key), value.Value);
    }
}
