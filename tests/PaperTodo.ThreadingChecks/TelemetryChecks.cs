using System.Collections.Concurrent;
using System.IO;

namespace PaperTodo.ThreadingChecks;

internal static partial class Program
{
    private static void CheckTelemetrySnapshotWrites()
    {
        var writes = new ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new TelemetrySnapshotWriter(snapshot =>
        {
            if (snapshot == "periodic")
            {
                started.Set();
                release.Wait();
            }
            if (snapshot == "disk error") throw new IOException("simulated write failure");
            writes.Enqueue(snapshot);
        });

        _ = writer.Enqueue("periodic");
        Task cleared;
        try
        {
            Assert(started.Wait(TimeSpan.FromSeconds(5)), "background write did not start");
            _ = writer.Enqueue("intermediate");
            cleared = writer.Enqueue("disabled and cleared");
            Assert(!cleared.IsCompleted, "flush returned while the previous write was blocked");
        }
        finally { release.Set(); }

        Assert(cleared.Wait(TimeSpan.FromSeconds(5)), "writer did not drain the queued snapshots");
        Assert(writes.SequenceEqual(new[] { "periodic", "disabled and cleared" }),
            "older data replaced the cleared state or an obsolete snapshot was written");

        writer.Enqueue("disk error").GetAwaiter().GetResult();
        writer.Enqueue("final").GetAwaiter().GetResult();
        Assert(writes.Last() == "final", "final snapshot did not persist after a failed write");
    }
}
