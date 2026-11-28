using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class InjectionSystemRunner
{
    private static async Task UntilJourney(Func<bool> condition, string reason, int timeoutMs = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException(reason + $"; keyboard={Keyboard.FocusedElement?.GetType().Name}, mouse={Mouse.DirectlyOver?.GetType().Name}, capture={Mouse.Captured?.GetType().Name}");
            await Task.Delay(15);
        }
        Check(true, reason);
    }
    private static IEnumerable<DependencyObject> JourneyDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in JourneyDescendants(child)) yield return nested;
        }
    }
    private static void JourneyLog(string message) => File.AppendAllText(
        Path.Combine(AppContext.BaseDirectory, "journey.log"), $"pid={Environment.ProcessId} {message}\n");

    private static void CaptureJourney(FrameworkElement view, string name)
    {
        var output = Environment.GetEnvironmentVariable("PAPERTODO_INJECTION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var mode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, Marker));
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(view.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(view.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(output, mode + "-" + name + ".png"));
        encoder.Save(stream);
    }

    // Writes the actual file, then presses the real plugin Apply button through the OS input queue.
    // This deliberately does not call ApplyUserInjection or synthesize a WPF Click routed event.
    private static async Task ExternalJourneyApply(GoodLockView view, params (string Id, object Value)[] values)
    {
        var json = JsonSerializer.Serialize(values.ToDictionary(v => v.Id, v => J(v.Value)));
        File.WriteAllText(UserInjection.FilePath, json);
        view.Window.Topmost = true;
        view.Window.Show(); view.Window.Activate();
        view.Session.OnActivated(); // Session lifecycle callback supplied by this isolated host.
        var button = (Button)view.View.FindName("ApplyButton");
        var clicked = false;
        RoutedEventHandler observe = (_, _) => clicked = true;
        button.Click += observe;
        try
        {
            await JourneyInput.Click(button);
            await UntilJourney(() => clicked, "Native click reaches Good Lock Apply");
            Check(File.ReadAllText(UserInjection.FilePath) == json, "External Apply rewrote user.json.");
            Check(UserInjection.Read().Parameters.Where(p => p.HasFileOverride).All(p => p.Status is "applied" or "restart"),
                "Native Apply left a requested parameter pending.");
        }
        finally { button.Click -= observe; view.Window.Hide(); }
        JourneyLog("APPLY " + json);
        await Settle();
    }

    private static class JourneyInput
    {
        internal static async Task Aim(FrameworkElement element, bool activate = true)
        {
            Check(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, "Native input target must be laid out.");
            var window = Window.GetWindow(element)!;
            if (activate)
            {
                window.Activate();
                _ = JSetForegroundWindow(new WindowInteropHelper(window).Handle);
            }
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            Move(point);
            await UntilJourney(() => element.IsMouseOver && PresentationSource.FromVisual(element) is HwndSource source &&
                JWindowFromPoint(new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) }) == source.Handle,
                "Native pointer hits " + element.GetType().Name);
        }
        internal static async Task Click(FrameworkElement element, bool activate = true)
        {
            await Aim(element, activate);
            Button(true); Button(false);
            await Settle();
        }
        internal static void Move(Point point)
        {
            var width = JGetSystemMetrics(78); var height = JGetSystemMetrics(79);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("No interactive desktop.");
            Send([new Input { Data = new InputData { Mouse = new MouseInput
            {
                X = (int)Math.Clamp((Math.Round(point.X) - JGetSystemMetrics(76) + .5) * 65536 / width, 0, 65535),
                Y = (int)Math.Clamp((Math.Round(point.Y) - JGetSystemMetrics(77) + .5) * 65536 / height, 0, 65535),
                Flags = 0xC001
            } } }]);
        }
        internal static void Button(bool down) => Send([new Input { Data = new InputData { Mouse = new MouseInput { Flags = down ? 2u : 4u } } }]);
        internal static void Chord(params ushort[] keys) => Send(keys.Select(k => Key(k, false)).Concat(keys.Reverse().Select(k => Key(k, true))).ToArray());
        internal static void Release() => Send([new Input { Data = new InputData { Mouse = new MouseInput { Flags = 4 } } }, Key(0x11, true), Key(0x10, true), Key(0x12, true)]);
        private static Input Key(ushort key, bool up) => new() { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } } };
        private static void Send(Input[] input)
        {
            if (JSendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length)
                throw new InvalidOperationException("SendInput failed: " + Marshal.GetLastWin32Error());
        }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputData
        { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] private static extern uint JSendInput(uint count, Input[] input, int size);
        [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool JSetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")] private static extern int JGetSystemMetrics(int index);
        [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern IntPtr JWindowFromPoint(NativePoint point);
    }
}
