using System.Diagnostics;
using PaperTodo;

internal static partial class NativeFreeQueueReviewChecks
{
    private static async Task RunAudit(AppController c, IReadOnlyDictionary<string, PaperWindow> windows)
    {
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "monitor");
        var papers = c.State.Papers.ToArray();
        var owner = papers.Single(p => p.Id == "review-10");
        var right = papers.Single(p => p.Id == "review-6");
        foreach (var count in new[] { 3, 5, 11 })
        {
            var ids = new[] {10, 6, 7, 8, 9, 0, 1, 2, 3, 4, 5}.Take(count).Select(i => "review-" + i).ToHashSet();
            foreach (var p in papers) p.IsVisible = ids.Contains(p.Id);
            c.State.UseCapsuleFreeQueues = true;
            c.State.CapsuleCollapseAllActiveQueues.Clear();
            c.ArrangeDeepCapsules(false);
            await Idle();
            var t = MemberTop(c, windows[right.Id], right);
            var ownerTop = MemberTop(c, windows[owner.Id], owner);
            Require(c.BeginNativeEdgeQueueDrag(owner, false, Point(monitor, ownerTop, left: true), false), "begin audit");
            try
            {
                Require(c.PreviewNativeEdgeQueueDrag(owner, Point(monitor,t), t, true), "floating");
                for (var i=0;i<16;i++) c.PreviewNativeEdgeQueueDrag(owner, Point(monitor,t+i%2), t+i%2, true);
                var drag = Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag");
                var first = drag.Drop!.Decision;
                Require(first.Kind == EdgeFreeQueueDropKind.Insert, "warm-up selects merge, actual=" + first.Kind);
                int p0=CapsuleAuditCounts.Plans, tx=CapsuleAuditCounts.Titles, mx=CapsuleAuditCounts.MasterTexts, gw=CapsuleAuditCounts.GhostWrites;
                var samples = new double[180];
                var before = GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<samples.Length;i++)
                {
                    var y=t+i%2;
                    var point=Point(monitor,y);
                    var start=Stopwatch.GetTimestamp();
                    c.PreviewNativeEdgeQueueDrag(owner,point,y,true);
                    samples[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                var bytes = GC.GetAllocatedBytesForCurrentThread()-before;
                Array.Sort(samples);
                Console.WriteLine($"CAPS PERF visible={count} samples={samples.Length} kind={drag.Drop!.Decision.Kind} same_slot={first.Index == drag.Drop.Decision.Index && first.TargetKey == drag.Drop.Decision.TargetKey} p50ms={samples[90]:F3} p95ms={samples[171]:F3} bytesPer={bytes/samples.Length} planCalls={CapsuleAuditCounts.Plans-p0} paperFormats={CapsuleAuditCounts.Titles-tx} masterFormats={CapsuleAuditCounts.MasterTexts-mx} ghostWrites={CapsuleAuditCounts.GhostWrites-gw}");
            }
            finally {c.EndNativeEdgeQueueDrag(owner.Id);}
            await Idle();
        }
        foreach (var p in papers) p.IsVisible = p.Id is "review-10" or "review-6" or "review-7";
        c.State.UseCapsuleFreeQueues = false;
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        var sourceKey=c.QueueKeyForPaper(owner);
        var targetKey=c.QueueKeyForPaper(right);
        c.State.DeepCapsuleQueueStartTopMargins[sourceKey]=50;
        c.State.DeepCapsuleQueueStartTopMargins[targetKey]=80;
        c.State.CapsuleCollapseAllActiveQueues[targetKey]=true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        var original=Masters(c)[targetKey].AnchorTop;
        Require(c.TryBeginMasterCapsuleQueueTransfer(owner.CapsuleMonitorDeviceName, EdgeCapsuleEdge.Left, c.MasterCapsuleStartTopMargin(sourceKey), out var transfer), "start old-style master");
        var release = Point(monitor,320);
        c.PreviewFreeMasterQueueDropGhost(transfer,release);
        var ghost=Field<EdgeCapsuleQueueDropGhost>(c,"_nativeQueueDropGhost");
        Require(Field<bool>(ghost,"_mergeHalo"), "folded halo");
        var haloBounds=Field<DeviceScreenRect>(ghost,"_lastBounds");
        var haloTop=(haloBounds.Top-monitor.WorkArea.Top)/monitor.DpiScaleY+4;
        Require(c.CommitMasterCapsuleQueueTransfer(transfer,release), "commit folded merge");
        c.EndFreeMasterQueueDropGhost();
        await Idle();
        Console.WriteLine($"CAPS FOLDED initial={original:F1} preview={haloTop:F1} requested=320 persisted={c.State.DeepCapsuleQueueStartTopMargins[targetKey]:F1} actual={Masters(c)[targetKey].AnchorTop:F1} folded={c.State.CapsuleCollapseAllActiveQueues.GetValueOrDefault(targetKey)}");
    }
}
