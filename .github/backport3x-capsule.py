from pathlib import Path
import os, sys, re, subprocess, textwrap
from xml.sax.saxutils import escape
import xml.etree.ElementTree as ET
ROOT=Path.cwd()
def read(p): return Path(p).read_text(encoding='utf-8')
def write(p,s):
    p=Path(p); p.parent.mkdir(parents=True,exist_ok=True); p.write_text(s,encoding='utf-8',newline='\n')
def replace(p,a,b):
    s=read(p)
    if s.count(a)!=1: raise RuntimeError(f'{p}: expected one anchor: {a[:120]} (found {s.count(a)})')
    write(p,s.replace(a,b,1))
def edit_method(p,signature,fn):
    s=read(p); a=s.index('    '+signature); b=s.index('\n    }',a)+6
    old=s[a:b]; new=fn(old)
    if old==new: raise RuntimeError('unchanged method '+signature)
    write(p,s[:a]+new+s[b:])
def commit(message,*paths):
    subprocess.run(['git','add','--',*paths],check=True)
    subprocess.run(['git','diff','--cached','--check'],check=True)
    subprocess.run(['git','commit','-m',message],check=True)
def resource(p,key,value,add=False):
    s=read(p)
    if add:
        if re.search(r'<data\s+name="'+re.escape(key)+'"',s): raise RuntimeError('duplicate resource '+key)
        s=s.replace('</root>',f'  <data name="{key}" xml:space="preserve">\n    <value>{escape(value)}</value>\n  </data>\n</root>')
    else:
        pattern=r'(<data\s+name="'+re.escape(key)+r'"[^>]*>\s*<value>).*?(</value>)'
        s,n=re.subn(pattern,lambda m:m[1]+escape(value)+m[2],s,flags=re.S)
        if n!=1: raise RuntimeError('resource anchor '+key)
    ET.fromstring(s); write(p,s)

if '--tests' not in sys.argv:
    p='src/AppController.Settings.cs'
    for name in ['ToggleCapsuleMode','ToggleDeepCapsuleMode']:
        edit_method(p,f'private void {name}()',lambda s:s.replace('            ResetDeepCapsuleStartTopMargins();\n','').replace('before disabling collapse-all and resetting queue\n            // margins;', 'before disabling collapse-all; queue heights remain user settings.\n            //'))
    p='src/AppController.cs'
    a=read(p).index('    // Reset ALL deep-capsule start heights')
    b=read(p).index('    private void ResetDeepCapsuleStartTopMargins()',a)
    s=read(p); write(p,s[:a]+'''    // Only explicit restore-defaults clears both legacy and per-queue heights. Disabling
    // capsule presentation clears transient slots/retraction, not this persistent preference.
'''+s[b:])
    commit('fix(3.x): 关闭胶囊功能时保留各队列高度记忆','src/AppController.Settings.cs',p)

    write('src/EdgeCapsuleOpenOriginPolicy.cs','''namespace PaperTodo;

internal static class EdgeCapsuleOpenOriginPolicy
{
    internal static bool ShouldMarkOpenedFromEdge(EdgeCapsuleSlotState slot, EdgeCapsulePaperForm form)
        => form == EdgeCapsulePaperForm.Expanded && slot is
            EdgeCapsuleSlotState.CollapsedDocked or
            EdgeCapsuleSlotState.RetractedCollapsed or
            EdgeCapsuleSlotState.RetractingCollapsed;
}
''')
    p='src/PaperWindow.EdgeCapsuleAdapter.cs'
    edit_method(p,'private bool ChangeEdgeCapsulePaperForm(',lambda old:'''    private bool ChangeEdgeCapsulePaperForm(
        EdgeCapsulePaperForm paperForm,
        bool reserveWhileExpanded)
    {
        // Capture origin before PaperFormChanged can detach the slot. Whether the slot remains
        // visible after opening must not decide whether expanded geometry can be remembered.
        var markOpenedFromEdge = EdgeCapsuleOpenOriginPolicy.ShouldMarkOpenedFromEdge(EdgeCapsuleSlot, paperForm);
        if (paperForm == EdgeCapsulePaperForm.Expanded && !reserveWhileExpanded)
        {
            CloseDeepCapsuleSlotContextMenu();
        }
        var accepted = DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.PaperFormChanged(paperForm, reserveWhileExpanded));
        if (accepted && markOpenedFromEdge) MarkEdgeCapsuleOpenedFromEdge();
        return accepted;
    }''')
    commit('fix(3.x): 边缘展开来源与保留胶囊开关解耦',p,'src/EdgeCapsuleOpenOriginPolicy.cs')

    p='src/AppController.cs'
    replace(p,'    public void OpenLinkedNote(string? noteId, Window? anchorWindow = null)','''    public void OpenLinkedNote(
        string? noteId,
        Window? anchorWindow = null,
        bool toggleIfExcludedFromCapsules = false)''')
    edit_method(p,'public void OpenLinkedNote(',lambda s:s.replace('''            if (IsLinkedNoteShown(note.Id))
            {
''','''            if (IsLinkedNoteShown(note.Id))
            {
                // Only an explicit linked-button click toggles excluded papers. Context-menu
                // Open and other programmatic callers must remain open/bring-to-front actions.
                if (toggleIfExcludedFromCapsules && State.CollapseExpandedDeepCapsuleOnClick &&
                    State.EnableTodoNoteLinks && State.HideLinkedNotesFromCapsules &&
                    IsNoteLinkedToAnyTodo(note))
                {
                    HidePaper(note);
                    return;
                }
'''))
    replace('src/PaperWindow.Todo.cs','                    _controller.OpenLinkedNote(item.LinkedNoteId, this);','                    _controller.OpenLinkedNote(item.LinkedNoteId, this, toggleIfExcludedFromCapsules: true);')
    p='src/AppController.Settings.cs'
    s=read(p)
    s,n=re.subn(r'_settingsCollapseExpandedDeepCapsuleOnClickCheckBox\.IsEnabled = .*?;', '''_settingsCollapseExpandedDeepCapsuleOnClickCheckBox.IsEnabled =
                (State.UseCapsuleMode && State.UseDeepCapsuleMode && State.ShowDeepCapsuleWhileExpanded) ||
                (State.EnableTodoNoteLinks && State.HideLinkedNotesFromCapsules);''',s,flags=re.S)
    if n!=1: raise RuntimeError('linked-toggle settings enable anchor')
    write(p,s)
    values={
        '':('再次点击胶囊收回纸片','再次点击已展开的边缘胶囊可收回纸片；关联纸片不显示为胶囊时，再次点击待办中的关联按钮可隐藏纸片。'),
        '.en':('Click again to close a paper','Click an expanded edge capsule again to collapse its paper. If a linked paper is excluded from capsules, click its Todo link again to hide it.'),
        '.ja':('再クリックで紙を閉じる','展開中の端カプセルを再クリックすると紙を折りたたみます。カプセルから除外した関連紙は、ToDo の関連ボタンを再クリックすると非表示になります。'),
        '.ko':('다시 클릭하여 종이 닫기','펼친 가장자리 캡슐을 다시 클릭하면 종이를 접습니다. 캡슐에서 제외한 연결 종이는 할 일의 연결 버튼을 다시 클릭하면 숨겨집니다.')}
    for lang,(label,tip) in values.items():
        resource('Resources/Strings'+lang+'.resx','SettingsCollapseExpandedDeepCapsuleOnClick',label)
        resource('Resources/Strings'+lang+'.resx','TipCollapseExpandedDeepCapsuleOnClick',tip)
    commit('fix(3.x): 关联入口支持按既有开关再次点击隐藏','src/AppController.cs',p,'src/PaperWindow.Todo.cs',*[f'Resources/Strings{x}.resx' for x in values])

    replace('src/Models.cs','    public bool UseCapsuleCollapseAll', '    public bool CompactMasterCapsule { get; set; }\n\n    public bool UseCapsuleCollapseAll')
    p='src/MasterCapsuleWindow.cs'
    replace(p,'        _label.Text = _active','        _label.Visibility = _controller.State.CompactMasterCapsule ? Visibility.Collapsed : Visibility.Visible;\n        _label.Text = _active')
    edit_method(p,'private double MasterDockedWidth(double pixelsPerDip)',lambda s:s.replace('        var expandedLabelWidth = MeasureText(','''        if (_controller.State.CompactMasterCapsule)
        {
            // Removing the label also removes its gap; keep the existing real HWND geometry
            // and input surface, rather than clipping the original full-width master.
            return Math.Max(1, Math.Ceiling(MasterLeftPadding + glyphWidth + MasterRightPadding +
                MasterInteriorBorderThickness) + WindowChromeMargin);
        }
        var expandedLabelWidth = MeasureText('''))
    p='src/AppController.Settings.cs'
    replace(p,'                CreateDeepCapsuleTitleMeasureLimitStepper()));','''                CreateDeepCapsuleTitleMeasureLimitStepper(),
                WrapWithHint(MarkAdvancedSetting(SettingsToggle(
                    Strings.Get("SettingsCompactMasterCapsule"), State.CompactMasterCapsule,
                    ToggleCompactMasterCapsule)), "TipCompactMasterCapsule")));''')
    replace(p,'        State.UseCapsuleCollapseAll = true;', '        State.UseCapsuleCollapseAll = true;\n        State.CompactMasterCapsule = false;')
    replace(p,'    private void ToggleRememberDeepCapsuleExpandedPosition()', '''    private void ToggleCompactMasterCapsule()
    {
        State.CompactMasterCapsule = !State.CompactMasterCapsule;
        ArrangeDeepCapsules(animate: false);
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleRememberDeepCapsuleExpandedPosition()''')
    compact={
        '':('主胶囊缩短','主胶囊仅显示展开／收起箭头，并缩短宽度；关闭后恢复原有文字与数量显示。'),
        '.en':('Compact master capsule','Show only the expand/collapse arrow and reduce the master capsule width. Disable to restore its original label and count.'),
        '.ja':('主カプセルを短くする','主カプセルの展開／収納矢印だけを表示して幅を短くします。オフにすると元の文字と件数の表示に戻ります。'),
        '.ko':('주 캡슐 짧게 표시','주 캡슐의 펼치기/접기 화살표만 표시하고 너비를 줄입니다. 끄면 기존 글자와 개수 표시로 돌아갑니다.')}
    for lang,(label,tip) in compact.items():
        resource('Resources/Strings'+lang+'.resx','SettingsCompactMasterCapsule',label,add=True)
        resource('Resources/Strings'+lang+'.resx','TipCompactMasterCapsule',tip,add=True)
    commit('feat(3.x): 增加默认关闭的主胶囊仅箭头模式',p,'src/Models.cs','src/MasterCapsuleWindow.cs',*[f'Resources/Strings{x}.resx' for x in compact])
    replace('CHANGELOG.md','\n### v0.1 初始构建','''
**胶囊与关联纸片**

- 关闭胶囊或贴边功能后仍保留各队列调整过的高度，再次开启时恢复；明确恢复默认设置时才重置。
- 修复关闭“展开时保留边缘胶囊”后，纸片移动和调整大小无法正确记住的问题。
- 开启再次点击收回选项后，已退出胶囊队列的关联纸片也可通过待办关联按钮再次点击隐藏；普通“打开”菜单仍只负责打开或置前。
- 新增默认关闭的“主胶囊缩短”高级选项，仅显示展开／收起箭头并同步缩短宽度，关闭后恢复原有样式。

### v0.1 初始构建''')
    replace('AGENTS.md','- 隐藏全部、关闭胶囊模式、关闭贴边模式、从边缘展开后再隐藏，都要清理临时 slot / 激发态 / 动画状态，避免下次显示错位或残留占位。','''- 隐藏全部、关闭胶囊模式、关闭贴边模式、从边缘展开后再隐藏，都要清理临时 slot / 激发态 / 动画状态，避免下次显示错位或残留占位。
- 关闭胶囊功能不删除队列高度记忆；旧全局高度与每队列高度仅在明确恢复默认时一起重置。展开来源与是否保留边缘槽位独立，不能因为展开后不占槽而丢失位置和大小记忆。''')
    commit('docs(3.x): 记录胶囊记忆、关联入口与紧凑模式','CHANGELOG.md','AGENTS.md')
else:
    target=Path(os.environ['RUNNER_TEMP'])/'paper-backport-check-capsule'; target.mkdir(exist_ok=True)
    write(target/'Check.csproj',f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT/'PaperTodo.csproj'}" /></ItemGroup></Project>''')
    write(target/'Program.cs',r'''using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

static class Check
{
    static readonly BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object? Call(object obj,string name,params object?[] args)
    {
        var type=obj as Type ?? obj.GetType();
        return type.GetMethods(F).Single(m=>m.Name==name && m.GetParameters().Length==args.Length).Invoke(obj is Type?null:obj,args);
    }
    static object? Field(object obj,string name)=>obj.GetType().GetField(name,F)!.GetValue(obj);
    static void Assert(bool x,string name) { if(!x) throw new Exception(name); Console.WriteLine("PASS "+name); }
    static void Pump()
    {
        for(int i=0;i<5;i++)
        {
            var f=new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>f.Continue=false)); Dispatcher.PushFrame(f);
        }
    }
    [STAThread] static int Main()
    {
        try
        {
            var app=new App(); app.InitializeComponent();
            var c=new AppController(); c.State.Papers.Clear(); c.State.EnableAnimations=false;
            c.State.DeepCapsuleStartTopMargin=93;
            c.State.DeepCapsuleQueueStartTopMargins["check-left"]=135;
            c.State.DeepCapsuleQueueStartTopMargins["check-right"]=245;
            c.State.UseCapsuleMode=true; c.State.UseDeepCapsuleMode=true;
            Call(c,"ToggleCapsuleMode");
            Assert(c.State.DeepCapsuleStartTopMargin==93 && c.State.DeepCapsuleQueueStartTopMargins.Count==2,"capsule disable preserves global and per-queue heights");
            c.State.UseCapsuleMode=true; c.State.UseDeepCapsuleMode=true;
            Call(c,"ToggleDeepCapsuleMode");
            Assert(c.State.DeepCapsuleQueueStartTopMargins["check-right"]==245,"edge disable preserves queue height");
            Assert(new StateStore().Load().DeepCapsuleQueueStartTopMargins["check-left"]==135,"height survives actual StateStore save/load");
            Call(c,"RestoreGeneralSettingsPageDefaults"); c.State.EnableAnimations=false;
            Assert(c.State.DeepCapsuleQueueStartTopMargins.Count==0 && c.State.DeepCapsuleStartTopMargin==new AppState().DeepCapsuleStartTopMargin,"explicit defaults still reset both height formats");
            var asm=typeof(PaperWindow).Assembly;
            var policy=asm.GetType("PaperTodo.EdgeCapsuleOpenOriginPolicy")!;
            var slots=asm.GetType("PaperTodo.EdgeCapsuleSlotState")!;
            var forms=asm.GetType("PaperTodo.EdgeCapsulePaperForm")!;
            foreach(var slot in Enum.GetValues(slots)) foreach(var form in Enum.GetValues(forms))
            {
                bool expected=form.ToString()=="Expanded" && slot.ToString() is "CollapsedDocked" or "RetractedCollapsed" or "RetractingCollapsed";
                Assert((bool)Call(policy,"ShouldMarkOpenedFromEdge",slot,form)!==expected,"origin policy "+slot+" -> "+form);
            }
            Assert(!new AppState().CompactMasterCapsule && !JsonSerializer.Deserialize<AppState>("{}")!.CompactMasterCapsule,"compact setting defaults off for new and old data");
            c.State.UseCapsuleMode=true; c.State.UseDeepCapsuleMode=true; c.State.ShowDeepCapsuleWhileExpanded=false;
            var paper=new PaperData{Id="memory-paper",Type=PaperTypes.Todo,IsVisible=true,X=80,Y=80,Width=320,Height=260,Items=new(){new PaperItem{Id="memory-item",Text="memory"}}};
            c.State.Papers.Add(paper);
            c.ShowPaper(paper); Pump();
            var windows=(IDictionary)Field(c,"_windows")!;
            var window=(PaperWindow)windows[paper.Id]!;
            window.SetCollapsedState(true,animate:false); c.ArrangeDeepCapsules(animate:false); Pump();
            window.SetCollapsedState(false,animate:false); Pump();
            window.Left=140; window.Top=150; window.Width=380; window.Height=300;
            Call(window,"SaveGeometryIfAllowed"); Pump();
            Assert(paper.DeepCapsuleExpandedX.HasValue && Math.Abs(paper.DeepCapsuleExpandedX.Value-window.Left)<2 && paper.DeepCapsuleExpandedWidth.HasValue && Math.Abs(paper.DeepCapsuleExpandedWidth.Value-window.Width)<2,"real edge open remembers geometry without retained slot");
            window.SetCollapsedState(true,animate:false); c.ArrangeDeepCapsules(animate:false); Pump();
            var masters=(IDictionary)Field(c,"_masterCapsules")!;
            var master=masters.Values.Cast<object>().First();
            var widthProperty=master.GetType().GetProperty("DesiredDockedWidth",F)!;
            var oldWidth=(double)widthProperty.GetValue(master)!;
            Call(c,"ToggleCompactMasterCapsule"); Pump();
            Assert((double)widthProperty.GetValue(master)!<oldWidth && ((TextBlock)Field(master,"_label")!).Visibility==Visibility.Collapsed,"compact master reduces real width and removes label");
            Assert(new StateStore().Load().CompactMasterCapsule,"compact setting persists through StateStore");
            Call(c,"ToggleCompactMasterCapsule"); Pump();
            Assert(Math.Abs((double)widthProperty.GetValue(master)!-oldWidth)<1,"disabling compact restores original width");
            c.State.EnableTodoNoteLinks=true; c.State.HideLinkedNotesFromCapsules=true; c.State.CollapseExpandedDeepCapsuleOnClick=true;
            var note=new PaperData{Id="linked-note",Type=PaperTypes.Note,Content="linked note",X=450,Y=80,Width=320,Height=240,IsVisible=false};
            paper.Items[0].LinkedNoteId=note.Id; c.State.Papers.Add(note);
            c.OpenLinkedNote(note.Id,null,true); Pump(); Assert(note.IsVisible,"link opens excluded paper");
            c.OpenLinkedNote(note.Id,null,false); Pump(); Assert(note.IsVisible,"ordinary Open does not toggle excluded paper");
            c.OpenLinkedNote(note.Id,null,true); Pump(); Assert(!note.IsVisible,"second linked click hides excluded paper");
            c.OpenLinkedNote(note.Id,null,true); Pump(); Assert(note.IsVisible,"third linked click reopens excluded paper");
            c.State.CollapseExpandedDeepCapsuleOnClick=false;
            c.OpenLinkedNote(note.Id,null,true); Pump(); Assert(note.IsVisible,"disabled toggle preserves bring-to-front behavior");
            foreach(PaperWindow w in windows.Values.Cast<PaperWindow>().ToArray()) w.CloseForReal(false);
            Console.WriteLine("CAPSULE BACKPORT CHECKS PASSED"); return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
''')
    subprocess.run(['dotnet','run','--project',str(target/'Check.csproj'),'-c','Release'],check=True)
