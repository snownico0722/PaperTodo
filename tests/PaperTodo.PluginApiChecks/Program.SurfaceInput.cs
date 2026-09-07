using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void PaperMenuVisibleHost()
    {
        // Keep a real original window hidden while a separate host presents its Paper menu.
        // Exercise the production menu renderer and WPF's real menu-invocation/close sequence.
        var (hiddenPaper, _) = Owner();
        hiddenPaper.Hide();
        var (visibleHost, target) = Owner();
        var controller = Controller(new AppState
        {
            Papers = [new() { Id = "edge-paper", Title = "Edge", Type = PaperTypes.Note }]
        });
        using var runtime = new PaperPluginRuntimeWorkspaceApi(controller, "test.plugin",
            [PaperTodoPermissionNames.PapersRead], () => true);
        var actions = (IPaperPluginPaperActions)runtime;
        PaperActionInvocation? invocation = null;
        actions.SetActionHandler(value => invocation = value);
        actions.SetActions("edge-paper", [Action("panel", 1)]);
        var menu = new ContextMenu { PlacementTarget = target };
        PaperWindow.AttachPluginPaperMenuActions(menu, controller, "edge-paper");
        menu.IsOpen = true;
        Pump();
        var item = menu.Items.OfType<MenuItem>().Single();
        var peer = new MenuItemAutomationPeer(item);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        PumpUntil(() => invocation != null && !menu.IsOpen);
        Pump(); // The MenuItem may now be gone; placement must survive on the visible host.
        Assert(invocation!.Paper.Id == "edge-paper" && invocation.Anchor != null,
            "The hidden paper's menu did not return a visible-host anchor.");
        Assert(!hiddenPaper.IsVisible, "Opening the menu revealed the original Paper window.");
        var handle = runtime.OpenPopup(invocation.Anchor!, new() { Id = "edge-panel" }, _ => new Content());
        Assert(handle.IsOpen, "The visible menu owner could not open an anchored panel.");
        visibleHost.Hide();
        Pump();
        Assert(!handle.IsOpen, "Hiding the actual visible owner did not dismiss its panel.");
        hiddenPaper.Close();
        visibleHost.Close();
    }

    private static void PopupKeyboard()
    {
        var (owner, button) = Owner();
        button.Focus();
        var anchors = new PluginUiAnchorStore();
        var lease = Guid.NewGuid();
        using var host = Host(anchors, lease, owner);
        var content = new Content(); // TextBlock only; no tab stops.
        var handle = host.OpenPopup(anchors.Capture(lease, "p", button, owner, false)!,
            new() { Id = "read-only" }, _ => content);
        Pump();
        var frame = (Border)VisualTreeHelper.GetParent(content.View);
        Assert(frame.IsKeyboardFocusWithin && ReferenceEquals(Keyboard.FocusedElement, frame),
            "Read-only popup left keyboard focus on the source window instead of its shell.");
        PressEscape();
        PumpUntil(() => !handle.IsOpen);
        Assert(content.DisposeCount == 1, "Keyboard dismissal did not dispose the read-only content.");

        var combo = new ComboBox { ItemsSource = new[] { "A", "B" }, SelectedIndex = 0 };
        handle = host.OpenPopup(anchors.Capture(lease, "p", button, owner, false)!,
            new() { Id = "select" }, context =>
            {
                context.Controls.ApplySelectStyle(combo, 12);
                return new Content(combo);
            });
        Pump();
        Assert(combo.IsKeyboardFocusWithin,
            $"Popup did not focus its interactive child (focused: {Keyboard.FocusedElement?.GetType().Name}, open: {handle.IsOpen}).");
        combo.IsDropDownOpen = true;
        Pump();
        PressEscape();
        PumpUntil(() => !combo.IsDropDownOpen);
        Assert(handle.IsOpen, "First real Escape closed the outer popup instead of its dropdown.");
        PressEscape();
        PumpUntil(() => !handle.IsOpen);

        // Independent windows must leave Escape to their content on the Native path too.
        var editor = new TextBox { Text = "unsaved" };
        var window = host.OpenWindow(new() { Id = "editor" }, _ => new Content(editor));
        editor.Focus();
        Pump();
        PressEscape();
        Pump();
        Assert(window.IsOpen && editor.Text == "unsaved", "Window inherited popup Escape behavior.");
        window.Close();
        owner.Close();
    }

    // Send actual Windows keyboard input to the focused HWND, not RaiseEvent on a chosen child.
    private static void PressEscape()
    {
        var inputs = new[]
        {
            new NativeInput { Type = 1, Data = new() { Keyboard = new() { VirtualKey = 0x1B } } },
            new NativeInput { Type = 1, Data = new() { Keyboard = new() { VirtualKey = 0x1B, Flags = 2 } } }
        };
        Assert(SendInput(2, inputs, Marshal.SizeOf<NativeInput>()) == 2,
            $"SendInput failed: {Marshal.GetLastWin32Error()}");
        Pump();
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public NativeInputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputData
    {
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
        [FieldOffset(0)] public NativeMouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    private static void WebEscape()
    {
        using var temp = new Temp();
        File.WriteAllText(Path.Combine(temp.Path, "popup.js"),
            WebPluginSurfaceContent.BridgeScript("https://surface.test", closeOnEscape: true), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(temp.Path, "window.js"),
            WebPluginSurfaceContent.BridgeScript("https://surface.test", closeOnEscape: false), new UTF8Encoding(false));
        var harness = Path.Combine(temp.Path, "escape.cjs");
        File.WriteAllText(harness, """
            const vm = require('node:vm'), fs = require('node:fs'), path = require('node:path');
            const assert = require('node:assert/strict');
            async function check(kind, cancel, composing, expected) {
              const keys = [], messages = [], tasks = [], posted = [];
              const scope = {
                location:{origin:'https://surface.test'},
                document:{documentElement:{style:{setProperty(){}}}},
                chrome:{webview:{postMessage:value=>posted.push(value), addEventListener:(_,fn)=>messages.push(fn)}},
                addEventListener:(type,fn)=>{ if(type==='keydown') keys.push(fn); },
                queueMicrotask,
                setTimeout:fn=>tasks.push(fn)
              };
              scope.window=scope; scope.top=scope;
              vm.runInNewContext(fs.readFileSync(path.join(__dirname,kind+'.js'),'utf8'),scope);
              for(const fn of messages) fn({data:{type:'initialize',token:'t',theme:{},data:{}}});
              // Register plugin code AFTER host injection, just like a real local HTML entry.
              keys.push(e=>{ if(cancel) e.preventDefault(); });
              const event = {key:'Escape',isComposing:composing,defaultPrevented:false,
                preventDefault(){this.defaultPrevented=true;}};
              for(const fn of keys) {
                fn(event);
                // Browsers may perform a microtask checkpoint between native event callbacks.
                await Promise.resolve();
              }
              for(const task of tasks) task();
              assert.equal(posted.filter(x=>x.method==='surface.close').length,expected,
                `${kind}: cancel=${cancel}, composing=${composing}`);
            }
            (async()=>{
              await check('popup',true,false,0);
              await check('popup',false,false,1);
              await check('popup',false,true,0);
              await check('window',false,false,0);
              await check('window',true,false,0);
            })().catch(e=>{console.error(e);process.exitCode=1;});
            """, new UTF8Encoding(false));
        var start = new ProcessStartInfo("node")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(harness);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(true); throw new TimeoutException("Web Escape check timed out."); }
        Assert(process.ExitCode == 0, error.GetAwaiter().GetResult());
    }
}
