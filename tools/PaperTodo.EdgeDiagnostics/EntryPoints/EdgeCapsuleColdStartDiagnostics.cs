namespace PaperTodo;

/// <summary>
/// Debug-only instrumentation for the first V3 Lite queue-proxy publication. It intentionally has
/// no Release behavior. The optional stall is controlled by environment variables so a one-frame
/// authority race can be stretched into a deterministic visual repro without changing production
/// ordering.
/// </summary>
internal static class EdgeCapsuleColdStartDiagnostics
{
#if DEBUG
    private sealed record Context(
        bool Cold,
        string QueueKey,
        long SessionOrdinal);

    private sealed class Scope(Context? previous) : IDisposable
    {
        private Context? _previous = previous;

        public void Dispose()
        {
            _current = _previous;
            _previous = null;
        }
    }

    [ThreadStatic]
    private static Context? _current;

    internal static IDisposable Enter(
        bool cold,
        string queueKey,
        long sessionOrdinal)
    {
        var previous = _current;
        _current = new Context(cold, queueKey, sessionOrdinal);
        return new Scope(previous);
    }

    internal static void Boundary(string stage)
    {
        var context = _current;
        if (context == null)
        {
            return;
        }

        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.coldstart phase=boundary stage={stage} " +
            $"session={context.SessionOrdinal} cold={context.Cold} " +
            $"queue={context.QueueKey}");
        MaybeStall(context, stage);
    }

    internal static void CloakMember(
        int index,
        int count,
        IntPtr handle,
        bool requestedCloak,
        bool success,
        double totalMilliseconds)
    {
        var context = _current;
        if (context == null)
        {
            return;
        }

        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.coldstart phase=cloak-member " +
            $"session={context.SessionOrdinal} cold={context.Cold} " +
            $"queue={context.QueueKey} index={index}/{count} " +
            $"hwnd=0x{handle.ToInt64():X} requested={requestedCloak} " +
            $"success={success} totalMs={totalMilliseconds:F3}");

        if (index == 1)
        {
            MaybeStall(context, "after-first-cloak");
        }
    }

    private static void MaybeStall(Context context, string stage)
    {
        if (!context.Cold)
        {
            return;
        }

        var configuredStage = Environment.GetEnvironmentVariable(
            "PAPERTODO_EDGE_COLD_STALL_STAGE");
        if (!string.Equals(
                configuredStage,
                stage,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var milliseconds = 200;
        var configuredMilliseconds = Environment.GetEnvironmentVariable(
            "PAPERTODO_EDGE_COLD_STALL_MS");
        if (int.TryParse(configuredMilliseconds, out var parsed))
        {
            milliseconds = Math.Clamp(parsed, 1, 2000);
        }

        EdgeCapsulePerformanceDiagnostics.Trace(
            $"fault.coldstart event=stall-begin stage={stage} " +
            $"session={context.SessionOrdinal} queue={context.QueueKey} " +
            $"ms={milliseconds}");
        System.Threading.Thread.Sleep(milliseconds);
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"fault.coldstart event=stall-end stage={stage} " +
            $"session={context.SessionOrdinal} queue={context.QueueKey} " +
            $"ms={milliseconds}");
    }
#else
    private sealed class NoopScope : IDisposable
    {
        internal static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    internal static IDisposable Enter(
        bool cold,
        string queueKey,
        long sessionOrdinal) => NoopScope.Instance;

    internal static void Boundary(string stage) { }

    internal static void CloakMember(
        int index,
        int count,
        IntPtr handle,
        bool requestedCloak,
        bool success,
        double totalMilliseconds) { }
#endif
}
