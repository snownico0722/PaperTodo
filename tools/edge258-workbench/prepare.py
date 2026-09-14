from pathlib import Path
import subprocess, re
REPO=Path('/mnt/data/PaperTodo-work')
CORE=Path('/mnt/data/edge258-core'); FULL=Path('/mnt/data/edge258-full')
OLD='37fcf9b97f4750be22ce96056b2dd667bd87f213'; MAIN='2a84601d732d2c6a169e525bd55f373191784532'
def blob(ref,path):return subprocess.check_output(['git','show',f'{ref}:{path}'],cwd=REPO)
def put(root,path,data):
    p=root/path;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data if isinstance(data,bytes) else data.encode())
def edit(root,path,old,new,count=1):
    s=(root/path).read_text();assert s.count(old)==count,(path,repr(old[:100]),s.count(old));put(root,path,s.replace(old,new))
def replace_method(root,path,name,new):
    s=(root/path).read_text();m=re.search(r'^    (?:private|internal|public) [^\n]*'+re.escape(name)+r'\(',s,re.M);assert m,(path,name)
    start=m.start();brace=s.index('{',m.end());depth=1;i=brace+1
    while depth:
        depth += (s[i]=='{')-(s[i]=='}');i+=1
    put(root,path,s[:start]+new+s[i:])
keep=['AGENTS.md','src/EdgeCapsuleFrameScheduler.cs','src/EdgeCapsulePresenter.cs',
'src/EdgeCapsuleReducer.cs','src/EdgeCapsuleRenderDemand.cs','src/EdgeCapsuleHost.cs',
'src/EdgeCapsulePreview.Markdown.Artifact.cs','src/EdgeCapsulePreview.Markdown.cs',
'src/EdgeCapsulePreview.Preload.cs','src/PaperWindow.EdgeCapsulePlacement.cs','src/WindowNative.cs',
'src/AppController.EdgeCapsuleVisualTransaction.cs','src/EdgeCapsuleQueueProxyWindow.cs',
'tests/PaperTodo.EdgeTitleChecks/DetachedPointerAdmissionChecks.cs',
'tests/PaperTodo.EdgeTitleChecks/RenderDemandChecks.cs',
'tests/PaperTodo.EdgeTitleChecks/RepeatedRenderingNotificationChecks.cs',
'tests/PaperTodo.EdgeTitleChecks/PreviewTransactionChecks.cs',
'tests/PaperTodo.EdgeTitleChecks/Program.cs','tests/PaperTodo.EdgeTitleChecks/ProxyInputReadinessChecks.cs',
'tests/PaperTodo.EdgePreviewChecks/ReviewIntegrationChecks.cs',
'doc/ARCHITECTURE.md','doc/DECISIONS.md','CHANGELOG.md','doc/CHANGELOG.en.md']
for p in keep:put(CORE,p,blob(OLD,p))
put(FULL,'doc/EXPERIMENTS.md',blob(MAIN,'doc/EXPERIMENTS.md'))
edit(CORE,'src/EdgeCapsulePresenter.cs','    internal bool IsSettledForPreacquisition => !HasActiveTransition &&\n        _visualTransactionDeferrals == 0 && !_nativeBatchApplyActive && !_nativeBatchRetryPending &&\n        !_nativeBatchApplyDeferred && !_reconcileScheduled && _dirty == EdgeCapsuleDirty.None;\n','')
edit(CORE,'src/AppController.EdgeCapsuleVisualTransaction.cs','        _edgePrewarm?.Cancel(queueKey);\n        _edgePrewarm?.NotifyInteraction();\n','')
p='src/EdgeCapsulePreview.Preload.cs'
s=(CORE/p).read_text();a=s.index('    // The application coordinator owns WHEN');b=s.index('    internal Task StartStartupWork()',a);s=s[:a]+s[b:]
s=s.replace('    private bool _suspended;\n','').replace('!_suspended && ','').replace('_suspended || ','').replace(' _suspended = false;','').replace(' suspended={_suspended}','')
s=s.replace('        // Suspension cancels only the optional pass. Share its completion with existing callers\n        // without starting new work or waiting for a future interaction/graphics preparation.\n','')
assert '_suspended' not in s;put(CORE,p,s)
edit(CORE,'tests/PaperTodo.EdgeTitleChecks/Program.cs','            ProxyRetentionChecks();\n','')
p='doc/ARCHITECTURE.md';s=(CORE/p).read_text();a=s.index('开启边缘预览时，符合条件的队列');b=s.index('Proxy 动画逻辑结束不等于',a);s=s[:a]+s[b:];put(CORE,p,s)
p='doc/DECISIONS.md';s=(CORE/p).read_text();a=s.index('## D-037 —');b=s.index('## D-038 —',a)
s=s[:a]+'''## D-037 — 可浏览队列提前接管并保留已验证的 live authority

**Status:** Deferred（从 #258 独立审查，尚未成为当前实现）

资源预热不等于长期接管输入。原候选及性能证据保留在 E-005～E-016 和原 #258 `37fcf9b`；静态提前接管、长期保留、最大容量/来源复用及协调器作为完整依赖组另行审查。现有短时动画代理、后继接续与显式交接继续保留。控件级悬停、完整手势、空闲观察与预热退让仍须单独验收，不以历史性能数字代替通过。

---

'''+s[b:]
s=s.replace('| D-023 | Lightweight Prewarm 保留一次性首用预热 | Partially superseded by D-037 |','| D-023 | Lightweight Prewarm 保留一次性首用预热 | Accepted |')
s=s.replace('**Status:** Partially superseded by D-037（graphics 预热保留，调度与真实队列提前接管由 D-037 扩展）','**Status:** Accepted')
s=s.replace('| D-037 | 可浏览队列保留已验证的 live authority | Accepted |','| D-037 | 可浏览队列保留已验证的 live authority | Deferred |');put(CORE,p,s)
p='CHANGELOG.md';s=(CORE/p).read_text();s=s.replace('启动和显示环境变化后，在空闲时提前准备可浏览队列，同一队列反复浏览及收回后重新进入时复用已有显示资源，减少首次与重复等待；交互时暂停后台预热，静置时减少无变化的界面更新；','');put(CORE,p,s)
print('Prepared core and preserved full worktrees')
