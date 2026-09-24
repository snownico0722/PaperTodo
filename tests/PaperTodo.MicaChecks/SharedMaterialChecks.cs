using System.Diagnostics;
using System.IO;
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
        var saved = (
            controller.State.PaperSkin,
            controller.State.Theme,
            controller.State.ColorScheme,
            controller.State.EnableAnimations,
            controller.State.MatchAuxiliaryMaterialStrength);

        var rear = new Window
        {
            Left = 20,
            Top = 20,
            Width = 800,
            Height = 600,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Background = Pattern()
        };
        Window? window = null;
        ContextMenu? menu = null;

        try
        {
            controller.State.PaperSkin = PaperSkins.Acrylic;
            controller.State.Theme = "light";
            controller.State.ColorScheme = ColorSchemes.Neutral;
            controller.State.EnableAnimations = true;
            controller.State.MatchAuxiliaryMaterialStrength = true;
            Theme.Invalidate();

            var marker = new Border
            {
                Width = 12,
                Height = 12,
                Background = Brushes.Lime,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var surface = new SkinBorder
            {
                IsCapsule = true,
                CornerRadius = new CornerRadius(28),
                Background = Theme.PaperBrush,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Child = marker
            };
            window = new Window
            {
                Left = 100,
                Top = 120,
                Width = 360,
                Height = 100,
                AllowsTransparency = true,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                Content = surface
            };

            rear.Show();
            window.Show();
            Ready(surface, 0, "layered Acrylic capsule takes one local snapshot");
            var hwnd = new WindowInteropHelper(window).Handle;
            Program.Assert(
                !surface.HasBackgroundCapture &&
                DesktopBackgroundCapture.ReadAffinity(hwnd) == 0 &&
                !surface.HasAeroReflectionSubscription,
                "completed static snapshot retains no capture lease or pointer-light behavior");

            var firstBitmap = surface.BackgroundSessionState!.Bitmap;
            var firstFrames = surface.BackgroundFrameCount;
            var beforeRearChange = Snapshot(surface);
            rear.Background = Brushes.OrangeRed;
            rear.UpdateLayout();
            Wait(180);
            var afterRearChange = Snapshot(surface);
            Program.Assert(
                ReferenceEquals(firstBitmap, surface.BackgroundSessionState!.Bitmap) &&
                surface.BackgroundFrameCount == firstFrames &&
                PixelDifference(beforeRearChange, afterRearChange) == 0,
                "stationary capsule freezes its background even when the desktop behind it changes");

            var full = beforeRearChange;
            controller.State.MatchAuxiliaryMaterialStrength = false;
            surface.RefreshSkin();
            Wait(60);
            var quiet = Snapshot(surface);
            Program.Assert(
                !surface.IsBackgroundActive &&
                !surface.HasBackgroundCapture &&
                surface.MaterialStrength == .4 &&
                PixelDifference(full, quiet) > 500,
                "full-material OFF removes sampled transmission and keeps an opaque quiet surface");
            CheckPaperDistance(full, quiet, surface, "Acrylic capsule");

            surface.UseLightweightMaterial = true;
            surface.UpdateLayout();
            Render(surface);
            var previewFrames = surface.BackgroundFrameCount;
            for (var size = 0; size < 4; size++)
            {
                window.Width += 10;
                window.UpdateLayout();
                Render(surface);
                Wait(20);
                Program.Assert(
                    !surface.HasBackgroundCapture &&
                    !surface.HasAeroReflectionSubscription &&
                    surface.BackgroundFrameCount == previewFrames &&
                    !surface.HasMaterialRelief,
                    "lightweight preview resizing owns no capture, parallax or relief");
            }

            window.Width = 360;
            window.UpdateLayout();
            surface.UseLightweightMaterial = false;
            Wait(60);
            Program.Assert(!surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "leaving preview while full material is off stays opaque and capture-free");
            controller.State.MatchAuxiliaryMaterialStrength = true;
            surface.RefreshSkin();
            Ready(surface, previewFrames, "enabling full material takes one fresh local snapshot");
            Program.Assert(
                new WindowInteropHelper(window).Handle == hwnd &&
                ReferenceEquals(surface.Child, marker) &&
                window.Opacity == 1 &&
                marker.Opacity == 1 &&
                VisualTreeHelper.HitTest(surface, new Point(180, 50)) != null,
                "static material keeps the real foreground, HWND and hit target");

            window.Opacity = .8;
            Wait(80);
            Program.Assert(
                !surface.IsBackgroundActive &&
                !surface.HasBackgroundCapture &&
                DesktopBackgroundCapture.ReadAffinity(hwnd) == 0,
                "partial opacity releases static sampled background state");

            var count = surface.BackgroundFrameCount;
            window.Opacity = 1;
            Ready(surface, count, "opacity restoration takes one new snapshot");
            window.Hide();
            Wait(80);
            Program.Assert(!surface.IsBackgroundActive && !surface.HasBackgroundCapture,
                "hidden capsule releases its snapshot");
            count = surface.BackgroundFrameCount;
            window.Show();
            Ready(surface, count, "reshown capsule takes one new snapshot");

            var template = (ControlTemplate)typeof(PaperWindow).GetMethod(
                "BuildContextMenuTemplate",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, null)!;
            var style = (Style)typeof(PaperWindow).GetMethod(
                "BuildCompactMenuItemStyle",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, null)!;

            menu = new MaterialContextMenu
            {
                Template = template,
                ItemContainerStyle = style,
                Background = Theme.PaperBrush,
                Foreground = Theme.TextBrush,
                BorderBrush = Theme.PaperBorderBrush,
                Padding = new Thickness(8),
                PlacementTarget = surface,
                Placement = PlacementMode.Right,
                MinWidth = 180
            };
            var parent = new MenuItem { Header = "Material submenu", Style = style };
            parent.Items.Add(new MenuItem { Header = "Static background", Style = style });
            menu.Items.Add(parent);
            menu.Items.Add(new MenuItem { Header = "Still clickable", Style = style });

            menu.IsOpen = true;
            Until(() => menu.IsOpen, "root menu opens");
            Wait(80);
            var menuSurface = Find<SkinBorder>(menu)!;
            Program.Assert(
                menuSurface is { IsMenu: true } &&
                menuSurface.FirstMenuRenderUsedBackground &&
                menuSurface.IsBackgroundActive &&
                !menuSurface.HasBackgroundCapture,
                "menu first paint uses one prepared immutable snapshot and never starts live capture");
            var menuBitmap = menuSurface.BackgroundSessionState!.Bitmap;
            var menuFrames = menuSurface.BackgroundFrameCount;
            var menuHwnd = ((HwndSource)PresentationSource.FromVisual(menuSurface)!).Handle;
            Program.Assert(
                menuHwnd != hwnd && DesktopBackgroundCapture.ReadAffinity(menuHwnd) == 0,
                "menu capture lease is already released after pre-open snapshot");

            rear.Background = Brushes.CadetBlue;
            Wait(120);
            Program.Assert(
                ReferenceEquals(menuBitmap, menuSurface.BackgroundSessionState!.Bitmap) &&
                menuSurface.BackgroundFrameCount == menuFrames,
                "open menu keeps the same background snapshot while desktop content changes");

            controller.State.MatchAuxiliaryMaterialStrength = false;
            SkinBorder.RefreshLoadedSurfaces();
            Wait(60);
            Program.Assert(
                !menuSurface.IsBackgroundActive &&
                !menuSurface.HasBackgroundCapture &&
                menuSurface.MaterialStrength == .4,
                "full-material OFF makes the open menu opaque and releases its snapshot");
            Save(Render(menuSurface), "static-menu-false");

            menu.IsOpen = false;
            Wait(80);
            controller.State.MatchAuxiliaryMaterialStrength = true;
            menu.IsOpen = true;
            Until(() => menu.IsOpen, "root menu reopens with full material");
            Wait(80);
            menuSurface = Find<SkinBorder>(menu)!;
            Program.Assert(menuSurface.FirstMenuRenderUsedBackground && menuSurface.IsBackgroundActive,
                "full material restores the prepared menu snapshot path");
            menuHwnd = ((HwndSource)PresentationSource.FromVisual(menuSurface)!).Handle;

            parent.IsSubmenuOpen = true;
            Until(() => parent.IsSubmenuOpen, "submenu opens");
            Wait(80);
            var popup = (Popup)parent.Template.FindName("PART_Popup", parent)!;
            var submenuSurface = Find<SkinBorder>(popup.Child)!;
            Program.Assert(
                submenuSurface is { IsMenu: true } &&
                submenuSurface.FirstMenuRenderUsedBackground &&
                submenuSurface.IsBackgroundActive &&
                !submenuSurface.HasBackgroundCapture,
                "submenu also uses one pre-open static snapshot");
            var subHwnd = ((HwndSource)PresentationSource.FromVisual(submenuSurface)!).Handle;
            Program.Assert(
                subHwnd != menuHwnd && subHwnd != hwnd &&
                DesktopBackgroundCapture.ReadAffinity(subHwnd) == 0,
                "submenu releases its one-shot capture lease after opening");

            parent.IsSubmenuOpen = false;
            menu.IsOpen = false;
            Wait(100);
            Program.Assert(
                !menuSurface.HasBackgroundCapture &&
                !submenuSurface.HasBackgroundCapture &&
                !surface.HasBackgroundCapture,
                "closing popups leaves no capture task behind");

            foreach (var skin in new[]
            {
                PaperSkins.Mica,
                PaperSkins.Acrylic,
                PaperSkins.ClearAcrylic,
                PaperSkins.TracingPaper
            })
            {
                controller.State.MatchAuxiliaryMaterialStrength = true;
                controller.State.PaperSkin = skin;
                Theme.Invalidate();
                var framesBefore = surface.BackgroundFrameCount;
                surface.RefreshSkin();
                if (!surface.IsBackgroundActive)
                    Ready(surface, framesBefore, skin + " takes a static snapshot");

                full = Snapshot(surface);
                controller.State.MatchAuxiliaryMaterialStrength = false;
                surface.RefreshSkin();
                Wait(40);
                quiet = Snapshot(surface);
                Program.Assert(
                    !surface.IsBackgroundActive &&
                    !surface.HasBackgroundCapture &&
                    PixelDifference(full, quiet) > 500,
                    $"{skin}: full-material OFF drops sampled transmission");
                CheckPaperDistance(full, quiet, surface, skin);
            }

            controller.State.MatchAuxiliaryMaterialStrength = true;
            controller.State.PaperSkin = PaperSkins.Aero;
            Theme.Invalidate();
            surface.RefreshSkin();
            Wait(80);
            Program.Assert(
                !surface.IsBackgroundActive &&
                !surface.HasBackgroundCapture &&
                DesktopBackgroundCapture.ReadAffinity(hwnd) == 0,
                "Aero uses direct layered transmission without desktop sampling");

            controller.State.PaperSkin = PaperSkins.Acrylic;
            Theme.Invalidate();
            surface.RefreshSkin();
            Ready(surface, surface.BackgroundFrameCount, "Acrylic static background returns after Aero");
        }
        finally
        {
            if (menu != null) menu.IsOpen = false;
            window?.Close();
            rear.Close();
            (
                controller.State.PaperSkin,
                controller.State.Theme,
                controller.State.ColorScheme,
                controller.State.EnableAnimations,
                controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
    }

    private static void CheckPaperDistance(byte[] full, byte[] quiet, SkinBorder surface, string name)
    {
        var image = Render(surface);
        var paper = ((SolidColorBrush)Theme.PaperBrush).Color;
        double fullDistance = 0, quietDistance = 0;
        for (var y = 32; y < image.PixelHeight - 32; y++)
        for (var x = 35; x < image.PixelWidth / 3; x++)
        {
            var i = (y * image.PixelWidth + x) * 4;
            fullDistance += Math.Abs(full[i] - paper.B) +
                Math.Abs(full[i + 1] - paper.G) +
                Math.Abs(full[i + 2] - paper.R);
            quietDistance += Math.Abs(quiet[i] - paper.B) +
                Math.Abs(quiet[i + 1] - paper.G) +
                Math.Abs(quiet[i + 2] - paper.R);
        }
        Program.Assert(fullDistance > 0 && quietDistance < fullDistance * .55,
            $"{name}: weak material stays closer to opaque paper ({quietDistance / fullDistance:F3})");
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
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 24, 24));
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 12, 12));
            dc.DrawRectangle(Brushes.Black, null, new Rect(12, 12, 12, 12));
        }
        drawing.Freeze();
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 24, 24)
        };
    }

    private static byte[] Snapshot(SkinBorder surface)
    {
        using var frozen = surface.FreezeBackgroundForEvidence();
        Wait(30);
        return Pixels(Render(surface));
    }

    private static int PixelDifference(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) +
                Math.Abs(a[i + 1] - b[i + 1]) +
                Math.Abs(a[i + 2] - b[i + 2]) > 8)
            {
                count++;
            }
        return count;
    }

    private static RenderTargetBitmap Render(FrameworkElement surface)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(surface.ActualWidth),
            (int)Math.Ceiling(surface.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(surface);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
        return data;
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        var output = Environment.GetEnvironmentVariable("PAPER_SKIN_CAPTURE");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(file);
    }

    private static void Ready(SkinBorder surface, int previous, string reason)
    {
        var clock = Stopwatch.StartNew();
        while ((surface.BackgroundFrameCount <= previous || surface.HasBackgroundCapture) &&
               surface.BackgroundFailure == null &&
               clock.ElapsedMilliseconds < 6000)
        {
            Wait(20);
        }
        Program.Assert(
            surface.BackgroundFrameCount > previous &&
            !surface.HasBackgroundCapture,
            $"{reason}: {surface.BackgroundFailure ?? "timeout"}");
    }

    private static void Until(Func<bool> predicate, string reason)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate() && clock.ElapsedMilliseconds < 5000) Wait(20);
        Program.Assert(predicate(), reason);
    }

    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
