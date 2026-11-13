// One-off PR #454 experiment. Compare two marker pixels on a controlled desktop.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

internal static class CaptureDesktopProbe
{
    private const uint Exclude = 0x11;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, IntPtr level, IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureInterop
    {
        IntPtr CreateForWindow(IntPtr hwnd, in Guid riid);
        IntPtr CreateForMonitor(IntPtr monitor, in Guid riid);
    }

    private static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(0, 1, 0, 32, 0, 0, 7, out var d3d, 0, 0);
        if (hr < 0) Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 5, 0, 32, 0, 0, 7, out d3d, 0, 0));
        var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, ref iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var abi));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(abi); }
                finally { Marshal.Release(abi); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(d3d); }
    }

    private static async Task<string> Wgc(IntPtr monitor, Point[] samples)
    {
        if (!GraphicsCaptureSession.IsSupported()) return "unavailable";
        var factory = GraphicsCaptureItem.As<ICaptureInterop>();
        var abi = factory.CreateForMonitor(monitor, new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760"));
        GraphicsCaptureItem item;
        try { item = GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
        using var device = CreateDevice();
        using var frames = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = frames.CreateCaptureSession(item);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        frames.FrameArrived += (_, _) => ready.TrySetResult();
        session.StartCapture();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
        using var frame = frames.TryGetNextFrame();
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);
        using var reader = DataReader.FromBuffer(buffer);
        var data = new byte[buffer.Length];
        reader.ReadBytes(data);
        return string.Join("/", samples.Select(p =>
        {
            var offset = ((int)p.Y * bitmap.PixelWidth + (int)p.X) * 4;
            return $"{data[offset + 2]:X2}{data[offset + 1]:X2}{data[offset]:X2}";
        }));
    }

    private static string Gdi(Point p)
    {
        using var pixel = new System.Drawing.Bitmap(1, 1);
        using (var g = System.Drawing.Graphics.FromImage(pixel))
        {
            var to = g.GetHdc();
            var screen = GetDC(0);
            try { if (!BitBlt(to, 0, 0, 1, 1, screen, (int)p.X, (int)p.Y, 0x40CC0020)) return "no-frame"; }
            finally { ReleaseDC(0, screen); g.ReleaseHdc(to); }
        }
        var c = pixel.GetPixel(0, 0);
        return $"{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    internal static void Run(AppController controller)
    {
        var state = controller.State;
        var mode = typeof(AppController).GetProperty("UsesNativeMicaWindows", Program.Private)!;
        var saved = (controller.UsesNativeMicaWindows, state.PaperSkin, state.HidePapersInCapture, state.ExperimentalInactivePaperOpacity);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border { Background = new SolidColorBrush(Color.FromRgb(231, 41, 87)) };
        var right = new Border { Background = new SolidColorBrush(Color.FromRgb(37, 217, 103)) };
        Grid.SetColumn(right, 1); grid.Children.Add(left); grid.Children.Add(right);
        var back = new Window { Left = 80, Top = 80, Width = 400, Height = 300, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Content = grid };
        try
        {
            back.Show(); back.Activate(); Program.Pump();
            var points = new[] { grid.PointToScreen(new Point(100, 145)), grid.PointToScreen(new Point(290, 145)) };
            // Fixture is on the primary monitor (origin 0,0).
            var monitor = MonitorFromWindow(new WindowInteropHelper(back).Handle, 2);
            bool tryWgc = true;
            void Sample(string label)
            {
                var settle = Stopwatch.StartNew();
                while (settle.ElapsedMilliseconds < 350) { Program.Pump(); Thread.Sleep(12); }
                DwmFlush();
                var gdi = string.Join("/", points.Select(Gdi));
                string wgc = "unavailable";
                if (tryWgc)
                {
                    try
                    {
                        var task = Task.Run(() => Wgc(monitor, points));
                        while (!task.IsCompleted && settle.ElapsedMilliseconds < 7500) { Program.Pump(); Thread.Sleep(10); }
                        wgc = task.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                    }
                    catch (Exception e) { wgc = e.GetBaseException().Message; tryWgc = false; }
                }
                Console.WriteLine($"CAPTURE-DESKTOP {label}: GDI={gdi} WGC={wgc}");
            }
            Console.WriteLine($"CAPTURE-DESKTOP OS={Environment.OSVersion.Version}, expected markers=E72957/25D967");
            Sample("background");
            foreach (var skin in new[] { PaperSkins.Paper, PaperSkins.Mica })
            {
                state.PaperSkin = skin; state.HidePapersInCapture = false; state.ExperimentalInactivePaperOpacity = false;
                mode.SetValue(controller, skin == PaperSkins.Mica);
                Theme.Invalidate();
                var paper = new PaperData { Type = PaperTypes.Todo, X = 80, Y = 80, Width = 400, Height = 300, IsVisible = true, AlwaysOnTop = true };
                state.Papers.Add(paper);
                var window = new PaperWindow(paper, controller);
                try
                {
                    window.Show(); window.Activate(); Program.Pump();
                    Sample(skin + " visible");
                    state.HidePapersInCapture = true; window.UpdateCaptureExclusion();
                    var handle = new WindowInteropHelper(window).Handle;
                    var read = GetWindowDisplayAffinity(handle, out var flags);
                    Sample($"{skin} excluded(read={read},affinity=0x{flags:X})");
                    if (!read || flags != Exclude)
                    {
                        var accepted = SetWindowDisplayAffinity(handle, Exclude);
                        Console.WriteLine($"CAPTURE-DESKTOP {skin}: native set={accepted}, error={Marshal.GetLastWin32Error()}");
                    }
                    state.HidePapersInCapture = false; window.UpdateCaptureExclusion();
                    Sample(skin + " restored");
                }
                finally { window.CloseForReal(); state.Papers.Remove(paper); }
            }
        }
        finally
        {
            back.Close();
            mode.SetValue(controller, saved.Item1);
            (state.PaperSkin, state.HidePapersInCapture, state.ExperimentalInactivePaperOpacity) = (saved.Item2, saved.Item3, saved.Item4);
            Theme.Invalidate();
        }
    }
}
