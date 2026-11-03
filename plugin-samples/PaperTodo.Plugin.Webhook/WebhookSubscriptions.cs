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

        var kinds = Enum.GetValues<PaperTodoEventKind>()
            .Where(kind => routes.ContainsKey(WebhookRoutes.EventName(kind)))
            .ToHashSet();
        var replaceSubscription = !_kinds.SetEquals(kinds);
        // Prepare the new listener first. Failure must preserve the active routes,
        // subscription, and queue instead of disabling a working webhook.
        var next = replaceSubscription && kinds.Count > 0
            ? _subscribe(new PaperTodoEventFilter { Kinds = kinds, ExcludeOwnOperations = false }, OnEvent)
            : null;

        if (!_routes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(routes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase), RouteComparer.Instance))
            _dispatcher.InvalidatePending();
        _routes = routes;
        if (!replaceSubscription) return;

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
            StringComparer.OrdinalIgnoreCase.Equals(x.Key, y.Key) &&
            StringComparer.Ordinal.Equals(x.Value.OriginalString, y.Value.OriginalString);
        public int GetHashCode(KeyValuePair<string, Uri> value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Key),
                StringComparer.Ordinal.GetHashCode(value.Value.OriginalString));
    }
}
