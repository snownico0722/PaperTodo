from pathlib import Path
for root in (Path('/mnt/data/edge258-core'),Path('/mnt/data/edge258-full')):
 p=root/'src/EdgeCapsuleQueueCompositionProxy.LightPrewarm.cs';s=p.read_text();assert 'static (_, _) => { }' in s;p.write_text(s.replace('static (_, _) => { }','static _ => { }'))
 p=root/'tests/PaperTodo.EdgeTitleChecks/ProxyInputReadinessChecks.cs';s=p.read_text();s=s.replace('        ProxyOutputWindowVisibility();','        ProxyOutputWindowVisibility();\n        ProxyPointerMessageCoordinates();\n        ProxyPendingInputChecks();');p.write_text(s)
 p=root/'tests/PaperTodo.EdgeTitleChecks/ProxyPointerTransferChecks.cs';p.write_text('''using System.Windows.Interop;
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

    private static void ProxyPendingInputChecks()
    {
        var clock = new RenderDemandClock();
        var pending = new EdgeCapsuleInputHandoff(clock);
        var current = true;
        var deliveries = 0;
        pending.Enqueue(() => current, () => { deliveries++; pending.Complete(); });
        pending.Prune(); // A failed handoff keeps the original request; no independent replay timer.
        Check(deliveries == 0 && pending.Count == 1, "A failed handoff retains the press without delivering early");
        pending.Complete(); pending.Complete();
        Check(deliveries == 1 && pending.Count == 0, "A later successful handoff delivers exactly once, including reentrant completion");
        pending.Enqueue(() => current, () => deliveries++);
        current = false;
        pending.Complete();
        Check(deliveries == 1 && pending.Count == 0, "Closed, hidden or replaced targets do not receive delayed input");
        current = true;
        pending.Enqueue(() => current, () => deliveries++);
        clock.Now += 1001;
        pending.Prune(); pending.Complete();
        Check(deliveries == 1 && pending.Count == 0, "An exhausted recovery cannot replay an old press indefinitely");
        pending.Enqueue(() => true, () => deliveries++);
        pending.Cancel(); pending.Complete();
        Check(deliveries == 1, "Shutdown cancels pending input instead of invoking user actions");
        Console.WriteLine("PASS proxy-pending-press-retry-cancel-and-exactly-once");
    }
}
''')
 p=root/'doc/ARCHITECTURE.md';s=p.read_text();marker='Proxy 动画逻辑结束不等于'
 i=s.index(marker);s=s[:i]+'''代理收到按下消息时保存原始客户区坐标转换得到的屏幕位置和按键状态。转交请求归属于当前代理代与原纸片/正文会话，只有真实端点成功释放后消费一次；取消、目标关闭/隐藏/替换或超时不迟到重放。该保证是原始按下消息不因首次交接重试而直接遗失，不等于完整按下—抬起手势或控件点击已经验收；真实 Windows 输入交接仍需要单独验证。

'''+s[i:];p.write_text(s)
 p=root/'CHANGELOG.md';s=p.read_text();s=s.replace('隐藏或恢复边缘队列时，正确结束旧的悬停状态。','隐藏或恢复边缘队列时，正确结束旧的悬停状态；动画期间的鼠标按下保留原始位置和按键状态，短时交接重试不直接遗失转交请求。');p.write_text(s)
 p=root/'doc/CHANGELOG.en.md';s=p.read_text();s=s.replace('Hiding or restoring edge queues clears outdated hover state.','Hiding or restoring edge queues clears outdated hover state. Mouse presses during handoff retain their original position and modifiers, with generation-bound transfer requests surviving a brief release retry.');p.write_text(s)
print('Added native payload and pending handoff tests')
