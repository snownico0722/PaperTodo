from pathlib import Path
import os
p = Path('.')
pr = os.environ['REVIEW_PR']
def edit(name, old, new):
    path = p / name
    text = path.read_text(encoding='utf-8-sig')
    assert text.count(old) == 1, (name, text.count(old), old[:80])
    path.write_text(text.replace(old, new), encoding='utf-8', newline='')
edit('src/AppController.DataReload.cs', '    private bool _dataReloadEvaluating;', '    private bool _dataReloadEvaluating;\n    private bool _exitAfterDataReload;')
edit('src/AppController.DataReload.cs', '        finally { _dataReloadEvaluating = false; }', '''        finally
        {
            _dataReloadEvaluating = false;
            ResumeExitAfterDataReload();
        }''')
edit('src/AppController.DataReload.cs', '            events.Dispose();\n        }', '            events.Dispose();\n            ResumeExitAfterDataReload();\n        }')
edit('src/AppController.DataReload.cs', '''        if (!IsDataReloading) return true;
        Application.Current.Dispatcher.BeginInvoke(new Action(Exit), DispatcherPriority.Background);
        return false;
    }''', '''        if (!IsDataReloading) return true;
        // A modal review or asynchronous workspace handoff may take arbitrarily long.
        // Record intent once; do not keep posting Exit and busy-spin the dispatcher.
        _exitAfterDataReload = true;
        return false;
    }

    private void ResumeExitAfterDataReload()
    {
        if (!_exitAfterDataReload || IsDataReloading || IsExiting) return;
        _exitAfterDataReload = false;
        Application.Current.Dispatcher.BeginInvoke(new Action(Exit), DispatcherPriority.Background);
    }''')
if pr == '351':
    edit('src/AppController.Workspaces.cs', '            _workspaceSwitching = false;\n            RefreshWorkspaceControls();', '            _workspaceSwitching = false;\n            RefreshWorkspaceControls();\n            ResumeExitAfterDataReload();')
    edit('src/AppController.Workspaces.cs', '''                if (reopenSettings && _settingsWindow == null) ShowSettingsWindow(settingsPage);
            }
        }''', '''                if (reopenSettings && _settingsWindow == null) ShowSettingsWindow(settingsPage);
            }
            ResumeExitAfterDataReload();
        }''')
else:
    edit('tests/PaperTodo.DataReloadChecks/LiveChecks.cs', 'private static void RunIsolatedLiveChecks()', 'private static void RunIsolatedLiveChecks(string fixtureArgument = "--live-fixture")')
    edit('tests/PaperTodo.DataReloadChecks/LiveChecks.cs', 'start.ArgumentList.Add("--live-fixture");', 'start.ArgumentList.Add(fixtureArgument);')
edit('tests/PaperTodo.DataReloadChecks/Program.cs', '        if (args.Contains("--live-fixture")) return RunLiveFixture();', '        if (args.Contains("--live-fixture")) return RunLiveFixture();\n        if (args.Contains("--exit-fixture")) return RunDeferredExitFixture();')
edit('tests/PaperTodo.DataReloadChecks/Program.cs', '            RunIsolatedLiveChecks();', '            RunIsolatedLiveChecks();\n            RunIsolatedLiveChecks("--exit-fixture");')
(p / 'tests/PaperTodo.DataReloadChecks/DeferredExitChecks.cs').write_text('''using System.IO;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static int RunDeferredExitFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker)))
            throw new InvalidOperationException("Refusing to use a non-fixture data directory.");
        var result = 1;
        var reviewed = false;
        var idleProcessed = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        app.Exit += (_, _) =>
        {
            try
            {
                Require(reviewed && idleProcessed && controller?.IsExiting == true,
                    "exit did not wait for the merge or starved lower-priority dispatcher work");
                Require(controller.PaperCommands.GetDataReloadStatus().LastResult?.Applied == true &&
                    Str(LivePaper(ReadLive(), "note-a")["content"]) == "external before exit",
                    "exit ran before accepting and saving the selected merge");
                result = 0;
                Console.WriteLine("PASS exit intent is coalesced, yields to idle review work and resumes after committed merge");
            }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
        };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var store = new StateStore();
                store.SaveJsonSync(store.SerializeState(NewState()), 1);
                controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);
                foreach (var window in Field<Dictionary<string, PaperWindow>>(controller, "_windows").Values)
                    window.EnsureShellBuilt();
                controller.SaveNow(sync: true);
                var external = ReadLive();
                var baseline = external.ToJsonString();
                var editor = Field<MarkdownTextBox>(Field<Dictionary<string, PaperWindow>>(controller, "_windows")["note-a"], "_noteBox");
                editor.Text = "local before exit";
                LivePaper(external, "note-a")["content"] = "external before exit";
                WriteLive(external);
                AppController.ConflictDialogDriver = (_, _) =>
                {
                    controller.Exit();
                    controller.Exit();
                    Require(!controller.IsExiting, "exit tore down a pending conflict review");
                    var frame = new DispatcherFrame();
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        idleProcessed = true;
                        frame.Continue = false;
                    }), DispatcherPriority.ApplicationIdle);
                    // Like a modal review, run a nested dispatcher. A continuously reposted
                    // Background Exit would starve the idle callback; no timing threshold is used.
                    Dispatcher.PushFrame(frame);
                    reviewed = true;
                    return PaperConflictChoice.KeepExternal;
                };
                Require(controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), baseline).Outcome == "pending",
                    "deferred-exit merge did not start");
                // The merge owner must trigger Exit; this fixture must not call Shutdown on success.
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(); }
        });
        app.Run();
        AppController.ConflictDialogDriver = null;
        controller?.Dispose();
        return result;
    }
}
''', encoding='utf-8', newline='')
