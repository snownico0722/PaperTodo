using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static class Program
{
    private const int SourceSize = 48;
    private const int Travel = 120;
    private const double DurationSeconds = 0.8;
    private const int StallMilliseconds = 360;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "--target", StringComparison.Ordinal))
            {
                RunCrossProcessTarget(args);
                return 0;
            }

            RunLagReproduction();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void RunLagReproduction()
    {
        Check(GetCursorPos(out var originalCursor), "The H8 probe requires an interactive desktop");
        using var target = CrossProcessTarget.Start();
        target.WaitReady();

        var workArea = GetPrimaryWorkArea();
        var outputBounds = new DeviceScreenRect(
            workArea.Left + 180,
            workArea.Top + 180,
            workArea.Left + 180 + SourceSize + Travel + 40,
            workArea.Top + 180 + SourceSize + 24);
        var initialBounds = new DeviceScreenRect(
            outputBounds.Left + 12,
            outputBounds.Top + 12,
            outputBounds.Left + 12 + SourceSize,
            outputBounds.Top + 12 + SourceSize);
        target.SetBounds(outputBounds);

        var root = new Border
        {
            Width = SourceSize,
            Height = SourceSize,
            Background = Brushes.Red,
            SnapsToDevicePixels = true
        };
        var sourceWindow = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Topmost = true,
            Left = -32000,
            Top = -32000,
            Width = SourceSize,
            Height = SourceSize,
            Content = root
        };

        EdgeCapsuleQueueProxyWindow? output = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? compositionTarget = null;
        IDCompositionVisual2? compositionRoot = null;
        IDCompositionVisual2? visual = null;
        IDCompositionAnimation? animation = null;
        IDisposable? surface = null;
        var sourceCloaked = false;
        try
        {
            sourceWindow.Show();
            WindowNative.ApplyNoActivateStyle(sourceWindow);
            var sourceHandle = new WindowInteropHelper(sourceWindow).Handle;
            Check(sourceHandle != IntPtr.Zero, "The H8 WPF source HWND was not created");
            Check(WindowNative.TrySetWindowDeviceBounds(sourceWindow, initialBounds),
                "The H8 source HWND could not be positioned in device pixels");
            sourceWindow.UpdateLayout();
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, static () => { });
            FlushDesktop("initial WPF source render");

            output = EdgeCapsuleQueueProxyWindow.TryCreate(
                outputBounds,
                topmost: true,
                point => EdgeCapsuleGeometry.Contains(initialBounds, point),
                static _ => { },
                static () => { },
                static () => { },
                static () => { });
            Check(output != null, "The production proxy HWND pair could not be created");

            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(IntPtr.Zero, ref iid, out var pointer));
            device = new IDCompositionDesktopDevice(pointer);
            device.CreateTargetForHwnd(output.Handle, topmost: true, out compositionTarget).CheckError();
            device.CreateVisual(out compositionRoot).CheckError();
            device.CreateSurfaceFromHwnd(sourceHandle, out var liveSurface).CheckError();
            surface = liveSurface;
            device.CreateVisual(out visual).CheckError();
            visual.SetContent(liveSurface).CheckError();
            visual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear).CheckError();
            visual.SetBorderMode(BorderMode.Soft).CheckError();
            visual.SetOffsetX(initialBounds.Left - outputBounds.Left).CheckError();
            visual.SetOffsetY(initialBounds.Top - outputBounds.Top).CheckError();
            compositionRoot.AddVisual(visual, insertAbove: true, referenceVisual: null!).CheckError();
            compositionTarget.SetRoot(compositionRoot).CheckError();
            device.Commit().CheckError();

            Check(output.TrySetInputRegions(new[] { initialBounds }),
                "The production input HWND could not publish the initial card region");
            Check(output.Show(outputBounds, topmost: true),
                "The production output/input HWND pair could not be shown");
            FlushDesktop("initial proxy publication");

            sourceCloaked = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        sourceHandle,
                        Cloaked: true,
                        RollbackCloaked: false)
                }) == WindowNative.WindowCloakBatchResult.Success;
            Check(sourceCloaked, "The real WPF source could not be cloaked behind the proxy");
            FlushDesktop("source cloak");

            var initialPixels = FindRedSpan(outputBounds, initialBounds.Top + SourceSize / 2);
            Check(initialPixels.Count >= SourceSize / 2,
                $"The H8 probe could not observe the initial redirected red source (count={initialPixels.Count})");
            Check(initialPixels.Right <= initialBounds.Right + 3,
                $"The initial redirected surface already extends beyond the stale region: {initialPixels}");

            animation = device.CreateAnimation();
            var from = (float)(initialBounds.Left - outputBounds.Left);
            var to = from + Travel;
            animation.SetAbsoluteBeginTime(Stopwatch.GetTimestamp()).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * Travel / DurationSeconds),
                (float)(-3 * Travel / (DurationSeconds * DurationSeconds)),
                (float)(Travel / (DurationSeconds * DurationSeconds * DurationSeconds))).CheckError();
            animation.End(DurationSeconds, to).CheckError();
            visual.SetOffsetX(animation).CheckError();
            device.Commit().CheckError();

            // Deliberately block the owner/UI thread. DirectComposition keeps advancing on the
            // compositor while a DispatcherTimer-based native region updater cannot run.
            Thread.Sleep(StallMilliseconds);
            FlushDesktop("stalled compositor sample");

            var stalledPixels = FindRedSpan(outputBounds, initialBounds.Top + SourceSize / 2);
            Check(stalledPixels.Count >= SourceSize / 2,
                $"The redirected source disappeared during the stall (count={stalledPixels.Count})");
            Check(stalledPixels.Right > initialBounds.Right + 12,
                $"The DComp visual did not advance beyond the stale input region: initialRight={initialBounds.Right} stalled={stalledPixels}");

            var leakPoint = new DeviceScreenPoint(
                Math.Max(initialBounds.Right + 6, stalledPixels.Right - 6),
                initialBounds.Top + SourceSize / 2);
            Check(leakPoint.X < stalledPixels.Right && IsRedPixel(leakPoint),
                $"Expected a visibly red leading-edge pixel outside the stale input region at {leakPoint}");
            Check(!EdgeCapsuleGeometry.Contains(initialBounds, leakPoint),
                "The selected leading-edge point must remain outside the stale native input region");

            SetCursorPos((int)Math.Round(leakPoint.X), (int)Math.Round(leakPoint.Y));
            Thread.Sleep(25);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(20);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            target.ExpectGesture();

            Console.WriteLine(
                $"PASS h8-real-dcomp-stall-cross-process-input-leak " +
                $"initial={initialPixels} stalled={stalledPixels} staleRight={initialBounds.Right} " +
                $"leakPoint={leakPoint.X:F0},{leakPoint.Y:F0} stallMs={StallMilliseconds}");
        }
        finally
        {
            try
            {
                if (sourceCloaked)
                {
                    var handle = new WindowInteropHelper(sourceWindow).Handle;
                    _ = WindowNative.TrySetWindowCloakedBatchDetailed(
                        new[]
                        {
                            new WindowNative.WindowCloakChange(
                                handle,
                                Cloaked: false,
                                RollbackCloaked: true)
                        });
                }
            }
            catch { }
            try
            {
                if (compositionTarget != null && device != null)
                {
                    compositionTarget.SetRoot(null!).CheckError();
                    device.Commit().CheckError();
                }
            }
            catch { }
            try { output?.Hide(); } catch { }
            try { animation?.Dispose(); } catch { }
            try { visual?.Dispose(); } catch { }
            try { surface?.Dispose(); } catch { }
            try { compositionRoot?.Dispose(); } catch { }
            try { compositionTarget?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            try { output?.Dispose(); } catch { }
            try { sourceWindow.Hide(); } catch { }
            try { sourceWindow.Close(); } catch { }
            _ = SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static void RunCrossProcessTarget(string[] args)
    {
        Check(args.Length == 2, "Target mode requires a pipe name");
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.Out);
        pipe.Connect(5000);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        var root = new Border { Background = Brushes.DodgerBlue };
        var window = new Window
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.DodgerBlue,
            Content = root,
            Width = 100,
            Height = 100,
            Left = -32000,
            Top = -32000
        };
        root.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) writer.WriteLine("DOWN");
        };
        root.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) writer.WriteLine("UP");
        };
        window.Show();
        writer.WriteLine("READY");
        Dispatcher.Run();
    }

    private static DeviceScreenRect GetPrimaryWorkArea()
    {
        Check(SystemParametersInfo(0x0030, 0, out var rect, 0), "SPI_GETWORKAREA failed");
        return new DeviceScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static RedSpan FindRedSpan(DeviceScreenRect bounds, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        Check(dc != IntPtr.Zero, "GetDC(NULL) failed");
        try
        {
            var left = int.MaxValue;
            var right = int.MinValue;
            var count = 0;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var color = GetPixel(dc, x, y);
                if (!IsRed(color)) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                count++;
            }
            return count == 0 ? new RedSpan(0, 0, 0) : new RedSpan(left, right, count);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static bool IsRedPixel(DeviceScreenPoint point)
    {
        var dc = GetDC(IntPtr.Zero);
        Check(dc != IntPtr.Zero, "GetDC(NULL) failed");
        try
        {
            return IsRed(GetPixel(dc, (int)Math.Round(point.X), (int)Math.Round(point.Y)));
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static bool IsRed(uint color)
    {
        if (color == 0xFFFFFFFF) return false;
        var r = (byte)(color & 0xFF);
        var g = (byte)((color >> 8) & 0xFF);
        var b = (byte)((color >> 16) & 0xFF);
        return r >= 180 && g <= 90 && b <= 90;
    }

    private static void FlushDesktop(string phase)
    {
        Marshal.ThrowExceptionForHR(DwmFlush());
        Console.WriteLine($"TRACE h8 phase={phase}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private readonly record struct RedSpan(int Left, int Right, int Count)
    {
        public override string ToString() => $"[{Left},{Right}) count={Count}";
    }

    private sealed class CrossProcessTarget : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly Process _process;
        private StreamReader? _reader;

        private CrossProcessTarget(NamedPipeServerStream server, Process process)
        {
            _server = server;
            _process = process;
        }

        public static CrossProcessTarget Start()
        {
            var pipeName = $"papertodo-pr260-h8-{Environment.ProcessId}-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(pipeName);
            var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start cross-process target");
            return new CrossProcessTarget(server, process);
        }

        public void WaitReady()
        {
            _server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            _reader = new StreamReader(_server);
            var line = _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            Check(line == "READY", $"Unexpected target readiness line: {line ?? "<null>"}");
        }

        public void SetBounds(DeviceScreenRect bounds)
        {
            var deadline = Stopwatch.StartNew();
            IntPtr handle = IntPtr.Zero;
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                var current = GetTopWindow(IntPtr.Zero);
                while (current != IntPtr.Zero)
                {
                    _ = GetWindowThreadProcessId(current, out var processId);
                    if (processId == _process.Id)
                    {
                        handle = current;
                        break;
                    }
                    current = GetWindow(current, 2);
                }
                if (handle != IntPtr.Zero) break;
                Thread.Sleep(20);
            }
            Check(handle != IntPtr.Zero, "Could not locate cross-process target HWND");
            Check(SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                0x0010 | 0x0040), "Could not position cross-process target HWND");
        }

        public void ExpectGesture()
        {
            Check(_reader != null, "Cross-process target is not connected");
            var first = _reader!.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var second = _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check(first == "DOWN" && second == "UP",
                $"Visible leading-edge click did not reach the lower process exactly once (lines={first},{second})");
        }

        public void Dispose()
        {
            try { _server.Dispose(); } catch { }
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch { }
            _process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice2(
        IntPtr renderingDevice,
        ref Guid iid,
        out IntPtr dcompositionDevice);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out NativeRect rect, uint winIni);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
}
