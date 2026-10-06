using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static class MasterMaterialHandoffChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run(AppController controller, IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var papers = windows.Values.ToArray();
        Require(papers.Length > 0, "material fixture must contain real papers");
        var presenters = papers.ToDictionary(w => w, w => (EdgeCapsulePresenter)Part(w, "_edgeCapsule"));
        var hosts = papers.ToDictionary(w => w, w => (EdgeCapsuleHost)Part(w, "_edgeCapsuleHost"));
        var skins = papers.ToDictionary(w => w, w => (SkinBorder)Part(hosts[w], "Chrome"));
        var proxies = (IDictionary)Part(controller, "_edgeCapsuleQueueCompositionProxies");
        var admitted = new HashSet<EdgeCapsuleQueueCompositionProxy>();
        var captureOverlappedProxy = false;
        var cursorSaved = GetCursorPos(out var cursor);

        void Observe()
        {
            foreach (EdgeCapsuleQueueCompositionProxy proxy in proxies.Values)
                admitted.Add(proxy);
            foreach (var paper in papers)
            {
                if (!controller.IsEdgeCapsuleQueueProxyRetainingSource(paper)) continue;
                captureOverlappedProxy |= !skins[paper].SampledBackgroundCaptureSuspended ||
                    skins[paper].HasBackgroundCapture ||
                    DesktopBackgroundCapture.ReadAffinity(hosts[paper].Handle) != 0;
            }
        }
        void Rendering(object? sender, EventArgs e) => Observe();

        bool Settled(bool retracted) => proxies.Count == 0 && papers.All(paper =>
            !presenters[paper].HasActiveTransition && !presenters[paper].NativeBatchRetryPending &&
            presenters[paper].AppliedPresentation.Visible &&
            presenters[paper].AppliedPresentation ==
                ((EdgeCapsuleTargetPresentation)Part(presenters[paper], "TargetPresentation")).ToFrame() &&
            (presenters[paper].AppliedPresentation.Surface == EdgeCapsuleSurfaceKind.DockedRetracted) == retracted &&
            hosts[paper].MatchesPresentation(presenters[paper].AppliedPresentation));

        async Task Toggle()
        {
            controller.SuppressEdgeCapsulePreviewForMasterQueueLayout("", EdgeCapsuleEdge.Right);
            controller.ToggleCapsuleCollapseAllActive("", EdgeCapsuleEdge.Right);
            // The logical flag changes synchronously. Let the already queued Send transaction run
            // before an empty ownership map can be treated as successful completion.
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Observe();
        }

        async Task CheckSettled(bool retracted, IReadOnlyDictionary<PaperWindow, int> framesBefore)
        {
            await Until(() => Settled(retracted), "exact applied master terminal frames and native geometry");
            if (!retracted)
                await Until(() => papers.All(w => skins[w].IsBackgroundActive &&
                    !skins[w].HasBackgroundCapture && skins[w].BackgroundFrameCount > framesBefore[w]),
                    "fresh material snapshots completed for every revealed endpoint");
            foreach (var paper in papers)
            {
                Require(!skins[paper].SampledBackgroundCaptureSuspended && !skins[paper].HasBackgroundCapture &&
                    DesktopBackgroundCapture.ReadAffinity(hosts[paper].Handle) == 0,
                    "terminal material capture or display-affinity lease remained active");
                Require(DwmGetWindowAttribute(hosts[paper].Handle, 14, out var cloaked, sizeof(int)) >= 0 && cloaked == 0,
                    "terminal real capsule HWND remained cloaked");
                Require(presenters[paper].AppliedPresentation.Opacity == (retracted ? 0 : 1),
                    "master terminal opacity was only approximately complete");
            }
            Require(!captureOverlappedProxy, "local material acquisition overlapped a retained proxy source");
            Require(admitted.All(p => !p.CoverLost), "normal master completion fell back to emergency cover loss");
        }

        CompositionTarget.Rendering += Rendering;
        try
        {
            Require(cursorSaved && SetCursorPos(10, 10), "park cursor outside the capsule queue");
            await Until(() => Settled(false), "initial real capsule presentation");
            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var retracted in new[] { true, false })
                {
                    var before = papers.ToDictionary(w => w, w => skins[w].BackgroundFrameCount);
                    await Toggle();
                    await CheckSettled(retracted, before);
                }
            }

            // Reverse a published transaction, not two coalesced uncommitted toggles.
            var beforeReverse = papers.ToDictionary(w => w, w => skins[w].BackgroundFrameCount);
            await Toggle();
            await Toggle();
            await CheckSettled(false, beforeReverse);
            Require(admitted.Count > 0, "fixture never exercised a live DComp queue proxy");

            var first = papers[0];
            var hit = presenters[first].AppliedPresentation.InteractiveBounds;
            Require(!hit.IsEmpty && SetCursorPos(hit.Left + hit.Width / 2, hit.Top + hit.Height / 2),
                "position native input over a revealed real capsule");
            MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            await Until(() => first.HasExpandedPaperSurface, "real capsule receives a complete native click after handoff");
            Console.WriteLine($"PASS full-material master handoff: {admitted.Count} actual proxy generations, exact endpoints, released cloak/capture, reversal and real click");
        }
        finally
        {
            CompositionTarget.Rendering -= Rendering;
            if (cursorSaved) SetCursorPos(cursor.X, cursor.Y);
        }
    }

    private static object Part(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        target.GetType().GetProperty(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().Name, name);
    private static async Task Until(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
