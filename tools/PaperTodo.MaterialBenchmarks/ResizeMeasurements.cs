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
        GetCursorPos(out var oldCursor);
        var canRestore = ReadFullWindowDrag(38,0,out var initialDrag,0);
        Program.Assert(canRestore && SetFullWindowDrag(37,1,IntPtr.Zero,2), "enable live sizing");
        var hiRes = timeBeginPeriod(1) == 0;
        var samples = new List<object>();
        var paper = new PaperData { Type = PaperTypes.Note, Title = "Empty note", Content = "",
            X = 110, Y = 80, Width = 365, Height = 240, AlwaysOnTop = true };
        controller.State.Papers.Add(paper);
        try
        {
            // Match the application's actual window settings on this runner.
            var transparent = !controller.UsesNativeMicaWindows;
            var kinds = new[] { "paper", "plain", "matched" };
            var axes = new[] { "idle", "bottom", "right", "corner" };
            for (var round = -1; round < 5; round++)
            {
                for (var k = 0; k < kinds.Length; k++)
                {
                    var kind = kinds[(round + 1 + k) % kinds.Length];
                    Window w = kind == "paper" ? new PaperWindow(paper, controller) : new Window();
                    try
                    {
                        w.WindowStartupLocation = WindowStartupLocation.Manual;
                        w.Left = 110; w.Top = 80; w.Width = 365; w.Height = 240;
                        if (kind != "paper")
                        {
                            w.Title = "Empty WPF control"; w.Topmost = true; w.ShowInTaskbar = false;
                            if (kind == "matched")
                            {
                                w.WindowStyle = WindowStyle.None; w.AllowsTransparency = transparent;
                                w.Background = Brushes.Transparent;
                                w.Content = new System.Windows.Controls.Border { Background = Brushes.White, Margin = new Thickness(8) };
                                w.SourceInitialized += (_, _) => InstallHitTest(w);
                            }
                            else w.Background = Brushes.White;
                        }
                        w.Show(); w.Activate(); Wait(340);
                        if (kind == "paper")
                            Program.Assert(Descendants(w).OfType<MarkdownTextBox>().Any(e => string.IsNullOrEmpty(e.Text.TrimEnd('\r','\n'))), "empty editor visible");
                        if (round < 0) Console.WriteLine($"CONTROL {kind} layered={w.AllowsTransparency} style={w.WindowStyle} dpi={VisualTreeHelper.GetDpi(w).DpiScaleX}");
                        for (var axisIndex=0; axisIndex<4; axisIndex++)
                        {
                            var axis = axes[(axisIndex + round + 1) % 4];
                            w.Width=365; w.Height=240; Wait(140);
                            if (axis == "idle") { SetCursorPos(65,55); Wait(50); }
                            var row = ResizeOnce(w, axis == "idle" ? "idle" : "mouse", axis, kind, false, round);
                            if (row == null) throw new Exception($"Invalid real input: {kind}/{axis}/{round}");
                            if (round >= 0)
                            {
                                samples.Add(row);
                                Console.WriteLine("RESIZE " + JsonSerializer.Serialize(row));
                            }
                        }
                    }
                    finally
                    {
                        if (w is PaperWindow p) p.CloseForReal(); else w.Close();
                        Wait(60);
                    }
                }
            }
            File.WriteAllText(file, JsonSerializer.Serialize(samples));
        }
        finally
        {
            controller.State.Papers.Remove(paper); mouse_event(4,0,0,0,UIntPtr.Zero);
            SetCursorPos(oldCursor.X,oldCursor.Y); if (hiRes) timeEndPeriod(1);
            SetFullWindowDrag(37,(uint)initialDrag,IntPtr.Zero,2);
        }
    }

    private static void InstallHitTest(Window w)
    {
        var src = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)!;
        src.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) => {
            if (msg != 0x84 || !GetWindowRect(h, out var r)) return IntPtr.Zero;
            int x=(short)(lp.ToInt64() & 0xffff), y=(short)((lp.ToInt64() >> 16) & 0xffff);
            bool right=x>=r.Right-14 && x<r.Right, bottom=y>=r.Bottom-14 && y<r.Bottom;
            if (!right && !bottom) return IntPtr.Zero;
            handled=true; return new IntPtr(right && bottom ? 17 : right ? 11 : 15);
        });
    }

    private static object? ResizeOnce(Window w, string mode, string axis, string note, bool gate, int round)
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
        var trackingError = new List<double>(160);
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

                        Thread.Sleep(8);
                        if (mode == "mouse" && GetWindowRect(hwnd, out var observed))
                            trackingError.Add(Math.Max(horizontal ? Math.Abs(observed.Right-start.Right-d) : 0,
                                vertical ? Math.Abs(observed.Bottom-start.Bottom-d) : 0));
                    }
                }
                finally { if (mode == "mouse") mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
            });
            while (!worker.IsCompleted && clock.Elapsed.TotalSeconds < 12) Wait(8);
            if (!worker.IsCompleted) throw new TimeoutException("Resize driver stalled");
            worker.GetAwaiter().GetResult(); Wait(40);
            var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
            bool moved = (horizontal ? excursionX >= 90 : excursionX <= 2) && (vertical ? excursionY >= 90 : excursionY <= 2);
            if (mode == "mouse" ? (!moved || sizes.Count < 15 || enter == 0 || leave == 0 || held < 120) : (sizes.Count != 0 || enter != 0 || leave != 0))
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
                slow25=frameGaps.Count(t=>t>25), slowPercent=Math.Round(100d * frameGaps.Count(t=>t>25)/Math.Max(frameGaps.Length,1),1),
                trackP95=Q(trackingError.ToArray(),.95), duration=inputs[^1]-inputs[0], topCalls=PaperWindow.ProbeCalls,
                topMs=Math.Round(PaperWindow.ProbeTicks*1000d/Stopwatch.Frequency,3), cpuMs=Math.Round(cpuMs,2) };
        }
        finally { source.RemoveHook(hook); w.LayoutUpdated -= updated; CompositionTarget.Rendering -= rendering; }
    }
}
