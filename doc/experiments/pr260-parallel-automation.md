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
