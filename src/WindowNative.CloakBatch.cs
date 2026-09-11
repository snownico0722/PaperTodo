using System.Runtime.InteropServices;

namespace PaperTodo;

internal static partial class WindowNative
{
#if DEBUG
    // Read-only observation: do not flush composition or change visibility for diagnostics.
    internal static string DescribeCompositionVisibility(IntPtr handle)
    {
        var alive = handle != IntPtr.Zero && IsWindow(handle);
        var bounds = alive && GetWindowRect(handle, out var rect)
            ? $"{rect.Left},{rect.Top},{rect.Right - rect.Left}x{rect.Bottom - rect.Top}"
            : "<unknown>";
        var cloakFlags = alive && DwmGetWindowAttribute(
            handle, DwmWaCloaked, out int flags, Marshal.SizeOf<int>()) == 0
            ? flags.ToString()
            : "<unknown>";
        return $"hwnd={handle.ToInt64():X} alive={alive} " +
            $"nativeVisible={alive && IsWindowVisible(handle)} " +
            $"cloakFlags={cloakFlags} nativeBounds={bounds}";
    }
#endif

    /// <summary>
    /// One member of an all-or-rollback DWM cloak transaction. Callers provide the known state to
    /// restore because every queue handoff already owns a uniform source state: newly acquired
    /// sources are visible, while sources released at the endpoint are app-cloaked.
    /// </summary>
    internal readonly record struct WindowCloakChange(
        IntPtr Handle,
        bool Cloaked,
        bool RollbackCloaked);

    internal enum WindowCloakBatchResult
    {
        Success = 0,
        RolledBack = 1,
        RollbackFailed = 2
    }

    internal static bool TryFlushDesktopComposition() =>
        DwmFlush() == 0;

    /// <summary>
    /// Applies a set of DWMWA_CLOAK changes as one desktop-composition boundary. DWM has no native
    /// multi-HWND cloak API, so the important ordering is: set every handle, optionally publish the
    /// caller's coordinated visual change, flush once, then verify every handle. If any step fails,
    /// every attempted handle and coordinated visual change are restored before one rollback flush.
    /// </summary>
    internal static bool TrySetWindowCloakedBatch(
        IReadOnlyCollection<WindowCloakChange> requestedChanges,
        Func<bool>? publishBeforeFlush = null,
        Action? rollbackBeforeFlush = null) =>
        TrySetWindowCloakedBatchDetailed(
            requestedChanges,
            publishBeforeFlush,
            rollbackBeforeFlush) ==
        WindowCloakBatchResult.Success;

    internal static WindowCloakBatchResult
        TrySetWindowCloakedBatchDetailed(
            IReadOnlyCollection<WindowCloakChange> requestedChanges,
            Func<bool>? publishBeforeFlush = null,
            Action? rollbackBeforeFlush = null)
    {
        if (requestedChanges.Count == 0 &&
            publishBeforeFlush == null)
        {
            return WindowCloakBatchResult.Success;
        }

        var changesByHandle =
            new Dictionary<IntPtr, WindowCloakChange>();
        foreach (var change in requestedChanges)
        {
            if (change.Handle == IntPtr.Zero ||
                !IsWindow(change.Handle))
            {
                return WindowCloakBatchResult.RolledBack;
            }
            if (changesByHandle.TryGetValue(
                    change.Handle,
                    out var existing))
            {
                if (existing.Cloaked != change.Cloaked ||
                    existing.RollbackCloaked !=
                        change.RollbackCloaked)
                {
                    return WindowCloakBatchResult.RolledBack;
                }
                continue;
            }
            changesByHandle.Add(change.Handle, change);
        }

        var changes = changesByHandle.Values.ToArray();
        var attempted =
            new List<WindowCloakChange>(changes.Length);
#if DEBUG
        var batchStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
        var setStartedAt = batchStartedAt;
#endif
        var setSucceeded = true;
        foreach (var change in changes)
        {
            attempted.Add(change);
            if (!TrySetWindowCloakAttribute(
                    change.Handle,
                    change.Cloaked))
            {
                setSucceeded = false;
                break;
            }
        }

        var publishSucceeded = setSucceeded;
        if (publishSucceeded &&
            publishBeforeFlush != null)
        {
            try
            {
                publishSucceeded = publishBeforeFlush();
            }
            catch
            {
                publishSucceeded = false;
            }
        }
#if DEBUG
        var setMilliseconds =
            EdgeCapsulePerformanceDiagnostics
                .ElapsedMilliseconds(setStartedAt);
        var flushStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var flushed =
            publishSucceeded && DwmFlush() == 0;
#if DEBUG
        var flushMilliseconds =
            EdgeCapsulePerformanceDiagnostics
                .ElapsedMilliseconds(flushStartedAt);
        var verifyStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var verified = flushed;
        if (verified)
        {
            foreach (var change in changes)
            {
                if (!TryGetWindowAppCloaked(
                        change.Handle,
                        out var actual) ||
                    actual != change.Cloaked)
                {
                    verified = false;
                    break;
                }
            }
        }
#if DEBUG
        var verifyMilliseconds =
            EdgeCapsulePerformanceDiagnostics
                .ElapsedMilliseconds(verifyStartedAt);
#endif
        if (verified)
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"native.cloak phase=batch outcome=success " +
                $"count={changes.Length} attempted={attempted.Count} " +
                $"coordinated={publishBeforeFlush != null} " +
                $"setMs={setMilliseconds:F3} " +
                $"flushMs={flushMilliseconds:F3} " +
                $"verifyMs={verifyMilliseconds:F3} " +
                $"totalMs=" +
                $"{EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(batchStartedAt):F3}");
#endif
            return WindowCloakBatchResult.Success;
        }

        var rollbackSucceeded = true;
        for (var index = attempted.Count - 1;
             index >= 0;
             index--)
        {
            var change = attempted[index];
            rollbackSucceeded &=
                TrySetWindowCloakAttribute(
                    change.Handle,
                    change.RollbackCloaked);
        }
        try
        {
            rollbackBeforeFlush?.Invoke();
        }
        catch
        {
            rollbackSucceeded = false;
        }
        rollbackSucceeded &= DwmFlush() == 0;
        if (rollbackSucceeded)
        {
            foreach (var change in attempted)
            {
                if (!TryGetWindowAppCloaked(
                        change.Handle,
                        out var actual) ||
                    actual != change.RollbackCloaked)
                {
                    rollbackSucceeded = false;
                    break;
                }
            }
        }

#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"native.cloak phase=batch outcome=" +
            $"{(rollbackSucceeded ? "rollback" : "rollback-failed")} " +
            $"count={changes.Length} attempted={attempted.Count} " +
            $"coordinated={publishBeforeFlush != null} " +
            $"setMs={setMilliseconds:F3} " +
            $"flushMs={flushMilliseconds:F3} " +
            $"verifyMs={verifyMilliseconds:F3} " +
            $"totalMs=" +
            $"{EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(batchStartedAt):F3}");
#endif
        return rollbackSucceeded
            ? WindowCloakBatchResult.RolledBack
            : WindowCloakBatchResult.RollbackFailed;
    }

    private static bool TrySetWindowCloakAttribute(
        IntPtr handle,
        bool cloaked)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return false;
        }
        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(
            handle,
            DwmWaCloak,
            ref value,
            Marshal.SizeOf<int>()) == 0;
    }

    private static bool TryGetWindowAppCloaked(
        IntPtr handle,
        out bool cloaked)
    {
        cloaked = false;
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return false;
        }
        if (DwmGetWindowAttribute(
                handle,
                DwmWaCloaked,
                out int flags,
                Marshal.SizeOf<int>()) != 0)
        {
            return false;
        }
        cloaked = (flags & DwmCloakedApp) != 0;
        return true;
    }
}
