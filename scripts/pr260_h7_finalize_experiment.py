from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    count = text.count(old)
    if count != 1:
        preview = old.splitlines()[0] if old else "<empty>"
        raise SystemExit(f"{path}: expected exactly one match, found {count}; anchor={preview!r}")
    p.write_text(text.replace(old, new), encoding="utf-8", newline="")


# The first H7 draft switched the live request before asking the controller to adopt the new
# session size, but its rollback helper still required the old request to be current. Make the
# request expected at rollback time explicit and restore content from the original constrained
# request on either pre-switch or post-switch failure.
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

# The record also has a copy constructor. Select the primary request constructor by its first two
# parameter types so the same test source can run against both the 5-field pinned #260 record and
# the 7-field H7 candidate record.
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

# QueueKey(PaperData) is a private static overload, while the fixture's shared reflection flags are
# instance-only. Resolve the exact static helper instead of broadening every fixture lookup.
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

# MaximumCapacityFixture intentionally bypasses AppController field initializers. H7 legitimately
# enters the real controller layout transaction, so restore only the structural fields used by that
# synchronous path; do not fake compositor ownership, source identity or publication results.
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

# Recovery and session replacement are synchronous. Pumping ApplicationIdle here would run the
# queued visual transaction against this intentionally partial controller fixture, which would test
# fixture construction instead of H7. Read the synchronous state and abort that queued commit after
# the happy-path assertions.
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

# Extend the candidate-only branch with an explicit post-Host-replacement controller rejection.
# This is the boundary where the first H7 draft's rollback could not match the current request.
replace_once(
    "tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs",
    """            Check(session is { } restored && restored.Size == target,
                \"Controller session adopts the recovered size instead of resetting the preview\");
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
