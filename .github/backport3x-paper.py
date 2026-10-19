from pathlib import Path
import os,sys,re,subprocess
ROOT=Path.cwd()
def read(p): return Path(p).read_text(encoding='utf-8')
def write(p,s):
    p=Path(p);p.parent.mkdir(parents=True,exist_ok=True);p.write_text(s,encoding='utf-8',newline='\n')
def replace(p,a,b):
    s=read(p)
    if s.count(a)!=1: raise RuntimeError(f'{p}: expected one anchor ({s.count(a)}): {a[:100]}')
    write(p,s.replace(a,b,1))
def edit_method(p,signature,fn):
    s=read(p);a=s.index('    '+signature);b=s.index('\n    }',a)+6
    old=s[a:b];new=fn(old)
    if old==new: raise RuntimeError('unchanged '+signature)
    write(p,s[:a]+new+s[b:])
def commit(message,*paths):
    subprocess.run(['git','add','--',*paths],check=True)
    subprocess.run(['git','diff','--cached','--check'],check=True)
    subprocess.run(['git','commit','-m',message],check=True)

if '--tests' not in sys.argv:
    write('src/PaperWindow.WindowInteractionShortcuts.cs','''using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private FrameworkElement? _windowCloseTitleBar;
    private bool _middleClickTitleArmed;

    private bool TryHandleWindowCloseKey(int message, IntPtr key, IntPtr keyData)
    {
        if (message != WmKeyDown || key.ToInt32() != 0x57 ||
            Keyboard.Modifiers != ModifierKeys.Control ||
            _windowLifecycle != PaperWindowLifecycleState.Alive || _paper.IsCollapsed) return false;
        // A held shortcut must not continue closing other papers after the first surface leaves.
        if ((keyData.ToInt64() & (1L << 30)) == 0) CloseExpandedPaperFromWindowGesture();
        return true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.Key != Key.W || Keyboard.Modifiers != ModifierKeys.Control ||
            _windowLifecycle != PaperWindowLifecycleState.Alive || _paper.IsCollapsed) return;
        e.Handled = true;
        if (!e.IsRepeat) CloseExpandedPaperFromWindowGesture();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle) return;
        _middleClickTitleArmed = !e.Handled && !_paper.IsCollapsed &&
            _windowLifecycle == PaperWindowLifecycleState.Alive && _windowCloseTitleBar?.IsMouseOver == true;
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle) return;
        var close = _middleClickTitleArmed && !e.Handled && !_paper.IsCollapsed &&
            _windowLifecycle == PaperWindowLifecycleState.Alive && _windowCloseTitleBar?.IsMouseOver == true;
        _middleClickTitleArmed = false;
        if (!close) return;
        e.Handled = true;
        CloseExpandedPaperFromWindowGesture();
    }

    private void CloseExpandedPaperFromWindowGesture()
    {
        // Keep the title-bar button as the only collapse-versus-hide authority. These gestures
        // never call DeletePaper, remove a model, or invent a second close/save path.
        _closeButton?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
}
''')
    p='src/PaperWindow.cs'
    replace(p,'    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)\n    {','''    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Note preview may have no WPF keyboard-focus target; use the existing HWND hook too.
        if (!handled && TryHandleWindowCloseKey(msg, wParam, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }''')
    replace(p,'        topHost.SetResourceReference(Border.BackgroundProperty, "TitleBarBrushKey");','        _windowCloseTitleBar = topHost;\n        topHost.SetResourceReference(Border.BackgroundProperty, "TitleBarBrushKey");')
    replace('src/PaperWindow.Lifecycle.cs','    private void AbortAllInteractions(InteractionAbortReason reason)\n    {','    private void AbortAllInteractions(InteractionAbortReason reason)\n    {\n        _middleClickTitleArmed = false;')
    commit('feat(3.x): Ctrl+W 与标题栏中键复用现有纸片关闭语义',p,'src/PaperWindow.Lifecycle.cs','src/PaperWindow.WindowInteractionShortcuts.cs')

    write('src/PaperWindow.TodoVisibility.cs','''namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Owned by the corresponding live row. Full rebuild clears the map; a rebuilt or removed
    // row replaces/removes its callback. No parallel model or persisted visibility state.
    private readonly Dictionary<string, Action> _todoLinkedPresentationRefreshers = new(StringComparer.Ordinal);

    public void RefreshTodoRowsForPaperVisibility()
    {
        if (_paper.Type != PaperTypes.Todo || _todoPanel == null ||
            !_controller.State.EnableTodoNoteLinks) return;
        // Merely change the linked button's appearance. Keep all editors, selection, native
        // undo history and row order; hidden/deferred bodies must not be built for this refresh.
        foreach (var refresh in _todoLinkedPresentationRefreshers.Values) refresh();
    }
}
''')
    p='src/PaperWindow.Todo.cs'
    replace(p,'        _todoRows.Clear();','        _todoRows.Clear();\n        _todoLinkedPresentationRefreshers.Clear();')
    replace(p,'    private UIElement BuildTodoRow(PaperItem item, bool isNewItem = false)\n    {','''    private UIElement BuildTodoRow(PaperItem item, bool isNewItem = false)
    {
        _todoLinkedPresentationRefreshers.Remove(item.Id);''')
    s=read(p)
    if '_todoEditors.Remove(item.Id);' not in s: raise RuntimeError('todo remove cleanup anchor')
    s=s.replace('_todoEditors.Remove(item.Id);','_todoEditors.Remove(item.Id);\n        _todoLinkedPresentationRefreshers.Remove(item.Id);')
    write(p,s)
    replace(p,'            Grid.SetColumn(linkButton, 2);','''            _todoLinkedPresentationRefreshers[item.Id] = () =>
            {
                linkedNoteActive = _controller.IsLinkedNoteShown(item.LinkedNoteId);
                var hovered = linkButton.IsMouseOver;
                linkButton.Background = linkedNoteActive
                    ? (hovered ? LinkedNoteMediumBgBrush : LinkedNoteLightBgBrush)
                    : (hovered ? LinkedNoteLightBgBrush : LinkedNoteNormalBgBrush);
                linkGlyph.Foreground = linkedNoteActive ? LinkedNoteActiveTextBrush
                    : (hovered ? TextBrush : WeakTextBrush);
                linkGlyph.Opacity = linkedNoteActive || hovered ? 1.0 : 0.7;
            };

            Grid.SetColumn(linkButton, 2);''')
    p='src/AppController.cs'
    for name in ['private void RefreshAllTodoRowsForPaperVisibility()','public void HideAllPapers()']:
        edit_method(p,name,lambda s:s.replace('window.RefreshTodoRowsForExternalChange();','window.RefreshTodoRowsForPaperVisibility();'))
    commit('perf(3.x): 全部显隐时原位更新关联按钮并保留所有待办编辑器',p,'src/PaperWindow.Todo.cs','src/PaperWindow.TodoVisibility.cs')

    p='src/Theme.cs'
    replace(p,'    public static Brush ResizeGripBrush(string? mode)','''    // The dynamic reference belongs on Path.Fill. A nested GeometryDrawing resource
    // does not reliably invalidate an already displayed Window-template ResizeGrip.
    public static DrawingBrush ResizeGripTileBrush(Brush dots)
    {
        var tile = new DrawingBrush(new GeometryDrawing(dots, null,
            Geometry.Parse("M 0,0 L 2,0 L 2,2 L 0,2 z")))
        {
            Viewbox = new Rect(0, 0, 3, 3),
            Viewport = new Rect(0, 0, 3, 3),
            TileMode = TileMode.Tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute
        };
        tile.Freeze();
        return tile;
    }

    public static Brush ResizeGripBrush(string? mode)''')
    p='App.xaml'
    replace(p,'        <SolidColorBrush x:Key="PaperResizeGripBrush" Color="#808A7A63" />','''        <SolidColorBrush x:Key="PaperResizeGripBrush" Color="#808A7A63" />
        <DrawingBrush x:Key="PaperResizeGripTileBrush"
                      Viewbox="0,0,3,3" Viewport="0,0,3,3"
                      TileMode="Tile" ViewportUnits="Absolute" ViewboxUnits="Absolute">
            <DrawingBrush.Drawing>
                <GeometryDrawing Brush="#808A7A63" Geometry="M 0,0 L 2,0 L 2,2 L 0,2 z" />
            </DrawingBrush.Drawing>
        </DrawingBrush>''')
    s=read(p)
    s,n=re.subn(r'(Data="M 9,0 L 11,0 L 11,11 L 0,11 L 0,9 L 3,9 L 3,6 L 6,6 L 6,3 L 9,3 z")>\s*<Path.Fill>.*?</Path.Fill>\s*</Path>',r'\1\n                                  Fill="{DynamicResource PaperResizeGripTileBrush}" />',s,flags=re.S)
    if n!=1: raise RuntimeError('grip template anchor')
    write(p,s)
    replace('src/AppController.Settings.cs','        resources["PaperResizeGripBrush"] = Theme.ResizeGripBrush(State.ResizeGripMode);','''        resources["PaperResizeGripBrush"] = Theme.ResizeGripBrush(State.ResizeGripMode);
        resources["PaperResizeGripTileBrush"] = Theme.ResizeGripTileBrush((Brush)resources["PaperResizeGripBrush"]);''')
    commit('fix(3.x): 切换缩放把手模式时实时刷新实际图案','App.xaml','src/Theme.cs','src/AppController.Settings.cs')
    replace('CHANGELOG.md','### Unreleased\n','''### Unreleased

**纸片交互与性能**

- 展开的纸片支持 Ctrl+W 或标题栏中键关闭，行为与原关闭按钮一致：能够折叠时折叠，否则隐藏，不删除内容；中键按下后移出标题栏再松开可取消。
- 全部显示／隐藏时仅原位更新关联按钮，不再重建整个待办列表，保留待办编辑框、选区和文字撤销记录，减少较长列表的卡顿。
- 修复“标准／柔和／隐藏”缩放把手设置无法立即更新角落点状图案的问题，不改变原有窗口缩放区域。
''')
    commit('docs(3.x): 记录纸片关闭、待办性能和把手修复','CHANGELOG.md')
else:
    target=Path(os.environ['RUNNER_TEMP'])/'paper-backport-check-paper'; target.mkdir(exist_ok=True)
    write(target/'Check.csproj',f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT/'PaperTodo.csproj'}" /></ItemGroup></Project>''')
    write(target/'Program.cs',r'''using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

static class Check
{
    static readonly BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object? Field(object o,string name)=>o.GetType().GetField(name,F)!.GetValue(o);
    static object? Call(object o,string name,params object?[] a)=>o.GetType().GetMethods(F).Single(m=>m.Name==name && m.GetParameters().Length==a.Length).Invoke(o,a);
    static void Assert(bool x,string name){if(!x)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static void Pump(int count=8)
    {
        for(int i=0;i<count;i++)
        {
            var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);Thread.Sleep(10);
        }
    }
    static IEnumerable<T> Find<T>(DependencyObject o) where T:DependencyObject
    {
        if(o is T t)yield return t;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++)foreach(var x in Find<T>(VisualTreeHelper.GetChild(o,i)))yield return x;
    }
    [StructLayout(LayoutKind.Sequential)] struct INPUT{public uint type;public UNION data;}
    [StructLayout(LayoutKind.Explicit)] struct UNION{[FieldOffset(0)]public KEY key;[FieldOffset(0)]public MOUSE mouse;}
    [StructLayout(LayoutKind.Sequential)] struct KEY{public ushort vk,scan;public uint flags,time;public IntPtr extra;}
    [StructLayout(LayoutKind.Sequential)] struct MOUSE{public int x,y;public uint mouseData,flags,time;public IntPtr extra;}
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint n,INPUT[] input,int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    static void CtrlW(Window w)
    {
        var h=new WindowInteropHelper(w).Handle;SetForegroundWindow(h);SetFocus(h);Pump();
        INPUT K(ushort vk,bool up)=>new(){type=1,data=new UNION{key=new KEY{vk=vk,flags=up?2u:0u}}};
        var input=new[]{K(0x11,false),K(0x57,false),K(0x57,true),K(0x11,true)};
        Assert(SendInput(4,input,Marshal.SizeOf<INPUT>())==4,"native Ctrl+W injected");Pump();
    }
    static void Middle(Point p,bool down)
    {
        SetCursorPos((int)p.X,(int)p.Y);Pump(2);
        var input=new[]{new INPUT{type=0,data=new UNION{mouse=new MOUSE{flags=down?0x20u:0x40u}}}};
        Assert(SendInput(1,input,Marshal.SizeOf<INPUT>())==1,"native middle "+(down?"down":"up"));Pump(2);
    }
    static long Alpha(Visual v)
    {
        var d=new DrawingVisual();using(var dc=d.RenderOpen())dc.DrawRectangle(new VisualBrush(v){Stretch=Stretch.Fill},null,new Rect(0,0,32,32));
        var b=new RenderTargetBitmap(32,32,96,96,PixelFormats.Pbgra32);b.Render(d);
        var data=new byte[32*32*4];b.CopyPixels(data,32*4,0);long sum=0;for(int i=3;i<data.Length;i+=4)sum+=data[i];return sum;
    }
    [STAThread] static int Main()
    {
        try
        {
            var app=new App();app.InitializeComponent();var c=new AppController();
            c.State.Papers.Clear();c.State.EnableAnimations=false;c.State.UseCapsuleMode=false;c.State.UseDeepCapsuleMode=false;
            c.State.EnableTodoNoteLinks=true;c.State.HideLinkedNotesFromCapsules=false;
            var notes=Enumerable.Range(0,2).Select(i=>new PaperData{Id="n"+i,Type=PaperTypes.Note,Content="note "+i,IsVisible=true,X=550+i*25,Y=60,Width=300,Height=240}).ToArray();
            c.State.Papers.AddRange(notes);
            var todos=Enumerable.Range(0,5).Select(i=>new PaperData{Id="t"+i,Type=PaperTypes.Todo,IsVisible=true,X=40+i*25,Y=80+i*20,Width=380,Height=350,
                Items=Enumerable.Range(0,200).Select(j=>new PaperItem{Id="t"+i+"-"+j,Order=j,Text="Todo "+i+" / "+j,LinkedNoteId=j<2?notes[j].Id:null}).ToList()}).ToArray();
            c.State.Papers.AddRange(todos);c.ShowAllPapers();Pump();
            var windows=(IDictionary)Field(c,"_windows")!;
            var refs=new Dictionary<string,object>();
            foreach(var p in todos)
            {
                var w=(PaperWindow)windows[p.Id]!;var editors=(IDictionary)Field(w,"_todoEditors")!;
                foreach(DictionaryEntry e in editors)refs[(string)e.Key]=e.Value!;
                Assert(((IDictionary)Field(w,"_todoLinkedPresentationRefreshers")!).Count==2,"only valid linked rows registered "+p.Id);
            }
            var editor=(TextBox)refs["t0-50"];editor.Focus();editor.Select(2,4);editor.SelectedText="preserved";editor.Select(1,5);
            var text=editor.Text;var start=editor.SelectionStart;var length=editor.SelectionLength;
            var models=JsonSerializer.Serialize(todos.Select(p=>p.Items));
            var sw=Stopwatch.StartNew();
            for(int round=0;round<3;round++)
            {
                c.HideAllPapers();Pump();Assert(c.State.Papers.All(p=>!p.IsVisible),"all models hidden "+round);
                c.ShowAllPapers();Pump();Assert(c.State.Papers.All(p=>p.IsVisible),"all models shown "+round);
                int retained=0;
                foreach(var p in todos)foreach(DictionaryEntry e in (IDictionary)Field(windows[p.Id]!,"_todoEditors")!)if(ReferenceEquals(e.Value,refs[(string)e.Key]))retained++;
                Assert(retained==1000,"1000/1000 editors retained across hide/show "+round);
                Assert(JsonSerializer.Serialize(todos.Select(p=>p.Items))==models,"visibility leaves Todo model fields unchanged "+round);
            }
            Console.WriteLine("Three visibility cycles (including dispatcher settling): "+sw.ElapsedMilliseconds+" ms");
            Assert(editor.Text==text && editor.SelectionStart==start && editor.SelectionLength==length,"editor text and selection retained");
            Assert(editor.CanUndo,"native text undo survives visibility refresh");
            var targetPaper=todos[0];var targetWindow=(PaperWindow)windows[targetPaper.Id]!;
            c.ShowPaper(targetPaper);Pump();CtrlW(targetWindow);
            Assert(!targetPaper.IsVisible && c.State.Papers.Contains(targetPaper),"Ctrl+W hides without deleting when capsules disabled");
            c.State.UseCapsuleMode=true;c.State.UseDeepCapsuleMode=false;
            c.ShowPaper(notes[0]);Pump();CtrlW((PaperWindow)windows[notes[0].Id]!);
            Assert(notes[0].IsCollapsed && notes[0].IsVisible,"Ctrl+W on Note preview collapses through existing close button");
            c.State.UseCapsuleMode=false;c.State.UseDeepCapsuleMode=false;
            c.ShowPaper(targetPaper);Pump();targetWindow.Activate();Pump();
            var bar=(FrameworkElement)Field(targetWindow,"_windowCloseTitleBar")!;
            var inside=bar.PointToScreen(new Point(8,bar.ActualHeight/2));
            var body=targetWindow.PointToScreen(new Point(80,150));
            Middle(inside,true);Middle(body,false);Assert(targetPaper.IsVisible,"middle release outside title bar cancels close");
            Middle(body,true);Middle(body,false);Assert(targetPaper.IsVisible,"middle click in body does not close");
            Middle(inside,true);Middle(inside,false);Assert(!targetPaper.IsVisible && c.State.Papers.Contains(targetPaper),"middle title click follows hide not delete");
            c.ShowPaper(targetPaper);Pump();targetWindow.ApplyTemplate();targetWindow.UpdateLayout();
            var grip=Find<ResizeGrip>(targetWindow).First();grip.ApplyTemplate();targetWindow.UpdateLayout();
            var path=Find<System.Windows.Shapes.Path>(grip).First();
            long[] alpha=new long[3];int k=0;
            foreach(var mode in new[]{ResizeGripModes.Standard,ResizeGripModes.Soft,ResizeGripModes.Hidden})
            {
                Call(c,"SetResizeGripMode",mode);Pump();
                Assert(ReferenceEquals(path.Fill,Application.Current.Resources["PaperResizeGripTileBrush"]),"live Path.Fill gets new resource "+mode);
                alpha[k++]=Alpha(path);Console.WriteLine("Actual grip alpha "+mode+": "+alpha[k-1]);
            }
            Assert(alpha[0]>alpha[1] && alpha[1]>alpha[2] && alpha[2]==0,"rendered grip standard/soft/hidden changes without rebuilding window");
            foreach(var w in windows.Values.Cast<PaperWindow>().ToArray())w.CloseForReal(false);
            Console.WriteLine("PAPER INTERACTION BACKPORT CHECKS PASSED");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
''')
    subprocess.run(['dotnet','run','--project',str(target/'Check.csproj'),'-c','Release'],check=True)
