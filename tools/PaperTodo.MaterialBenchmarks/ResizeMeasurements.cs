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
            bool platform = Environment.GetEnvironmentVariable("PAPER_RESIZE_SUITE") == "platform";
            var kinds = platform ? new[] { "win32","wpf","matched","paper" } :
                new[] { "paper","observed","topDiff","noHeader","chromeOnly","flat" };
            var axes = platform ? new[] { "corner","fastCorner" } : new[] { "bottom","corner" };
            PaperWindow? shared = null;
            if (!platform)
            {
                controller.State.McpEnabled = false;
                controller.State.TelemetryEnabled = false;
                controller.State.UsePersistentPowerShellProcess = false;
                var startup = controller.StartAsync(createDefaultPaper: false);
                while (!startup.IsCompleted) Wait(16);
                startup.GetAwaiter().GetResult();
                shared = Application.Current.Windows.OfType<PaperWindow>().Single(x => x.PaperId == paper.Id);
                shared.Activate(); Wait(500);
                Console.WriteLine($"BOOTED actual={shared.IsVisible} layered={shared.AllowsTransparency}");
            }
            try
            {
                for (var round = -1; round<5; round++)
                for (var j=0; j<kinds.Length; j++)
                {
                    var kind=kinds[(round+1+j)%kinds.Length];
                    Window? w = null;
                    IntPtr hwnd=IntPtr.Zero;
                    Action? restore=null;
                    try
                    {
                        if (!platform)
                        {
                            w=shared!;
                            restore=ConfigurePaper(shared!,kind);
                        }
                        else if (kind=="win32") hwnd=OpenNative();
                        else
                        {
                            w=kind=="paper" ? new PaperWindow(paper,controller) : new Window();
                            w.WindowStartupLocation=WindowStartupLocation.Manual;
                            w.Left=110; w.Top=80; w.Width=365; w.Height=240;
                            if (kind!="paper")
                            {
                                w.Title="Empty WPF"; w.Topmost=true; w.ShowInTaskbar=false;
                                if (kind=="matched")
                                {
                                    w.WindowStyle=WindowStyle.None; w.AllowsTransparency=!controller.UsesNativeMicaWindows;
                                    w.Background=Brushes.Transparent; w.Content=EmptySurface();
                                    w.SourceInitialized+=(_,_)=>InstallHitTest(w);
                                }
                                else w.Background=Brushes.White;
                            }
                            w.Show(); w.Activate();
                        }
                        if (w!=null) hwnd=new WindowInteropHelper(w).Handle;
                        Wait(250);
                        for (var a=0;a<axes.Length;a++)
                        {
                            var axis=axes[(a+round+1)%axes.Length];
                            Program.Assert(SetWindowPos(hwnd,IntPtr.Zero,110,80,365,240,0x14),"reset HWND");
                            Wait(140);
                            var row=ResizeOnce(hwnd,w,platform,axis,kind,round);
                            if (row==null) throw new Exception($"Invalid native drag: {kind}/{axis}/{round}");
                            if (round>=0)
                            {
                                samples.Add(row);
                                Console.WriteLine("RESIZE "+JsonSerializer.Serialize(row));
                            }
                        }
                    }
                    finally
                    {
                        restore?.Invoke();
                        if (platform)
                        {
                            if (w is PaperWindow pw) pw.CloseForReal();
                            else if(w!=null) w.Close();
                            else if(hwnd!=IntPtr.Zero) DestroyWindow(hwnd);
                        }
                        Wait(60);
                    }
                }
            }
            finally { shared?.CloseForReal(); }
            File.WriteAllText(file, JsonSerializer.Serialize(samples));
        }
        finally
        {
            controller.State.Papers.Remove(paper); mouse_event(4,0,0,0,UIntPtr.Zero);
            SetCursorPos(oldCursor.X,oldCursor.Y); if (hiRes) timeEndPeriod(1);
            SetFullWindowDrag(37,(uint)initialDrag,IntPtr.Zero,2);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowCallback(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassCallback(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, UIntPtr data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint style;
        public IntPtr procedure;
        public int classBytes, windowBytes;
        public IntPtr instance,icon,cursor,background;
        public string? menu,name;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern ushort RegisterClassW(ref WindowClass wc);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string name, uint style, int x,int y,int width,int height,
        IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr h,IntPtr id);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr h,SubclassCallback cb, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr h,SubclassCallback cb, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr h,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr h, out long creation,out long exit,out long kernel,out long user);
    private static readonly WindowCallback NativeProcedure = (h,m,wp,lp) => DefWindowProcW(h,m,wp,lp);
    private static bool classReady;
    private static long UiTime() => GetThreadTimes(GetCurrentThread(),out _,out _,out var k,out var u) ? k+u : 0;

    private static IntPtr OpenNative()
    {
        const string name = "PaperTodoResizeNativeControl";
        var module = GetModuleHandleW(null);
        if (!classReady)
        {
            var wc = new WindowClass {name=name, procedure=Marshal.GetFunctionPointerForDelegate(NativeProcedure),
                cursor=LoadCursorW(IntPtr.Zero,new IntPtr(32512)), instance=module,background=new IntPtr(6)};
            Program.Assert(RegisterClassW(ref wc)!=0,"register native HWND class");
            classReady=true;
        }
        var hwnd=CreateWindowExW(8,name,"Native empty",0x10cf0000,110,80,365,240,IntPtr.Zero,IntPtr.Zero,module,IntPtr.Zero);
        Program.Assert(hwnd!=IntPtr.Zero,"create native HWND");
        SetForegroundWindow(hwnd);
        return hwnd;
    }

    private static System.Windows.Controls.Border EmptySurface() => new()
        { Background=Brushes.White, Margin=new Thickness(8) };
    private static Action ConfigurePaper(PaperWindow w,string kind)
    {
        var editor=Descendants(w).OfType<MarkdownTextBox>().Single();
        var chrome=Descendants(w).OfType<PaperChromeBorder>().First();
        Program.Assert(editor.IsVisible && editor.Text.Trim().Length==0 && chrome.HasNineSliceShadow,"verify empty paper, editor and shadow");
        var parent=(System.Windows.Controls.Panel)editor.Parent;
        var index=parent.Children.IndexOf(editor);
        var body=w.Content;
        var child=chrome.Child;
        var header=w.ProbeHeader!;
        var originalHeader=header.Visibility;
        PaperWindow.ProbeTopDiff=kind=="topDiff";
        if(kind=="noHeader") header.Visibility=Visibility.Collapsed;
        if(kind=="chromeOnly") chrome.Child=null;
        var detach=kind is "noEditor" or "noEditorShadow";
        if(detach) parent.Children.RemoveAt(index);
        PaperWindow.ProbeSkipShadow=kind is "noShadow" or "noEditorShadow";
        PaperWindow.ProbeSkipGeometry=kind=="noGeometry";
        if(kind=="flat") w.Content=EmptySurface();
        chrome.InvalidateVisual();
        return () => {
            PaperWindow.ProbeTopDiff=false;
            if(kind=="noHeader") header.Visibility=originalHeader;
            if(kind=="chromeOnly") chrome.Child=child;
            PaperWindow.ProbeSkipShadow=false; PaperWindow.ProbeSkipGeometry=false;
            if(kind=="flat") w.Content=body;
            if(detach) parent.Children.Insert(index,editor);
            chrome.InvalidateVisual();
        };
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

    private static object? ResizeOnce(IntPtr hwnd, Window? w, bool platform, string axis, string note, int round)
    {
        const string mode = "mouse";
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
        double nativeWorkMs=0;
        var nativeSamples=0;
        int nest=0;
        SubclassCallback hook = (h, msg, wp, lp, id, data) => {
            if (msg == 0x214) sizingMessages++;
            if (msg == 0x231) enter++;
            if (msg == 0x232) leave++;
            if (msg == 5 && GetWindowRect(h, out var r))
            {
                sizes.Add(clock.Elapsed.TotalMilliseconds);
                excursionX = Math.Max(excursionX, Math.Abs(r.Right-r.Left-width));
                excursionY = Math.Max(excursionY, Math.Abs(r.Bottom-r.Top-height));
            }
            if (msg != 0x47 && msg != 5) return DefSubclassProc(h,msg,wp,lp);
            var stamp = Stopwatch.GetTimestamp();
            nest++;
            try { return DefSubclassProc(h,msg,wp,lp); }
            finally { if (--nest==0) { nativeWorkMs += Stopwatch.GetElapsedTime(stamp).TotalMilliseconds; nativeSamples++; } }
        };
        Program.Assert(SetWindowSubclass(hwnd,hook,(UIntPtr)41,UIntPtr.Zero),"observe window messages");
        if (note == "observed") { w!.LayoutUpdated += updated; CompositionTarget.Rendering += rendering; }
        PaperWindow.ProbeCalls = PaperWindow.ProbeTicks = 0;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var uiBefore=UiTime();
        PaperWindow.ProbeGeometryTicks=PaperWindow.ProbeSnapTicks=PaperWindow.ProbeShadowCalls=0;
        try
        {
            var worker = Task.Run(() => {
                try
                {
                    if (mode == "mouse") { mouse_event(2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(30); }
                    for (var i = 1; i <= 128; i++)
                    {
                        var d = (i <= 64 ? i * 2 : 128 - (i - 64));
                        if(axis=="fastCorner") { int k=i%64; d=6*(k<=32 ? k : 64-k); }
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
            return new { mode, note, axis, platform, round, nativeWorkMs, nativeSamples, nSize=sizes.Count, nRender=r.Length, enter, leave, sizingMessages, held, mouseX, mouseY, layout,
                inputP50=Q(inputGaps,.5), sizeP95=Q(sizeGaps,.95), frameP50=Q(frameGaps,.5), frameP95=Q(frameGaps,.95),
                slow25=frameGaps.Count(t=>t>25), slowPercent=Math.Round(100d * frameGaps.Count(t=>t>25)/Math.Max(frameGaps.Length,1),1),
                trackP95=Q(trackingError.ToArray(),.95), duration=inputs[^1]-inputs[0], topCalls=PaperWindow.ProbeCalls,
                uiMs=(UiTime()-uiBefore)/10000d, geomMs=PaperWindow.ProbeGeometryTicks*1000d/Stopwatch.Frequency, snapMs=PaperWindow.ProbeSnapTicks*1000d/Stopwatch.Frequency, shadowDraws=PaperWindow.ProbeShadowCalls, topMs=Math.Round(PaperWindow.ProbeTicks*1000d/Stopwatch.Frequency,3), cpuMs=Math.Round(cpuMs,2) };
        }
        finally { RemoveWindowSubclass(hwnd,hook,(UIntPtr)41); if (note == "observed") { w!.LayoutUpdated -= updated; CompositionTarget.Rendering -= rendering; } GC.KeepAlive(hook); }
    }
}
