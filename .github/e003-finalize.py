from pathlib import Path
import runpy
import sys

root=Path(sys.argv[1])
sys.argv=[sys.argv[0],str(root),'combined']
ns=runpy.run_path(str(Path(__file__).with_name('e003-refine.py')))
replace=ns['replace']

replace('src/EdgeCapsulePreview.Preload.cs',
 '        if (!_enabled || _dispatcher.HasShutdownStarted || RunnableCount == 0) return Task.CompletedTask;',
 '        if (!_enabled || _dispatcher.HasShutdownStarted) return Task.CompletedTask;\n        if (RunnableCount == 0) return _drainTask;')
replace('src/AppController.cs', '''        try
        {
            Application.Current.Shutdown();
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static void TryExitCleanup''', '''        // Let the owning Dispatcher finish WPF shutdown and App.OnExit (single-instance
        // listener, telemetry and Application resources). Environment.Exit here preempts
        // that queued work and was slower in the process-exit A/B; owned work is already stopped.
        Application.Current.Shutdown();
    }

    private static void TryExitCleanup''')

p='tests/PaperTodo.LifecycleChecks/Program.cs'
replace(p,'"scripts", "real-exit"];', '"scripts", "real-exit", "preview-before-shell", "early-expand", "cancel-prewarm", "real-exit-scripts", "early-exit"];')
replace(p,'            var result = 1;', '''            // Explicit shutdown can stop the Dispatcher before the awaiting caller resumes.
            // Failures set the exit code directly; success does not depend on that continuation.
            var result = 0;''')
replace(p,'                catch (Exception ex) { Console.Error.WriteLine(ex); }', '                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }')
replace(p,'            app.Run();','            app.Exit += (_, _) => Console.WriteLine("WPF_EXIT_COMPLETED");\n            app.Run();')
replace(p,'            if (name == "real-exit")\n            {\n                var saved', '            if (name.StartsWith("real-exit") || name == "early-exit")\n            {\n                var saved')
replace(p,'Require(paper.GetProperty("content").GetString() == "pending editor text at exit", "last editor change was lost");', 'Require(paper.GetProperty("content").GetString() == (name == "early-exit" ? "short note 0" : "pending editor text at exit"), "last editor change was lost");')
replace(p,'                var line = text.Split(\'\\n\').Single(value => value.StartsWith("EXIT_REQUEST "));', '''                Require(baseline || text.Contains("WPF_EXIT_COMPLETED"), "normal WPF exit event was skipped");
                foreach (var childLine in text.Split('\n').Where(value => value.StartsWith("SCRIPT_CHILD ")))
                {
                    var id = int.Parse(childLine["SCRIPT_CHILD ".Length..]);
                    Process? script;
                    try { script = Process.GetProcessById(id); }
                    catch (ArgumentException) { continue; }
                    using (script) Require(script.HasExited, "script survived real application exit");
                }
                var line = text.Split('\n').Single(value => value.StartsWith("EXIT_REQUEST "));'''.replace("'\n'", "'\\n'"))
replace(p,'            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");', '''            if (name == "early-exit")
            {
                Console.WriteLine("EXIT_REQUEST " + Stopwatch.GetTimestamp());
                controller.Exit();
                return;
            }
            if (name == "cancel-prewarm")
            {
                controller.HideAllPapers();
                await (Task)Field(controller, "_startupShellPrewarmTask");
                await Until(() => cache.PendingCount == 0, "cancelled preview drain");
                Require(windows.Values.All(window => !window.HasVisibleSurface) && cache.ArtifactCount == 0,
                    "deferred startup resurrected a hidden surface or its cache");
                return;
            }
            long[]? previewVersions = null;
            if (name is "preview-before-shell" or "early-expand")
            {
                await cache.StartStartupWork();
                Require(cache.ArtifactCount == count, "previews were not available ahead of shells");
                Require(windows.Values.All(window => !window.IsShellBuilt), "optional shells blocked the first preview pass");
                previewVersions = windows.Values.Select(window =>
                    ((EdgeCapsulePreviewInvalidationSource)Field(window, "_edgeCapsulePreviewInvalidationSource")).Version).ToArray();
                if (name == "early-expand")
                {
                    windows["fixture-0"].ActivateFromEdgeShortcut();
                    Require(windows["fixture-0"].IsShellBuilt && windows["fixture-0"].HasExpandedPaperSurface,
                        "early demand did not construct and show the selected paper");
                }
            }
            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");''')
replace(p,'            var ready = Stopwatch.GetTimestamp();', '''            var ready = Stopwatch.GetTimestamp();
            if (name == "preview-before-shell")
            {
                Require(cache.ArtifactCount == count && cache.WarmCompletions == count,
                    "shell initialization discarded or rebuilt valid preview artifacts");
                Require(previewVersions!.SequenceEqual(windows.Values.Select(window =>
                    ((EdgeCapsulePreviewInvalidationSource)Field(window, "_edgeCapsulePreviewInvalidationSource")).Version)),
                    "initial title/capsule materialization changed the content generation");
            }''')
replace(p,'                if (name.StartsWith("capsules-") && count <= 10)', '                if ((name.StartsWith("capsules-") && count <= 10) || name == "preview-before-shell")')
replace(p,'                    var keyBefore = source.Version;', '                    var keyBefore = source.Version;\n                    var completionsBefore = cache.WarmCompletions;')
replace(p,'                    Require(source.Version >= keyBefore, "source generation regressed");', '''                    Require(source.Version > keyBefore && cache.WarmCompletions > completionsBefore,
                        "real editor changes did not invalidate and rebuild preview content");''')
replace(p,'            if (name == "scripts")','            if (name is "scripts" or "real-exit-scripts")')
replace(p,'                    children.Add(process);','                    children.Add(process);\n                    Console.WriteLine("SCRIPT_CHILD " + process.Id);')
replace(p,'            if (name == "real-exit")\n            {\n                var editor', '            if (name.StartsWith("real-exit"))\n            {\n                var editor')
replace(p,'                throw new InvalidOperationException("Exit unexpectedly returned");','                return;')

p='doc/ARCHITECTURE.md'
replace(p, '`AppController.StartupPrewarm` 在 UI Dispatcher 上按短批次补建 Shell，完成 Task 供插件 startupPaper 等待，不使用 Shell-ready 轮询；插件初始化本身仍在 idle 阶段，不同步阻挡 StartAsync 返回。', '`AppController.StartupPrewarm` 先等待既有 Markdown 预热队列的首轮完成，再在 UI Dispatcher 的低优先级短批次补建折叠纸片的完整 Shell；提前展开仍由 `EnsureShellBuilt` 当场完成所选纸片，不另建备用路径。Shell 完成 Task 供插件 startupPaper 等待，不使用 Shell-ready 轮询；插件初始化本身仍在 idle 阶段，不同步阻挡 StartAsync 返回。DComp 与拖拽的一次性可选预热保留，但不排在启动主流程返回之前。')
replace(p, '退出不为即将销毁的图片缓存执行额外回收，也不重复提交已由 controller 保存的编辑内容。', '退出不为即将销毁的图片缓存执行额外回收，也不重复提交已由 controller 保存的编辑内容。普通主实例退出在已停止 owned work 后调用 `Application.Shutdown` 并让 Dispatcher 完成 `App.OnExit`、单实例监听和应用资源清理，不再紧接着调用 `Environment.Exit` 截断 WPF 生命周期；崩溃边界和次实例转发退出保持独立。')
replace(p, '启动恢复先登记首批工作，在 Shell 预建完成后直接唤醒既有队列，避免初始化再次重置预热延迟；后续编辑仍使用共享一次性合并延迟。', '启动恢复直接唤醒首批工作，使用现有 Edge Host 和模型生成预览，不等待折叠纸片的完整 Shell。队列暴露本轮完成 Task 供可选 Shell 预建排序，日常失效与调度仍由队列自身拥有。首次标题和胶囊 UI 初始化不把未变化的 Markdown 内容当成编辑作废；实际文本规范化、编辑及资源变更仍走原失效路径，后续编辑保留一次性合并延迟。')
p='CHANGELOG.md'
replace(p, '少量纸片按短批次预建，插件启动纸片不再轮询等待。正常退出保留最后一次保存，随后先撤下界面，并同时停止脚本进程，减少逐个等待。', '边缘笔记先准备可浏览的预览，再按低优先级短批次补建折叠纸片，右键菜单和可选初始化不阻挡首轮恢复；插件启动纸片不再轮询等待。正常退出保留最后一次保存，随后先撤下界面，同时停止脚本进程，并完成正常窗口退出流程，减少逐个等待和进程结束拖延。')
print('Applied E003 product changes and behavioral regression cases; no probe code added')
