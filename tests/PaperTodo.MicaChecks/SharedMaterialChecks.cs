using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static class SharedMaterialChecks
{
    internal static void Run(AppController controller)
    {
        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
            controller.State.EnableAnimations, controller.State.LiquidGlassRefraction, controller.State.MatchAuxiliaryMaterialStrength);
        var rear = new Window { Left = 20, Top = 20, Width = 800, Height = 600,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, Background = Pattern() };
        Window? window = null; ContextMenu? menu = null;
        try
        {
            controller.State.PaperSkin = PaperSkins.LiquidGlass; controller.State.Theme = "light";
            controller.State.ColorScheme = ColorSchemes.Neutral; controller.State.EnableAnimations = true;
            controller.State.LiquidGlassRefraction = true; controller.State.MatchAuxiliaryMaterialStrength = true;
            Theme.Invalidate();
            // This really is a layered top-level capsule HWND, not a Border rendered without a host.
            var marker = new Border { Width = 12, Height = 12, Background = Brushes.Lime,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var surface = new SkinBorder { IsCapsule = true, CornerRadius = new CornerRadius(28),
                Background = Theme.PaperBrush, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = marker };
            window = new Window { Left = 100, Top = 120, Width = 360, Height = 100, AllowsTransparency = true,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.Transparent,
                ShowInTaskbar = false, Topmost = true, Content = surface };
            rear.Show(); window.Show();
            Ready(surface, 0, "layered capsule starts the actual live lens");
            var hwnd = new WindowInteropHelper(window).Handle;
            Program.Assert(DesktopLensCapture.ReadAffinity(hwnd) == 0x11 && surface.HasLensLightSubscription,
                "capsule owns its real exclusion lease and pointer light");
            CheckOpticalPixels(surface, marker, "capsule");
            using (surface.FreezeRefractionForEvidence())
            {
                GetCursorPos(out var oldPointer);
                try
                {
                    SetCursorPos(112, 135); Wait(120); var nearLeft = Pixels(Render(surface));
                    SetCursorPos(448, 180); Wait(120); var nearRight = Pixels(Render(surface));
                    Program.Assert(PixelDifference(nearLeft, nearRight) > 100,
                        "real pointer movement changes the live highlight without moving or distorting content");
                    var light = (RadialGradientBrush)typeof(SkinBorder).GetField("_lensLight", Program.Private)!.GetValue(surface)!;
                    Program.Assert(light.MappingMode == BrushMappingMode.Absolute && light.RadiusX == light.RadiusY &&
                        light.RadiusX is >= 64 and <= 160, "long capsule keeps a bounded circular light in DIPs");
                    SetCursorPos(780, 550); Wait(120);
                    Program.Assert((light.Center - new Point(surface.ActualWidth * .24, surface.ActualHeight * .05)).Length < .1,
                        "pointer leave restores ambient light instead of leaving a stuck hotspot");
                    Program.Assert(window.Opacity == 1 && marker.Opacity == 1 && ReferenceEquals(surface.Child, marker),
                        "highlight interaction does not change foreground opacity or content ownership");
                }
                finally { SetCursorPos(oldPointer.X, oldPointer.Y); }
            }
            using (surface.FreezeRefractionForEvidence())
            {
                Wait(100);
                using (NativeSurfaceChecks.Capture(window, Environment.GetEnvironmentVariable("PAPER_SKIN_CAPTURE"), "shared-layered-desktop")) { }
            }
            var full = Snapshot(surface);
            controller.State.MatchAuxiliaryMaterialStrength = false; surface.RefreshSkin(); Wait(120);
            Program.Assert(surface.HasRefractionWorker && surface.MaterialStrength == .4,
                "quiet capsule keeps its worker, scene and all optical stages, not a static fallback");
            var quiet = Snapshot(surface);
            Program.Assert(PixelDifference(full, quiet) > 500, "full/quiet capsule processing visibly differs");
            Program.Assert(new WindowInteropHelper(window).Handle == hwnd && ReferenceEquals(surface.Child, marker) &&
                window.Opacity == 1 && marker.Opacity == 1 && VisualTreeHelper.HitTest(surface, new Point(180,50)) != null,
                "material strength keeps the real foreground, HWND and hit target");
            window.Opacity = .8; Wait(60);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0,
                "externally requested partial opacity releases capture");
            var count = surface.RefractionFrameCount; window.Opacity = 1;
            Ready(surface, count, "opacity restoration resumes without recreating the capsule");
            window.Hide(); Wait(60);
            Program.Assert(!surface.HasRefractionWorker && !surface.HasLensLightSubscription && DesktopLensCapture.ReadAffinity(hwnd) == 0,
                "hidden capsule releases capture and lighting");
            count = surface.RefractionFrameCount; window.Show(); Ready(surface, count, "reshown capsule resumes");

            // Open the production root and submenu templates: each popup must sample its
            // own HWND, never the owning capsule and never a disconnected test control.
            var template = (ControlTemplate)typeof(PaperWindow).GetMethod("BuildContextMenuTemplate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
            var style = (Style)typeof(PaperWindow).GetMethod("BuildCompactMenuItemStyle", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
            menu = new MaterialContextMenu { Template = template, ItemContainerStyle = style, Background = Theme.PaperBrush,
                Foreground = Theme.TextBrush, BorderBrush = Theme.PaperBorderBrush, Padding = new Thickness(8),
                PlacementTarget = surface, Placement = PlacementMode.Right, MinWidth = 180 };
            var parent = new MenuItem { Header = "Optical submenu", Style = style };
            parent.Items.Add(new MenuItem { Header = "Same live background", Style = style });
            menu.Items.Add(parent); menu.Items.Add(new MenuItem { Header = "Still clickable", Style = style });
            menu.IsOpen = true; Wait(100);
            var menuSurface = Find<SkinBorder>(menu)!;
            Program.Assert(menuSurface is { IsMenu: true }, "opened production menu instantiates the material root");
            Ready(menuSurface!, 0, "actual context menu receives live background");
            Program.Assert(menuSurface!.FirstMenuRenderUsedBackground,
                "root menu's first paint already contains its prepared scene");
            var menuHwnd = ((HwndSource)PresentationSource.FromVisual(menuSurface!)!).Handle;
            Program.Assert(menuHwnd != hwnd && DesktopLensCapture.ReadAffinity(menuHwnd) == 0x11,
                "context menu excludes its own popup, not the owner");
            foreach (var fullStrength in new[] { false, true })
            {
                controller.State.MatchAuxiliaryMaterialStrength = fullStrength; SkinBorder.RefreshLoadedSurfaces(); Wait(100);
                Program.Assert(menuSurface!.HasRefractionWorker && surface.HasRefractionWorker &&
                    menuSurface.MaterialStrength == (fullStrength ? 1 : .4), "both menu strength settings keep actual optics");
                Save(Render(menuSurface), $"shared-menu-{fullStrength}");
            }
            parent.IsSubmenuOpen = true; Wait(100);
            var popup = (Popup)parent.Template.FindName("PART_Popup", parent)!;
            var submenuSurface = Find<SkinBorder>(popup.Child)!;
            Program.Assert(submenuSurface is { IsMenu: true }, "production submenu carries material role");
            Ready(submenuSurface!, 0, "actual submenu has its own sampled background");
            Program.Assert(submenuSurface!.FirstMenuRenderUsedBackground,
                "submenu's first paint already contains its prepared scene");
            var subHwnd = ((HwndSource)PresentationSource.FromVisual(submenuSurface!)!).Handle;
            Program.Assert(subHwnd != menuHwnd && subHwnd != hwnd && DesktopLensCapture.ReadAffinity(subHwnd) == 0x11,
                "submenu owns a distinct background lease");
            Save(Render(submenuSurface!), "shared-submenu-live");
            using (surface.FreezeRefractionForEvidence())
            using (menuSurface!.FreezeRefractionForEvidence())
            using (submenuSurface!.FreezeRefractionForEvidence())
            {
                Wait(100);
                using (NativeSurfaceChecks.Capture(rear, Environment.GetEnvironmentVariable("PAPER_SKIN_CAPTURE"), "shared-popups-desktop")) { }
            }
            parent.IsSubmenuOpen = false; menu.IsOpen = false; Wait(100);
            Program.Assert(!menuSurface!.HasRefractionWorker && !submenuSurface!.HasRefractionWorker && surface.HasRefractionWorker,
                "closing popup/submenu releases only their workers, not the owner");

            foreach (var skin in new[] { PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic, PaperSkins.TracingPaper })
            {
                var worker = typeof(SkinBorder).GetField("_capture", Program.Private)!.GetValue(surface);
                controller.State.PaperSkin = skin; Theme.Invalidate(); surface.RefreshSkin();
                Program.Assert(surface.IsRefractionActive && ReferenceEquals(worker,
                    typeof(SkinBorder).GetField("_capture", Program.Private)!.GetValue(surface)),
                    $"{skin} changes its recipe without discarding the actual scene or capture worker");
                controller.State.MatchAuxiliaryMaterialStrength = true; surface.RefreshSkin(); Wait(80); full = Snapshot(surface);
                controller.State.MatchAuxiliaryMaterialStrength = false; surface.RefreshSkin(); Wait(80); quiet = Snapshot(surface);
                Program.Assert(surface.HasRefractionWorker && PixelDifference(full, quiet) > 500,
                    $"{skin} weak processing retains diffusion and transmission");
                Save(Render(surface), $"shared-{skin}-quiet");
            }
            controller.State.PaperSkin = PaperSkins.Aero; Theme.Invalidate(); surface.RefreshSkin(); Wait(80);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0,
                "Aero uses layered transmission without desktop exclusion");
            Save(Render(surface), "shared-aero-alpha");
            controller.State.PaperSkin = PaperSkins.LiquidGlass; Theme.Invalidate(); surface.RefreshSkin();
            Ready(surface, surface.RefractionFrameCount, "liquid returns after other materials");
            controller.State.LiquidGlassRefraction = false; SkinBorder.RefreshLoadedSurfaces(); Wait(60);
            Program.Assert(!surface.HasRefractionWorker && DesktopLensCapture.ReadAffinity(hwnd) == 0,
                "global background switch restores screenshot visibility on auxiliary surfaces too");
        }
        finally
        {
            if (menu != null) menu.IsOpen = false;
            window?.Close(); rear.Close();
            (controller.State.PaperSkin, controller.State.Theme, controller.State.ColorScheme,
                controller.State.EnableAnimations, controller.State.LiquidGlassRefraction, controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckOpticalPixels(SkinBorder surface, FrameworkElement marker, string name)
    {
        using var frozen = surface.FreezeRefractionForEvidence(); Wait(80);
        var foreground = Render(marker);
        surface.SetDispersionForEvidence(0); Wait(60); var achromatic = Render(surface);
        surface.SetDispersionForEvidence(1); Wait(60); var chromatic = Render(surface);
        var a = Pixels(achromatic); var b = Pixels(chromatic); var colored = 0; var centerChanged = 0;
        for (var y = 2; y < chromatic.PixelHeight - 2; y++) for (var x = 2; x < chromatic.PixelWidth - 2; x++)
        {
            var i = (y * chromatic.PixelWidth + x) * 4;
            var delta = Math.Abs(a[i]-b[i])+Math.Abs(a[i+1]-b[i+1])+Math.Abs(a[i+2]-b[i+2]);
            if (delta > 8) colored++;
            if (x > 35 && x < chromatic.PixelWidth-35 && y > 35 && y < chromatic.PixelHeight-35 && delta > 2) centerChanged++;
        }
        Program.Assert(colored > 80 && centerChanged == 0,
            $"real RGB dispersion changes the curved edge, not the body or a painted rainbow: {colored}/{centerChanged}");
        Console.WriteLine($"RGB LENS: {colored} shoulder pixels changed; {centerChanged} body pixels changed.");
        surface.SetRefractionStrengthForEvidence(0); Wait(60); var flat = Render(surface);
        surface.SetRefractionStrengthForEvidence(1); Wait(60); var curved = Render(surface);
        Program.Assert(PixelDifference(Pixels(flat), Pixels(curved)) > 300, "capsule edge truly bends the sampled scene");
        var finish = (DrawingVisual)typeof(SkinBorder).GetField("_opticalFinish", Program.Private)!.GetValue(surface)!;
        finish.Opacity = 0; Wait(60); var uncoated = Render(surface);
        finish.Opacity = 1; Wait(60);
        Program.Assert(PixelDifference(Pixels(uncoated), Pixels(Render(surface))) > 300,
            "live surface really draws its highlights above the captured scene");
        Program.Assert(Pixels(foreground).SequenceEqual(Pixels(Render(marker))), "refraction never touches actual foreground pixels");
        Save(achromatic, $"shared-{name}-no-dispersion"); Save(chromatic, $"shared-{name}-dispersion");
        Save(flat, $"shared-{name}-flat"); Save(curved, $"shared-{name}-curved");
    }
    private static T? Find<T>(DependencyObject? node) where T : DependencyObject
    {
        if (node is T match) return match;
        if (node == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find<T>(VisualTreeHelper.GetChild(node, i)) is { } result) return result;
        return null;
    }
    private static Brush Pattern()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0,0,24,24));
            dc.DrawRectangle(Brushes.Black, null, new Rect(0,0,12,12));
            dc.DrawRectangle(Brushes.Black, null, new Rect(12,12,12,12));
        }
        drawing.Freeze();
        return new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0,0,24,24) };
    }
    private static byte[] Snapshot(SkinBorder surface)
    { using var frozen = surface.FreezeRefractionForEvidence(); Wait(60); return Pixels(Render(surface)); }
    private static int PixelDifference(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i]-b[i])+Math.Abs(a[i+1]-b[i+1])+Math.Abs(a[i+2]-b[i+2]) > 8) count++;
        return count;
    }
    private static RenderTargetBitmap Render(FrameworkElement surface)
    {
        surface.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
            (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface); return bitmap;
    }
    private static byte[] Pixels(BitmapSource bitmap)
    { var data = new byte[bitmap.PixelWidth*bitmap.PixelHeight*4]; bitmap.CopyPixels(data, bitmap.PixelWidth*4, 0); return data; }
    private static void Save(BitmapSource bitmap, string name)
    {
        var output = Environment.GetEnvironmentVariable("PAPER_SKIN_CAPTURE"); if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name+".png")); encoder.Save(file);
    }
    private static void Ready(SkinBorder surface, int previous, string reason)
    {
        var clock = Stopwatch.StartNew();
        while (surface.RefractionFrameCount <= previous && surface.RefractionFailure == null && clock.ElapsedMilliseconds < 6000) Wait(30);
        Program.Assert(surface.HasRefractionWorker && surface.RefractionFrameCount > previous, $"{reason}: {surface.RefractionFailure ?? "timeout"}");
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_,_) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
