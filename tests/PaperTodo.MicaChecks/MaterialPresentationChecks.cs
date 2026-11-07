using System.Diagnostics;
using System.IO;
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

        var saved = (controller.State.PaperSkin, controller.State.Theme,
            controller.State.EnableAnimations, controller.State.MicaAlwaysActive);
        Window? settings = null;
        var owner = new Window { Width = 200, Height = 100, Left = 40, Top = 40, ShowInTaskbar = false, Content = new Border() };
        ContextMenu? menu = null, aeroMenu = null;
        try
        {
            controller.State.PaperSkin = PaperSkins.Acrylic; controller.State.Theme = "light";
            controller.State.EnableAnimations = false;
            Theme.Invalidate();
            owner.Show();
            menu = controller.CreateTrayMenu(); menu.Items.Add(new MenuItem { Header = "Cancel before background is ready" });
            menu.PlacementTarget = (UIElement)owner.Content; menu.Placement = PlacementMode.Bottom;
            var opens = 0; menu.Opened += (_, _) => opens++;
            menu.SetCurrentValue(ContextMenu.IsOpenProperty, true);
            Until(() => menu.IsOpen && opens == 1, "tray menu opens immediately");
            menu.IsOpen = false; Wait(80);
            Program.Assert(!menu.IsOpen && opens == 1, "dismissed menu stays closed");
            menu.SetCurrentValue(ContextMenu.IsOpenProperty, true);
            Until(() => menu.IsOpen && opens == 2, "tray menu can reopen immediately");
            var menuSurface = Find(menu);
            Program.Assert(menuSurface is { IsMenu: true } && menu.Opacity == 1,
                "tray menu uses the normal popup without a deferred background capture");
            menu.IsOpen = false; Wait(60);

            controller.State.PaperSkin = PaperSkins.Aero; Theme.Invalidate();
            aeroMenu = controller.CreateTrayMenu();
            aeroMenu.PlacementTarget = (UIElement)owner.Content; aeroMenu.Placement = PlacementMode.Bottom;
            aeroMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);
            Until(() => aeroMenu.IsOpen, "Aero menu opens immediately without capture preparation"); Wait(80);
            var aeroSurface = Find(aeroMenu);
            Program.Assert(aeroSurface is { Skin: PaperSkins.Aero, IsMenu: true } &&
                aeroMenu.Opacity == 1, "Aero menu uses the normal immediate popup path");
            aeroMenu.IsOpen = false; Wait(60);
            controller.State.PaperSkin = PaperSkins.Acrylic; Theme.Invalidate();

            CheckRealRightClicks(controller);
            CheckPaperTopBar(controller);
            CheckTopBarSettingsDrag(controller);
            CheckMasterRightClicks(controller);
            CheckEdgeExternalForeground(controller);
            controller.State.PaperSkin = PaperSkins.Acrylic; controller.State.Theme = "light";
            controller.State.EnableAnimations = false; Theme.Invalidate();

            var pageType = typeof(AppController).GetNestedType("SettingsPage", BindingFlags.NonPublic)!;
            var show = typeof(AppController).GetMethod("ShowSettingsWindow", Program.Private, null, [pageType], null)!;
            show.Invoke(controller, [Enum.Parse(pageType, "General")]);
            settings = (Window)typeof(AppController).GetField("_settingsWindow", Program.Private)!.GetValue(controller)!;
            var shell = (SkinBorder)settings.Content;
            Wait(120);
            Program.Assert(shell.Background != null && settings.Opacity == 1,
                "expanded settings shell keeps its native material composition");
            var oldContent = shell.Child;
            show.Invoke(controller, [Enum.Parse(pageType, "Visual")]);
            Program.Assert(ReferenceEquals(shell, settings.Content) && !ReferenceEquals(oldContent, shell.Child) &&
                settings.Opacity == 1,
                "page switch replaces only Settings content on the same native window");
            settings.Width += 12; settings.UpdateLayout(); shell.RefreshBackground();
            Program.Assert(settings.Opacity == 1 && ReferenceEquals(shell, settings.Content),
                "resizing Settings retains its shell and visible content");

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
                controller.State.MicaAlwaysActive) = saved;
            Theme.Invalidate();
        }
    }
    private static void CheckPaperTopBar(AppController controller)
    {
        var state = controller.State;
        var oldOrder = state.TopBarShortcutOrder.ToList();
        var oldDivider = state.TopBarPinnedCount;
        var oldHidden = state.HiddenTopBarShortcuts.ToList();
        var oldSkin = state.PaperSkin;
        var oldLinks = state.EnableTodoPaperLinks;
        var oldTether = state.ExperimentalWindowTethering;
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
            state.HiddenTopBarShortcuts = [];
            state.EnableTodoPaperLinks = true;
            state.ExperimentalWindowTethering = false;
            // Preserve an explicit two-creation-button fixture for width and overflow tests.
            state.TopBarShortcutOrder =
                [PaperTopBarShortcuts.NewTodo, PaperTopBarShortcuts.NewNote,
                 PaperTopBarShortcuts.Association, PaperTopBarShortcuts.ExternalEditor];
            state.TopBarPinnedCount = 2;
            Theme.Invalidate();
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
            var globalActions = new[]
            {
                new PaperTopBarAction { Id = "global-one", ToolTip = "Global one",
                    Icon = PaperTopBarIcon.Character("3") },
                new PaperTopBarAction { Id = "global-two", ToolTip = "Global two",
                    Icon = PaperTopBarIcon.Character("4") }
            };
            controller.SetPluginPaperTopBarActions(paperOwner, "topbar-fixture",
                paper.Id, paperActions, PaperHostTopBarActions.None, () => true, _ => { });
            controller.SetPluginGlobalTopBarActions(globalOwner, "topbar-fixture",
                globalActions, () => true, _ => { });
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
            void LayoutAt(double width)
            {
                window.Width = width;
                window.UpdateLayout();
                Program.Pump();
            }
            void OpenMenu()
            {
                more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Program.Pump();
            }
            static bool Has(ContextMenu menu, string header) =>
                menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, header));

            LayoutAt(235);
            Program.Assert(plugins.Children.Count == 0 &&
                createTodo.Visibility == Visibility.Visible &&
                createNote.Visibility == Visibility.Visible &&
                association.Visibility != Visibility.Visible &&
                host.Visibility == Visibility.Visible,
                "configured global pins outrank even the paper's own plugin buttons");

            LayoutAt(185);
            Program.Assert(createTodo.Visibility != Visibility.Visible &&
                createNote.Visibility != Visibility.Visible &&
                plugins.Children.Count == 0 && host.Visibility == Visibility.Visible,
                "narrow title bar overflows all built-in buttons together");
            OpenMenu();
            var narrowMenu = more.ContextMenu!;
            Program.Assert(Has(narrowMenu, Strings.Get("MenuQuick")) &&
                Has(narrowMenu, Strings.Get("MenuNewTodoPaper")) &&
                Has(narrowMenu, Strings.Get("MenuNewNotePaper")) &&
                Has(narrowMenu, Strings.Get("MenuAssociationTarget")) &&
                Has(narrowMenu, Strings.Get("TraySettings")) &&
                Has(narrowMenu, controller.PaperCapsuleTitle(paper)) &&
                !Has(narrowMenu, Strings.Get("MenuCustomizeTopBar")),
                "overflow restores Quick, paper title and Settings, without title-bar customization");
            var exportMenu = narrowMenu.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuExport")));
            exportMenu.IsSubmenuOpen = true;
            Program.Pump();
            Program.Assert(exportMenu.IsSubmenuOpen && exportMenu.Items.OfType<MenuItem>()
                    .Select(item => item.Header as string).SequenceEqual(new[] {
                        Strings.Get("MenuExportCurrent"), Strings.Get("MenuExportAllNotes"),
                        Strings.Get("MenuExportAllTodos"), Strings.Get("MenuExportAllPapers") }),
                "export scopes remain reachable inside one real submenu");
            exportMenu.IsSubmenuOpen = false;
            var narrowPlugins = narrowMenu.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuTopBarPluginActions")));
            Program.Assert(narrowPlugins.Items.Count == 4,
                "all hidden plugin commands remain reachable inside Quick");
            narrowMenu.IsOpen = false;

            LayoutAt(280);
            var shownPlugins = plugins.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible)
                .Select(button => button.ToolTip as string).ToArray();
            Program.Assert(shownPlugins.Length is >= 2 and < 4 &&
                createTodo.Visibility == Visibility.Visible &&
                createNote.Visibility == Visibility.Visible,
                "automatic plugin actions use remaining space without a user pin switch");
            OpenMenu();
            var partial = more.ContextMenu!;
            var hiddenPlugins = partial.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuTopBarPluginActions")))
                .Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray();
            Program.Assert(hiddenPlugins.Length == 4 - shownPlugins.Length &&
                shownPlugins.All(x => !hiddenPlugins.Contains(x)),
                "menu contains only the plugin commands not already shown");
            partial.IsOpen = false;

            LayoutAt(430);
            Program.Assert(plugins.Children.Count == 4 &&
                createTodo.Visibility == Visibility.Visible &&
                createNote.Visibility == Visibility.Visible,
                "all plugin icons return after expansion");

            Program.Assert(controller.MoveTopBarShortcut(PaperTopBarShortcuts.Association, true, 2),
                "global settings can drag association into the title-bar group");
            LayoutAt(235);
            Program.Assert(association.Visibility == Visibility.Visible &&
                createTodo.Visibility == Visibility.Visible &&
                createNote.Visibility == Visibility.Visible &&
                plugins.Children.Count == 0,
                "three built-in pins display together ahead of plugins");

            LayoutAt(185);
            OpenMenu();
            var dragMenu = more.ContextMenu!;
            var dragEntry = dragMenu.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuAssociationTarget")));
            Program.Assert(dragEntry.StaysOpenOnClick &&
                Equals(dragEntry.ToolTip, Strings.Get("MenuTopBarAssociationDragTodo")),
                "association remains a draggable menu command when the whole group overflows");
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
                    "actual drag can begin from the overflow menu");
            }
            finally
            {
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                dragMenu.IsOpen = false;
                Wait(25);
            }

            LayoutAt(430);
            OpenMenu();
            var wideMenu = more.ContextMenu!;
            Program.Assert(!Has(wideMenu, Strings.Get("MenuNewTodoPaper")) &&
                !Has(wideMenu, Strings.Get("MenuNewNotePaper")) &&
                !Has(wideMenu, Strings.Get("MenuAssociationTarget")) &&
                !Has(wideMenu, Strings.Get("MenuTopBarPluginActions")) &&
                Has(wideMenu, Strings.Get("MenuQuick")) &&
                Has(wideMenu, Strings.Get("TraySettings")),
                "visible built-in/plugin icons are not duplicated in the shared menu");
            var titles = wideMenu.Items.OfType<MenuItem>()
                .Select(item => item.Header as string).ToArray();
            wideMenu.IsOpen = false;

            // A still-visible edge capsule has its own menu, even when the source
            // paper is expanded and both creation commands are pinned in its top bar.
            var buildCapsuleMenu = typeof(PaperWindow).GetMethod(
                "BuildDeepCapsuleSlotContextMenu", Program.Private)!;
            var capsuleMenu = (ContextMenu)buildCapsuleMenu.Invoke(window, null)!;
            Program.Assert(Has(capsuleMenu, Strings.Get("MenuNewTodoPaper")) &&
                Has(capsuleMenu, Strings.Get("MenuNewNotePaper")),
                "expanded edge capsule retains both creation entries despite visible top-bar shortcuts");

            var chrome = Field<SkinBorder>("_paperChrome");
            var rightClick = chrome.ContextMenu!;
            rightClick.PlacementTarget = chrome;
            rightClick.IsOpen = true;
            Program.Pump();
            Program.Assert(rightClick.Items.OfType<MenuItem>()
                    .Select(item => item.Header as string).SequenceEqual(titles),
                "cached right-click menu mirrors the latest ellipsis menu");
            rightClick.IsOpen = false;

            Program.Assert(controller.MoveTopBarShortcut(PaperTopBarShortcuts.NewNote, false, 0),
                "dragging New Note below the divider is accepted");
            OpenMenu();
            var overflow = more.ContextMenu!;
            Program.Assert(Has(overflow, Strings.Get("MenuNewNotePaper")) &&
                createNote.Visibility != Visibility.Visible,
                "moving a shortcut into the menu immediately removes the live title-bar icon");
            overflow.IsOpen = false;

            var orderBeforeHide = state.TopBarShortcutOrder.ToArray();
            controller.SetTopBarShortcutHidden(PaperTopBarShortcuts.NewTodo, true);
            controller.SetTopBarShortcutHidden(PaperTopBarShortcuts.NewNote, true);
            Program.Pump();
            OpenMenu();
            var hiddenMenu = more.ContextMenu!;
            Program.Assert(createTodo.Visibility != Visibility.Visible &&
                createNote.Visibility != Visibility.Visible &&
                !Has(hiddenMenu, Strings.Get("MenuNewTodoPaper")) &&
                !Has(hiddenMenu, Strings.Get("MenuNewNotePaper")),
                "Hide removes both pinned and menu shortcuts without falling back to overflow");
            hiddenMenu.IsOpen = false;
            var hiddenCapsuleMenu = (ContextMenu)buildCapsuleMenu.Invoke(window, null)!;
            rightClick.IsOpen = true;
            Program.Pump();
            Program.Assert(!Has(hiddenCapsuleMenu, Strings.Get("MenuNewTodoPaper")) &&
                !Has(hiddenCapsuleMenu, Strings.Get("MenuNewNotePaper")) &&
                !Has(rightClick, Strings.Get("MenuNewTodoPaper")) &&
                !Has(rightClick, Strings.Get("MenuNewNotePaper")) &&
                Has(rightClick, Strings.Get("TraySettings")),
                "Hide applies to edge and cached paper context menus while fixed Settings remains reachable");
            rightClick.IsOpen = false;
            controller.SetTopBarShortcutHidden(PaperTopBarShortcuts.NewTodo, false);
            controller.SetTopBarShortcutHidden(PaperTopBarShortcuts.NewNote, false);
            LayoutAt(430);
            OpenMenu();
            Program.Assert(state.TopBarShortcutOrder.SequenceEqual(orderBeforeHide) &&
                createTodo.Visibility == Visibility.Visible &&
                Has(more.ContextMenu!, Strings.Get("MenuNewNotePaper")),
                "Unhide restores each original title-bar or menu location");
            more.ContextMenu!.IsOpen = false;

            // Menu-only descriptors remain reachable at any width without creating title-bar
            // buttons. They use the same Paper/Runtime callbacks and teardown as normal actions.
            var menuInvocations = new List<PaperTopBarActionInvocation>();
            controller.SetPluginPaperTopBarActions(paperOwner, "topbar-fixture", paper.Id,
                [new() { Id = "paper-menu", ToolTip = "Paper menu", MenuOnly = true }],
                PaperHostTopBarActions.None, () => true, menuInvocations.Add);
            controller.SetPluginGlobalTopBarActions(globalOwner, "topbar-fixture",
                [new() { Id = "global-menu", ToolTip = "Global menu", MenuOnly = true }],
                () => true, menuInvocations.Add);
            LayoutAt(600);
            Program.Assert(plugins.Children.Count == 0 && plugins.Visibility == Visibility.Collapsed,
                "menu-only actions neither materialize title-bar buttons nor reserve title-bar space");
            OpenMenu();
            var menuOnly = more.ContextMenu!;
            var menuActions = menuOnly.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, Strings.Get("MenuTopBarPluginActions")))
                .Items.OfType<MenuItem>().ToArray();
            Program.Assert(menuActions.Select(item => item.Header as string)
                    .SequenceEqual(new[] { "Paper menu", "Global menu" }),
                "menu-only actions preserve scope ordering and use their text labels");
            foreach (var item in menuActions) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Program.Assert(menuInvocations.Count == 2 &&
                menuInvocations[0].Scope == PaperTopBarActionScope.Paper &&
                menuInvocations[1].Scope == PaperTopBarActionScope.Global &&
                menuInvocations.All(invocation => invocation.TargetPaperId == paper.Id),
                "menu-only callbacks retain the correct target paper and scope");
            menuOnly.IsOpen = false;
            controller.RemovePluginPaperTopBarSession(paperOwner);
            controller.RemovePluginGlobalTopBarRuntime(globalOwner, "topbar-fixture");
            foreach (var item in menuActions) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Program.Assert(menuInvocations.Count == 2, "revoked menu-only actions must not invoke old callbacks");
            OpenMenu();
            Program.Assert(!Has(more.ContextMenu!, Strings.Get("MenuTopBarPluginActions")),
                "removed owners leave no plugin menu actions");
            more.ContextMenu!.IsOpen = false;
        }
        finally
        {
            controller.RemovePluginPaperTopBarSession(paperOwner);
            controller.RemovePluginGlobalTopBarRuntime(globalOwner, "topbar-fixture");
            window?.CloseForReal();
            state.Papers.Remove(paper);
            state.TopBarShortcutOrder = oldOrder;
            state.TopBarPinnedCount = oldDivider;
            state.HiddenTopBarShortcuts = oldHidden;
            state.EnableTodoPaperLinks = oldLinks;
            state.ExperimentalWindowTethering = oldTether;
            state.PaperSkin = oldSkin;
            SetCursorPos(previousCursor.X, previousCursor.Y);
            Theme.Invalidate();
        }
    }

    private static void CheckTopBarSettingsDrag(AppController controller)
    {
        // A single physical drag covers the settings source gesture, routed drop
        // and committed order without enumerating every decorative drop region.
        var state = controller.State;
        var originalOrder = state.TopBarShortcutOrder.ToArray();
        var originalCount = state.TopBarPinnedCount;
        var originalHidden = state.HiddenTopBarShortcuts.ToList();
        GetCursorPos(out var savedCursor);
        Window? surface = null;
        try
        {
            // Use an explicit order so the gesture does not depend on default pins.
            state.TopBarShortcutOrder =
                [PaperTopBarShortcuts.NewTodo, PaperTopBarShortcuts.NewNote,
                 PaperTopBarShortcuts.Association, PaperTopBarShortcuts.ExternalEditor];
            state.TopBarPinnedCount = 2;
            surface = new Window
            {
                Width = 380, Height = 435, Left = 285, Top = 105,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false
            };
            var builder = typeof(AppController).GetMethod(
                "BuildTopBarShortcutSettings", Program.Private)!;
            var root = (StackPanel)builder.Invoke(controller, null)!;
            surface.Content = root;
            surface.Show();
            surface.Activate();
            surface.UpdateLayout();
            Program.Pump();
            var dropArea = (StackPanel)((Border)root.Children[1]).Child;
            var upperGroup = (StackPanel)dropArea.Children[1];
            var menuHeading = (FrameworkElement)dropArea.Children[3];
            var sourceRow = (Grid)upperGroup.Children[0]; // New Todo
            var hide = Descendants(sourceRow).OfType<CheckBox>().Single();
            var hideAt = hide.PointToScreen(new Point(hide.ActualWidth / 2, hide.ActualHeight / 2));
            SetCursorPos((int)hideAt.X, (int)hideAt.Y);
            Wait(30);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos((int)hideAt.X + 9, (int)hideAt.Y);
            Wait(40);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Program.Pump();
            Program.Assert(PaperTopBarShortcuts.IsHidden(state, PaperTopBarShortcuts.NewTodo) &&
                state.TopBarPinnedCount == 2 && state.TopBarShortcutOrder[0] == PaperTopBarShortcuts.NewTodo,
                "moving a checkbox press past the drag threshold only toggles visibility");
            var from = sourceRow.PointToScreen(new Point(
                sourceRow.ActualWidth / 2, sourceRow.ActualHeight / 2));
            var to = menuHeading.PointToScreen(new Point(
                menuHeading.ActualWidth / 2, menuHeading.ActualHeight / 2));
            var sourceX = (int)Math.Round(from.X);
            var sourceY = (int)Math.Round(from.Y);
            var destX = (int)Math.Round(to.X);
            var destY = (int)Math.Round(to.Y);
            SetCursorPos(sourceX, sourceY);
            Wait(50);
            try
            {
                var movePointer = Task.Run(() =>
                {
                    Thread.Sleep(70);
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(65);
                    // Start inside the row before leaving it, to preserve the
                    // expected drag threshold and pointer capture behavior.
                    SetCursorPos(sourceX + 9, sourceY + 6);
                    Thread.Sleep(100);
                    SetCursorPos(destX, destY);
                    Thread.Sleep(140);
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                });
                Wait(490);
                movePointer.GetAwaiter().GetResult();
                Until(() => state.TopBarPinnedCount == 1,
                    "real left-button drag moves New Todo into the lower section");
                Program.Assert(PaperTopBarShortcuts.Pinned(state).SequenceEqual(
                        [PaperTopBarShortcuts.NewNote]) &&
                    PaperTopBarShortcuts.Menu(state)[0] == PaperTopBarShortcuts.NewTodo,
                    "mouse-driven WPF drag and drop persists a single global section move");
            }
            finally
            {
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            }
        }
        finally
        {
            surface?.Close();
            SetCursorPos(savedCursor.X, savedCursor.Y);
            state.TopBarShortcutOrder = originalOrder.ToList();
            state.TopBarPinnedCount = originalCount;
            state.HiddenTopBarShortcuts = originalHidden;
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
                    Program.Assert(Find(menu!) is { IsMenu: true } && menu!.Opacity == 1,
                        "service-opened menu paints the lightweight material immediately");
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
        var original = (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations);
        controller.State.PaperSkin = PaperSkins.Paper; Theme.Invalidate();
        var master = new MasterCapsuleWindow(controller, EdgeCapsuleEdge.Left, "");
        GetCursorPos(out var cursor);
        ContextMenu? menu = null;
        Window? outside = null;
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
            rendered = (_, _) =>
            {
                if (!menu.IsOpen) return;
                observedFrames++;
            };
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
                    var beforeFrames = observedFrames;
                    var point = pill.PointToScreen(new Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
                    SetCursorPos((int)point.X, (int)point.Y); Wait(25);
                    mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
                    Until(() => menu.IsOpen, "actual MASTER right click opens " + skin);
                    Wait(180);
                    Program.Assert(observedFrames > beforeFrames, "observe actual opening frames, not just the settled menu");
                    var surface = Find(menu)!;
                    Program.Assert(surface.Skin == skin && surface.IsMenu,
                        $"master {skin}/{theme}: correct lightweight surface after real right-click");
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
            // Cross-process foreground is different from the same-thread outside window above.
            // Reproduce a right click on a NOACTIVATE owner while another app is foreground.
            outside.Close();
            outside = null;
            CheckMasterExternalForeground(controller, master, pill, menu);
        }
        finally
        {
            if (rendered != null) CompositionTarget.Rendering -= rendered;
            if (menu != null)
            {
                menu.IsOpen = false;
            }
            outside?.Close();
            master.CloseForReal(); SetCursorPos(cursor.X, cursor.Y);
            (controller.State.PaperSkin, controller.State.Theme, controller.State.EnableAnimations) = original;
            Theme.Invalidate();
        }
    }
    private static void CheckEdgeExternalForeground(AppController controller)
    {
        var state = controller.State;
        var original = (state.UseCapsuleMode, state.UseDeepCapsuleMode,
            state.EnableAnimations, state.ExperimentalEdgeCapsuleHoverPreview,
            state.PaperSkin, state.Theme);
        var area = SystemParameters.WorkArea;
        var paper = new PaperData
        {
            Type = PaperTypes.Todo, Title = "Edge menu regression",
            IsVisible = false, IsCollapsed = true, CapsuleSide = DeepCapsuleSides.Right,
            X = area.Left + 100, Y = area.Top + 100, Width = 320, Height = 260
        };
        try
        {
            state.UseCapsuleMode = true;
            state.UseDeepCapsuleMode = true;
            state.EnableAnimations = false;
            state.ExperimentalEdgeCapsuleHoverPreview = false;
            state.Papers.Add(paper);
            controller.ShowPaper(paper, activate: false);
            var windows = (Dictionary<string, PaperWindow>)typeof(AppController)
                .GetField("_windows", Program.Private)!.GetValue(controller)!;
            Program.Assert(windows.TryGetValue(paper.Id, out var window),
                "edge menu fixture has a real PaperWindow");
            var hostField = typeof(PaperWindow).GetField("_edgeCapsuleHost", Program.Private)!;
            Until(() => hostField.GetValue(window) is EdgeCapsuleHost,
                "real Edge capsule host is constructed after the placement is dispatched");
            var host = (EdgeCapsuleHost)hostField.GetValue(window)!;
            var content = (FrameworkElement)typeof(EdgeCapsuleHost)
                .GetProperty("ContentArea", Program.Private)!.GetValue(host)!;
            Until(() => host.IsVisible && content.ContextMenu != null &&
                content.ActualWidth > 0 && content.ActualHeight > 0,
                "real edge capsule has an initialized context menu");
            var nativeWindow = (Window)typeof(EdgeCapsuleHost)
                .GetProperty("Window", Program.Private)!.GetValue(host)!;
            Program.Assert(WindowNative.HasNoActivateStyle(nativeWindow),
                "edge owner stays NOACTIVATE for normal capsule interactions");
            CheckCapsuleExternalForeground(controller, content, content.ContextMenu!,
                host.Handle, "EDGE", host.RefreshSkin,
                area.Left + 180, area.Top + 120);
            Console.WriteLine("PASS real EDGE right-click: external foreground, stable popup and outside click");
        }
        finally
        {
            if (state.Papers.Contains(paper)) controller.DeletePaper(paper);
            (state.UseCapsuleMode, state.UseDeepCapsuleMode,
                state.EnableAnimations, state.ExperimentalEdgeCapsuleHoverPreview,
                state.PaperSkin, state.Theme) = original;
            Theme.Invalidate();
        }
    }

    private static void CheckMasterExternalForeground(
        AppController controller, MasterCapsuleWindow master, FrameworkElement pill, ContextMenu menu) =>
        CheckCapsuleExternalForeground(controller, pill, menu,
            new WindowInteropHelper(master).Handle, "MASTER", master.UpdateTheme,
            master.Left + master.Width + 240, master.Top + 70);

    private static void CheckCapsuleExternalForeground(
        AppController controller, FrameworkElement target, ContextMenu menu,
        IntPtr ownerHwnd, string name, Action refreshTheme, double left, double top)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PaperTodo.MicaChecks.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--external-menu-window");
        start.ArgumentList.Add(left.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(top.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var external = Process.Start(start) ??
            throw new InvalidOperationException("Unable to launch the external foreground fixture");
        try
        {
            var ready = external.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            Program.Assert(ready?.StartsWith("READY ", StringComparison.Ordinal) == true,
                "external process exposes its real window handle");
            var externalHwnd = new IntPtr(long.Parse(ready![6..],
                System.Globalization.CultureInfo.InvariantCulture));
            var externalX = (int)Math.Round(left + 95);
            var externalY = (int)Math.Round(top + 60);
            foreach (var skin in new[] { PaperSkins.Mica, PaperSkins.Acrylic })
            {
                controller.State.PaperSkin = skin;
                controller.State.Theme = "light";
                Theme.Invalidate();
                refreshTheme();
                Wait(70);
                // PaperWindow can replace Edge context-menu instances when its shell
                // is refreshed; exercise the menu currently attached to the real surface.
                menu = target.ContextMenu ??
                    throw new InvalidOperationException(name + " has no live owner context menu");

                SetCursorPos(externalX, externalY);
                Wait(30);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                Until(() => GetForegroundWindow() == externalHwnd,
                    "external process owns foreground before " + name + " right click");

                var point = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                var clickX = (int)Math.Round(point.X);
                var clickY = (int)Math.Round(point.Y);
                SetCursorPos(clickX, clickY);
                Wait(30);
                var hit = WindowFromPoint(new CursorPoint { X = clickX, Y = clickY });
                if (name == "EDGE")
                {
                    Console.WriteLine($"EDGE native hit: point={clickX},{clickY} window={hit} owner={ownerHwnd} " +
                        $"target={target.ActualWidth:F1}x{target.ActualHeight:F1}");
                    Program.Assert(hit == ownerHwnd,
                        "real mouse must hit the Edge HWND rather than a transparent margin or another window");
                }
                var openedCount = 0;
                var closedCount = 0;
                RoutedEventHandler opened = (_, _) => openedCount++;
                RoutedEventHandler closed = (_, _) => closedCount++;
                menu.Opened += opened;
                menu.Closed += closed;
                try
                {
                    mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
                    Until(() => menu.IsOpen || openedCount > 0,
                        name + " menu receives a real right click");
                    Wait(150);
                    Program.Assert(menu.IsOpen,
                        $"{name} menu remains open (opened={openedCount}, closed={closedCount}, foreground={GetForegroundWindow()})");
                }
                finally
                {
                    menu.Opened -= opened;
                    menu.Closed -= closed;
                }
                var popupHwnd = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? IntPtr.Zero;
                Program.Assert(menu.IsOpen && popupHwnd != IntPtr.Zero &&
                    GetForegroundWindow() is var foreground &&
                    (foreground == ownerHwnd || foreground == popupHwnd),
                    name + " menu activation precedes WPF mouse capture without immediate dismissal");

                SetCursorPos(externalX, externalY);
                Wait(30);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                Until(() => !menu.IsOpen, "cross-process outside click dismisses " + name + " popup");
                Wait(50);
            }
        }
        finally
        {
            menu.IsOpen = false;
            if (!external.HasExited)
            {
                try { external.StandardInput.WriteLine(); }
                catch (IOException) { }
                if (!external.WaitForExit(3000))
                {
                    external.Kill(entireProcessTree: true);
                }
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(CursorPoint point);

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
