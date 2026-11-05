using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void QueuePrewarmLifetime()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                var before = PrewarmOutputWindows();
                EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(dispatcher);
                var warm = PrewarmOutputWindows().Except(before).ToHashSet();
                if (warm.Count != 1 || warm.Any(IsPrewarmWindowVisible))
                    throw new InvalidOperationException("Prewarm must leave only one hidden reusable output, not its probe windows.");
                for (var i = 0; i < 3; i++)
                    EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(dispatcher);
                if (!PrewarmOutputWindows().Except(before).ToHashSet().SetEquals(warm))
                    throw new InvalidOperationException("Repeated prewarm created additional native output windows.");
                dispatcher.InvokeShutdown();
                if (warm.Any(WindowNative.IsWindowHandleAlive))
                    throw new InvalidOperationException("Dispatcher shutdown retained a prewarm output HWND.");
                EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(dispatcher);
                if (PrewarmOutputWindows().Except(before).Any())
                    throw new InvalidOperationException("Prewarm recreated output resources after dispatcher shutdown.");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                if (!dispatcher.HasShutdownFinished) dispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(20)), "Isolated prewarm lifetime check completed");
        if (error != null) throw new InvalidOperationException("Prewarm resource lifecycle failed", error);
        Console.WriteLine("PASS prewarm: probe HWNDs released, one hidden spare, repeated calls stable, shutdown drains");
    }

    private static HashSet<IntPtr> PrewarmOutputWindows()
    {
        var windows = new HashSet<IntPtr>();
        PrewarmEnumWindowsProc collect = (hwnd, _) =>
        {
            var name = new StringBuilder(64);
            _ = GetPrewarmWindowClass(hwnd, name, name.Capacity);
            var style = GetPrewarmWindowStyle(hwnd, -20).ToInt64();
            // Identify actual native compositor outputs rather than WPF's own hidden message
            // infrastructure. This test observes HWND lifetime, not private pool bookkeeping.
            const long outputFlags = 0x00200000 | 0x08000000 | 0x00000080;
            if (name.ToString() == "Static" && (style & outputFlags) == outputFlags)
                windows.Add(hwnd);
            return true;
        };
        _ = EnumPrewarmThreadWindows(GetPrewarmThreadId(), collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        return windows;
    }

    private delegate bool PrewarmEnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetPrewarmThreadId();
    [DllImport("user32.dll", EntryPoint = "EnumThreadWindows")]
    private static extern bool EnumPrewarmThreadWindows(uint thread, PrewarmEnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetPrewarmWindowClass(IntPtr hwnd, StringBuilder name, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetPrewarmWindowStyle(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsPrewarmWindowVisible(IntPtr hwnd);
}
