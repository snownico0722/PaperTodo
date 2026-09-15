using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PaperTodo;

internal static partial class Program
{
    private static void ProxyInputReadiness()
    {
        ProxyOutputWindowVisibility();
        ProxyPointerMessageCoordinates();
        ProxyImmediateInputChecks();
        // Exercise the real native mouse-message adapter and proxy callback. Lifecycle fields are
        // injected to cover reentrant publication/retirement without requiring a live DComp device;
        // this is not a substitute for testing the complete compositor handoff on a real desktop.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(EdgeCapsuleQueueCompositionProxy);
        var proxy = (EdgeCapsuleQueueCompositionProxy)RuntimeHelpers.GetUninitializedObject(type);
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(proxy, value);
        var resting = EdgeCapsulePresentationFrame.Hidden with
        {
            Surface = EdgeCapsuleSurfaceKind.DockedResting
        };
        void SetPlan(EdgeCapsulePresentationFrame start, EdgeCapsulePresentationFrame target) =>
            Set("_plan", new EdgeCapsuleQueueProxyPlan("input-readiness",
                new DeviceScreenRect(0, 0, 100, 40), EdgeCapsuleEdge.Left,
                0, 1, 1, 120, false,
                new[] { new EdgeCapsuleQueueProxyMemberPlan("test", start, start, target) }));
        SetPlan(resting, resting);
        var received = new List<int>();
        Set("_interactionRequested", (Action<EdgeCapsulePointerDown>)(input => received.Add(input.Message)));
        var route = type.GetMethod("HandleInteractionRequested", flags)!
            .CreateDelegate<Action<EdgeCapsulePointerDown>>(proxy);
        using var window = EdgeCapsuleQueueProxyWindow.TryCreate(new DeviceScreenRect(0, 0, 100, 40),
            false, _ => true, route, () => { }, () => { }, () => { });
        Check(window != null, "Create native proxy input regression HWND");
        InitializeProxyCheckHostOwnership(proxy, window!);
        var messages = new[] { 0x0201, 0x0204, 0x0207 };
        void Click()
        {
            foreach (var message in messages)
                SendProxyInputCheckMessage(window!.InputHandle, message, IntPtr.Zero, IntPtr.Zero);
        }
        void Ready()
        {
            foreach (var field in new[] { "_disposed", "_starting", "_coverLost", "_sourcesReleased",
                "_finishing", "_successorHeld" }) Set(field, false);
            Set("_coverPublished", true);
        }

        Ready();
        Click();
        Check(received.SequenceEqual(messages), "Published proxy routes native left/right/middle input");
        foreach (var (field, value) in new[]
        {
            ("_coverPublished", false), ("_starting", true), ("_finishing", true),
            ("_successorHeld", true), ("_sourcesReleased", true), ("_coverLost", true), ("_disposed", true)
        })
        {
            Ready();
            Set(field, value);
            var before = received.Count;
            Click();
            Check(received.Count == before, "Native proxy input must wait for published authority: " + field);
            Ready();
            Click();
            Check(received.Count == before + messages.Length, "Ready authority resumes native input: " + field);
        }

        foreach (var retractAtStart in new[] { false, true })
        {
            var retracted = resting with { Surface = EdgeCapsuleSurfaceKind.DockedRetracted };
            SetPlan(retractAtStart ? retracted : resting, retractAtStart ? resting : retracted);
            Ready();
            var before = received.Count;
            Click();
            Check(received.Count == before, "Master collapse/release remains pointer-transparent");
        }
        Console.WriteLine("PASS proxy-native-input-publication-and-retirement");
    }

    private static void ProxyOutputWindowVisibility()
    {
        // These are native visibility/placement checks. An output without a DComp root has no
        // rendered content, so IsWindowVisible is not evidence that any frame reached the screen.
        foreach (var topmost in new[] { false, true })
        {
            var initial = new DeviceScreenRect(100, 100, 200, 140);
            using var output = EdgeCapsuleQueueProxyWindow.TryCreate(initial, topmost,
                _ => false, _ => { }, () => { }, () => { }, () => { });
            Check(output != null, "Create proxy output for native publication checks");
            var handle = output!.Handle;
            Check(handle != IntPtr.Zero && !IsProxyCheckWindowVisible(handle),
                "A newly created proxy output remains hidden until publication");
            var first = new DeviceScreenRect(120, 150, 320, 230);
            var reused = new DeviceScreenRect(140, 180, 360, 280);
            void CheckShown(DeviceScreenRect bounds)
            {
                Check(output.Show(bounds, topmost), "Publish proxy visibility and geometry together");
                Check(IsProxyCheckWindowVisible(handle), "Show makes the native proxy HWND visible");
                Check(GetProxyCheckWindowRect(handle, out var actual) &&
                    new DeviceScreenRect(actual.Left, actual.Top, actual.Right, actual.Bottom) == bounds,
                    "Published native proxy bounds match the requested physical rectangle");
                Check(output.Handle == handle, "Showing the proxy preserves its reusable HWND identity");
            }

            CheckShown(first);
            CheckShown(first);
            output.Hide();
            Check(!IsProxyCheckWindowVisible(handle), "Hide removes native proxy visibility");
            CheckShown(reused);
            output.Hide();
            Check(!IsProxyCheckWindowVisible(handle), "Reused output can be hidden again");
        }
        Console.WriteLine("PASS proxy-native-output-show-hide-and-reuse");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProxyCheckNativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProxyCheckWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProxyCheckWindowRect(IntPtr window, out ProxyCheckNativeRect bounds);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendProxyInputCheckMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
