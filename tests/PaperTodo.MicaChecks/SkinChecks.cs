using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class SkinChecks
{
    internal static void Run(AppController controller)
    {
        var before = (controller.State.PaperSkin, controller.State.ColorScheme, controller.State.Theme,
            controller.State.EnableAnimations, controller.State.HideSurfaceOutline);
        try
        {
            controller.State.Theme = "light";
            controller.State.ColorScheme = ColorSchemes.Warm;
            controller.State.EnableAnimations = false;
            controller.State.HideSurfaceOutline = false;
            CheckAuxiliaryContent(controller);
            CheckLiveSwitch(controller);
        }
        finally
        {
            (controller.State.PaperSkin, controller.State.ColorScheme, controller.State.Theme,
                controller.State.EnableAnimations, controller.State.HideSurfaceOutline) = before;
            Theme.Invalidate();
        }
    }

    private static void CheckAuxiliaryContent(AppController controller)
    {
        // One native recipe and one locally painted skin exercise the two rendering paths.
        foreach (var skin in new[] { PaperSkins.Acrylic, PaperSkins.Pixel })
        foreach (var capsule in new[] { false, true })
        {
            controller.State.PaperSkin = skin;
            Theme.Invalidate();
            var surface = new SkinBorder
            {
                Width = 240, Height = 80, IsCapsule = capsule, IsMenu = !capsule,
                CornerRadius = new CornerRadius(12), Background = Theme.PaperBrush,
                Child = new Border { Width = 12, Height = 12, Background = Brushes.Lime,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center }
            };
            var pixels = Pixels(Render(surface, 1));
            var center = (40 * 240 + 120) * 4;
            Program.Assert(pixels[center + 1] == 255 && pixels[center + 3] == 255,
                $"{skin}: auxiliary material preserves opaque foreground content");
            Program.Assert(VisualTreeHelper.HitTest(surface, new Point(120, 40)) != null,
                $"{skin}: auxiliary content remains interactive");
        }
    }

    private static void CheckLiveSwitch(AppController controller)
    {
        var paper = new PaperData { Type = PaperTypes.Note, Title = "材质切换 · 同一张纸", Content = "# 今日待办\n\n换肤与缩放保留 **正文**。", Width = 360, Height = 320, X = 40, Y = 40 };
        controller.State.Papers.Add(paper);
        var window = new PaperWindow(paper, controller);
        try
        {
            window.Show(); Program.Pump();
            var handle = new WindowInteropHelper(window).Handle;
            var noteProperty = typeof(PaperWindow).GetProperty("_noteBox", Program.Private)!;
            var editor = noteProperty.GetValue(window);
            Program.Assert(editor != null, "real note editor mounted");
            foreach (var skin in new[] { PaperSkins.Paper, PaperSkins.Mica, PaperSkins.Aero, PaperSkins.Pixel })
            {
                controller.State.PaperSkin = skin;
                Theme.Invalidate(); window.UpdateTheme(); Program.Pump();
                CheckResizeHitTests(window, $"native {skin}");
                if (skin == PaperSkins.Mica)
                    DragFromPaperCorner(window, "native Mica after skin change");
                Program.Assert(ReferenceEquals(editor, noteProperty.GetValue(window)) && new WindowInteropHelper(window).Handle == handle, "actual editor and HWND preserved");
                window.SetCollapsedState(true, animate: false, saveGeometry: false); Program.Pump();
                Program.Assert(!window.IsNativeMicaEffective, "capsule remains on the solid path");
                window.SetCollapsedState(false, animate: false, saveGeometry: false); Program.Pump();
            }
        }
        finally { window.CloseForReal(); controller.State.Papers.Remove(paper); }
    }

    internal static void CheckLayeredResize(AppController controller)
    {
        var previousSkin = controller.State.PaperSkin;
        var window = new PaperWindow(new PaperData
        {
            Type = PaperTypes.Note, Width = 360, Height = 320, X = 60, Y = 60
        }, controller);
        try
        {
            Program.Assert(window.AllowsTransparency, "layered resize check requires a layered paper");
            window.Show(); Program.Pump();
            foreach (var skin in new[] { PaperSkins.Paper, PaperSkins.Aero })
            {
                controller.State.PaperSkin = skin;
                Theme.Invalidate();
                window.UpdateTheme();
                Program.Pump();
                CheckResizeHitTests(window, $"layered {skin}");
                DragFromPaperCorner(window, $"layered {skin} after skin change");
            }
        }
        finally
        {
            window.CloseForReal();
            controller.State.PaperSkin = previousSkin;
            Theme.Invalidate();
        }
    }

    private static void CheckResizeHitTests(PaperWindow window, string scenario)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Program.Assert(window.ResizeMode == ResizeMode.CanResize,
            $"{scenario}: expanded paper must allow resizing");
        Program.Assert(GetWindowRect(hwnd, out var rect), $"{scenario}: HWND rectangle");

        var chrome = (PaperChromeBorder)typeof(PaperWindow)
            .GetField("_paperChrome", Program.Private)!.GetValue(window)!;
        var dpi = VisualTreeHelper.GetDpi(window);
        var leftInset = (int)Math.Round((chrome.Margin.Left + 2) * dpi.DpiScaleX);
        var rightInset = (int)Math.Round((chrome.Margin.Right + 2) * dpi.DpiScaleX);
        var topInset = (int)Math.Round((chrome.Margin.Top + 2) * dpi.DpiScaleY);
        var bottomInset = (int)Math.Round((chrome.Margin.Bottom + 2) * dpi.DpiScaleY);
        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        Program.Assert(ResizeHit(hwnd, rect.Left + leftInset, centerY) == 10,
            $"{scenario}: painted left edge must resize");
        Program.Assert(ResizeHit(hwnd, rect.Right - rightInset, centerY) == 11,
            $"{scenario}: painted right edge must resize");
        Program.Assert(ResizeHit(hwnd, centerX, rect.Bottom - bottomInset) == 15,
            $"{scenario}: painted bottom edge must resize");
        Program.Assert(ResizeHit(hwnd, rect.Right - rightInset, rect.Bottom - bottomInset) == 17,
            $"{scenario}: painted bottom-right corner must resize");
        // Ensure body content near the corner still receives normal clicks.
        Program.Assert(ResizeHit(hwnd, rect.Right - rightInset - 15, rect.Bottom - bottomInset - 15) == 1,
            $"{scenario}: body beside resize edge remains interactive");
        if (chrome.HeaderOpacity != 0)
            Program.Assert(ResizeHit(hwnd, centerX, rect.Top + topInset) == 12,
                $"{scenario}: painted top edge must resize");
    }

    private static void DragFromPaperCorner(PaperWindow window, string scenario)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Program.Assert(GetWindowRect(hwnd, out var before), $"{scenario}: original bounds");
        var chrome = (PaperChromeBorder)typeof(PaperWindow)
            .GetField("_paperChrome", Program.Private)!.GetValue(window)!;
        var dpi = VisualTreeHelper.GetDpi(window);
        var origin = new NativePoint(
            before.Right - (int)Math.Round((chrome.Margin.Right + 4) * dpi.DpiScaleX),
            before.Bottom - (int)Math.Round((chrome.Margin.Bottom + 4) * dpi.DpiScaleY));
        Program.Assert(ResizeHit(hwnd, origin.X, origin.Y) == 17,
            $"{scenario}: bottom-right painted edge enables diagonal resize");
        Program.Assert(GetCursorPos(out var priorCursor), $"{scenario}: original cursor");
        window.Activate();
        Program.Pump();
        var nativeTarget = WindowFromPoint(origin);
        Program.Assert(nativeTarget == hwnd,
            $"{scenario}: painted edge must receive native input " +
            $"(target={nativeTarget}, paper={hwnd}, point={origin.X},{origin.Y})");

        try
        {
            // Win32 enters a modal sizing loop on mouse-down; inject the entire
            // press/move/release from another thread while WPF continues pumping.
            var drag = Task.Run(() =>
            {
                if (!SetCursorPos(origin.X, origin.Y)) throw new InvalidOperationException("Move to resize edge failed");
                Thread.Sleep(80);
                try
                {
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
                    Thread.Sleep(100);
                    for (var step = 1; step <= 4; step++)
                    {
                        if (!SetCursorPos(origin.X + step * 18, origin.Y + step * 15))
                            throw new InvalidOperationException("Resize drag pointer move failed");
                        Thread.Sleep(55);
                    }
                }
                finally { MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); } // LEFTUP
            });
            var elapsed = Stopwatch.StartNew();
            while (!drag.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(8))
                Program.Pump();
            Program.Assert(drag.IsCompleted, $"{scenario}: native resize gesture timed out");
            drag.GetAwaiter().GetResult();
            Program.Pump();
            Program.Assert(GetWindowRect(hwnd, out var after), $"{scenario}: final bounds");
            var oldW = before.Right - before.Left;
            var oldH = before.Bottom - before.Top;
            var newW = after.Right - after.Left;
            var newH = after.Bottom - after.Top;
            Program.Assert(newW >= oldW + 25 && newH >= oldH + 20,
                $"{scenario}: real mouse drag must increase BOTH dimensions " +
                $"({oldW}x{oldH} -> {newW}x{newH})");
        }
        finally
        {
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            _ = SetCursorPos(priorCursor.X, priorCursor.Y);
            Program.Pump();
        }
    }

    private static int ResizeHit(IntPtr hwnd, int x, int y)
    {
        var packed = new IntPtr(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        return SendMessage(hwnd, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, packed).ToInt32();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
        public NativePoint(int x, int y) => (X, Y) = (x, y);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static RenderTargetBitmap Render(FrameworkElement element, double scale)
    {
        if (element is not Window) { element.Measure(new Size(element.Width, element.Height)); element.Arrange(new Rect(0, 0, element.Width, element.Height)); }
        element.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(element); return image;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
}
