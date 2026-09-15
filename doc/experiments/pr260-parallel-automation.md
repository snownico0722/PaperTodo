# PR260 parallel experiment ledger

Task line: independent experiments from PR #260 `953ae6266af2bc8c5d1696be7660e5006525202b`.

This branch is intentionally separate from PR #269. It reads #269 as evidence but does not write its branch.

## R0 / 2026-09-15 11:00 CST

### Read state

- #260 remains Draft at `953ae626`; its own description still lists physical-frame validation, mixed-DPI/long-idle cost and unrelated desktop pointer activity as open risks.
- #269 exists and by its R2 checkpoint has already validated/fixed H1 cross-queue prewarm invalidation, H2 moving-pointer selective handoff, H4 empty-startup completion and H3 predecessor managed lifetime. It plans H6 unified graphics prewarm first, then H5 unrelated desktop pointer scope.
- This branch therefore avoids redoing H1/H2/H3/H4/H6 first and starts on the complementary H7 capacity-recovery question.

### H7 hypothesis

When `PrepareEdgeCapsulePreview()` cannot grow a live source retained by a queue proxy, it records the requested size, constrains the current request to existing host capacity, and creates content against that constrained size. After proxy release, `ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease()` can grow the source HWND and clear `_edgeCapsulePendingPreviewCapacity`, but it does not change `_edgeCapsulePreviewRequest.Size` or rebuild the current content. `AppController.ApplyEdgeCapsulePreviewLayout()` continues to treat the constrained request size as the active session size.

Important counter-evidence: `EdgeCapsulePreviewDescriptor` currently documents that the host “freezes” normalized size for the preview session. So H7 must not be called a product bug until a focused user-path test proves that this constrained-session freeze is unintended or visibly wrong.

### R0 plan

1. Create this independent PR/branch checkpoint.
2. Add narrow diagnostics around successful deferred-capacity resume so a Windows focused run can distinguish host-capacity recovery from current-request recovery.
3. Add a focused regression/harness only after identifying the smallest real production path; do not fake a result by mutating private request fields alone.
4. If baseline proves current open preview remains undersized after authority release, prefer reusing the existing guarded open/transfer path to rebuild the same owner instead of mutating `Request.Size` without recreating size-dependent content.

### Evidence level

Current H7 status is static source tracing only. No Windows/WPF reproduction, no timing claim and no physical-present claim yet.

## R4 / 2026-09-15 15:00 CST — H8 small-worker alternative rejected

### Why this round did not follow #269

- #269 has already closed H6 by removing the old `PaperWindow` direct `PrewarmLightweight()` path.
- Its current head is 27 commits beyond the latest written R4 checkpoint and the new files are overwhelmingly H7 capacity-restore harness/workflow/scripts. This branch therefore stayed on H8 rather than duplicating H7.
- Our R3 already upgraded H8 from a static timing concern to a real Windows/cross-process correctness defect: during a 360 ms UI-thread stall, autonomous DComp motion advances the visible card while the UI-owned input HRGN remains behind, causing both visible-pixel click leakage and stale-empty-region blocking.

### Experiment

Before attempting a dedicated native input thread or changing the production animation model, test the smallest possible correction:

> Keep the existing input HWND owned by the UI thread, but let a background worker follow the exact same QPC/ease-out clock and call the existing `TrySetInputRegions()` while the UI thread is stalled.

One-shot test commits were `db7ab8d` / `c34fdac` / `effa1734`; Windows run `34941737943` built Release with 0 warnings / 0 errors and ran the real WPF + `CreateSurfaceFromHwnd` + DirectComposition setup.

Observed at the end of the 360 ms UI stall:

```text
attemptsAtWake=1
completedAtWake=0
successfulAtWake=0
lastCompletedLeft=232
initialLeft=232
pixelsAtWake=[352,400) count=48
ownsLeadingPixel=False
firstCompletionTimestamp=0
```

The compositor advanced the entire 48 px source by 120 px, but the worker's first region mutation had not returned at all. This matches the production implementation note that `SetWindowRgn` sends synchronous window-position messages: moving the existing HRGN mutation onto an arbitrary worker does not decouple it from the UI-owned HWND/message pump.

### Conclusion / rejected direction

`ThreadPool/Timer -> existing UI-owned InputHandle -> TrySetInputRegions/SetWindowRgn` is **not** a viable H8 fix. It cannot make progress during the exact UI unavailability window that causes H8, so merely replacing the `DispatcherTimer` with a worker timer would retain the correctness defect.

This does **not** prove a dedicated input-owner thread is sufficient. Even a separate message pump would still need to demonstrate that QPC-driven HRGN updates do not produce leading/trailing-frame leakage under normal scheduling jitter. R3's two-sided cross-process acceptance test remains the required gate.

The one-shot worker probe and its workflow were removed immediately after evidence collection (`11f8200`, `73206c2`, `c6ab4bf`) so #271 does not retain a dead alternative or another permanent CI lane.

### Next experiment

The next H8 candidate should be narrowly prototyped before production integration:

1. create the input-only HWND on a dedicated native owner thread/message loop (output/DComp stays on the current architecture);
2. give that owner an immutable animation ticket: QPC start, duration, source/target device-pixel rectangles, generation/owner token;
3. during active translation, that owner computes the same curve independently of WPF Dispatcher progress and publishes finite HRGNs; static/retained shape changes still arrive as validated commands from the UI side;
4. rerun the R3 real-DComp 360 ms UI-stall test and require both conditions to be zero: visible-pixel leakage to the lower process and stale-empty-region blocking;
5. if a separately-owned HWND still exhibits frame-edge mismatch, stop adding timers and move to a different authority protocol rather than hiding the failure with a full motion-envelope region.

### Evidence level

This round is a Windows automated experiment on real user32/DComp/WPF primitives, not a product fix and not a physical-scanout proof. Actual model: GPT-5.6 Sol. No merge/main/#260/#269 writes were performed.
