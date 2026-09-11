using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static class MaterialPresentationChecks
{
    internal static void Run(AppController controller)
    {
        var desktop = new Int32Rect(-8192, -2160, 16384, 8640);
        var ordinary = LensCaptureLayout.Create(new Int32Rect(100, 100, 560, 440),
            new DesktopLensCapture.Region(0, 0, 560, 440, 64), desktop)!;
        Program.Assert(ordinary.PixelWidth == ordinary.Bounds.Width && ordinary.PixelHeight == ordinary.Bounds.Height,
            "ordinary glass keeps physical 1:1 detail rather than spending most of its resolution on overscan");
        foreach (var size in new[] { new Size(2400, 1200), new Size(2048, 2048), new Size(3968, 896) })
        {
            var region = new DesktopLensCapture.Region(0, 0, (int)size.Width, (int)size.Height, 64);
            var a = LensCaptureLayout.Create(new Int32Rect(-3500, 100, region.Width, region.Height), region, desktop)!;
            for (var offset = 1; offset < 100; offset++)
            {
                var b = LensCaptureLayout.Create(new Int32Rect(-3500 + offset, 100 + offset, region.Width, region.Height), region, desktop)!;
                var step = a.Bounds.Width / a.PixelWidth;
                Program.Assert(b.Bounds.Width / b.PixelWidth == step && (b.Bounds.X - a.Bounds.X) % step == 0 &&
                    (b.Bounds.Y - a.Bounds.Y) % step == 0 && (long)b.PixelWidth * b.PixelHeight <= LensCaptureLayout.PixelBudget,
                    "dragging retains downsample density and world-space phase, including budget boundaries");
            }
        }
        Program.Assert(DesktopLensCapture.CaptureInterval(false, 0) == 100 && DesktopLensCapture.CaptureInterval(true, 20) == 100 &&
            DesktopLensCapture.CaptureInterval(false, 20) >= 100, "readback stays at 100ms even while motion reprojects each frame");
        var overscan = LensCaptureLayout.Create(new Int32Rect(400, 400, 360, 300),
            new DesktopLensCapture.Region(0, 0, 360, 300, 256), desktop)!;
        Program.Assert(overscan.Bounds.X <= 400-200 && overscan.Bounds.Y <= 400-200 &&
            overscan.Bounds.X+overscan.Bounds.Width >= 400+360+200 &&
            (long)overscan.PixelWidth*overscan.PixelHeight <= LensCaptureLayout.PixelBudget,
            "bounded wider scene covers motion between low-rate samples");

        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations, controller.State.LiquidGlassRefraction);
        Window? settings = null;
        var owner = new Window { Width = 200, Height = 100, Left = 40, Top = 40, ShowInTaskbar = false, Content = new Border() };
        ContextMenu? menu = null;
        try
        {
            controller.State.PaperSkin = PaperSkins.LiquidGlass; controller.State.Theme = "light";
            controller.State.EnableAnimations = false; controller.State.LiquidGlassRefraction = true; Theme.Invalidate();
            owner.Show();
            menu = controller.CreateTrayMenu(); menu.Items.Add(new MenuItem { Header = "Cancel before background is ready" });
            menu.PlacementTarget = (UIElement)owner.Content; menu.Placement = PlacementMode.Bottom;
            var opens = 0; menu.Opened += (_, _) => opens++;
            menu.SetCurrentValue(ContextMenu.IsOpenProperty, true); menu.IsOpen = false; Wait(400);
            Program.Assert(!menu.IsOpen && opens == 0, "cancelling an async menu open never reopens it later");
            menu.SetCurrentValue(ContextMenu.IsOpenProperty, true); Until(() => menu.IsOpen && opens == 1, "reopen after cancellation"); Wait(80);
            var menuSurface = Find(menu);
            Program.Assert(menuSurface != null && menuSurface.FirstMenuRenderUsedBackground && menu.Opacity == 1,
                "production tray menu opens with the real material, without a foreground fade");
            menu.IsOpen = false; Wait(60);

            CheckRealRightClicks(controller);
            CheckMasterRightClicks(controller);
            controller.State.PaperSkin = PaperSkins.LiquidGlass; controller.State.Theme = "light";
            controller.State.EnableAnimations = false; Theme.Invalidate();

            var pageType = typeof(AppController).GetNestedType("SettingsPage", BindingFlags.NonPublic)!;
            var show = typeof(AppController).GetMethod("ShowSettingsWindow", Program.Private, null, [pageType], null)!;
            show.Invoke(controller, [Enum.Parse(pageType, "General")]);
            settings = (Window)typeof(AppController).GetField("_settingsWindow", Program.Private)!.GetValue(controller)!;
            var shell = (SkinBorder)settings.Content;
            Until(() => shell.IsRefractionActive, "settings background");
            var worker = typeof(SkinBorder).GetField("_capture", Program.Private)!.GetValue(shell);
            var oldContent = shell.Child;
            show.Invoke(controller, [Enum.Parse(pageType, "Visual")]);
            Program.Assert(ReferenceEquals(shell, settings.Content) && !ReferenceEquals(oldContent, shell.Child) &&
                ReferenceEquals(worker, typeof(SkinBorder).GetField("_capture", Program.Private)!.GetValue(shell)),
                "page switch replaces only content and preserves the existing scene and capture worker");
            var scene = (ContainerVisual)typeof(SkinBorder).GetField("_refractionVisual", Program.Private)!.GetValue(shell)!;
            settings.Width += 12; settings.UpdateLayout(); shell.RefreshRefraction();
            Program.Assert(ReferenceEquals(scene, typeof(SkinBorder).GetField("_refractionVisual", Program.Private)!.GetValue(shell)) &&
                scene.Opacity == 1 && settings.Opacity == 1, "resize never hides the last usable background while a new sample is pending");
        }
        finally
        {
            if (menu != null) menu.IsOpen = false;
            settings?.Close(); owner.Close();
            (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations, controller.State.LiquidGlassRefraction) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckRealRightClicks(AppController controller)
    {
        var paper = new PaperData { Type = PaperTypes.Todo, Title = "Right-click regression",
            X = 70, Y = 70, Width = 360, Height = 300 };
        controller.State.Papers.Add(paper);
        var window = new PaperWindow(paper, controller) { Topmost = true };
        GetCursorPos(out var cursor);
        try
        {
            window.Show(); window.Activate(); Wait(150);
            foreach (var collapsed in new[] { false, true })
            {
                window.SetCollapsedState(collapsed, animate: false, saveGeometry: false); Wait(100);
                var target = (FrameworkElement)typeof(PaperWindow).GetField(
                    collapsed ? "_capsuleLeftArea" : "_paperChrome", Program.Private)!.GetValue(window)!;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var menu = target.ContextMenu;
                    Program.Assert(menu != null, "real paper/capsule has its production menu");
                    var point = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                    SetCursorPos((int)point.X, (int)point.Y); Wait(30);
                    // Real input reaches WPF ContextMenuService rather than assigning IsOpen.
                    mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
                    Until(() => menu!.IsOpen, $"actual right click opens {(collapsed ? "capsule" : "paper")} menu");
                    Wait(80);
                    Program.Assert(Find(menu!)?.FirstMenuRenderUsedBackground == true,
                        "service-opened menu retains its prepared first-frame material");
                    menu!.SetCurrentValue(ContextMenu.IsOpenProperty, false); Wait(80);
                    Program.Assert(!menu.IsOpen, "real right-click menu closes and can reopen");
                }
            }
        }
        finally
        {
            window.CloseForReal(); controller.State.Papers.Remove(paper);
            SetCursorPos(cursor.X, cursor.Y);
        }
    }
    private static void CheckMasterRightClicks(AppController controller)
    {
        var original = (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations,
            controller.State.MatchAuxiliaryMaterialStrength);
        controller.State.PaperSkin = PaperSkins.Paper; Theme.Invalidate();
        var master = new MasterCapsuleWindow(controller, EdgeCapsuleEdge.Left, "");
        GetCursorPos(out var cursor);
        ContextMenu? menu = null;
        try
        {
            master.ShowPlaced(1, false, false); Wait(120);
            var pill = (FrameworkElement)typeof(MasterCapsuleWindow).GetField("_pill", Program.Private)!.GetValue(master)!;
            menu = pill.ContextMenu!;
            // The very same detached template survives Paper -> Mica -> Acrylic.
            menu.ApplyTemplate();
            foreach (var theme in new[] { "light", "dark" })
            foreach (var skin in new[] { PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic })
            {
                controller.State.PaperSkin = skin; controller.State.Theme = theme;
                controller.State.EnableAnimations = true;
                Theme.Invalidate(); master.UpdateTheme(); Wait(60);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    controller.State.MatchAuxiliaryMaterialStrength = attempt == 1;
                    var point = pill.PointToScreen(new Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
                    SetCursorPos((int)point.X, (int)point.Y); Wait(25);
                    mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
                    Until(() => menu.IsOpen, "actual MASTER right click opens " + skin);
                    Wait(180);
                    var surface = Find(menu)!;
                    var opacityOwners = new List<string>();
                    for (DependencyObject? node = surface; node is Visual; node = VisualTreeHelper.GetParent(node))
                        if (node is UIElement ui) opacityOwners.Add($"{ui.GetType().Name}:{ui.Opacity:F3}/{ui.IsVisible}");
                    Console.WriteLine($"MASTER {skin}/{theme}/{attempt}: recipe={surface.Skin}; first={surface.FirstMenuRenderUsedBackground}; fallback={surface.MenuFallbackRenderCount}; worker={surface.HasRefractionWorker}; frames={surface.RefractionFrameCount}; suppress={surface.SuppressLiveBackgroundForOpening}; failure={surface.RefractionFailure}; opacity={string.Join(',', opacityOwners)}");
                    Program.Assert(surface.Skin == skin && surface.FirstMenuRenderUsedBackground && surface.MenuFallbackRenderCount == 0,
                        $"master {skin}/{theme}: prepared current recipe from first render, no later plain-paper frame");
                    Program.Assert(menu.Opacity == 1, "master menu does not hide material flicker with a foreground fade");
                    menu.IsOpen = false; Wait(80);
                }
            }
        }
        finally
        {
            if (menu != null) menu.IsOpen = false;
            master.CloseForReal(); SetCursorPos(cursor.X, cursor.Y);
            (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations,
                controller.State.MatchAuxiliaryMaterialStrength) = original;
            Theme.Invalidate();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private static SkinBorder? Find(DependencyObject node)
    {
        if (node is SkinBorder surface) return surface;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find(VisualTreeHelper.GetChild(node, i)) is { } result) return result;
        return null;
    }
    private static void Until(Func<bool> predicate, string reason)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate() && clock.ElapsedMilliseconds < 6000) Wait(20);
        Program.Assert(predicate(), reason);
    }
    private static void Wait(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
