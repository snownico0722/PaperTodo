using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PaperTodo;

// Audit-only: use the REAL master drag, native HWND and sampled material paths.
// Native SetWindowPos moves are deterministic stand-ins for the Windows drag loop,
// not equivalent to real mouse polling, DWM presentation or display scanout.
internal static class Pr326DeterministicCost
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Sample(
        string Skin, int Iteration, double UiStartMs, double FirstNativeMoveMs,
        double SnapshotAwaitMs, double NativeMoveMedianMs, double NativeMoveP95Ms,
        int BackgroundFramesDuringMove, long ProjectedPositions,
        double ReleaseCallMs, double? BackgroundRecoveryMs, bool FrozenDragSnapshot,
        bool NativeAffinityRestored, bool MaterialActiveAtEnd);
    private static readonly List<Sample> Samples = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static void Run(AppController controller, string output)
    {
        typeof(AppController).GetProperty("UsesNativeMicaWindows", Private)!.SetValue(controller, true);
        controller.State.ColorScheme = ColorSchemes.Warm;
        controller.State.Theme = "light";
        controller.State.EnableAnimations = false;
        controller.State.MatchAuxiliaryMaterialStrength = true;
        controller.State.UseCapsuleMode = true;
        controller.State.UseDeepCapsuleMode = false;
        controller.State.ExperimentalInactivePaperOpacity = false;
        controller.State.ExperimentalRestingCapsuleOpacity = false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        foreach (var skin in new[] { PaperSkins.Acrylic, PaperSkins.TracingPaper, PaperSkins.Paper })
        {
            controller.State.PaperSkin = skin;
            Theme.Invalidate();
            RunCase(controller, skin, output);
        }
        Write(output);
    }

    private static void RunCase(AppController controller, string skin, string output)
    {
        var paper = new PaperWindow(new PaperData { Type = PaperTypes.Note }, controller);
        var master = new MasterCapsuleWindow(controller, EdgeCapsuleEdge.Right, "");
        var hostField = typeof(MasterCapsuleWindow).GetField("_floatingDragHost", Private)!;
        var prepare = typeof(MasterCapsuleWindow).GetMethod("PrepareFloatingDragBackgroundAsync", Private)!;
        var release = typeof(MasterCapsuleWindow).GetMethod("ReleaseFloatingDragHostHandlers", Private)!;
        var shape = new EdgeCapsuleFloatingShape(true, EdgeCapsuleSurfaceKind.FloatingFree,
            160, 40, 32, 16, true);
        var options = (EdgeCapsuleDragWindowOptions)typeof(PaperWindow)
            .GetMethod("CreateDeepCapsuleFloatingDragHostOptions", Private)!
            .Invoke(paper, [shape])!;
        EdgeCapsuleDragWindow? host = null;
        try
        {
            host = EdgeCapsuleDragWindow.Rent(options with { Icon = "▾", Label = "5" });
            hostField.SetValue(master, host);
            host.ShowWithEntrance(new DeviceScreenPoint(380, 240), false, 1, 0);
            var surface = (SkinBorder)typeof(EdgeCapsuleDragWindow)
                .GetField("_paperBackground", Private)!.GetValue(host)!;
            var hwnd = new WindowInteropHelper(host).Handle;
            var sampled = PaperSkins.UsesSampledAuxiliary(skin);
            if (sampled)
                Until(() => surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                    "initial static sampled material");

            for (var n = 0; n < 6; n++) // 2 warmups, 4 steady samples per skin
            {
                hostField.SetValue(master, host);
                var begin = Stopwatch.GetTimestamp();
                var job = master.Dispatcher.Invoke(() => (Task)prepare.Invoke(master, [host])!);
                var uiStartMs = MsSince(begin);
                GetWindowRect(hwnd, out var rect);
                var first = Stopwatch.GetTimestamp();
                Require(SetWindowPos(hwnd, IntPtr.Zero, rect.Left + 4, rect.Top + 3,
                    0, 0, 0x0015), "first real native move");
                var firstNativeMoveMs = MsSince(first);
                var untilSnapshot = Stopwatch.GetTimestamp();
                Until(() => job.IsCompleted, "virtual desktop snapshot");
                job.GetAwaiter().GetResult();
                var snapshotAwaitMs = MsSince(untilSnapshot);
                var texture = surface.BackgroundSessionState?.Bitmap;
                var validSnapshot = !sampled ||
                    (texture is System.Windows.Media.Imaging.BitmapSource bitmap &&
                     bitmap.IsFrozen && bitmap.PixelWidth > 50);
                var framesAtMove = surface.BackgroundFrameCount;
                var projectionStart = surface.BackgroundProjectionCount;
                var nativeMoves = new List<double>();
                for (var move = 0; move < 14; move++)
                {
                    GetWindowRect(hwnd, out rect);
                    var tick = Stopwatch.GetTimestamp();
                    Require(SetWindowPos(hwnd, IntPtr.Zero,
                        rect.Left + (move % 2 == 0 ? 8 : -8),
                        rect.Top + (move % 2 == 0 ? 4 : -4),
                        0, 0, 0x0015), "scripted native movement");
                    nativeMoves.Add(MsSince(tick));
                    Wait(8);
                }
                var deltaFrames = surface.BackgroundFrameCount - framesAtMove;
                var deltaProjections = surface.BackgroundProjectionCount - projectionStart;

                var releaseStart = Stopwatch.GetTimestamp();
                release.Invoke(master, null);
                var releaseMs = MsSince(releaseStart);
                double? recaptureMs = null;
                if (sampled)
                {
                    var doneAt = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
                    while (Stopwatch.GetTimestamp() < doneAt &&
                           (!surface.IsBackgroundActive || surface.HasBackgroundCapture ||
                            surface.BackgroundFrameCount <= framesAtMove))
                        Wait(10);
                    if (surface.IsBackgroundActive && !surface.HasBackgroundCapture &&
                        surface.BackgroundFrameCount > framesAtMove)
                        recaptureMs = MsSince(releaseStart);
                }
                var affinity = DesktopBackgroundCapture.ReadAffinity(hwnd) == 0;
                var active = !sampled || surface.IsBackgroundActive;
                if (n >= 2)
                {
                    Samples.Add(new Sample(skin, n - 2, uiStartMs,
                        firstNativeMoveMs, snapshotAwaitMs,
                        Percentile(nativeMoves, .5), Percentile(nativeMoves, .95),
                        deltaFrames, deltaProjections, releaseMs, recaptureMs,
                        validSnapshot, affinity, active));
                    Write(output);
                }
                Console.WriteLine($"COST {skin} i={n} UI={uiStartMs:F3}ms firstMove={firstNativeMoveMs:F3}ms " +
                    $"nativeP95={Percentile(nativeMoves,.95):F3}ms capture={snapshotAwaitMs:F1}ms " +
                    $"recover={(recaptureMs?.ToString("F1") ?? "N/A")}ms affinity={affinity}");
                if (n < 5) Wait(40);
            }
        }
        finally
        {
            try { release.Invoke(master, null); } catch { }
            if (host != null) host.ReturnToPool();
            master.CloseForReal();
            paper.CloseForReal();
            Wait(40);
        }
    }

    private static double Percentile(List<double> values, double fraction) =>
        values.Order().ElementAt(Math.Min(values.Count - 1, (int)Math.Floor((values.Count - 1) * fraction)));

    private static double MsSince(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static void Require(bool condition, string explanation)
    {
        if (!condition) throw new InvalidOperationException(explanation);
    }
    private static void Write(string output)
    {
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Revision = Environment.GetEnvironmentVariable("PAPER_BENCH_REVISION"),
            OS = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            Scope = "Same WPF/real native HWND/material code. Moves injected via SetWindowPos; NOT real mouse-to-photon latency.",
            Samples
        }, Json));
    }
    private static void Until(Func<bool> predicate, string reason)
    {
        var start = Stopwatch.StartNew();
        while (!predicate())
        {
            if (start.ElapsedMilliseconds > 9000)
                throw new TimeoutException(reason);
            Wait(10);
        }
    }
    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ms)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RectI { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
        int x, int y, int width, int height, uint flags);
}
