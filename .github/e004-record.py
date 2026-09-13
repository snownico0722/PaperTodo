from pathlib import Path
import sys
root=Path(sys.argv[1]);record=Path(sys.argv[2]).read_text(encoding='utf-8')
assert '@FINAL_VALIDATION@' not in record and '@REAL_TABLE@' not in record
assert record.startswith('---\n\n## E-004 ')
p=root/'doc/EXPERIMENTS.md';s=p.read_text(encoding='utf-8-sig')
assert '## E-004 ' not in s
anchor='| E-003 | 2026-09-13 | 预览优先、折叠 Shell 延后与正常 WPF 退出 | Completed | — |'
assert s.count(anchor)==1
s=s.replace(anchor,anchor+'\n| E-004 | 2026-09-13 | 首帧候选筛选与基于记录的后台 JIT | Completed | — |')
s=s.rstrip()+'\n\n'+'\n'.join(line.rstrip() for line in record.splitlines())+'\n'
p.write_text(s,encoding='utf-8',newline='\n')
p=root/'doc/ARCHITECTURE.md';s=p.read_text(encoding='utf-8-sig')
anchor='`AppController` 尚未完成启动时收到的单实例命令先排队，待 controller 可用后再执行。普通纸片窗口全部关闭不等于退出应用，进程使用显式 shutdown 生命周期。'
assert s.count(anchor)==1
addition='''

`App.OnStartup` 在主 GUI 实例取得 Mutex 后通过 `StartupCompilationProfile` 开启 CLR 的可选启动编译记录；MCP bridge、次实例与仅退出命令不参与。CoreLib 有文件路径时启用，因此 FDD 单文件和多文件构建可用，运行库也在 bundle 内的自包含单文件直接跳过。记录位于 `%LOCALAPPDATA%/PaperTodo/Cache/StartupCompilation/startup.prof`，是可丢弃的方法使用记录，不属于便携纸片数据，也不缓存正文或 WPF 对象；首次运行建立记录，后续由 CLR 在后台编译可能用到的方法，不在该线程执行 UI。记录不可用时普通启动继续，现有预览/Shell 队列及退出保存职责不变。'''
s=s.replace(anchor,anchor+addition)
p.write_text(s,encoding='utf-8',newline='\n')
p=root/'CHANGELOG.md';s=p.read_text(encoding='utf-8-sig')
anchor='- **启动与退出响应**：'
assert s.count(anchor)==1
start=s.index(anchor);end=s.index('\n',start)
s=s[:end]+' 精简版和多文件版新增可丢弃的本地编译记录，首次运行后可减少后续启动等待；完整版单文件不受此项影响。'+s[end:]
p.write_text(s,encoding='utf-8',newline='\n')
print('E-004 experiment, current architecture and existing Unreleased entry updated.')
