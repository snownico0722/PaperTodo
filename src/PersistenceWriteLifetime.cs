using System.Threading;

namespace PaperTodo;

/// <summary>
/// One process/session write admission signal, shared by all of its workspace stores.
/// Stop never waits on I/O or dispatches UI work, so it is safe at the crash boundary.
/// An already admitted OS replacement may finish; no queued writer may start afterwards.
/// </summary>
internal sealed class PersistenceWriteLifetime
{
    private int _stopped;
    internal bool IsStopped => Volatile.Read(ref _stopped) != 0;
    internal void Stop() => Interlocked.Exchange(ref _stopped, 1);
    internal void ThrowIfStopped()
    {
        if (IsStopped) throw new OperationCanceledException("Persistence is stopping; no new write was accepted.");
    }
}
