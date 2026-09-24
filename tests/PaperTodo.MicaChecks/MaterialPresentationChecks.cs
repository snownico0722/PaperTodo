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
        CheckDefaultPaperLightweightShadow(controller);

        var desktop = new Int32Rect(-8192, -2160, 16384, 8640);
        var ordinary = BackgroundCaptureLayout.Create(
            new Int32Rect(100, 100, 560, 440),
            new DesktopBackgroundCapture.Region(0, 0, 560, 440, 64),
            desktop)!;
        Program.Assert(
            ordinary.PixelWidth == ordinary.Bounds.Width &&
            ordinary.PixelHeight == ordinary.Bounds.Height,
            "ordinary one-shot snapshots keep physical 1:1 detail");

        foreach (var size in new[] { new Size(2400, 1200), new Size(2048, 2048), new Size(3968, 896) })
        {
            var region = new DesktopBackgroundCapture.Region(0, 0, (int)size.Width, (int)size.Height, 96);
            var layout = BackgroundCaptureLayout.Create(
                new Int32Rect(-3500, 100, region.Width, region.Height),
                region,
                desktop)!;
            Program.Assert(
                (long)layout.PixelWidth * layout.PixelHeight <= BackgroundCaptureLayout.PixelBudget &&
                layout.PixelWidth <= 2048 &&
                layout.PixelHeight <= 2048,
                "large static snapshots downsample only to the bounded pixel budget");
        }

        var saved = (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations,
            controller.State.MatchAuxiliaryMaterialStrength);
        Window? settings = null;
        var owner = new Window { Width = 200, Height = 100, Left = 40, Top = 40, ShowInTaskbar = false, Content = new Border() };
        ContextMenu? menu = null, aeroMenu = null;
        try
        {
            controller.State.PaperSkin = PaperSkins.Acrylic; controller.State.Theme = "light";
            controller.State.EnableAnimations = false;
            controller.State.MatchAuxiliaryMaterialStrength = true;
            Theme.Invalidate();
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

            controller.State.PaperSkin = PaperSkins.Aero; Theme.Invalidate();
            Program.Assert(!MaterialMenuOpening.NeedsBackground && MaterialMenuOpening.SuppressPopupAnimation,
                "Aero popup skips screen capture but suppresses the solid-to-transparent Fade");
            aeroMenu = controller.CreateTrayMenu();
            aeroMenu.PlacementTarget = (UIElement)owner.Content; aeroMenu.Placement = PlacementMode.Bottom;
            aeroMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);
            Until(() => aeroMenu.IsOpen, "Aero menu opens immediately without capture preparation"); Wait(80);
            AssertNoPopupFade(aeroMenu);
            var aeroSurface = Find(aeroMenu);
            Program.Assert(aeroSurface is { Skin: PaperSkins.Aero } && !aeroSurface.HasBackgroundCapture &&
                aeroSurface.MenuFallbackRenderCount == 0,
                "Aero menu uses direct transparent transmission and never starts a background sampler");
            aeroMenu.IsOpen = false; Wait(60);
            controller.State.PaperSkin = PaperSkins.Acrylic; Theme.Invalidate();

            CheckRealRightClicks(controller);
            CheckMasterRightClicks(controller);
            controller.State.PaperSkin = PaperSkins.Acrylic; controller.State.Theme = "light";
            controller.State.EnableAnimations = false; Theme.Invalidate();

            var pageType = typeof(AppController).GetNestedType("SettingsPage", BindingFlags.NonPublic)!;
            var show = typeof(AppController).GetMethod("ShowSettingsWindow", Program.Private, null, [pageType], null)!;
            show.Invoke(controller, [Enum.Parse(pageType, "General")]);
            settings = (Window)typeof(AppController).GetField("_settingsWindow", Program.Private)!.GetValue(controller)!;
            var shell = (SkinBorder)settings.Content;
            Wait(120);
            Program.Assert(!shell.IsBackgroundActive && !shell.HasBackgroundCapture,
                "expanded Settings uses the native Acrylic backdrop without a software background-capture worker");
            var oldContent = shell.Child;
            show.Invoke(controller, [Enum.Parse(pageType, "Visual")]);
            Program.Assert(ReferenceEquals(shell, settings.Content) && !ReferenceEquals(oldContent, shell.Child) &&
                !shell.IsBackgroundActive && !shell.HasBackgroundCapture,
                "page switch replaces only Settings content and keeps the native material path capture-free");
            settings.Width += 12; settings.UpdateLayout(); shell.RefreshBackground();
            Program.Assert(!shell.IsBackgroundActive && !shell.HasBackgroundCapture && settings.Opacity == 1,
                "resizing Settings keeps the native material path capture-free and fully visible");
        }
        finally
        {
            if (menu != null) menu.IsOpen = false;
            if (aeroMenu != null) aeroMenu.IsOpen = false;
            settings?.Close(); owner.Close();
            (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations,
                controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckDefaultPaperLightweightShadow(AppController controller)
    {
        var nativeProperty = typeof(AppController).GetProperty(
            "UsesNativeMicaWindows",
            Program.Private)!;
        var savedNative = (bool)nativeProperty.GetValue(controller)!;
        var saved = (
            controller.State.PaperSkin,
            controller.State.Theme,
            controller.State.UseCapsuleMode);
        var paper = new PaperData
        {
            Type = PaperTypes.Todo,
            Title = "Lightweight shadow regression",
            X = 80,
            Y = 80,
            Width = 360,
            Height = 280
        };
        PaperWindow? window = null;
        try
        {
            nativeProperty.SetValue(controller, false);
            controller.State.PaperSkin = PaperSkins.Paper;
            controller.State.Theme = "light";
            controller.State.UseCapsuleMode = true;
            Theme.Invalidate();

            controller.State.Papers.Add(paper);
            window = new PaperWindow(paper, controller);
            window.Show();
            Wait(120);

            var chrome = (PaperChromeBorder)typeof(PaperWindow)
                .GetField("_paperChrome", Program.Private)!.GetValue(window)!;
            Program.Assert(
                window.AllowsTransparency &&
                chrome.Effect == null &&
                chrome.HasLightweightShadow,
                "default floating paper uses lightweight shadow instead of DropShadowEffect");
            AssertLightweightShadowRamp(window);

            window.SetCollapsedState(true, animate: false, saveGeometry: false);
            Wait(80);
            Program.Assert(
                chrome.Effect == null && chrome.HasLightweightShadow,
                "default capsule keeps the lightweight shadow path");

            window.SetCollapsedState(false, animate: false, saveGeometry: false);
            Wait(80);
            Program.Assert(
                chrome.Effect == null && chrome.HasLightweightShadow,
                "expanded default paper restores the lightweight shadow path");

            // Theme/skin refresh may change only the shadow recipe here. During a real form
            // transition Margin/CornerRadius are frame-owned, so refreshing paint must not
            // snap those geometry values to an endpoint.
            var normalMargin = chrome.Margin;
            var normalCorner = chrome.CornerRadius;
            var transitionMargin = new Thickness(3.25);
            var transitionCorner = new CornerRadius(11.5);
            chrome.Margin = transitionMargin;
            chrome.CornerRadius = transitionCorner;
            window.RefreshSkin();
            Program.Assert(
                chrome.Margin == transitionMargin &&
                chrome.CornerRadius == transitionCorner &&
                chrome.Effect == null &&
                chrome.HasLightweightShadow,
                "skin refresh changes paper shadow without rewriting transition geometry");
            chrome.Margin = normalMargin;
            chrome.CornerRadius = normalCorner;
            window.UpdateLayout();

            var savedOutline = controller.State.HideSurfaceOutline;
            controller.State.HideSurfaceOutline = true;
            window.RefreshSkin();
            Program.Assert(
                chrome.Effect == null && chrome.HasLightweightShadow,
                "hiding the outer border does not disable the lightweight paper shadow");
            AssertLightweightShadowRamp(window);
            controller.State.HideSurfaceOutline = savedOutline;
            window.RefreshSkin();

            controller.State.PaperSkin = PaperSkins.Acrylic;
            Theme.Invalidate();
            window.RefreshSkin();
            Program.Assert(
                !chrome.HasLightweightShadow && chrome.Effect is System.Windows.Media.Effects.DropShadowEffect,
                "switching away from paper clears the lightweight shadow before restoring the legacy skin effect");

            controller.State.PaperSkin = PaperSkins.Paper;
            Theme.Invalidate();
            window.RefreshSkin();
            Program.Assert(
                chrome.HasLightweightShadow && chrome.Effect == null,
                "switching back to paper restores only the lightweight shadow");
        }
        finally
        {
            window?.CloseForReal();
            controller.State.Papers.Remove(paper);
            (controller.State.PaperSkin,
                controller.State.Theme,
                controller.State.UseCapsuleMode) = saved;
            nativeProperty.SetValue(controller, savedNative);
            Theme.Invalidate();
        }
    }

    private static void AssertLightweightShadowRamp(PaperWindow window)
    {
        window.UpdateLayout();
        if (window.Content is not FrameworkElement host ||
            host.ActualWidth <= 24 ||
            host.ActualHeight <= 24)
        {
            throw new InvalidOperationException("default paper host is not renderable");
        }

        var dpi = VisualTreeHelper.GetDpi(host);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(host.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(host.ActualHeight * dpi.DpiScaleY));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(host);

        byte Alpha(double xDip, double yDip)
        {
            var x = Math.Clamp((int)Math.Round(xDip * dpi.DpiScaleX), 0, pixelWidth - 1);
            var y = Math.Clamp((int)Math.Round(yDip * dpi.DpiScaleY), 0, pixelHeight - 1);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return pixel[3];
        }

        var midX = host.ActualWidth / 2;
        var midY = host.ActualHeight / 2;
        var left = new[] { Alpha(0, midY), Alpha(4, midY), Alpha(7, midY) };
        var right = new[]
        {
            Alpha(host.ActualWidth - 1, midY),
            Alpha(host.ActualWidth - 4, midY),
            Alpha(host.ActualWidth - 7, midY)
        };
        var top = new[] { Alpha(midX, 0), Alpha(midX, 4), Alpha(midX, 7) };
        var bottom = new[]
        {
            Alpha(midX, host.ActualHeight - 1),
            Alpha(midX, host.ActualHeight - 4),
            Alpha(midX, host.ActualHeight - 7)
        };

        static bool FadesIn(byte[] samples) =>
            samples[0] < samples[1] &&
            samples[1] < samples[2] &&
            samples[1] >= 2;

        Program.Assert(
            FadesIn(left) && FadesIn(right) && FadesIn(top) && FadesIn(bottom),
            $"lightweight shadow fades through every gutter edge: " +
            $"L={string.Join(',', left)} R={string.Join(',', right)} " +
            $"T={string.Join(',', top)} B={string.Join(',', bottom)}");
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
        Window? outside = null;
        RoutedEventHandler? opened = null;
        EventHandler? rendered = null;
        var observedFrames = 0;
        try
        {
            master.ShowPlaced(1, false, false); Wait(120);
            outside = new Window
            {
                Width = 120,
                Height = 80,
                Left = master.Left + master.Width + 180,
                Top = master.Top + 100,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Content = new Border { Background = Brushes.DimGray }
            };
            outside.Show(); Wait(60);
            var pill = (FrameworkElement)typeof(MasterCapsuleWindow).GetField("_pill", Program.Private)!.GetValue(master)!;
            menu = pill.ContextMenu!;
            opened = (_, _) =>
            {
                if (controller.State.MatchAuxiliaryMaterialStrength) AssertNoPopupFade(menu);
            };
            rendered = (_, _) =>
            {
                if (!menu.IsOpen) return;
                if (controller.State.MatchAuxiliaryMaterialStrength) AssertNoPopupFade(menu);
                observedFrames++;
            };
            menu.Opened += opened;
            CompositionTarget.Rendering += rendered;
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
                    var beforeFrames = observedFrames;
                    var point = pill.PointToScreen(new Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
                    SetCursorPos((int)point.X, (int)point.Y); Wait(25);
                    mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
                    Until(() => menu.IsOpen, "actual MASTER right click opens " + skin);
                    Wait(180);
                    Program.Assert(observedFrames > beforeFrames, "observe actual opening frames, not just the settled menu");
                    var fullMaterial = controller.State.MatchAuxiliaryMaterialStrength;
                    // Only transparent material popups need a forced local None. Opaque quiet
                    // menus may use the normal system animation without exposing a plain frame.
                    menu.Resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.Fade;
                    if (fullMaterial) AssertNoPopupFade(menu);
                    menu.Resources.Remove(SystemParameters.MenuPopupAnimationKey);
                    var surface = Find(menu)!;
                    var opacityOwners = new List<string>();
                    for (DependencyObject? node = surface; node is Visual; node = VisualTreeHelper.GetParent(node))
                        if (node is UIElement ui) opacityOwners.Add($"{ui.GetType().Name}:{ui.Opacity:F3}/{ui.IsVisible}");
                    Console.WriteLine($"MASTER {skin}/{theme}/{attempt}: recipe={surface.Skin}; first={surface.FirstMenuRenderUsedBackground}; active={surface.IsBackgroundActive}; fallback={surface.MenuFallbackRenderCount}; capture={surface.HasBackgroundCapture}; frames={surface.BackgroundFrameCount}; suppress={surface.SuppressStaticBackgroundForOpening}; failure={surface.BackgroundFailure}; opacity={string.Join(',', opacityOwners)}");
                    Program.Assert(surface.Skin == skin && !surface.HasBackgroundCapture,
                        $"master {skin}/{theme}: current recipe stays capture-free after opening");
                    Program.Assert(fullMaterial ? surface.IsBackgroundActive : !surface.IsBackgroundActive,
                        $"master {skin}/{theme}: background transmission follows the full-material switch");
                    if (fullMaterial)
                        Program.Assert(surface.MenuFallbackRenderCount == 0,
                            $"master {skin}/{theme}: transparent material has no plain-paper fallback frame");
                    Program.Assert(menu.Opacity == 1, "master menu never hides content with a foreground opacity fade");
                    if (theme == "light" && skin == PaperSkins.Mica && attempt == 0)
                    {
                        var outsidePoint = outside!.PointToScreen(new Point(
                            outside.ActualWidth / 2,
                            outside.ActualHeight / 2));
                        SetCursorPos((int)outsidePoint.X, (int)outsidePoint.Y); Wait(30);
                        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                        Until(() => !menu.IsOpen, "actual outside click closes MASTER material menu");
                    }
                    else if (theme == "light" && skin == PaperSkins.Mica && attempt == 1)
                    {
                        keybd_event(0x1B, 0, 0, UIntPtr.Zero);
                        keybd_event(0x1B, 0, 0x0002, UIntPtr.Zero);
                        Until(() => !menu.IsOpen, "actual Escape closes MASTER material menu");
                    }
                    else
                    {
                        menu.IsOpen = false;
                    }
                    Wait(80);
                }
            }
        }
        finally
        {
            if (rendered != null) CompositionTarget.Rendering -= rendered;
            if (menu != null)
            {
                if (opened != null) menu.Opened -= opened;
                menu.IsOpen = false;
            }
            outside?.Close();
            master.CloseForReal(); SetCursorPos(cursor.X, cursor.Y);
            (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations,
                controller.State.MatchAuxiliaryMaterialStrength) = original;
            Theme.Invalidate();
        }
    }
    private static void AssertNoPopupFade(ContextMenu menu)
    {
        var popup = LogicalTreeHelper.GetParent(menu) as Popup;
        Program.Assert(popup != null && popup.PopupAnimation == PopupAnimation.None &&
            !DependencyPropertyHelper.GetValueSource(popup, Popup.PopupAnimationProperty).IsExpression,
            "live popup animation is a local None, not a dynamic system Fade expression");
        for (DependencyObject? node = menu; node is Visual; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement ui)
                Program.Assert(ui.Opacity == 1 && !DependencyPropertyHelper.GetValueSource(ui, UIElement.OpacityProperty).IsAnimated,
                    "menu and PopupRoot have no fade at Opened or during composition");
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extra);

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
