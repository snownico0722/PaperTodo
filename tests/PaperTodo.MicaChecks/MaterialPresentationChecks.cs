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
using PaperTodo.Plugin;

internal static class MaterialPresentationChecks
{
    internal static void Run(AppController controller)
    {
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
            controller.State.MatchAuxiliaryMaterialStrength, controller.State.MicaAlwaysActive);
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
            CheckPaperTopBar(controller);
            CheckPaperStartupFade(controller);
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

            // This real settings interaction was previously trapped inside the discarded
            // CopyFromScreen fixture; keep the independent WPF behavior without desktop pixels.
            var alwaysActive = Descendants(settings).OfType<CheckBox>().Single(box =>
                Equals(box.Content, Strings.Get("SettingsMicaAlwaysActive")));
            if (alwaysActive.IsChecked != true)
            {
                alwaysActive.IsChecked = true;
                alwaysActive.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Program.Assert(controller.State.MicaAlwaysActive,
                "clicking the native material appearance setting publishes the preference");
        }
        finally
        {
            if (menu != null) menu.IsOpen = false;
            if (aeroMenu != null) aeroMenu.IsOpen = false;
            settings?.Close(); owner.Close();
            (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations,
                controller.State.MatchAuxiliaryMaterialStrength, controller.State.MicaAlwaysActive) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckPaperTopBar(AppController controller)
    {
        var state = controller.State;
        var oldPins = state.TodoTopBarShortcuts.ToList();
        var oldSkin = state.PaperSkin;
        var oldLinks = state.EnableTodoPaperLinks;
        var paper = new PaperData
        {
            Type = PaperTypes.Todo, Title = "Top bar commands",
            X = 65, Y = 70, Width = 235, Height = 245
        };
        state.Papers.Add(paper);
        PaperWindow? window = null;
        var paperOwner = Guid.NewGuid();
        var globalOwner = Guid.NewGuid();
        GetCursorPos(out var previousCursor);
        try
        {
            state.PaperSkin = PaperSkins.Paper;
            state.EnableTodoPaperLinks = true;
            state.TodoTopBarShortcuts =
                [PaperTopBarShortcuts.PluginActions, PaperTopBarShortcuts.NewTodo,
                 PaperTopBarShortcuts.NewNote];
            Theme.Invalidate();
            // Use the real controller-owned window so a menu pin change must refresh
            // the live instance through AppController, not just mutate AppState.
            controller.ShowPaper(paper);
            var liveWindows = (Dictionary<string, PaperWindow>)typeof(AppController)
                .GetField("_windows", Program.Private)!.GetValue(controller)!;
            window = liveWindows[paper.Id];
            window.UpdateLayout();
            Wait(40);

            var paperActions = new[]
            {
                new PaperTopBarAction { Id = "paper-one", ToolTip = "Paper one",
                    Icon = PaperTopBarIcon.Character("1") },
                new PaperTopBarAction { Id = "paper-two", ToolTip = "Paper two",
                    Icon = PaperTopBarIcon.Character("2") }
            };
            var globals = new[]
            {
                new PaperTopBarAction { Id = "global-one", ToolTip = "Global one",
                    Icon = PaperTopBarIcon.Character("3") },
                new PaperTopBarAction { Id = "global-two", ToolTip = "Global two",
                    Icon = PaperTopBarIcon.Character("4") }
            };
            controller.SetPluginPaperTopBarActions(paperOwner, "topbar-fixture",
                paper.Id, paperActions, PaperHostTopBarActions.None, () => true, _ => { });
            controller.SetPluginGlobalTopBarActions(globalOwner, "topbar-fixture",
                globals, () => true, _ => { });
            window.RefreshPluginTopBarActions();
            window.UpdateLayout();
            Wait(50);

            T Field<T>(string name) => (T)typeof(PaperWindow)
                .GetField(name, Program.Private)!.GetValue(window)!;
            var host = Field<StackPanel>("_topBarActionButtonsHost");
            var plugins = Field<StackPanel>("_pluginTopBarButtonsHost");
            var createTodo = Field<Button>("_newTodoButton");
            var createNote = Field<Button>("_newNoteButton");
            var association = Field<Button>("_windowBindingButton");
            var more = Field<Button>("_moreActionsButton");
            var close = Field<Button>("_closeButton");
            var pin = Field<Button>("_paperIconButton");
            void LayoutAt(double width)
            {
                window.Width = width;
                window.UpdateLayout();
                Program.Pump();
            }

            // User pins are a single group even if the stored plugin pin comes first.
            LayoutAt(235);
            Program.Assert(plugins.Children.Count == 0 &&
                createTodo.Visibility == Visibility.Visible &&
                createNote.Visibility == Visibility.Visible &&
                close.Visibility == Visibility.Visible &&
                more.Visibility == Visibility.Visible &&
                pin.Visibility == Visibility.Visible &&
                host.Visibility == Visibility.Visible,
                "built-in shortcuts outrank every plugin icon while fixed controls survive");

            LayoutAt(185);
            Program.Assert(plugins.Children.Count == 0 &&
                createTodo.Visibility != Visibility.Visible &&
                createNote.Visibility != Visibility.Visible &&
                host.Visibility == Visibility.Visible,
                "a narrow paper overflows all built-in pins together, not one by one");
            more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Program.Pump();
            var narrowMenu = more.ContextMenu!;
            Program.Assert(narrowMenu.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, Strings.Get("MenuNewTodoPaper"))) &&
                narrowMenu.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, Strings.Get("MenuNewNotePaper"))) &&
                narrowMenu.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, Strings.Get("MenuTopBarAssociationDrag"))),
                "all hidden built-in commands, including a draggable association entry, are in overflow");
            var narrowPlugins = narrowMenu.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuTopBarPluginActions")));
            Program.Assert(narrowPlugins.Items.Count == 4,
                "all hidden plugin actions remain accessible in the shared paper menu");
            narrowMenu.IsOpen = false;

            LayoutAt(280);
            var shownPlugins = plugins.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible)
                .Select(button => button.ToolTip as string).ToArray();
            Program.Assert(shownPlugins.Length is >= 2 and < 4 &&
                createTodo.Visibility == Visibility.Visible &&
                createNote.Visibility == Visibility.Visible,
                "global plugin actions consume only the space left after the complete user group");
            more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Program.Pump();
            var partialMenu = more.ContextMenu!;
            var partialPlugins = partialMenu.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuTopBarPluginActions")));
            var hiddenPlugins = partialPlugins.Items.OfType<MenuItem>()
                .Select(item => item.Header as string).ToArray();
            Program.Assert(hiddenPlugins.Length == 4 - shownPlugins.Length &&
                shownPlugins.All(label => !hiddenPlugins.Contains(label)),
                "plugin menu contains only buttons actually hidden by overflow");
            partialMenu.IsOpen = false;

            LayoutAt(430);
            Program.Assert(plugins.Children.Count == 4 &&
                createTodo.Visibility == Visibility.Visible &&
                createNote.Visibility == Visibility.Visible,
                "expanding restores all built-in and plugin controls");

            state.TodoTopBarShortcuts =
                [PaperTopBarShortcuts.PluginActions, PaperTopBarShortcuts.NewTodo,
                 PaperTopBarShortcuts.Association, PaperTopBarShortcuts.NewNote];
            window.UpdateTopBarNewPaperButtons();
            LayoutAt(235);
            Program.Assert(createTodo.Visibility == Visibility.Visible &&
                association.Visibility == Visibility.Visible &&
                createNote.Visibility == Visibility.Visible &&
                plugins.Children.Count == 0,
                "pinning association keeps the complete user group ahead of plugins");

            LayoutAt(185);
            Program.Assert(createTodo.Visibility != Visibility.Visible &&
                createNote.Visibility != Visibility.Visible &&
                association.Visibility != Visibility.Visible &&
                plugins.Children.Count == 0 &&
                host.Visibility == Visibility.Visible,
                "the entire shortcut group, including association, enters overflow at narrow widths");
            more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Program.Pump();
            var narrowAssociationMenu = more.ContextMenu!;
            var dragEntry = narrowAssociationMenu.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuTopBarAssociationDrag")));
            Program.Assert(dragEntry.StaysOpenOnClick,
                "association in overflow remains a drag origin instead of a no-op click");
            // Exercise WPF's real mouse capture on the popup MenuItem. A mere
            // StaysOpenOnClick flag would not prove users can drag from this HWND.
            try
            {
                var from = dragEntry.PointToScreen(
                    new Point(dragEntry.ActualWidth / 2, dragEntry.ActualHeight / 2));
                var sx = (int)Math.Round(from.X);
                var sy = (int)Math.Round(from.Y);
                SetCursorPos(sx, sy);
                Wait(30);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                Wait(30);
                SetCursorPos(sx + 65, sy + 15);
                Wait(70);
                var drag = typeof(PaperWindow).GetField("_topBarDrag", Program.Private)!
                    .GetValue(window);
                Program.Assert(drag != null &&
                    drag.GetType().GetProperty("IsDragging")!.GetValue(drag) is true,
                    "actual mouse movement begins association drag from the overflow popup");
            }
            finally
            {
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                narrowAssociationMenu.IsOpen = false;
                Wait(25);
            }

            LayoutAt(430);
            Program.Assert(plugins.Children.Count == 4 &&
                association.Visibility == Visibility.Visible,
                "user pins and lowest-priority plugins return on expansion");
            more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Program.Pump();
            var popup = more.ContextMenu!;
            Program.Assert(!popup.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, Strings.Get("MenuNewTodoPaper")) ||
                    Equals(item.Header, Strings.Get("MenuNewNotePaper")) ||
                    Equals(item.Header, Strings.Get("MenuTopBarAssociationDrag")) ||
                    Equals(item.Header, Strings.Get("MenuTopBarPluginActions"))),
                "commands visible as title-bar icons are not duplicated in overflow");
            var titles = popup.Items.OfType<MenuItem>()
                .Select(item => item.Header as string).ToArray();
            popup.IsOpen = false;
            var chrome = Field<SkinBorder>("_paperChrome");
            var context = chrome.ContextMenu!;
            context.PlacementTarget = chrome;
            context.IsOpen = true;
            Program.Pump();
            Program.Assert(context.Items.OfType<MenuItem>()
                    .Select(item => item.Header as string).SequenceEqual(titles),
                "existing right-click menu refreshes to match the ellipsis after resizes");
            context.IsOpen = false;

            // Actual checkbox click still refreshes the controller-owned window.
            more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Program.Pump();
            popup = more.ContextMenu!;
            var customize = popup.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuCustomizeTopBar")));
            var newNote = customize.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuNewNotePaper")));
            Program.Assert(newNote.IsChecked, "pinned command is checked in the menu");
            newNote.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Program.Assert(!PaperTopBarShortcuts.Contains(state, paper.Type,
                    PaperTopBarShortcuts.NewNote) &&
                createNote.Visibility != Visibility.Visible,
                "clicking the menu checkbox removes the live title-bar shortcut");
            popup.IsOpen = false;
            more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Program.Pump();
            popup = more.ContextMenu!;
            Program.Assert(popup.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, Strings.Get("MenuNewNotePaper"))),
                "a shortcut removed from the title bar appears in overflow again");
            popup.IsOpen = false;
        }
        finally
        {
            controller.RemovePluginPaperTopBarSession(paperOwner);
            controller.RemovePluginGlobalTopBarRuntime(globalOwner, "topbar-fixture");
            window?.CloseForReal();
            state.Papers.Remove(paper);
            state.TodoTopBarShortcuts = oldPins;
            state.EnableTodoPaperLinks = oldLinks;
            state.PaperSkin = oldSkin;
            SetCursorPos(previousCursor.X, previousCursor.Y);
            Theme.Invalidate();
        }
    }

    private static void CheckPaperStartupFade(AppController controller)
    {
        var state = controller.State;
        var oldAnimations = state.EnableAnimations;
        var paper = new PaperData { Type = PaperTypes.Todo,
            Title = "Startup opacity", Width = 300, Height = 220, X = 80, Y = 80 };
        state.Papers.Add(paper);
        try
        {
            state.EnableAnimations = true;
            controller.ShowPaper(paper);
            Wait(700);
            var windows = (Dictionary<string, PaperWindow>)typeof(AppController)
                .GetField("_windows", Program.Private)!.GetValue(controller)!;
            Program.Assert(windows.TryGetValue(paper.Id, out var window) &&
                !window.HasAnimatedProperties,
                "startup fade releases its opacity clock after showing the real paper");
            window!.CloseForReal();
        }
        finally
        {
            state.Papers.Remove(paper);
            state.EnableAnimations = oldAnimations;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
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
