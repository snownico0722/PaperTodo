from pathlib import Path
import difflib, json, os, subprocess, urllib.request

base = 'f24ea7edf36013ce5a98c08d7a58979e340b996c'
assert subprocess.check_output(['git','rev-parse','HEAD'], text=True).strip() == base
changes = {}
def replace(path, old, new):
    before, current = changes.get(path, (None,None))
    if current is None:
        before = current = Path(path).read_text(encoding='utf-8')
    assert current.count(old) == 1, f'{path}: expected exactly one anchor: {old[:80]}'
    changes[path] = (before,current.replace(old,new,1))

replace('src/PaperWindow.cs', '''    internal void HideWithoutGeometrySave()
    {
        MoveWindowWithoutGeometrySave(Hide);
    }''', '''    internal void HideWithoutGeometrySave()
    {
        HandoffForegroundBeforeSurfaceRemoval();
        MoveWindowWithoutGeometrySave(Hide);
    }''')
replace('src/PaperWindow.Lifecycle.cs', '''        // Hand off before OnClosing detaches/destroys the hidden owner and WPF chooses a
        // replacement active window. Only the actual foreground paper participates; shutdown,
        // background deletion and a user who already switched away must not steal focus.
        if (_controller.IsRunning)
        {
            WindowNative.TryHandoffForegroundBeforeClose(new WindowInteropHelper(this).Handle);
        }
    }

    private void CompletePaperWindowClose()''', '''        HandoffForegroundBeforeSurfaceRemoval();
    }

    private void HandoffForegroundBeforeSurfaceRemoval()
    {
        // Native hide/close can activate a same-thread paper behind an external window, even
        // without a hidden owner. Choose from the live stack immediately before removal, not
        // at the start of a fade or in a delayed focus-repair callback. The native helper checks
        // actual foreground again, so background removal and a newer user activation are no-ops.
        if (_controller.IsRunning)
        {
            WindowNative.TryHandoffForegroundBeforeClose(
                new WindowInteropHelper(this).Handle,
                static handle => HwndSource.FromHwnd(handle)?.RootVisual is not PaperWindow paper ||
                    (paper._windowLifecycle == PaperWindowLifecycleState.Alive &&
                     paper._paper.IsVisible && !paper.IsExperimentalPassive));
        }
        // In particular, HideAll marks every paper invisible before withdrawing their HWNDs:
        // none of those still-visible, soon-to-hide papers may become the handoff target.
    }

    private void CompletePaperWindowClose()''')
replace('src/WindowNative.CloseActivation.cs', '''    internal static bool TryHandoffForegroundBeforeClose(IntPtr closingWindow) =>''', '''    internal static bool TryHandoffForegroundBeforeClose(
        IntPtr closingWindow,
        Func<IntPtr, bool>? canActivate = null) =>''')
replace('src/WindowNative.CloseActivation.cs', '''            window => IsCloseActivationTarget(window, closingWindow),''', '''            window => IsCloseActivationTarget(window, closingWindow) &&
                (canActivate?.Invoke(window) ?? true),''')
replace('CHANGELOG.md', '''- **Foreground after deletion**: Fix deleting the active paper switching to the wrong window. Focus is handed to the next eligible window in the current stacking order before the paper's hidden owner is destroyed; deleting a background paper does not actively change focus.''', '''- **Window focus**: Closing, hiding or deleting the active paper now keeps the next usable window in front instead of raising a covered background window. Removing a background paper leaves the current foreground alone.''')
replace('CHANGELOG.zh.md', '### Unreleased\n', '### Unreleased\n\n- **窗口焦点**：关闭、隐藏或删除前台纸片后，正常回到其后方的可用窗口，不再把被遮挡的后台窗口突然拉到前面；处理后台纸片不会主动抢焦点。\n')
replace('doc/ARCHITECTURE.md', '### 7.2 匿名使用统计', '''纸片实际隐藏与销毁前共用 `PaperWindow` 的前台交接入口，以当前 OS foreground 和实时 Z-order 选择仍可操作的下一窗口；批量隐藏已标记不可见的纸片不参与接替。业务关闭语义、数据删除与辅助 owner 生命周期不由此入口改变。后台撤下、退出和用户已切走时不主动激活；不保留旧前台窗口、不排队重试抢焦点。

### 7.2 匿名使用统计''')
path = 'tests/PaperTodo.WindowStackChecks/Program.cs'
replace(path, 'new[] { "delete", "close-hide", "background-delete", "animated-hide" }', '''new[] { "delete", "close-hide", "button-hide", "background-delete", "background-hide",
            "animated-hide", "focus-change-during-fade", "hide-all", "peer-next", "animated-reopen" }''')
replace(path, 'EnableAnimations = operation == "animated-hide",', '''EnableAnimations = operation is "animated-hide" or "focus-change-during-fade" or "animated-reopen",''')
replace(path, '''            foreach (var handle in known.Reverse()) BringToTop(handle);''', '''            var initialStack = operation is "peer-next" or "hide-all"
                ? new[] { z0, z2, z1, z3 } : known;
            foreach (var handle in initialStack.Reverse()) BringToTop(handle);''')
replace(path, 'Stack(known).SequenceEqual(known)', 'Stack(known).SequenceEqual(initialStack)')
replace(path, 'if (operation == "background-delete")', 'if (operation is "background-delete" or "background-hide")')
replace(path, '''            if (operation is "close-hide" or "animated-hide") closing.Close();
            else controller.DeletePaper(paper);

            await Until(() => !closing.IsVisible || closing.IsClosed, "remove Z0 surface");''', '''            if (operation == "hide-all") controller.HideAllPapers();
            else if (operation == "button-hide")
            {
                var button = (System.Windows.Controls.Button)typeof(PaperWindow)
                    .GetField("_closeButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(closing)!;
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            else if (operation is "delete" or "background-delete" or "peer-next") controller.DeletePaper(paper);
            else closing.Close();

            if (operation == "animated-reopen")
            {
                controller.ShowPaper(paper);
                await Settle();
                Require(closing.IsVisible && !closing.IsClosed && GetForegroundWindow() == z0,
                    "Cancelled hide withdrew or deactivated the reopened paper: " + Describe(known));
                Require(Stack(known).SequenceEqual(initialStack), "Cancelled hide reordered the stack.");
                Console.WriteLine($"AFTER {visibility}/{operation} {Describe(known)}");
                return;
            }
            var expectedForeground = operation == "peer-next" ? z2 : z1;
            if (operation == "focus-change-during-fade")
            {
                // Simulate a newer user activation before the fade completion reaches Hide().
                Require(SetForegroundWindow(z3), "Could not switch away during the fade.");
                expectedForeground = z3;
            }
            await Until(() => !closing.IsVisible || closing.IsClosed, "remove Z0 surface");''')
replace(path, '''            await Until(() => GetForegroundWindow() == z1, "Z1 must take foreground, not Z2/Z3; " + Describe(known));
            await Settle();
            var survivors = new[] { z1, z2, z3 };
            Require(GetForegroundWindow() == z1, "Foreground changed again after close: " + Describe(known));
            Require(Stack(survivors).SequenceEqual(survivors), "Close reordered background windows: " + Describe(known));
            Require(peer.IsVisible && !peer.IsClosed, "Closing one paper destroyed its peer.");''', '''            await Until(() => GetForegroundWindow() == expectedForeground,
                "Wrong foreground after removal; " + Describe(known));
            await Settle();
            var survivors = operation switch
            {
                "hide-all" => new[] { z1, z3 },
                "peer-next" => new[] { z2, z1, z3 },
                "focus-change-during-fade" => new[] { z3, z1, z2 },
                _ => new[] { z1, z2, z3 }
            };
            Require(GetForegroundWindow() == expectedForeground, "Foreground changed again after close: " + Describe(known));
            Require(Stack(survivors).SequenceEqual(survivors), "Close reordered background windows: " + Describe(known));
            Require(!peer.IsClosed && peer.IsVisible == (operation != "hide-all"),
                "Wrong peer lifecycle after removal.");''')

# Upload only the expected source blobs. Ref updates remain a separate reviewed connector step.
repo = 'snownico0722/PaperTodo'
for path,(old,new) in changes.items():
    print(''.join(difflib.unified_diff(old.splitlines(True), new.splitlines(True), fromfile=path,tofile=path)), flush=True)
    data=json.dumps({'content':new,'encoding':'utf-8'}).encode('utf-8')
    request=urllib.request.Request(f'https://api.github.com/repos/{repo}/git/blobs', data=data,
        headers={'Authorization':'Bearer '+os.environ['GH_TOKEN'], 'Accept':'application/vnd.github+json',
                 'Content-Type':'application/json'}, method='POST')
    with urllib.request.urlopen(request,timeout=30) as response:
        blob=json.load(response)
    print('PATCH_BLOB '+json.dumps({'path':path,'sha':blob['sha']}),flush=True)
