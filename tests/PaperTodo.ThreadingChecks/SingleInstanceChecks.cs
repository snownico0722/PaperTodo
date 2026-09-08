using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace PaperTodo.ThreadingChecks;

internal static partial class Program
{
    private static void CheckSingleInstanceTimeout()
    {
        var name = "PaperTodo-check-" + Guid.NewGuid().ToString("N");
        // Exercise the production deadline and client retry budget together; a shortened
        // server timeout can hide a client that gives up before the stalled peer is evicted.
        using var helper = new SingleInstanceHelper(name, name);
        var received = new ConcurrentQueue<IReadOnlyList<string>>();
        using var delivered = new ManualResetEventSlim();
        helper.StartListener(args => { received.Enqueue(args); delivered.Set(); });
        using var stalled = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        stalled.Connect(5_000);
        stalled.Write(Encoding.UTF8.GetBytes("unfinished-without-a-newline"));
        stalled.Flush();
        // Keep the first client open; only the server's deadline can release this connection.
        helper.SignalPrimaryInstance(["--show"]);
        Assert(delivered.Wait(TimeSpan.FromSeconds(5)), "listener did not accept a valid client after timeout");
        Assert(received.Count == 1 && received.TryDequeue(out var args) && args.SequenceEqual(new[] { "--show" }),
            "timed-out input was dispatched, or the subsequent valid command was lost");
        helper.Dispose();
        Assert(ReadField<Task>(helper, "_listenerTask").Wait(TimeSpan.FromSeconds(5)), "listener did not stop");
    }

    private static void CheckSingleInstanceCancellation()
    {
        var name = "PaperTodo-check-" + Guid.NewGuid().ToString("N");
        using var helper = new SingleInstanceHelper(name, name, TimeSpan.FromSeconds(30));
        var callbacks = 0;
        helper.StartListener(_ => Interlocked.Increment(ref callbacks));
        using var stalled = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        stalled.Connect(5_000);
        stalled.Write(Encoding.UTF8.GetBytes("partial"));
        stalled.Flush();
        helper.Dispose();
        Assert(ReadField<Task>(helper, "_listenerTask").Wait(TimeSpan.FromSeconds(5)), "Dispose did not cancel the connected reader");
        Assert(Volatile.Read(ref callbacks) == 0, "cancelled partial command was dispatched");
    }
}
