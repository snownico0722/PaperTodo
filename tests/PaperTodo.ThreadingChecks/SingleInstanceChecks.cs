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
        helper.StartListener(args => { received.Enqueue(args); delivered.Set(); return true; });
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
        helper.StartListener(_ => { Interlocked.Increment(ref callbacks); return true; });
        using var stalled = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        stalled.Connect(5_000);
        stalled.Write(Encoding.UTF8.GetBytes("partial"));
        stalled.Flush();
        helper.Dispose();
        Assert(ReadField<Task>(helper, "_listenerTask").Wait(TimeSpan.FromSeconds(5)), "Dispose did not cancel the connected reader");
        Assert(Volatile.Read(ref callbacks) == 0, "cancelled partial command was dispatched");
    }
    private static void CheckSingleInstanceResults()
    {
        var name = "PaperTodo-result-" + Guid.NewGuid().ToString("N");
        using var helper = new SingleInstanceHelper(name, name);
        var calls = 0;
        helper.StartListener(args =>
        {
            Interlocked.Increment(ref calls);
            if (args[0] == "throw") throw new InvalidOperationException("command failure");
            return args[0] == "success";
        });
        Assert(helper.SignalPrimaryInstance(["success"], waitForResult: true) == 0, "actual success was lost");
        Assert(helper.SignalPrimaryInstance(["denied"], waitForResult: true) == 1, "refusal reported success");
        Assert(helper.SignalPrimaryInstance(["throw"], waitForResult: true) == 1, "exception reported success");
        Assert(Volatile.Read(ref calls) == 3, "an acknowledged command was replayed");
        helper.Dispose();
        Assert(ReadField<Task>(helper, "_listenerTask").Wait(TimeSpan.FromSeconds(5)), "listener did not stop");
        using var absent = new SingleInstanceHelper(name + "-absent", name + "-absent");
        Assert(absent.SignalPrimaryInstance(["--enable-mcp-for-codex"], waitForResult: true) == 2,
            "missing primary reported success");
    }

    private static void CheckSingleInstanceLostResult()
    {
        var name = "PaperTodo-result-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var peer = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new System.IO.StreamReader(server);
            Assert((await reader.ReadLineAsync())?.StartsWith("RESULT ") == true, "missing result request");
            // Simulate a command executed but its reply lost: close without an acknowledgement.
        });
        using var client = new SingleInstanceHelper(name, name);
        Assert(client.SignalPrimaryInstance(["--enable-mcp-for-codex"], waitForResult: true) == 2,
            "lost reply reported success");
        Assert(peer.Wait(TimeSpan.FromSeconds(5)), "peer did not close");
    }

}
