from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one match, found {count}")
    p.write_text(text.replace(old, new), encoding="utf-8", newline="")


# The first draft switched the live request to restoredRequest before asking the controller to
# accept the new session size. Its rollback helper still required the old request to be current,
# making rollback deterministically fail on that boundary. Keep both identities explicit: the
# request expected to be current at rollback time, and the original constrained request to restore.
replace_once(
    "src/PaperWindow.EdgeCapsulePreviewCapacityRecovery.cs",
    """        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            bodyGeneration != _bodySessionGeneration ||
            contentGeneration != _edgeCapsulePreviewContentGeneration ||
            !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
            !IsEdgeCapsulePreviewOpen ||
            !_controller.IsEdgeCapsulePreviewOwner(this))
        {
            TryRollbackConstrainedPreviewContent(host, request, replacement);
            return false;
        }""",
    """        if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
            bodyGeneration != _bodySessionGeneration ||
            contentGeneration != _edgeCapsulePreviewContentGeneration ||
            !ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
            !IsEdgeCapsulePreviewOpen ||
            !_controller.IsEdgeCapsulePreviewOwner(this))
        {
            TryRollbackConstrainedPreviewContent(
                host,
                expectedCurrentRequest: request,
                originalRequest: request,
                replacement);
            return false;
        }""",
)

replace_once(
    "src/PaperWindow.EdgeCapsulePreviewCapacityRecovery.cs",
    """        if (!_controller.TryRestoreEdgeCapsulePreviewSessionSize(
                this,
                request.Size,
                requestedSize))
        {
            if (TryRollbackConstrainedPreviewContent(host, request, replacement))
            {
                _edgeCapsulePreviewRequest = request;
            }
            return false;
        }""",
    """        if (!_controller.TryRestoreEdgeCapsulePreviewSessionSize(
                this,
                request.Size,
                requestedSize))
        {
            if (TryRollbackConstrainedPreviewContent(
                    host,
                    expectedCurrentRequest: restoredRequest,
                    originalRequest: request,
                    replacement))
            {
                _edgeCapsulePreviewRequest = request;
            }
            return false;
        }""",
)

replace_once(
    "src/PaperWindow.EdgeCapsulePreviewCapacityRecovery.cs",
    """    private bool TryRollbackConstrainedPreviewContent(
        EdgeCapsuleHost host,
        EdgeCapsulePreviewRequest request,
        FrameworkElement replacement)
    {
        if (!ReferenceEquals(_edgeCapsulePreviewRequest, request) ||
            !host.OwnsPreviewContent(replacement))
        {
            return false;
        }
        var oldSize = request.Size.ContentSize;
        return host.ReplacePreviewContent(
            replacement,
            request.Content,
            oldSize.Width,
            oldSize.Height);
    }""",
    """    private bool TryRollbackConstrainedPreviewContent(
        EdgeCapsuleHost host,
        EdgeCapsulePreviewRequest expectedCurrentRequest,
        EdgeCapsulePreviewRequest originalRequest,
        FrameworkElement replacement)
    {
        if (!ReferenceEquals(_edgeCapsulePreviewRequest, expectedCurrentRequest) ||
            !host.OwnsPreviewContent(replacement))
        {
            return false;
        }
        var oldSize = originalRequest.Size.ContentSize;
        return host.ReplacePreviewContent(
            replacement,
            originalRequest.Content,
            oldSize.Width,
            oldSize.Height);
    }""",
)

# Use the primary request constructor in both the seven-parameter candidate and the five-parameter
# pinned #260 baseline. The previous BindingFlags helper could select a record copy constructor and
# then pass a five/seven element argument array, which caused TargetParameterCountException before
# either product behavior was exercised.
replace_once(
    "tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs",
    """        var ctor = typeof(EdgeCapsulePreviewRequest).GetConstructors(CapacityCheckFields).Single();
        var args = ctor.GetParameters().Length > 5
            ? new object?[] { constrained, oldContent, null, null, null, target, factory }
            : new object?[] { constrained, oldContent, null, null, null };
        var request = (EdgeCapsulePreviewRequest)ctor.Invoke(args);""",
    """        var ctor = typeof(EdgeCapsulePreviewRequest)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length >= 5 &&
                    parameters[0].ParameterType == typeof(EdgeCapsulePreviewSize) &&
                    parameters[1].ParameterType == typeof(System.Windows.FrameworkElement);
            })
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();
        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = constrained;
        args[1] = oldContent;
        if (parameters.Length >= 7)
        {
            args[5] = target;
            args[6] = factory;
        }
        var request = (EdgeCapsulePreviewRequest)ctor.Invoke(args);""",
)

# QueueKey(PaperData) is a private static helper. CapacityCheckFields is intentionally instance-only
# for most fixture access, so using it here returned null and the second H7 run died before product
# recovery was exercised. Resolve the exact static overload instead of weakening the shared flags.
replace_once(
    "tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs",
    """        var queueKeyMethod = typeof(AppController).GetMethod("QueueKey", CapacityCheckFields)!;
        var queueKey = (string)queueKeyMethod.Invoke(controller, new object[] { fixture.Paper })!;""",
    """        var queueKeyMethod = typeof(AppController).GetMethod(
            "QueueKey",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(PaperData) },
            modifiers: null)!;
        var queueKey = (string)queueKeyMethod.Invoke(null, new object[] { fixture.Paper })!;""",
)

# MaximumCapacityFixture intentionally creates AppController with GetUninitializedObject so it can
# focus on HWND/source-capacity behavior without loading user state. H7 now legitimately enters the
# real controller layout transaction, whose readonly collection/gate fields would normally have
# been created by field initializers. Recreate only those structural fields the synchronous layout
# path needs; do not fake queue-proxy ownership or compositor results.
replace_once(
    "tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs",
    """        var controller = GetCapacityCheckField<AppController>(fixture.Window, "_controller");
        var windows = GetCapacityCheckField<Dictionary<string, PaperWindow>>(controller, "_windows");
        windows[fixture.Paper.Id] = fixture.Window;
""",
    """        var controller = GetCapacityCheckField<AppController>(fixture.Window, "_controller");
        var windows = GetCapacityCheckField<Dictionary<string, PaperWindow>>(controller, "_windows");
        windows[fixture.Paper.Id] = fixture.Window;
        foreach (var fieldName in new[]
                 {
                     "_deepCapsuleArrangeGate",
                     "_deepCapsuleContextMenuOwners",
                     "_masterCapsules",
                     "_edgeCapsuleVisualTransactionEntries",
                     "_edgeCapsuleVisualTransactionQueueKeys"
                 })
        {
            var field = typeof(AppController).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (field.GetValue(controller) == null)
            {
                field.SetValue(controller, Activator.CreateInstance(field.FieldType, nonPublic: true));
            }
        }
""",
)

# Recovery and controller-session replacement are synchronous. Pumping ApplicationIdle here would
# execute the pending Send-priority visual transaction against the intentionally partial controller
# fixture, testing fixture construction rather than H7. Assert the synchronous state first, abort
# that queued visual commit, then exercise the rollback boundary independently.
replace_once(
    "tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs",
    """        fixture.Retained.Clear();
        fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
        fixture.Host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var actual = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,""",
    """        fixture.Retained.Clear();
        fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();

        var actual = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,""",
)

replace_once(
    "tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs",
    """            Check(session is { } restored && restored.Size == target,
                \"Controller session adopts the recovered size instead of resetting the preview\");

            var restoredContent = actual.Content;""",
    """            Check(session is { } restored && restored.Size == target,
                \"Controller session adopts the recovered size instead of resetting the preview\");
            var pendingVisualCommit = GetCapacityCheckField<DispatcherOperation?>(controller,
                \"_edgeCapsuleVisualTransactionCommitOperation\");
            pendingVisualCommit?.Abort();

            var restoredContent = actual.Content;""",
)

replace_once(
    "tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs",
    """            fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            fixture.Host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var rolledBack = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,""",
    """            fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            var rolledBack = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,""",
)

# After the happy-path recovery, put the same live request back into a constrained state while the
# controller still owns the same paper but deliberately carries a mismatched session size. That
# allows content replacement to happen, then forces TryRestoreEdgeCapsulePreviewSessionSize to
# reject. The product must roll both the Host content and the PaperWindow request back atomically.
replace_once(
    "tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs",
    """            Check(session is { } restored && restored.Size == target,
                \"Controller session adopts the recovered size instead of resetting the preview\");
            var pendingVisualCommit = GetCapacityCheckField<DispatcherOperation?>(controller,
                \"_edgeCapsuleVisualTransactionCommitOperation\");
            pendingVisualCommit?.Abort();

            var restoredContent = actual.Content;
            Check(fixture.Host.ReplacePreviewContent(
                    restoredContent,
                    oldContent,
                    constrained.ContentSize.Width,
                    constrained.ContentSize.Height),
                \"Rollback regression restores a constrained Host setup before forcing controller rejection\");
            SetCapacityCheckField(fixture.Window, \"_edgeCapsulePreviewRequest\", request);
            SetCapacityCheckField(fixture.Window, \"_edgeCapsulePendingPreviewCapacity\", target);
            var mismatchedSessionSize = constrained with
            {
                HeightDip = Math.Max(1, constrained.HeightDip - 1)
            };
            SetCapacityCheckField(controller, \"_edgeCapsulePreviewSession\",
                new EdgeCapsulePreviewLayoutSession(queueKey, fixture.Paper.Id, mismatchedSessionSize,
                    new[] { fixture.Paper.Id }, new Dictionary<string, double>(StringComparer.Ordinal)
                    { [fixture.Paper.Id] = 0 }));

            fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            var rolledBack = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,
                \"_edgeCapsulePreviewRequest\");
            Check(ReferenceEquals(rolledBack, request) && rolledBack.Size == constrained &&
                ReferenceEquals(rolledBack.Content, oldContent),
                \"Controller size rejection restores the original constrained preview request\");
            Check(fixture.Host.OwnsPreviewContent(oldContent),
                \"Controller size rejection restores the original constrained Host content\");
            Check(GetCapacityCheckField<int>(fixture.Window, \"_edgeCapsulePreviewContentGeneration\") ==
                    contentGeneration,
                \"Rollback keeps the same preview content generation\");
            Console.WriteLine(\"RESULT pr260-capacity-rollback protected=True\");
        }
        else""",
    """            Check(session is { } restored && restored.Size == target,
                \"Controller session adopts the recovered size instead of resetting the preview\");
            var pendingVisualCommit = GetCapacityCheckField<DispatcherOperation?>(controller,
                \"_edgeCapsuleVisualTransactionCommitOperation\");
            pendingVisualCommit?.Abort();

            var restoredContent = actual.Content;
            Check(fixture.Host.ReplacePreviewContent(
                    restoredContent,
                    oldContent,
                    constrained.ContentSize.Width,
                    constrained.ContentSize.Height),
                \"Rollback regression restores a constrained Host setup before forcing controller rejection\");
            SetCapacityCheckField(fixture.Window, \"_edgeCapsulePreviewRequest\", request);
            SetCapacityCheckField(fixture.Window, \"_edgeCapsulePendingPreviewCapacity\", target);
            var mismatchedSessionSize = constrained with
            {
                HeightDip = Math.Max(1, constrained.HeightDip - 1)
            };
            SetCapacityCheckField(controller, \"_edgeCapsulePreviewSession\",
                new EdgeCapsulePreviewLayoutSession(queueKey, fixture.Paper.Id, mismatchedSessionSize,
                    new[] { fixture.Paper.Id }, new Dictionary<string, double>(StringComparer.Ordinal)
                    { [fixture.Paper.Id] = 0 }));

            fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            var rolledBack = GetCapacityCheckField<EdgeCapsulePreviewRequest?>(fixture.Window,
                \"_edgeCapsulePreviewRequest\");
            Check(ReferenceEquals(rolledBack, request) && rolledBack.Size == constrained &&
                ReferenceEquals(rolledBack.Content, oldContent),
                \"Controller size rejection restores the original constrained preview request\");
            Check(fixture.Host.OwnsPreviewContent(oldContent),
                \"Controller size rejection restores the original constrained Host content\");
            Check(GetCapacityCheckField<int>(fixture.Window, \"_edgeCapsulePreviewContentGeneration\") ==
                    contentGeneration,
                \"Rollback keeps the same preview content generation\");
            Console.WriteLine(\"RESULT pr260-capacity-rollback protected=True\");
        }
        else""",
)
