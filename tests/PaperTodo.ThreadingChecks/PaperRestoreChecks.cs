using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PaperTodo.ThreadingChecks;

internal static partial class Program
{
    private static void CheckRememberedPaperRestore()
    {
        // Regression: a paper at physical X=2100 on a 150% secondary has WPF Left=1400.
        // Multiplying by the 100% primary's scale would incorrectly look it up on the primary.
        var saved = new Rect(1400, 100, 300, 400);
        Assert(PaperRestoreGeometry.TryGetDeviceBounds(saved, 1.5, out var physical), "saved DPI conversion");
        Assert(physical == new DeviceScreenRect(2100, 150, 2550, 750), "150% position lost its monitor");
        var secondary = new MonitorGeometry("secondary", new DeviceScreenRect(1920, 0, 4480, 1440), 1.5, 1.5);
        var placement = PaperRestoreGeometry.ClampToMonitor(physical, 300, 400, secondary);
        Assert(placement.Bounds == physical && placement.WidthDip == 300, "same-DPI restore changed the rectangle");
        var changedDpi = PaperRestoreGeometry.ClampToMonitor(physical, 300, 400,
            secondary with { DpiScaleX = 2, DpiScaleY = 2 });
        Assert(changedDpi.Bounds == new DeviceScreenRect(2100, 150, 2700, 950),
            "destination DPI must change size without rescaling the saved physical position");
        Assert(PaperRestoreGeometry.TryGetDeviceBounds(new Rect(-1400, -100, 300, 400), 1.5, out var negative) &&
            negative.Left == -2100 && negative.Top == -150, "negative desktop coordinates were lost");
        var rescued = PaperRestoreGeometry.ClampToMonitor(physical, 300, 400,
            new MonitorGeometry("primary", new DeviceScreenRect(0, 0, 1920, 1080), 1, 1));
        Assert(rescued.Bounds == new DeviceScreenRect(1612, 150, 1912, 550), "disconnected-screen rescue must fit the live work area");
        Assert(!PaperRestoreGeometry.TryGetDeviceBounds(saved, double.NaN, out _) &&
            !PaperRestoreGeometry.TryGetDeviceBounds(new Rect(1e20, 0, 300, 400), 1, out _), "invalid native bounds accepted");

        CheckRememberedPaperNativeRoundTrip();
        CheckRememberedPaperDpiPersistence();
    }

    private static void CheckRememberedPaperNativeRoundTrip()
    {
        var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
        var paper = new PaperData { CapsuleSide = DeepCapsuleSides.Right };
        SetField(controller, "<State>k__BackingField", new AppState { Papers = [paper] });
        SetField(controller, "_suppressDirty", true);
        var window = new Window { Width = 300, Height = 240, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            Assert(WindowNative.TryGetWindowDeviceBounds(window, out var original), "native window unavailable");
            Assert(WindowWorkAreaHelper.TryGetMonitorGeometryForDeviceBounds(original, out var monitor), "native monitor unavailable");
            var target = PaperRestoreGeometry.ClampToMonitor(original, 300, 240, monitor);
            Assert(WindowNative.TrySetWindowDeviceBounds(window, target.Bounds), "native placement failed");
            window.UpdateLayout();
            Assert(WindowNative.TryGetWindowDeviceBounds(window, out var captured), "native capture failed");
            typeof(AppController).GetMethod("UpdateDeepCapsuleExpandedGeometry", PrivateInstance)!
                .Invoke(controller, [paper, window]);
            Assert(paper.DeepCapsuleExpandedDpiScale > 0, "real HWND save did not capture its DPI");
            Assert(controller.TryGetRememberedDeepCapsuleExpandedGeometry(paper, 300, 240, out var restored) &&
                EdgeCapsuleGeometry.DeviceBoundsMatch(restored.Bounds, captured, tolerance: 1), "controller restore changed the physical snapshot");
            Assert(WindowNative.TrySetWindowDeviceBounds(window, captured.WithVerticalEdges(captured.Top + 20, captured.Bottom + 20)), "test move failed");
            Assert(WindowNative.TrySetWindowDeviceBounds(window, restored.Bounds) &&
                WindowNative.TryGetWindowDeviceBounds(window, out var applied) &&
                EdgeCapsuleGeometry.DeviceBoundsMatch(applied, restored.Bounds, tolerance: 1), "native restore did not return to the saved position");
            paper.DeepCapsuleExpandedSide = DeepCapsuleSides.Left;
            Assert(!controller.TryGetRememberedDeepCapsuleExpandedGeometry(paper, 300, 240, out _), "changed queue reused stale history");
        }
        finally { window.Close(); }
    }

    private static void CheckRememberedPaperDpiPersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "papertodo-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new StateStore(directory, DurableAtomicFileWriter.Shared);
            var paper = new PaperData
            {
                DeepCapsuleExpandedX = 1400, DeepCapsuleExpandedY = 100,
                DeepCapsuleExpandedWidth = 300, DeepCapsuleExpandedHeight = 400
            };
            var state = new AppState { Papers = [paper] };
            foreach (var scale in new double?[] { null, 1.5, 0, double.NaN })
            {
                paper.DeepCapsuleExpandedDpiScale = scale;
                File.WriteAllText(store.FilePath, store.SerializeState(state));
                var loaded = store.Load().Papers.Single();
                Assert(loaded.DeepCapsuleExpandedX == 1400 && loaded.DeepCapsuleExpandedWidth == 300,
                    "DPI metadata changed legacy geometry values");
                double? expectedScale = scale == 1.5 ? 1.5 : null;
                Assert(loaded.DeepCapsuleExpandedDpiScale == expectedScale,
                    "DPI round-trip, missing-field compatibility or invalid-value cleanup failed");
            }
            paper.DeepCapsuleExpandedDpiScale = 1.5;
            paper.DeepCapsuleExpandedX = null;
            File.WriteAllText(store.FilePath, store.SerializeState(state));
            Assert(store.Load().Papers.Single().DeepCapsuleExpandedDpiScale == null,
                "cleared geometry retained stale DPI metadata");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
