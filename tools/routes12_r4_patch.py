from pathlib import Path
import re


def replace_once(path: str, pattern: str, replacement: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"Expected one match in {path}, got {count}")
    p.write_text(updated, encoding="utf-8", newline="")


replace_once(
    "src/EdgeCapsuleQueueCompositionProxy.Visuals.cs",
    r"    private void ConfigureAnimations\(long absoluteBeginTimestamp\)\s*\{.*?\n    \}\n\n    private IDCompositionAnimation\? ApplyAnimatedValue",
    '''    private EdgeCapsuleQueueInputAnimationTicket? ConfigureAnimations(long absoluteBeginTimestamp)
    {
        foreach (var state in _visuals)
        {
            state.OffsetXAnimation = ApplyAnimatedValue(
                state.StartOffsetX,
                state.TargetOffsetX,
                value => state.Visual.SetOffsetX(value),
                animation => state.Visual.SetOffsetX(animation),
                absoluteBeginTimestamp);
            state.OffsetYAnimation = ApplyAnimatedValue(
                state.StartOffsetY,
                state.TargetOffsetY,
                value => state.Visual.SetOffsetY(value),
                animation => state.Visual.SetOffsetY(animation),
                absoluteBeginTimestamp);
        }

        if (!RoutesPointerInput) return null;
        var inputMembers = _members
            .Where(member => member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            .Select(member => member.Plan)
            .ToArray();
        return inputMembers.Length == 0
            ? null
            : new EdgeCapsuleQueueInputAnimationTicket(
                absoluteBeginTimestamp,
                _plan.DurationMilliseconds,
                inputMembers);
    }

    private IDCompositionAnimation? ApplyAnimatedValue''')

replace_once(
    "src/EdgeCapsuleQueueCompositionProxy.Startup.cs",
    r'''                ConfigureAnimations\(animationTimestamp\);\n#if DEBUG\n                using \(var edgeJournalNative = EdgeDiagnosticObservation.Begin\("native\.dcomp-commit"\)\)\n#endif\n                    _device\.Commit\(\)\.CheckError\(\);\n                _animationStartedAtTimestamp = animationTimestamp;''',
    '''                var inputTicket = ConfigureAnimations(animationTimestamp);
#if DEBUG
                using (var edgeJournalNative = EdgeDiagnosticObservation.Begin("native.dcomp-commit"))
#endif
                    _device.Commit().CheckError();
                // The input owner receives the same absolute QPC only after DComp accepted the
                // visual transaction. If Commit stalls, HRGN remains at the old finite region;
                // activation immediately samples the elapsed ticket instead of starting a new clock.
                if (inputTicket != null && !_window.TryStartInputAnimation(inputTicket))
                {
                    throw new InvalidOperationException(
                        "The dedicated native input owner could not activate the committed queue animation ticket.");
                }
                _animationStartedAtTimestamp = animationTimestamp;''')

replace_once(
    "src/EdgeCapsuleQueueProxyWindow.cs",
    r'''    internal bool TrySetInputRegions\(IReadOnlyList<DeviceScreenRect> screenBounds\)\s*\{.*?\n    \}\n\n    internal bool TryStartInputAnimation''',
    '''    internal bool TrySetInputRegions(IReadOnlyList<DeviceScreenRect> screenBounds)
    {
        ArgumentNullException.ThrowIfNull(screenBounds);
        if (_disposed || _disposing || Handle == IntPtr.Zero || _inputOwner == null) return false;
        // While a committed immutable queue ticket owns translation, Dispatcher-side samples are
        // observational only. A non-empty refresh must not supersede that native clock; a new queue
        // generation starts a new ticket, while empty clear/hide/environment invalidation still retire it.
        if (_inputOwner.IsAnimationActive && screenBounds.Count > 0) return true;
        return _inputOwner.TrySetRegions(screenBounds);
    }

    internal bool TryStartInputAnimation''')

probe = "tests/PaperTodo.EdgeTitleChecks/Routes12R3InputOnlyIntegrationProbe.cs"
replace_once(
    probe,
    r'''        var targetRect = new DeviceScreenRect\(initial\.Left, initial\.Top \+ 120, initial\.Right, initial\.Bottom \+ 120\);\n        var x = initial\.Left \+ 24;''',
    '''        var targetRect = new DeviceScreenRect(initial.Left + 120, initial.Top, initial.Right + 120, initial.Bottom);
        var sampleY = initial.Top + 24;''')
replace_once(
    probe,
    r'''            var initialPixels = Routes12R3CaptureRedVertical\(bounds, x\);\n            Check\(initialPixels\.Count >= 40 && Math\.Abs\(initialPixels\.Top - initial\.Top\) <= 3,\n                \$"\{route\}: physical desktop sees initial live pixels \(\{initialPixels\}\)"\);''',
    '''            var initialPixels = Routes12CaptureRedSpan(bounds, sampleY, out _);
            Check(initialPixels.Count >= 40 && Math.Abs(initialPixels.Left - initial.Left) <= 3,
                $"{route}: physical desktop sees initial live pixels ({initialPixels})");''')
replace_once(
    probe,
    r'''            animation = device\.CreateAnimation\(\);\n            var from = \(float\)\(initial\.Top - bounds\.Top\);.*?            var dynamicSummary = Routes12R3Summarize\(observations\);''',
    '''            animation = device.CreateAnimation();
            var from = (float)(initial.Left - bounds.Left);
            var travel = targetRect.Left - initial.Left;
            var durationSeconds = durationMilliseconds / 1000.0;
            animation.SetAbsoluteBeginTime(startedAt).CheckError();
            animation.AddCubic(
                0,
                from,
                (float)(3 * travel / durationSeconds),
                (float)(-3 * travel / (durationSeconds * durationSeconds)),
                (float)(travel / (durationSeconds * durationSeconds * durationSeconds))).CheckError();
            animation.End(durationSeconds, from + travel).CheckError();
            visual.SetOffsetX(animation).CheckError();

            // Deliberately hold the DComp transaction before Commit. The dedicated input owner must
            // remain at the resting HRGN and inactive; starting it before Commit would reproduce an
            // input-ahead window whenever DComp submission stalls.
            var preCommitInput = Routes12CaptureInputSpan(pair.InputHandle, bounds, sampleY);
            Thread.Sleep(90);
            var delayedInput = Routes12CaptureInputSpan(pair.InputHandle, bounds, sampleY);
            Check(!pair.IsInputAnimationActive && !preCommitInput.IsEmpty &&
                delayedInput.Left == preCommitInput.Left && delayedInput.Right == preCommitInput.Right,
                route + ": delayed DComp commit cannot advance native input authority early");

            device.Commit().CheckError();
            Check(pair.TryStartInputAnimation(ticket),
                route + ": production input owner activates the shared absolute-QPC ticket only after DComp commit");
            Thread.Sleep(30);
            var updatesBeforeRefresh = pair.InputRegionUpdateCount;
            Check(pair.TrySetInputRegions([initial]) && pair.IsInputAnimationActive &&
                pair.InputRegionUpdateCount == updatesBeforeRefresh,
                route + ": stale non-empty UI refresh cannot steal the active native animation clock");

            var updatesBeforeStall = pair.InputRegionUpdateCount;
            var observer = Task.Run(() => Routes12ObserveDynamic(pair.InputHandle, bounds, sampleY, 430));
            Thread.Sleep(430);
            var observations = observer.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(pair.InputRegionUpdateCount >= updatesBeforeStall + 4,
                route + ": native HRGN keeps advancing while the WPF/UI thread is unavailable");
            var dynamicSummary = Routes12Summarize(observations);''')
replace_once(
    probe,
    r'''            var terminalPixels = Routes12R3CaptureRedVertical\(bounds, x\);\n            Check\(terminalPixels\.Count >= 40 && Math\.Abs\(terminalPixels\.Top - targetRect\.Top\) <= 4,\n                \$"\{route\}: terminal DComp pixels reach the intended endpoint \(\{terminalPixels\}\)"\);''',
    '''            var terminalPixels = Routes12CaptureRedSpan(bounds, sampleY, out _);
            Check(terminalPixels.Count >= 40 && Math.Abs(terminalPixels.Left - targetRect.Left) <= 4,
                $"{route}: terminal DComp pixels reach the intended endpoint ({terminalPixels})");''')

print("R4 source patch applied")
