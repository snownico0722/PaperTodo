using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class TodoContextMenuChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string PaperId = "context-menu-todo";
    private const string LinkedPaperId = "context-menu-note";

    internal static void Prepare(AppState state)
    {
        state.Papers.Clear();
        state.EnableAnimations = false;
        state.UseCapsuleMode = false;
        state.UseDeepCapsuleMode = false;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
        state.ExperimentalTodoReminders = true;
        state.ExperimentalTodoReminderShowButton = true;
        state.EnableTodoPaperLinks = true;
        state.ShowTodoBottomBar = true;
        var path = Path.Combine(AppContext.BaseDirectory, "context-menu-target.txt");
        File.WriteAllText(path, "isolated context-menu target");
        var items = new[] { "mouse", "keyboard", "apps", "path", "paper", "reminder", "done" }
            .Select((id, index) => new PaperItem { Id = id, Text = "Todo " + id, Order = index })
            .ToList();
        items[3].LinkPath(path, isDirectory: false);
        items[4].LinkPaper(LinkedPaperId);
        items[5].ReminderAt = DateTimeOffset.Now.AddDays(2);
        items[6].Done = true;
        var area = SystemParameters.WorkArea;
        state.Papers.Add(new PaperData
        {
            Id = PaperId, Type = PaperTypes.Todo, Items = items,
            IsVisible = true, IsCollapsed = false,
            X = area.Left + 40, Y = area.Top + 40, Width = 640, Height = 500
        });
        state.Papers.Add(new PaperData
        {
            Id = LinkedPaperId, Type = PaperTypes.Note, Title = "Linked menu fixture",
            Content = "linked note", IsVisible = false, IsCollapsed = false,
            X = area.Left + 60, Y = area.Top + 60, Width = 300, Height = 240
        });
    }

    internal static async Task Run(AppController controller, IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var window = windows[PaperId];
        var rows = (List<Border>)Part(window, "_todoRows");
        var editors = (Dictionary<string, TodoTextBox>)Part(window, "_todoEditors");
        var selected = (HashSet<string>)Part(window, "_selectedTodoItemIds");
        await Flush();
        LifecycleInput.Focus(window, editors["mouse"]);
        await Flush();
        Require(rows.Count == 7 && rows.All(row => row.ActualHeight > 0), "menu fixture rows were not laid out");

        var owners = rows.SelectMany(Descendants).OfType<FrameworkElement>()
            .Where(element => element.ContextMenu != null).ToArray();
        Require(owners.Length >= rows.Count * 4, "menu fixture did not include the native row menu owners");
        Require(owners.All(owner => owner.ContextMenu!.Items.Count == 0),
            "Todo construction eagerly populated a row context menu");
        var initialThemedMenus = ThemedMenus(window);
        Require(owners.All(owner => !initialThemedMenus.Contains(owner.ContextMenu!)),
            "unopened row placeholders were registered as themed menus");

        var invocations = new List<PaperTodoActionInvocation>();
        controller.SetPluginTodoActions(Guid.NewGuid(), "fixture.context-menu", PaperId, "mouse",
            [new PaperTodoAction
            {
                Id = "menu-action", Text = "Fixture plugin action", Icon = PaperTopBarIcon.Character("+"),
                Placement = PaperTodoActionPlacement.ContextMenu
            }], () => true, invocations.Add);
        await Flush();
        ContextMenu? opened = null;
        TodoTextBox? textMenuOwner = null;
        var keptMenus = new List<ContextMenu>(initialThemedMenus);
        try
        {
            opened = await Open(editors["mouse"], "mouse");
            AssertSingleItemMenu(opened);
            var plugin = Item(opened, "Fixture plugin action");
            plugin.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(invocations.Count == 1 && invocations[0].PaperId == PaperId &&
                    invocations[0].TodoId == "mouse" && invocations[0].Todo.Text == "Todo mouse",
                "the first-open plugin menu did not invoke the current Todo target");
            await Close();

            LifecycleInput.Focus(window, editors["mouse"]);
            editors["mouse"].Select(editors["mouse"].Text.Length, 0);
            LifecycleInput.Text("!");
            await Until(() => editors["mouse"].Text == "Todo mouse!", "editing after the first menu close");
            LifecycleInput.KeyChord(0x11, 0x5A);
            await Until(() => editors["mouse"].Text == "Todo mouse", "native text undo after the first menu close");

            opened = await Open(editors["keyboard"], "shift-f10");
            AssertSingleItemMenu(opened);
            await Close();
            opened = await Open(editors["apps"], "apps");
            AssertSingleItemMenu(opened);
            await Close();

            var row = Row("mouse");
            opened = await Open(row, "mouse", new Point(1, row.ActualHeight / 2));
            AssertSingleItemMenu(opened);
            await Close();
            opened = await Open(((Grid)row.Child).Children.OfType<CheckBox>().Single(), "mouse");
            AssertSingleItemMenu(opened);
            await Close();

            var handle = Descendants(row).OfType<Border>()
                .Single(border => border.Cursor == Cursors.SizeAll);
            opened = await Open((FrameworkElement)handle.Child, "mouse");
            Require(ReferenceEquals(handle.ContextMenu, opened) &&
                    opened.Items.OfType<MenuItem>().Count(item => Header(item) == "Fixture plugin action") == 1,
                "a drag-handle child glyph opened or decorated the row's different menu");
            await Close();

            foreach (var id in new[] { "path", "paper" })
            {
                var link = ((Grid)Row(id).Child).Children.OfType<Border>()
                    .Single(border => Grid.GetColumn(border) == 2);
                opened = await Open(link, "mouse");
                Item(opened, Strings.Get(id == "path" ? "MenuUnlinkPath" : "MenuUnlinkPaper"));
                if (id == "path") Item(opened, Strings.Get("MenuOpenLinkedPathLocation"));
                await Close();
            }

            foreach (var id in new[] { "mouse", "reminder" })
            {
                var reminderHost = ((Grid)Row(id).Child).Children.OfType<Grid>()
                    .Single(grid => Grid.GetColumn(grid) == 3);
                var reminder = reminderHost.Children.OfType<Border>()
                    .Single(border => border.Visibility == Visibility.Visible);
                opened = await Open(reminder, "mouse");
                var submenu = Item(opened, Strings.Get("TodoReminderSet"));
                Require(submenu.IsEnabled, "an unfinished item's reminder menu was disabled");
                submenu.IsSubmenuOpen = true;
                await Until(() => submenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled),
                    "reminder submenu population");
                submenu.IsSubmenuOpen = false;
                await Close();
            }
            opened = await Open(editors["done"], "apps");
            Require(!Item(opened, Strings.Get("TodoReminderSet")).IsEnabled,
                "a completed item's reminder menu was enabled");
            await Close();

            Select("mouse", "keyboard");
            opened = await Open(editors["mouse"], "shift-f10");
            Require(selected.SetEquals(["mouse", "keyboard"]), "keyboard first-open lost the selected group");
            Item(opened, Strings.Get("MenuCopySelectedTodos"));
            var translated = Item(opened, CopyTranslationStrings.Get("MenuCopyAsMarkdown"));
            Require(opened.Items.OfType<MenuItem>().Count(item => Header(item) == Header(translated)) == 1,
                "translated copy was duplicated during lazy construction");
            Require(opened.Items.OfType<MenuItem>().Count(item => Header(item) == "Fixture plugin action") == 1,
                "multi-selection first-open lost or duplicated the plugin action");
            translated.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(Clipboard.GetText() == TodoClipboardFormatter.ToMarkdown(
                    controller.State.Papers.Single(paper => paper.Id == PaperId).Items.Take(2)
                        .Select(item => (item.Text, item.Done))),
                "translated copy did not use the selected current Todos");
            await Close();

            opened = await Open(editors["apps"], "apps");
            Require(selected.Count == 0, "keyboard context menu outside the group retained stale selection");
            AssertSingleItemMenu(opened);
            await Close();

            Select("mouse", "keyboard");
            opened = await Open((FrameworkElement)handle.Child, "mouse");
            var complete = Item(opened, Strings.Get("MenuCompleteSelectedTodos"));
            await Close();
            complete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Flush();
            Require(controller.State.Papers.Single(paper => paper.Id == PaperId).Items
                    .Where(item => item.Id is "mouse" or "keyboard").All(item => item.Done),
                "a lazily constructed selection action did not update both live Todos");
            Console.WriteLine("PASS Todo lazy menus: native mouse, Shift+F10, Apps, child owners, selection, reminders, links, plugin and translated copy");
        }
        finally
        {
            if (opened != null) opened.IsOpen = false;
            selected.Clear();
        }

        Border Row(string id) => rows.Single(row => row.Tag as string == id);
        void Select(params string[] ids)
        {
            selected.Clear();
            selected.UnionWith(ids);
            foreach (var editor in editors.Values) editor.Select(0, 0);
            typeof(PaperWindow).GetMethod("ApplyTodoSelectionVisuals", Private)!.Invoke(window, null);
        }
        async Task<ContextMenu> Open(FrameworkElement target, string input, Point? point = null)
        {
            var owner = ContextMenuOwner(target);
            Require(owner != null, "context-menu test target had no owner");
            if (input != "mouse") LifecycleInput.Focus(window, target);
            else LifecycleInput.Focus(window, null);
            await Flush();
            var before = ThemedMenus(window);
            var requested = owner!.ContextMenu!;
            var wasInitialized = before.Contains(requested);
            textMenuOwner = owner as TodoTextBox;
            keptMenus.AddRange(before); // keep weak registrations stable while checking one opening
            if (input == "mouse")
            {
                var localPoint = point ?? new Point(target.ActualWidth / 2, target.ActualHeight / 2);
                var screenPoint = target.PointToScreen(localPoint);
                // Startup first shows a transparent HWND. Wait until the requested
                // screen point belongs to this paper before sending any mouse input.
                await Until(() => LifecycleInput.IsMouseTarget(window, screenPoint),
                    "native context-menu target window is ready");
                // Inject actual movement, then let WPF establish the target before
                // injecting buttons. Moving the cursor alone can leave DirectlyOver null.
                LifecycleInput.MoveCursor(screenPoint);
                await Until(() => ReferenceEquals(ContextMenuOwner(Mouse.DirectlyOver as DependencyObject), owner),
                    "native cursor reaches the requested context-menu owner");
                LifecycleInput.RightClick();
            }
            else if (input == "shift-f10") LifecycleInput.KeyChord(0x10, 0x79);
            else LifecycleInput.KeyChord(0x5D);
            await Until(() => owner.ContextMenu?.IsOpen == true, input + " first context-menu opening");
            var menu = owner!.ContextMenu!;
            await Flush();
            Require(ReferenceEquals(menu, requested), input + " replaced the menu after WPF installed its close handler");
            Require(ThemedMenus(window).Except(before).Count() == (wasInitialized ? 0 : 1),
                input + " registered more than the requested owner's first themed menu");
            if (textMenuOwner != null)
                Require(TextEditorMenuIsOpen(textMenuOwner), input + " bypassed WPF's text-editor context-menu state");
            keptMenus.Add(menu);
            Require(menu.Items.Count > 0, input + " displayed the empty placeholder");
            return menu;
        }
        async Task Close()
        {
            if (opened == null) return;
            if (opened.IsOpen)
            {
                LifecycleInput.KeyChord(0x1B);
                await Until(() => !opened.IsOpen, "native context-menu close");
            }
            opened = null;
            await Flush();
            if (textMenuOwner != null)
                Require(!TextEditorMenuIsOpen(textMenuOwner), "the closed menu stranded WPF's text-editor context-menu state");
            textMenuOwner = null;
        }
    }

    private static void AssertSingleItemMenu(ContextMenu menu)
    {
        Item(menu, Strings.Get("MenuTodoItem"));
        Item(menu, Strings.Get("MenuDeleteItem"));
        Item(menu, Strings.Get("MenuClearDone"));
    }

    private static MenuItem Item(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().SingleOrDefault(item => Header(item) == header) ??
        throw new InvalidOperationException("Missing context menu action: " + header);

    private static string? Header(MenuItem item) => item.Header is TextBlock text ? text.Text : item.Header as string;
    private static bool TextEditorMenuIsOpen(TodoTextBox textBox)
    {
        var editor = typeof(TextBoxBase).GetProperty("TextEditor", Private)!.GetValue(textBox)!;
        return (bool)editor.GetType().GetProperty("IsContextMenuOpen", Private)!.GetValue(editor)!;
    }
    private static object Part(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static List<ContextMenu> ThemedMenus(PaperWindow window) =>
        ((List<WeakReference<ContextMenu>>)Part(window, "_themedContextMenus"))
            .Select(reference => reference.TryGetTarget(out var menu) ? menu : null)
            .OfType<ContextMenu>().ToList();

    private static FrameworkElement? ContextMenuOwner(DependencyObject? target)
    {
        while (target != null)
        {
            if (target is FrameworkElement { ContextMenu: not null } owner) return owner;
            target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static async Task Flush() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);

    private static async Task Until(Func<bool> ready, string name)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 4) throw new TimeoutException(name);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal static class LifecycleInput
{
    internal static void Focus(PaperWindow window, IInputElement? element)
    {
        window.Activate();
        SetForegroundWindow(new WindowInteropHelper(window).Handle);
        if (element != null) Keyboard.Focus(element);
    }

    internal static void KeyChord(params ushort[] keys)
    {
        var input = keys.Select(key => Key(key, up: false))
            .Concat(keys.Reverse().Select(key => Key(key, up: true))).ToArray();
        Send(input);
    }

    internal static void MoveCursor(Point point)
    {
        var left = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var top = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        var width = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        var height = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (width <= 0 || height <= 0) throw new InvalidOperationException("No virtual desktop for native input.");
        // As in WPF's UIAutomation input provider, target the center of the desired
        // pixel's normalized range so truncation cannot move into an adjacent pixel.
        var x = (int)Math.Clamp((Math.Round(point.X) - left + 0.5) * 65536 / width, 0, 65535);
        var y = (int)Math.Clamp((Math.Round(point.Y) - top + 0.5) * 65536 / height, 0, 65535);
        Send([new Input { Type = 0, Data = new InputData { Mouse = new MouseInput
        {
            X = x, Y = y, Flags = 0x0001 | 0x8000 | 0x4000 // MOVE | ABSOLUTE | VIRTUALDESK
        } } }]);
    }

    internal static bool IsMouseTarget(PaperWindow window, Point point) =>
        WindowFromPoint(new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) }) ==
        new WindowInteropHelper(window).Handle;

    internal static void RightClick()
    {
        Send([new Input { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = 0x0008 } } },
            new Input { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = 0x0010 } } }]);
    }

    internal static void Text(string value) => Send(value.SelectMany(character => new[]
    {
        new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { ScanCode = character, Flags = 0x0004 } } },
        new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { ScanCode = character, Flags = 0x0006 } } }
    }).ToArray());

    private static Input Key(ushort key, bool up) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput
        {
            VirtualKey = key, Flags = (up ? 0x0002u : 0u) | (key == 0x5D ? 0x0001u : 0u)
        } }
    };

    private static void Send(Input[] input)
    {
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length)
            throw new InvalidOperationException("Native fixture input injection failed: " + Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
}
