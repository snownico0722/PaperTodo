using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using PaperTodo;

internal static class QueueFeatureBoundaryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task Run(AppController controller, IReadOnlyDictionary<string, PaperWindow> windows, string mode)
    {
        var initial = windows.Values.ToArray();
        var proxies = (IDictionary)Part(controller, "_edgeCapsuleQueueCompositionProxies")!;
        var presenters = initial.ToDictionary(w => w, w => (EdgeCapsulePresenter)Part(w, "_edgeCapsule")!);
        var outputs = new HashSet<IntPtr>();
        GetCursorPos(out var cursor);
        try
        {
            Require(SetCursorPos(10, 10), "park native pointer away from the queue");
            await Until(() => initial.All(w => presenters[w].AppliedPresentation.Visible && !presenters[w].HasActiveTransition), "initial applied capsules");
            controller.SuppressEdgeCapsulePreviewForMasterQueueLayout("", EdgeCapsuleEdge.Right);
            controller.ToggleCapsuleCollapseAllActive("", EdgeCapsuleEdge.Right);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            var active = proxies.Values.Cast<EdgeCapsuleQueueCompositionProxy>().ToArray();
            Require(active.Length > 0 && initial.Any(controller.IsEdgeCapsuleQueueProxyRetainingSource),
                "mutation must intersect a real admitted queue proxy, not an uncommitted toggle");
            foreach (var proxy in active) outputs.Add(proxy.OutputHandle);
            var first = controller.State.Papers.First();
            switch (mode)
            {
                case "hide": controller.HidePaper(first); break;
                case "hide-all": controller.HideAllPapers(); break;
                case "delete": controller.DeletePaper(first); break;
                case "disable-edge": Set("capsule.edge_enabled", false); break;
                case "disable-master": Set("capsule.master_enabled", false); break;
                case "disable-animations": Set("appearance.animations", false); break;
                case "disable-material": Set("appearance.match_auxiliary_material", false); break;
                case "switch-skin": Set("appearance.paper_skin", PaperSkins.Paper); break;
                default: throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown queue boundary case");
            }
            await Until(() => proxies.Count == 0 && initial.All(w => w.IsClosed || !controller.IsEdgeCapsuleQueueProxyRetainingSource(w)),
                mode + ": old proxy releases all live source authority");
            await Until(() => outputs.All(h => h == IntPtr.Zero || !WindowNative.IsWindowHandleAlive(h)),
                mode + ": old master output HWND retires");
            foreach (var window in initial.Where(w => !w.IsClosed))
            {
                if (Part(window, "_edgeCapsuleHost") is not EdgeCapsuleHost host || !WindowNative.IsWindowHandleAlive(host.Handle)) continue;
                var skin = (SkinBorder)Part(host, "Chrome")!;
                await Until(() => !skin.HasBackgroundCapture, mode + ": local capture completes");
                Require(!skin.SampledBackgroundCaptureSuspended && DesktopBackgroundCapture.ReadAffinity(host.Handle) == 0,
                    mode + ": capture suspension or affinity remained active");
                Require(DwmGetWindowAttribute(host.Handle, 14, out var cloaked, sizeof(int)) >= 0 && cloaked == 0,
                    mode + ": real source remained cloaked");
            }
            if (mode is "hide" or "hide-all")
                Require(!windows[first.Id].HasVisibleSurface, "hidden paper still has a visible surface");
            if (mode == "delete") Require(windows[first.Id].IsClosed, "deleted paper native lifetime is still active");

            // Restore through the actual settings/visibility APIs; later interaction must work,
            // not only cleanup of the previous generation.
            Set("appearance.paper_skin", PaperSkins.Acrylic);
            Set("appearance.match_auxiliary_material", true);
            Set("appearance.animations", true);
            Set("capsule.master_enabled", true);
            Set("capsule.edge_enabled", true);
            controller.ShowAllPapers();
            if (controller.State.CapsuleCollapseAllActiveQueues.TryGetValue("|" + DeepCapsuleSides.Right, out var retracted) && retracted)
            {
                controller.SuppressEdgeCapsulePreviewForMasterQueueLayout("", EdgeCapsuleEdge.Right);
                controller.ToggleCapsuleCollapseAllActive("", EdgeCapsuleEdge.Right);
            }
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            var survivor = controller.State.Papers.First();
            var paper = windows[survivor.Id];
            var presenter = presenters[paper];
            await Until(() => proxies.Count == 0 && !presenter.HasActiveTransition &&
                presenter.AppliedPresentation.Visible && presenter.AppliedPresentation.Opacity == 1 &&
                !presenter.AppliedPresentation.InteractiveBounds.IsEmpty, mode + ": restored capsule terminal state");
            var hit = presenter.AppliedPresentation.InteractiveBounds;
            Require(SetCursorPos(hit.Left + hit.Width / 2, hit.Top + hit.Height / 2), "position on restored capsule");
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero); MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            await Until(() => paper.HasExpandedPaperSurface, mode + ": restored real capsule responds to native click");
            Console.WriteLine("PASS live queue feature boundary: " + mode);
        }
        finally { SetCursorPos(cursor.X, cursor.Y); }
        void Set<T>(string id, T value) => controller.PublicSettings.Set(id, JsonSerializer.SerializeToElement(value));
    }
    private static object? Part(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ?? target.GetType().GetProperty(name, Private)?.GetValue(target);
    private static async Task Until(Func<bool> predicate, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
