using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;

internal static partial class MaterialDragBenchmarks
{
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flag);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr order, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
    private static extern bool ReadFullWindowDrag(uint action, uint value, out int result, uint flags);
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
    private static extern bool SetFullWindowDrag(uint action, uint value, IntPtr unused, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    internal static void RunResize(AppController controller, string file)
    {
        var renderTier = RenderCapability.Tier >> 16;
        Console.WriteLine($"ENV OS={RuntimeInformation.OSDescription} .NET={Environment.Version} tier={renderTier} screen={SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight} cpu={Environment.ProcessorCount}");
        controller.State.PaperSkin = PaperSkins.Paper;
        Theme.Invalidate();
        var notes = new[] { "", "整理本周要交付的内容，先把需要确认的事项记下来，明天开会时逐条过一遍。\n预览窗口改动，也看一下几个常用按钮。\n检查文字在不同宽度下如何换行，再把结论发给同事。\n周三下午预留时间整理反馈。\n最后确认下周的安排。" };
        GetCursorPos(out var oldCursor);
        var couldRead = ReadFullWindowDrag(38,0,out var initialDrag,0);
        var enabled = couldRead && SetFullWindowDrag(37,1,IntPtr.Zero,2);
        ReadFullWindowDrag(38,0,out var activeDrag,0);
        Console.WriteLine($"DRAG SETTING initial={initialDrag} enabled={enabled} current={activeDrag}");
        var hiRes = timeBeginPeriod(1) == 0;
        var samples = new List<object>();
        try
        {
            foreach (var mode in new[] { "mouse", "replay" })
            {
                var valid = true;
                var sampleCount = samples.Count;
                foreach (var note in notes)
                {
                    var paper = new PaperData { Type = PaperTypes.Note, Title = "Resize measurement", Content = note,
                        X = 110, Y = 80, Width = 365, Height = 240, AlwaysOnTop = true };
                    controller.State.Papers.Add(paper);
                    var window = new PaperWindow(paper, controller);
                    try
                    {
                        window.Show(); window.Activate(); Wait(500);
                        var editors = Descendants(window).OfType<MarkdownTextBox>().ToArray();
                        Program.Assert(editors.Any(editor => editor.Text.Replace("\r", "").TrimEnd('\n') == note),
                            $"note editor loaded with expected text ({editors.Length} editors, {note.Length} chars)");
                        // One warm-up per direction. Rotate order over measured rounds, alternating bottom A/B order.
                        for (var round = -1; round < 3 && valid; round++)
                        {
                            var seq = round switch { 0 => new[] { 0, 1, 2, 3 }, 1 => new[] { 3, 2, 1, 0 },
                                2 => new[] { 1, 2, 0, 3 }, _ => new[] { 2, 1, 0, 3 } };
                            foreach (var index in seq)
                            {
                                var axis = new[] { "bottom", "right", "corner", "bottom" }[index];
                                PaperWindow.ProbeWidthOnly = index == 3;
                                window.Width = 365; window.Height = 240; Wait(130);
                                var sample = ResizeOnce(window, mode, axis, note.Length == 0 ? "empty" : "text", index == 3, round);
                                if (sample == null)
                                {
                                    valid = false;
                                    Console.WriteLine($"INVALID mode={mode} axis={axis} round={round}");
                                    break;
                                }
                                if (round >= 0) { samples.Add(sample); Console.WriteLine("RESIZE " + JsonSerializer.Serialize(sample)); }
                            }
                        }
                    }
                    finally { window.CloseForReal(); controller.State.Papers.Remove(paper); Wait(80); }
                    if (!valid) break;
                }
                if (valid) { File.WriteAllText(file, JsonSerializer.Serialize(samples)); return; }
                samples.RemoveRange(sampleCount, samples.Count - sampleCount);
                if (mode == "replay") throw new InvalidOperationException("Could not resize the live PaperWindow.");
            }
        }
        finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(oldCursor.X, oldCursor.Y); if (hiRes) timeEndPeriod(1); if (enabled) SetFullWindowDrag(37,(uint)initialDrag,IntPtr.Zero,2); }
    }

    private static object? ResizeOnce(PaperWindow w, string mode, string axis, string note, bool gate, int round)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var source = HwndSource.FromHwnd(hwnd)!;
        GetWindowRect(hwnd, out var start);
        int width = start.Right - start.Left, height = start.Bottom - start.Top;
        bool horizontal = axis != "bottom", vertical = axis != "right";
        var point = new NativePoint();
        if (mode == "mouse")
        {
            var expected = axis == "bottom" ? 15 : axis == "right" ? 11 : 17;
            var found = false;
            for (var d = 1; d <= 32 && !found; d++)
            {
                point.X = horizontal ? start.Right - d : start.Left + width / 2;
                point.Y = vertical ? start.Bottom - d : start.Top + height / 2;
                var at = WindowFromPoint(point);
                var packed = new IntPtr((point.Y << 16) | (point.X & 0xffff));
                found = (at == hwnd || GetAncestor(at, 2) == hwnd) && SendMessage(hwnd, 0x84, IntPtr.Zero, packed).ToInt64() == expected;
            }
            if (!found || !SetCursorPos(point.X, point.Y)) return null;
            Wait(40);
        }
        var sizes = new List<double>(160);
        var renders = new List<double>(160);
        var inputs = new List<double>(160);
        var sizingMessages = 0; var mouseX = 0; var mouseY = 0; var held = 0; var enter = 0; var leave = 0; var layout = 0; var excursionX = 0; var excursionY = 0;
        var clock = Stopwatch.StartNew();
        var previousRender = TimeSpan.MinValue;
        EventHandler rendering = (_, ev) => {
            var time = ((RenderingEventArgs)ev).RenderingTime;
            if (time != previousRender) renders.Add(clock.Elapsed.TotalMilliseconds);
            previousRender = time;
        };
        EventHandler updated = (_, _) => layout++;
        HwndSourceHook hook = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) => {
            if (msg == 0x214) sizingMessages++;
            if (msg == 0x231) enter++;
            if (msg == 0x232) leave++;
            if (msg == 5 && GetWindowRect(h, out var r))
            {
                sizes.Add(clock.Elapsed.TotalMilliseconds);
                excursionX = Math.Max(excursionX, Math.Abs(r.Right-r.Left-width));
                excursionY = Math.Max(excursionY, Math.Abs(r.Bottom-r.Top-height));
            }
            return IntPtr.Zero;
        };
        source.AddHook(hook); w.LayoutUpdated += updated; CompositionTarget.Rendering += rendering;
        PaperWindow.ProbeCalls = PaperWindow.ProbeTicks = 0;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        try
        {
            var worker = Task.Run(() => {
                try
                {
                    if (mode == "mouse") { mouse_event(2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(30); }
                    for (var i = 1; i <= 128; i++)
                    {
                        var d = (i <= 64 ? i * 2 : 128 - (i - 64));
                        inputs.Add(clock.Elapsed.TotalMilliseconds);
                        if (mode == "mouse")
                        {
                            if (!SetCursorPos(point.X + (horizontal ? d : 0), point.Y + (vertical ? d : 0)))
                                throw new InvalidOperationException("SetCursorPos failed during drag");
                            if ((GetAsyncKeyState(1) & 0x8000) != 0) held++;
                            if (GetCursorPos(out var actual))
                            {
                                mouseX = Math.Max(mouseX, Math.Abs(actual.X-point.X));
                                mouseY = Math.Max(mouseY, Math.Abs(actual.Y-point.Y));
                            }
                        }
                        else if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width + (horizontal ? d : 0),
                            height + (vertical ? d : 0), 0x4016)) throw new InvalidOperationException("SetWindowPos failed");
                        Thread.Sleep(8);
                    }
                }
                finally { if (mode == "mouse") mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
            });
            while (!worker.IsCompleted && clock.Elapsed.TotalSeconds < 12) Wait(8);
            if (!worker.IsCompleted) throw new TimeoutException("Resize driver stalled");
            worker.GetAwaiter().GetResult(); Wait(40);
            var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
            bool moved = (horizontal ? excursionX >= 90 : excursionX <= 2) && (vertical ? excursionY >= 90 : excursionY <= 2);
            if (!moved || sizes.Count < 15 || mode == "mouse" && (enter == 0 || leave == 0))
            {
                Console.WriteLine($"CHECK mode={mode} axis={axis} changes={sizes.Count} excursion={excursionX},{excursionY} enter={enter} leave={leave} sizing={sizingMessages} button={held} cursor={mouseX},{mouseY}");
                return null;
            }
            var r = renders.Where(t => t >= inputs[0] && t <= inputs[^1]).ToArray();
            var frameGaps = r.Zip(r.Skip(1), (a,b) => b-a).ToArray();
            var sizeGaps = sizes.Zip(sizes.Skip(1), (a,b) => b-a).ToArray();
            var inputGaps = inputs.Zip(inputs.Skip(1), (a,b) => b-a).ToArray();
            double Q(double[] a, double q) => Math.Round(Percentile(a,q), 2);
            return new { mode, note, axis, gate, round, nSize=sizes.Count, nRender=r.Length, enter, leave, sizingMessages, held, mouseX, mouseY, layout,
                inputP50=Q(inputGaps,.5), sizeP95=Q(sizeGaps,.95), frameP50=Q(frameGaps,.5), frameP95=Q(frameGaps,.95),
                slow25=frameGaps.Count(t=>t>25), topCalls=PaperWindow.ProbeCalls,
                topMs=Math.Round(PaperWindow.ProbeTicks*1000d/Stopwatch.Frequency,3), cpuMs=Math.Round(cpuMs,2) };
        }
        finally { source.RemoveHook(hook); w.LayoutUpdated -= updated; CompositionTarget.Rendering -= rendering; }
    }
}
