namespace PaperTodo;

internal sealed class TelemetrySnapshotWriter(Action<string> write)
{
    private readonly object _gate = new();
    private string? _pending;
    private Task? _worker;

    // Snapshots contain the entire state. A newer pending snapshot supersedes the previous one.
    public Task Enqueue(string snapshot)
    {
        lock (_gate)
        {
            _pending = snapshot;
            return _worker ??= Task.Run(Drain);
        }
    }

    private void Drain()
    {
        while (true)
        {
            string snapshot;
            lock (_gate)
            {
                if (_pending == null)
                {
                    _worker = null;
                    return;
                }

                snapshot = _pending;
                _pending = null;
            }

            try { write(snapshot); }
            catch
            {
                // Disk writes are best effort. Still attempt any newer pending snapshot.
            }
        }
    }
}
