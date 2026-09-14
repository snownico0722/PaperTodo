using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void ProxySourceLifecycleChecks()
    {
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "source lifecycle monitor");
        ProxySourceThemeLifecycleChecks(monitor);
        using var host = NewHost();
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left, 0, 0,
            180, 0, 40, 280, 120, true, 1, 1, 420, 200);
        var presenter = new EdgeCapsulePresenter();
        Require(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
            EdgeCapsulePaperForm.Collapsed, false)).Accepted, "source lifecycle attach");
        presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        presenter.Reconcile(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
            () => layout, () => null, frame => frame, host.Apply);
        Pump();
        var original = host.Handle;
        var frame = presenter.AppliedPresentation;
        var ownerWindow = Window.GetWindow(host.MarkdownPreloadAnchor!) ??
            throw new InvalidOperationException("source lifecycle real Host window is absent");
        EdgeCapsuleProxySourceLease? first = null, successor = null, replacement = null;
        void PresentPreview(bool open)
        {
            Require(presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(open)).Accepted,
                "source lifecycle preview intent");
            presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Preview));
            presenter.Reconcile(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
                () => layout, () => null, value => value with { ContentOpacity = .35, Opacity = .8 }, host.Apply);
            Require(presenter.AppliedPresentation.Surface == (open
                ? EdgeCapsuleSurfaceKind.DockedPreview : EdgeCapsuleSurfaceKind.DockedResting),
                "source lifecycle uses the presenter's actual preview/compact endpoint");
        }
        try
        {
            Require(host.TryAcquireProxySource(out first) && first != null, "source lifecycle first lease");
            Require(first!.SourceHandle != original && first.OwnerHandle == original && first.IsCurrent,
                "source/input HWND identities remain distinct");
            Require(host.TryAcquireProxySource(out successor) && successor != null &&
                successor.SourceHandle == first.SourceHandle, "successor shares the same live atlas");
            first.CanReplacePreview = static () => true;
            successor!.CanReplacePreview = static () => true;
            var generation = first.Generation;
            var atlas = first.SourceHandle;
            var dim = frame with { ContentOpacity = .35, Opacity = .8 };
            Require(host.Apply(dim), "apply independent alpha channels");
            Pump();
            Require(first.IsCurrent &&
                host.ActualContentOpacity == .35 && ownerWindow.Opacity == .8,
                "real Host preserves both independent alpha channels");
            var discarded = new TextBlock { Text = "Canceled before opening", Width = 272, Height = 112 };
            Require(host.StagePreviewContent(discarded, 272, 112) && host.Apply(dim),
                "staged preview canceled by a compact endpoint");
            Pump();
            Require(!host.HasPreviewContent && first.IsCurrent && first.Description.Preview == null,
                "Resting Apply legitimately detaches a staged preview before it enters the source slot");
            var updates = 0;
            var invalidations = 0;
            first.Updated += () => updates++;
            first.Invalidated += () => invalidations++;
            var body = new TextBlock { Text = "A final-layout live preview", Width = 272, Height = 112 };
            Require(host.StagePreviewContent(body, 272, 112), "stage first source preview");
            PresentPreview(true);
            Pump();
            Require(host.OwnsPreviewContent(body) && first.Description.Preview != null &&
                first.Generation == generation && first.IsCurrent && !first.IsLayoutPending &&
                updates > 0 && invalidations == 0,
                "natural layout fills the first preview slot and wakes the same live source");
            var revision = first.Description.Revision;
            var previousUpdates = updates;
            body.InvalidateArrange();
            Require(!first.Synchronize(presenter.AppliedPresentation) && first.IsLayoutPending && !first.IsCurrent,
                "temporarily unarranged live preview defers without claiming current geometry");
            Pump();
            Require(first.IsCurrent && !first.IsLayoutPending && first.Description.Revision == revision &&
                updates > previousUpdates && invalidations == 0,
                "natural layout wakes a deferred consumer even when the description is unchanged");
            previousUpdates = updates;
            Pump();
            Require(updates == previousUpdates, "ready source does not keep issuing layout wake notifications");
            PresentPreview(false);
            Pump();
            Require(!host.HasPreviewContent && first.IsCurrent && first.Description.Revision == revision &&
                first.SourceHandle == atlas, "ordinary preview clear keeps retained source and slot");
            previousUpdates = updates;
            var next = new TextBlock { Text = "Replacement final-layout preview", Width = 272, Height = 112 };
            Require(host.StagePreviewContent(next, 272, 112), "stage replacement with consumers hidden");
            PresentPreview(true);
            Pump();
            Require(first.Generation == generation && first.SourceHandle == atlas && first.IsCurrent &&
                first.Description.Revision > revision, "unseen preview slot replaces in the same generation");
            Require(updates > previousUpdates,
                "same-size live Visual replacement notifies consumers even when plane coordinates are unchanged");
            first.Dispose(); first.Dispose(); first = null;
            Require(successor.IsCurrent && WindowNative.IsWindowHandleAlive(atlas),
                "predecessor disposal cannot close successor source");

            // A lease which still owns the old plane must keep it alive through retirement. Host
            // staging continues normally; a new generation waits for its own real layout.
            PresentPreview(false);
            Pump();
            successor.CanReplacePreview = static () => false;
            var retiredRevision = successor.Description.Revision;
            var retiredInvalidations = 0;
            successor.Invalidated += () => retiredInvalidations++;
            var third = new TextBlock { Text = "A protected replacement", Width = 272, Height = 112 };
            Require(host.StagePreviewContent(third, 272, 112), "retirement cannot block Host staging");
            PresentPreview(true);
            Pump();
            Require(!successor.IsCurrent && !successor.IsLayoutPending && retiredInvalidations == 1 &&
                successor.Description.Revision == retiredRevision && WindowNative.IsWindowHandleAlive(atlas),
                "retired lease keeps its old source intact and has no pending layout wake");
            Require(host.TryAcquireProxySource(out replacement) && replacement != null &&
                replacement.Generation > generation && replacement.SourceHandle != atlas && replacement.IsCurrent,
                "replacement generation binds the newly arranged live preview");
            var activeReplacement = replacement!;
            successor.Dispose(); successor.Dispose(); successor = null;
            Require(!WindowNative.IsWindowHandleAlive(atlas) && activeReplacement.IsCurrent,
                "last retired lease closes only the retired generation");
            var replacementHandle = activeReplacement.SourceHandle;
            var replacementGeneration = activeReplacement.Generation;
            var replacementUpdates = 0;
            activeReplacement.Updated += () => replacementUpdates++;
            third.InvalidateArrange();
            Require(!activeReplacement.Synchronize(presenter.AppliedPresentation) && activeReplacement.IsLayoutPending,
                "final release covers a pending natural layout wake");
            var opacityBeforeRelease = host.ActualContentOpacity;
            var windowOpacityBeforeRelease = ownerWindow.Opacity;
            Require(opacityBeforeRelease == presenter.AppliedPresentation.ContentOpacity,
                "real source alpha agrees with the current Presenter frame before release");
            Require(windowOpacityBeforeRelease == presenter.AppliedPresentation.Opacity,
                "real window alpha agrees with the current Presenter frame before release");
            activeReplacement.Dispose(); activeReplacement.Dispose(); replacement = null;
            Pump();
            var atlasAliveAfterRelease = WindowNative.IsWindowHandleAlive(replacementHandle);
            var ownerAliveAfterRelease = WindowNative.IsWindowHandleAlive(original);
            var opacityAfterRelease = host.ActualContentOpacity;
            Console.WriteLine(FormattableString.Invariant(
                $"SOURCE_FINAL_RELEASE atlasAlive={atlasAliveAfterRelease} updates={replacementUpdates} ownerAlive={ownerAliveAfterRelease} alphaBefore={opacityBeforeRelease:R} alphaAfter={opacityAfterRelease:R} presenterAlpha={presenter.AppliedPresentation.ContentOpacity:R} windowAlphaBefore={windowOpacityBeforeRelease:R} windowAlphaAfter={ownerWindow.Opacity:R}"));
            Require(atlasAliveAfterRelease, "Host ownership retains its valid current atlas after the last lease");
            Require(replacementUpdates == 0, "last lease cancels all pending source Updated notifications");
            Require(ownerAliveAfterRelease, "last lease leaves the real Host HWND alive");
            Require(opacityAfterRelease == opacityBeforeRelease,
                FormattableString.Invariant($"last lease preserves real source alpha: before={opacityBeforeRelease:R}, after={opacityAfterRelease:R}"));
            Require(ownerWindow.Opacity == windowOpacityBeforeRelease,
                "last lease preserves the real window's independent overall alpha");
            Require(host.TryAcquireProxySource(out replacement) && replacement != null && replacement.IsCurrent &&
                replacement.SourceHandle == replacementHandle && replacement.Generation == replacementGeneration,
                "a new consumer reuses the Host-owned cached HWND and generation after natural layout");
            replacement!.Dispose(); replacement = null;
            Pump();
            Require(WindowNative.IsWindowHandleAlive(replacementHandle) && replacementUpdates == 0 &&
                host.ActualContentOpacity == opacityBeforeRelease && ownerWindow.Opacity == windowOpacityBeforeRelease,
                "releasing the reused current source preserves cache, alpha and detached consumer silence");
            Require(host.Apply(EdgeCapsulePresentationFrame.Hidden), "hide Host with a cached current source");
            Pump();
            Require(!WindowNative.IsWindowHandleAlive(replacementHandle) && WindowNative.IsWindowHandleAlive(original),
                "Host hide destroys its unleased cached atlas while retaining the real HWND");
        }
        finally
        {
            first?.Dispose(); successor?.Dispose(); replacement?.Dispose();
            presenter.ClearDeferredWork();
        }
        Console.WriteLine("PASS live proxy source ownership, retained preview slot and alpha lifecycle");
    }

    private static void ProxySourceThemeLifecycleChecks(MonitorGeometry monitor)
    {
        using var host = NewHost();
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left, 0, 0,
            180, 0, 40, 280, 120, true, 1, 1, 420, 200);
        var presenter = new EdgeCapsulePresenter();
        Require(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
            EdgeCapsulePaperForm.Collapsed, false)).Accepted, "theme lifecycle attach");
        presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        presenter.Reconcile(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
            () => layout, () => null, value => value, host.Apply);
        Pump();
        var frame = presenter.AppliedPresentation;
        var ownerHandle = host.Handle;
        var resources = new EdgeCapsulePreviewThemeResources(Brushes.Blue, Brushes.Gray,
            Brushes.Blue, Brushes.Gray, Brushes.LightGray, Brushes.Blue);
        void Theme(Brush paper, Brush border, Brush outline) => host.UpdateTheme(paper, border, outline,
            Brushes.LightGray, Brushes.Gray, Brushes.Black, Brushes.Gray, "✓", 13, resources);
        EdgeCapsuleProxySourceLease? current = null, pending = null;
        try
        {
            Require(host.TryAcquireProxySource(out current) && current != null, "theme lifecycle first source");
            var initial = current!;
            var initialHandle = initial.SourceHandle;
            var initialGeneration = initial.Generation;
            var sameThemeInvalidations = 0;
            initial.Invalidated += () => sameThemeInvalidations++;
            for (var i = 0; i < 4; i++)
            {
                Theme(i < 2 ? Brushes.White : new SolidColorBrush(Colors.White),
                    i < 2 ? Brushes.Gray : new SolidColorBrush(Colors.Gray),
                    i < 2 ? Brushes.Blue : new SolidColorBrush(Colors.Blue));
                Require(host.Apply(frame), "same-value theme applies normally");
                Pump();
                Require(initial.IsCurrent && sameThemeInvalidations == 0 &&
                    host.TryAcquireProxySource(out pending) && pending != null,
                    "same-value shell theme keeps the retained source current");
                var shared = pending!;
                Require(shared.SourceHandle == initialHandle && shared.Generation == initialGeneration,
                    "same-value theme, including new SolidColorBrush instances, reuses HWND and generation");
                shared.Dispose(); pending = null;
            }

            var colors = new[] { Colors.White, Colors.Gray, Colors.Blue };
            var changedColors = new[] { Colors.Beige, Colors.DarkSlateGray, Colors.OrangeRed };
            for (var channel = 0; channel < colors.Length; channel++)
            {
                var retired = current!;
                var retiredHandle = retired.SourceHandle;
                var retiredGeneration = retired.Generation;
                var retiredStyle = retired.Description.Style;
                var invalidations = 0;
                retired.Invalidated += () => invalidations++;
                colors[channel] = changedColors[channel];
                Theme(new SolidColorBrush(colors[0]), new SolidColorBrush(colors[1]), new SolidColorBrush(colors[2]));
                Require(!retired.IsCurrent && WindowNative.IsWindowHandleAlive(retiredHandle),
                    "actual shell color change retires the source without destroying a leased HWND");
                Require(host.Apply(frame), "changed shell theme applies normally");
                Pump();
                Require(invalidations == 1 && ReferenceEquals(retired.Description.Style, retiredStyle),
                    "retired source retains its original shell description and emits one invalidation");
                Require(host.TryAcquireProxySource(out pending) && pending != null && pending.IsCurrent &&
                    pending.SourceHandle != retiredHandle && pending.Generation > retiredGeneration,
                    "changed shell color creates a distinct current source generation");
                var next = pending!;
                var style = next.Description.Style;
                Require(((SolidColorBrush)style.PaperBrush).Color == colors[0] &&
                    ((SolidColorBrush)style.PaperBorderBrush).Color == colors[1] &&
                    ((SolidColorBrush)style.OutlineBrush).Color == colors[2],
                    "new generation captures the actual paper, border and outline colors");
                retired.Dispose();
                current = next; pending = null;
                Require(!WindowNative.IsWindowHandleAlive(retiredHandle) && current.IsCurrent &&
                    WindowNative.IsWindowHandleAlive(ownerHandle),
                    "releasing the retired theme source leaves the current source and real Host alive");
            }
            var finalSource = current!;
            var finalHandle = finalSource.SourceHandle;
            var finalGeneration = finalSource.Generation;
            var finalAlpha = host.ActualContentOpacity;
            var releasedConsumerNotifications = 0;
            finalSource.Updated += () => releasedConsumerNotifications++;
            finalSource.Invalidated += () => releasedConsumerNotifications++;
            finalSource.Dispose(); current = null;
            Pump();
            Require(WindowNative.IsWindowHandleAlive(finalHandle) && WindowNative.IsWindowHandleAlive(ownerHandle) &&
                host.ActualContentOpacity == finalAlpha && releasedConsumerNotifications == 0,
                "Host owns the current theme atlas after lease release without notifying the detached consumer");
            Require(host.TryAcquireProxySource(out pending) && pending != null && pending.IsCurrent &&
                pending.SourceHandle == finalHandle && pending.Generation == finalGeneration,
                "current theme atlas is reused from the Host cache");
            pending!.Dispose(); pending = null;
            Require(WindowNative.IsWindowHandleAlive(finalHandle), "reused theme atlas returns to the Host cache");
            Require(host.Apply(EdgeCapsulePresentationFrame.Hidden), "hide Host with a cached theme atlas");
            Pump();
            Require(!WindowNative.IsWindowHandleAlive(finalHandle) && WindowNative.IsWindowHandleAlive(ownerHandle) &&
                releasedConsumerNotifications == 0,
                "Host hide closes the cached theme atlas without touching the real HWND or released consumers");
        }
        finally
        {
            pending?.Dispose(); current?.Dispose();
            presenter.ClearDeferredWork();
        }
        Console.WriteLine("PASS same-value source theme reuse and actual shell color retirement");
    }
}
