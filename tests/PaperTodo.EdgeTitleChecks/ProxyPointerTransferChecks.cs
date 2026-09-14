using System.Windows.Interop;
using PaperTodo;

internal static partial class Program
{
    private static void ProxyPointerMessageCoordinates()
    {
        var received = new List<EdgeCapsulePointerDown>();
        var bounds = new DeviceScreenRect(-640, -360, -440, -220);
        using var output = EdgeCapsuleQueueProxyWindow.TryCreate(bounds, false,
            _ => true, received.Add, () => { }, () => { }, () => { });
        Check(output != null, "Create an offscreen HWND to verify signed message coordinates");
        foreach (var message in new[] { 0x0201, 0x0204, 0x0207 })
        foreach (var point in new[] { (X: 21, Y: 33), (X: -7, Y: -11) })
        {
            // Modifiers and another pressed button must not be reconstructed from message type.
            var keys = new IntPtr(0x0001 | 0x0002 | 0x0004 | 0x0008 | 0x0010);
            var packed = new IntPtr(unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16))));
            SendProxyInputCheckMessage(output!.Handle, message, keys, packed);
            var actual = received[^1];
            Check(actual == new EdgeCapsulePointerDown(
                    new DeviceScreenPoint(bounds.Left + point.X, bounds.Top + point.Y), message, keys),
                "Native press retains signed lParam position and original key state, independent of live cursor");
        }
        Check(received.Count == 6, "Each native press reaches the adapter exactly once");

        using var target = new HwndSource(new HwndSourceParameters("Edge press transfer checks")
        {
            PositionX = -320, PositionY = -220, Width = 100, Height = 80,
            WindowStyle = unchecked((int)0x80000000)
        });
        var delivered = new List<(int Message, IntPtr Keys, IntPtr Position)>();
        target.AddHook((IntPtr hwnd, int message, IntPtr keys, IntPtr position, ref bool handled) =>
        {
            if (message is 0x0201 or 0x0204 or 0x0207)
            {
                delivered.Add((message, keys, position)); handled = true;
            }
            return IntPtr.Zero;
        });
        foreach (var press in received)
            Check(WindowNative.TryPostMouseButtonDown(target.Handle, press, new DeviceScreenPoint(-303, -201)),
                "The source HWND accepts the original press payload");
        DrainTransactionChecksDispatcher();
        Check(delivered.Count == received.Count && delivered.Select(item => item.Keys).SequenceEqual(received.Select(item => item.KeyState)),
            "Posting to the real source retains every original modifier mask");
        Check(delivered.All(item => item.Position.ToInt64() == (17 | (19 << 16))),
            "The handoff converts the resolved endpoint into the real source client coordinates");
        Console.WriteLine("PASS proxy-original-press-coordinates-and-modifiers");
    }

    private static void ProxyImmediateInputChecks()
    {
        var pending = new EdgeCapsuleInputHandoff();
        var current = true;
        var deliveries = 0;

        pending.Enqueue(() => current, () => { deliveries++; pending.Complete(); });
        pending.Complete(); pending.Complete();
        Check(deliveries == 1 && pending.Count == 0,
            "An immediately successful handoff delivers exactly once, including reentrant completion");

        pending.Enqueue(() => current, () => deliveries++);
        pending.Prune(); // Scheduling any retry deliberately drops this press.
        pending.Complete();
        Check(deliveries == 1 && pending.Count == 0,
            "A failed synchronous handoff drops the press instead of replaying it on a later retry");

        current = false;
        pending.Enqueue(() => current, () => deliveries++);
        pending.Complete();
        Check(deliveries == 1 && pending.Count == 0,
            "A target that becomes invalid during the synchronous handoff does not receive input");

        pending.Enqueue(() => true, () => deliveries++);
        pending.Cancel(); pending.Complete();
        Check(deliveries == 1,
            "Cancellation drops pending input instead of invoking a user action");
        Console.WriteLine("PASS proxy-immediate-press-transfer-and-drop-on-retry");
    }
}
