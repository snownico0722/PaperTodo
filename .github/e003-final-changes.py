from pathlib import Path
import sys, subprocess
root=Path(sys.argv[1]);tools=Path(__file__).parent
subprocess.run([sys.executable,str(tools/'e003-candidate.py'),str(root),'ordered'],check=True)
def replace(p,a,b):
 path=root/p;s=path.read_text(encoding='utf-8-sig')
 if s.count(a)!=1:raise RuntimeError(f'{p}: {a[:90]!r}: {s.count(a)}')
 path.write_text(s.replace(a,b,1),encoding='utf-8',newline='\n')
replace('src/EdgeCapsulePreview.Preload.cs', '_debounce.Interval = TimeSpan.FromMilliseconds(500); _drainTask = DrainAsync();', '_debounce.Interval = TimeSpan.FromMilliseconds(500); if (_work == null) _drainTask = DrainAsync();')
replace('src/AppController.cs', '''        // Shell construction can invalidate preview resources. Queue every reader now, but
        // release the startup debounce only after those shells finish, not just before they reset it.''', '''        // The small initial preview batch reads the existing model before optional hidden Shells
        // are constructed. Building those controls must not invalidate unchanged preview content.''')
p='tests/PaperTodo.LifecycleChecks/Program.cs'
replace(p,'"cancel-monitor", "scripts", "real-exit"];','"cancel-monitor", "scripts", "real-exit", "preview-before-shell", "startup-demand", "startup-cancel", "startup-exit"];')
replace(p,'            if (name == "real-exit")\n            {\n                var saved', '            if (name is "real-exit" or "startup-exit")\n            {\n                var saved')
replace(p,'            var returned = Stopwatch.GetTimestamp();', '''            var returned = Stopwatch.GetTimestamp();
            if (name == "startup-exit")
            {
                controller.State.Papers[0].Content = "pending editor text at exit";
                controller.MarkDirty();
                Console.WriteLine("EXIT_REQUEST " + Stopwatch.GetTimestamp());
                controller.Exit();
                throw new InvalidOperationException("Exit unexpectedly returned");
            }
            if (name == "preview-before-shell")
            {
                await cache.StartStartupWorkAsync();
                Require(cache.ArtifactCount == count, "previews were not ready before optional shells");
                Require(windows.Values.Any(window => !window.IsShellBuilt), "startup did not defer optional shells");
            }
            if (name == "startup-demand")
            {
                windows["fixture-0"].ActivateFromEdgeShortcut();
                Require(windows["fixture-0"].IsShellBuilt && windows["fixture-0"].HasExpandedPaperSurface,
                    "first use did not construct and show its deferred shell");
            }
            if (name == "startup-cancel")
            {
                controller.HideAllPapers();
                await (Task)Field(controller, "_startupShellPrewarmTask");
                await Until(() => cache.PendingCount == 0, "cancelled startup drain");
                Require(cache.ArtifactCount == 0 && windows.Values.All(window => !window.HasVisibleSurface),
                    "deferred startup work resurrected a hidden paper or preview");
            }''')
replace(p,'                    Require(cache.ArtifactCount == count, "small workset did not cache all short notes");', '''                    Require(cache.ArtifactCount == count, "small workset did not cache all short notes");
                    Require(cache.WarmCompletions == count, "hidden Shell initialization discarded and rebuilt startup artifacts");''')
replace(p,'                    var first = windows["fixture-0"];', '''                    var first = windows["fixture-0"];
                    var read = (MarkdownEdgePreviewPreload.ReadResult)first.GetType()
                        .GetMethod("ReadMarkdownPreloadTarget", Private)!.Invoke(first, null)!;
                    Require(read.Target != null, "completed startup note is no longer eligible");
                    var target = read.Target!;
                    var content = cache.Capture(target.Context);
                    var binding = cache.Bind(target.Context, content, target.Context.Paper.TextZoom);
                    var key = MarkdownEdgePreviewPreload.MakeKey(binding, target.Anchor,
                        new Size(MarkdownEdgeCapsulePreviewRenderer.ArtifactBodyWidth(target.Size), 0));
                    Require(key != null && cache.TryGetArtifact(key, out _), "first preview missed after Shell construction");''')
replace('doc/ARCHITECTURE.md', '启动恢复先登记首批工作，在 Shell 预建完成后直接唤醒既有队列，避免初始化再次重置预热延迟；后续编辑仍使用共享一次性合并延迟。', '启动恢复先登记首批工作；小工作集直接从现有模型完成首轮可运行的预览预热，再在 idle 短批次补建完整 Shell，不设置固定启动等待。Shell 首次填充标题和普通胶囊仅复制已有内容，不使已完成的 artifact 失效。该等待只覆盖当前可运行的 drain，暂不可用的 Host 仍休眠，用户输入可以取消可选工作；后续编辑仍使用共享一次性合并延迟。')
replace('doc/ARCHITECTURE.md', '`AppController.StartupPrewarm` 在 UI Dispatcher 上按短批次补建 Shell，完成 Task 供插件 startupPaper 等待，不使用 Shell-ready 轮询；', '`AppController.StartupPrewarm` 在小工作集首轮预览就绪后，于 UI Dispatcher 上按短批次补建 Shell，完成 Task 供插件 startupPaper 等待，不使用 Shell-ready 轮询；')
replace('CHANGELOG.md', '少量纸片按短批次预建，插件启动纸片不再轮询等待。', '少量边缘笔记先准备悬停预览，再自动补建完整纸片，初始化不会丢掉已完成的预览缓存；启动命令不再等待非必要的 idle 预热，插件启动纸片不再轮询等待。')
