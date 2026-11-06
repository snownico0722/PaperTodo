using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static class MasterCapsuleSizedQueueChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(
        static () => { }, DispatcherPriority.ApplicationIdle).Task;

    internal static void Prepare(AppState state)
    {
        state.UseCapsuleFreeQueues = true;
        state.UseCapsuleCollapseAll = true;
        state.MasterCapsuleSize = 50;
        state.EnableAnimations = false;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
        state.Papers = state.Papers.Take(2).ToList();
        state.Papers[0].CapsuleQueueId = "large-a";
        state.Papers[1].CapsuleQueueId = "large-b";
        state.DeepCapsuleQueueStartTopMargins["|right#large-a"] = 48;
        state.DeepCapsuleQueueStartTopMargins["|right#large-b"] = 380;
        var pixels = new byte[1000 * 1000 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 2] = 180;
            pixels[i + 3] = 255;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1000, 1000, 96, 96,
            PixelFormats.Pbgra32, null, pixels, 4000)));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "papertodomain.png"));
        encoder.Save(output);
    }

    internal static async Task Run(AppController controller, IReadOnlyDictionary<string, PaperWindow> windows)
    {
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "fixture monitor");
        await Idle();
        var masters = Field<Dictionary<string, MasterCapsuleWindow>>(controller, "_masterCapsules");
        var owner = controller.State.Papers[0];
        var key = controller.QueueKeyForPaper(owner);
        var master = masters[key];
        var clip = MasterCapsuleSkin.Visuals.Normal!;
        foreach (var size in new[] { 50, 10, 50 })
        {
            controller.PublicSettings.Set("capsule.master_size", JsonSerializer.SerializeToElement(size));
            controller.ArrangeDeepCapsules(animate: false);
            await Idle();
            clip.ApplyDisplaySize();
            var height = MasterCapsuleSizes.HeightDip(size);
            Require(WindowNative.TryGetWindowDeviceBounds(master, out var bounds) &&
                    bounds.Height == (int)Math.Round(height * monitor.DpiScaleY) &&
                    master.QueueProxyTarget.Bounds.Height == bounds.Height &&
                    master.QueueProxyTarget.InteractiveBounds == master.QueueProxyTarget.Bounds,
                "the real master, yield proxy and image hit area disagree after resizing");
            var member = Field<EdgeCapsulePresenter>(windows[owner.Id], "_edgeCapsule").AppliedPresentation;
            Require(Math.Abs(member.Bounds.Top - bounds.Bottom - controller.DeepCapsuleGap * monitor.DpiScaleY) <= 1,
                "the first free-queue member overlaps the enlarged artwork");
            Require(clip.FirstFrame.PixelHeight <= Math.Ceiling(height * monitor.DpiScaleY) + 1 &&
                    clip.CanvasHeight == 1000 && master.DesiredDockedWidth == height,
                "the displayed frame cache must shrink without changing source geometry");
        }

        // Ctrl carries one master and one member. The real preview, ghost and
        // persisted independent drop must reserve the same enlarged first slot.
        var firstTop = master.AnchorTop + controller.MasterCapsuleHeightDip + controller.DeepCapsuleGap;
        DeviceScreenPoint Point(double top) => new(monitor.WorkArea.Right - 10,
            monitor.WorkArea.Top + (top + PaperLayoutDefaults.CapsuleHeight / 2) * monitor.DpiScaleY);
        Require(controller.BeginNativeEdgeQueueDrag(owner, true, Point(firstTop), false), "begin enlarged Ctrl drag");
        var requestedOwnerTop = controller.MasterCapsuleHeightDip + controller.DeepCapsuleGap;
        controller.PreviewNativeEdgeQueueDrag(owner, Point(requestedOwnerTop), requestedOwnerTop, false);
        var drag = Field<NativeEdgeQueueDrag>(controller, "_nativeEdgeQueueDrag");
        Require(drag.Drop?.Decision.Kind == EdgeFreeQueueDropKind.Move, "independent large-image preview");
        var ghost = Field<EdgeCapsuleQueueDropGhost>(controller, "_nativeQueueDropGhost");
        var ghostWindow = Field<Window>(ghost, "_window");
        Require(Math.Abs(ghostWindow.Height - (controller.MasterCapsuleHeightDip +
                controller.DeepCapsuleGap + PaperLayoutDefaults.CapsuleHeight)) < .1,
            "the independent ghost omitted the enlarged master's height");
        var expectedTop = drag.Drop!.Decision.TopDip;
        Require(controller.CommitNativeEdgeQueueDrag(owner, Point(requestedOwnerTop), requestedOwnerTop, false),
            "commit enlarged Ctrl drop");
        controller.EndNativeEdgeQueueDrag(owner.Id);
        await Idle();
        Require(Math.Abs(masters[controller.QueueKeyForPaper(owner)].AnchorTop - expectedTop) < .1,
            "the enlarged queue did not land at its preview position");
        Console.WriteLine("PASS sized master: display-sized frames, 5x/1x geometry, proxy hit bounds, Ctrl ghost and commit");
    }
}
