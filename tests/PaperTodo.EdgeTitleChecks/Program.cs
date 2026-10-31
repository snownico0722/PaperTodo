using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static int assertions;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetPhysicalCursorPos(out NativeCursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeCursorPoint
    {
        public int X;
        public int Y;
    }

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            DetachedPointerAdmissionChecks();
            CycleAndUnicode();
            Console.WriteLine("PASS title-cycle-and-unicode");
            QueueInstanceKeys();
            Console.WriteLine("PASS queue-instance-key-codec");
            QueueDragModifierLatch();
            Console.WriteLine("PASS queue-drag-modifier-latch");
            CursorPhysicalCoordinates();
            Console.WriteLine("PASS cursor-physical-coordinates");
            QueueMagnet();
            Console.WriteLine("PASS queue-magnet-merge-detach-reorder");
            EdgeCapsuleQueueInsertionSolverChecks();
            Console.WriteLine("PASS queue-insertion-solver");
            EdgeCapsuleRestingLayoutChecks();
            Console.WriteLine("PASS queue-resting-layout");
            EdgeCapsuleShortcutOrdinalChecks();
            Console.WriteLine("PASS free-queue-shortcut-ordinal");
            BottomQueueInsertionMagnet();
            Console.WriteLine("PASS bottom-queue-insertion-magnet");
            Persistence();
            Console.WriteLine("PASS legacy-and-zero-persistence");
            Geometry();
            Console.WriteLine("PASS title-presentation-and-transition-geometry");
            HostContentVisibility();
            Console.WriteLine("PASS host-title-plugin-and-icon-slot-layout");
            QueuedPreviewTransactions();
            Console.WriteLine("PASS queued-preview-transaction-ordering");
            Console.WriteLine($"Edge title checks: {assertions} assertions passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Application.Current.Shutdown(); }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    // 第 4 项回归：即使线程处于 System Aware 上下文，包装函数也必须返回物理像素。
    // 锁屏/无输入桌面时 GetPhysicalCursorPos 失败，跳过而不阻塞 CI。
    private static void CursorPhysicalCoordinates()
    {
        if (!GetPhysicalCursorPos(out var expected))
        {
            return;
        }

        var previous = SetThreadDpiAwarenessContext(new IntPtr(-2));
        try
        {
            if (!WindowNative.TryGetCursorScreenPosition(out var sampled))
            {
                Check(false, "The cursor wrapper failed while GetPhysicalCursorPos succeeded");
                return;
            }

            // 两次调用之间光标可能移动，保留 2 像素容差。
            Check(Math.Abs(sampled.X - expected.X) <= 2 &&
                  Math.Abs(sampled.Y - expected.Y) <= 2,
                "The cursor wrapper returns physical coordinates outside PMv2");
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                _ = SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    // A free queue narrows (monitor, side) with an instance id. The whole backward-compatibility
    // story rests on one invariant: an empty id must compose to the byte-identical pre-D-058 key.
    private static void QueueInstanceKeys()
    {
        const string edge = "DISPLAY1|right";
        Check(EdgeCapsuleQueueIds.Compose(edge, "") == edge, "Empty id keeps the legacy key byte-identical");
        Check(EdgeCapsuleQueueIds.Compose(edge, null) == edge, "Null id keeps the legacy key");
        Check(EdgeCapsuleQueueIds.Compose(edge, "q1") == $"{edge}#q1", "Instance id appends after the separator");

        // Normalization is what keeps hand-edited or hostile data from producing bogus keys.
        Check(EdgeCapsuleQueueIds.Normalize(null) == "", "Null id");
        Check(EdgeCapsuleQueueIds.Normalize("   ") == "", "Whitespace-only id");
        Check(EdgeCapsuleQueueIds.Normalize("!!!") == "", "Id with no usable characters");
        Check(EdgeCapsuleQueueIds.Normalize(" Q1 ") == "q1", "Trim and lowercase");
        Check(EdgeCapsuleQueueIds.Normalize("a-b_c") == "abc", "Disallowed characters are dropped, not rejected");
        Check(EdgeCapsuleQueueIds.Compose(edge, " Q1 ") == $"{edge}#q1", "Compose normalizes before appending");
        Check(EdgeCapsuleQueueIds.Normalize(new string('x', 64)).Length == EdgeCapsuleQueueIds.MaxLength,
            "Over-long id is truncated");

        Check(EdgeCapsuleQueueIds.TrySplit($"{edge}#q1", out var splitEdge, out var splitId) &&
              splitEdge == edge && splitId == "q1", "Split a suffixed key");
        Check(!EdgeCapsuleQueueIds.TrySplit(edge, out var plainEdge, out var plainId) &&
              plainEdge == edge && plainId == "", "Unsuffixed key splits to an empty id");

        // The primary display serializes to an empty monitor segment; the separator must survive.
        Check(EdgeCapsuleQueueIds.TrySplit("|left#a", out var bareEdge, out var bareId) &&
              bareEdge == "|left" && bareId == "a", "Empty monitor segment");

        Check(EdgeCapsuleQueueIds.StripInstance($"{edge}#q1") == edge, "Strip removes the instance");
        Check(EdgeCapsuleQueueIds.StripInstance(edge) == edge, "Strip is a no-op without an instance");
        Check(EdgeCapsuleQueueIds.StripInstance("|right#a") == "|right", "Strip preserves an empty monitor segment");

        // 边级范围解析：迷你视图避让必须同时按 monitor 和 side 过滤。
        Check(EdgeCapsuleQueueIds.TryGetEdge($"{edge}#q1", out var edgeMonitor, out var edgeSide) &&
              edgeMonitor == "DISPLAY1" && edgeSide == "right",
            "TryGetEdge splits the monitor and side");
        Check(EdgeCapsuleQueueIds.TryGetEdge("|left#a", out var primaryMonitor, out var primarySide) &&
              primaryMonitor == "" && primarySide == "left",
            "TryGetEdge preserves the empty primary monitor segment");
        Check(!EdgeCapsuleQueueIds.TryGetEdge("right", out _, out _),
            "TryGetEdge rejects a key without an edge separator");
        Check(!EdgeCapsuleQueueIds.TryGetEdge(null, out _, out _),
            "TryGetEdge rejects a null key");
        Check(EdgeCapsuleQueueIds.IsSameEdge($"{edge}#q1", $"{edge}#q2"),
            "Same monitor and side compare equal across instance ids");
        Check(!EdgeCapsuleQueueIds.IsSameEdge($"{edge}#q1", "DISPLAY2|right#q1"),
            "Different monitors must not share an edge");
        Check(!EdgeCapsuleQueueIds.IsSameEdge("DISPLAY1|left", "DISPLAY1|right"),
            "Different sides must not share an edge");
        Check(EdgeCapsuleQueueIds.IsSameEdge("|right#a", "|right"),
            "The primary monitor edge matches without an instance id");
        Check(!EdgeCapsuleQueueIds.IsSameEdge("right", "right"),
            "Malformed keys are never the same edge");

        Check(EdgeCapsuleQueueIds.New() != EdgeCapsuleQueueIds.New(), "New ids differ");
        Check(EdgeCapsuleQueueIds.New().Length <= EdgeCapsuleQueueIds.MaxLength, "New id fits the length bound");
    }

    // The magnet is a Schmitt trigger: enter at 18 DIP, leave at 32 DIP, dead band between.
    // Everything it decides must be derivable from (position, candidates, held), so the whole
    // behaviour is checkable without a window, a pointer, or a clock.
    private static void QueueMagnet()
    {
        const double slot = 50;   // capsule height 46 + standard gap 4
        const double height = 46;

        // Every case below is purely vertical, so the drag centre is placed inside this band and
        // the horizontal term is exactly 0. That makes the two-axis distance reduce to the old
        // one-axis distance bit for bit, which is what keeps these assertions valid as the
        // regression baseline for "same-wall dragging feels unchanged".
        const double bandLeft = 0;
        const double bandWidth = 200;
        const double centreLeft = 100;

        EdgeCapsuleQueueMagnetPlan Evaluate(double center, string source,
            EdgeCapsuleQueueMagnetCandidate[] candidates, string held = "") =>
            EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
                center, source, candidates, slot, height,
                EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
                EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
                held, centreLeft));

        // Queue "a" spans one slot at top 100; "b" sits far below at 600.
        EdgeCapsuleQueueMagnetCandidate[] TwoQueues() =>
        [
            new("a", 1, 100, slot, bandLeft, bandWidth, 1, 100),
            new("b", 1, 600, slot, bandLeft, bandWidth, 1, 600)
        ];

        Check(Evaluate(100, "", TwoQueues()).Outcome == EdgeCapsuleQueueMagnetOutcome.Merge,
            "Dragging onto a queue merges into it");
        Check(Evaluate(100, "", TwoQueues()).TargetQueueKey == "a", "Merges into the nearest queue");

        // Nothing within merge range -> the capsule becomes its own queue at the drop height.
        var fresh = Evaluate(300, "", TwoQueues());
        Check(fresh.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue, "A far drop starts a new queue");
        Check(Math.Abs(fresh.NewQueueStartTopDip - (300 - height / 2)) < 0.001,
            "A new queue anchors the drop by its capsule top, not its centre");

        // Queue "a" rises from top 100 and its single capsule really occupies [100, 146]. The magnet
        // measures edge to edge, so the zero-distance band for the drag centre is [77, 169] and the
        // merge-reachable band is [59, 187]. The dead band is therefore 187 .. 201: too far to
        // acquire from scratch, close enough to keep holding. That asymmetry is the whole point.
        const double deadBandCentre = 194;   // 25 DIP below the zero-distance band
        Check(Evaluate(deadBandCentre, "", TwoQueues()).Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "The dead band is too far to acquire a queue from scratch");
        Check(Evaluate(deadBandCentre, "", TwoQueues(), held: "a").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.Merge,
            "The dead band keeps an already-held queue attached");

        const double beyondDetach = 206;     // 37 DIP below the zero-distance band
        Check(Evaluate(beyondDetach, "", TwoQueues(), held: "a").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A held queue is released once the detach distance is exceeded");

        // Edge-to-edge distance must be symmetric: the same 18 DIP approach from above and below
        // has to snap, and one more DIP has to stop snapping on both sides.
        var single = new EdgeCapsuleQueueMagnetCandidate("a", 1, 100, slot, bandLeft, bandWidth, 1, 100);
        const double topReach = 100 - height / 2 - EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip;
        const double bottomReach = 100 + height + height / 2 +
            EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip;
        var fromAbove = Evaluate(topReach, "", [single]);
        var fromBelow = Evaluate(bottomReach, "", [single]);
        Check(fromAbove.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge &&
              fromBelow.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge,
            "The merge reach is symmetric above and below a single capsule");
        Check(Math.Abs(fromAbove.ScoreDip - 18) < 0.001 && Math.Abs(fromBelow.ScoreDip - 18) < 0.001,
            "Approaching from either end reports the same edge-to-edge distance");
        Check(Evaluate(topReach - 1, "", [single]).Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue &&
              Evaluate(bottomReach + 1, "", [single]).Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "One DIP beyond the reach stops snapping on both sides");

        // A three-slot queue really occupies [100, 246]; its zero-distance centre band is [77, 269]
        // and its merge-reachable band is [59, 287].
        var multi = new EdgeCapsuleQueueMagnetCandidate("m", 3, 100, slot, bandLeft, bandWidth, 3, 100);
        var multiAbove = Evaluate(65, "", [multi]);
        var multiBelow = Evaluate(281, "", [multi]);
        Check(multiAbove.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge &&
              multiBelow.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge &&
              Math.Abs(multiAbove.ScoreDip - multiBelow.ScoreDip) < 0.001,
            "A multi-member queue snaps symmetrically from both ends");
        Check(Evaluate(58, "", [multi]).Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue &&
              Evaluate(288, "", [multi]).Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A multi-member queue rejects both ends beyond the merge radius");

        // Same rules govern the source queue, so leaving it is distance-based too.
        var sourceQueue = new EdgeCapsuleQueueMagnetCandidate("src", 2, 100, slot, bandLeft, bandWidth, 2, 100);
        Check(Evaluate(100 + 2 * slot, "src", [sourceQueue], held: "src").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.Reorder,
            "Staying inside the source queue reorders rather than merging");
        Check(Evaluate(400, "src", [sourceQueue], held: "src").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "Dragging well past the source queue leaves it");

        // A single-member source queue has nothing left behind: any displacement detaches.
        var solo = new EdgeCapsuleQueueMagnetCandidate("solo", 0, 100, slot, bandLeft, bandWidth, 0, 100);
        Check(Evaluate(101, "solo", [solo], held: "solo").Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A solo source queue cannot hold its member by proximity");

        // Ties must resolve deterministically, not by enumeration order.
        var tieA = Evaluate(100, "", [new("bbb", 1, 100, slot, bandLeft, bandWidth, 1, 100),
            new("aaa", 1, 100, slot, bandLeft, bandWidth, 1, 100)]);
        var tieB = Evaluate(100, "", [new("aaa", 1, 100, slot, bandLeft, bandWidth, 1, 100),
            new("bbb", 1, 100, slot, bandLeft, bandWidth, 1, 100)]);
        Check(tieA.TargetQueueKey == tieB.TargetQueueKey && tieA.TargetQueueKey == "aaa",
            "Equidistant candidates resolve to the same queue regardless of input order");

        // 第 1 项回归：每条候选的插入后占位都会扩容，但目标选择必须优先使用当前真实占位。
        // A 单颗 [48,94] 的插入区间会覆盖 B，指针却在 B 的当前占位 [248,294] 正中。
        var farA = new EdgeCapsuleQueueMagnetCandidate(
            "a", 1, 48, slot, bandLeft, bandWidth, 5, 48);
        var nearB = new EdgeCapsuleQueueMagnetCandidate(
            "b", 1, 248, slot, bandLeft, bandWidth, 5, 248);
        Check(Evaluate(271, "", [farA, nearB]).TargetQueueKey == "b",
            "A queue's hypothetical insertion span must not steal a real hit on another queue");

        // 单颗拖动：A 三颗 [48,194] 的插入区间 [48,244] 会盖住指针，
        // B 当前 [210,256] 才是真实命中。旧算法两者距离都是 0，会按 key 序选中 A。
        var tallA = new EdgeCapsuleQueueMagnetCandidate(
            "a", 3, 48, slot, bandLeft, bandWidth, 4, 48);
        var smallB = new EdgeCapsuleQueueMagnetCandidate(
            "b", 1, 210, slot, bandLeft, bandWidth, 2, 210);
        Check(Evaluate(233, "", [tallA, smallB]).TargetQueueKey == "b",
            "A single capsule drop prefers the queue it actually occupies");

        // held 仅靠插入区间保持时，遇到真实命中必须立即让位；没有真实命中则继续吸附。
        var bottomHeld = new EdgeCapsuleQueueMagnetCandidate(
            "bottom", 1, 986, slot, bandLeft, bandWidth, 4, 836);
        var realHit = new EdgeCapsuleQueueMagnetCandidate(
            "real", 1, 850, slot, bandLeft, bandWidth, 2, 850);
        Check(Evaluate(873, "", [bottomHeld, realHit], held: "bottom").TargetQueueKey == "real",
            "A held queue kept only by its insertion span yields to a real hit");
        Check(Evaluate(859, "", [bottomHeld], held: "bottom").TargetQueueKey == "bottom",
            "A held bottom queue stays attached through its insertion span");

        // Reordering within the source queue reports Reorder; merging into another reports Merge.
        Check(Evaluate(100, "a", TwoQueues(), held: "a").Outcome == EdgeCapsuleQueueMagnetOutcome.Reorder,
            "Held source queue yields Reorder, not Merge");
        Check(Evaluate(600, "a", TwoQueues(), held: "b").Outcome == EdgeCapsuleQueueMagnetOutcome.Merge,
            "Held foreign queue yields Merge");

        // Degenerate inputs must never throw, never match, and never leak a NaN into layout.
        Check(Evaluate(double.NaN, "", TwoQueues()).Outcome == EdgeCapsuleQueueMagnetOutcome.None,
            "A NaN centre resolves to no plan rather than matching every queue");
        Check(Evaluate(double.PositiveInfinity, "", TwoQueues()).Outcome == EdgeCapsuleQueueMagnetOutcome.None,
            "An infinite centre resolves to no plan");
        Check(Evaluate(100, "", [new("a", 0, 100, slot, bandLeft, bandWidth, 0, 100)]).Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A zero-slot candidate is ignored");
        Check(Evaluate(100, "", [new("a", 1, double.NaN, slot, bandLeft, bandWidth, 1, double.NaN)]).Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A NaN queue top is ignored");
        Check(Evaluate(100, "", System.Array.Empty<EdgeCapsuleQueueMagnetCandidate>(), held: "ghost").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A held queue that vanished from the candidate set does not resurrect");

        NewQueueDropAvoidsOverlap();
    }

    // 磁吸插入指示：完整槽位的目标高度、插入线边界，以及设备矩形到窗口内 DIP 的换算。
    // 纯函数检查，不依赖 WPF 窗口。


    // 插入求解器：锚点固定、受影响链、工作区边界、不可行拒绝、输入顺序稳定。

    // 纯竖直时，超出合并半径的落点必然已经和队列分离；但斜向拖拽的水平分量会把总距离推出
    // 半径，胶囊在竖直方向却仍可能与队列重叠。Ctrl 整队搬移还有“第一颗不重叠、后续成员重叠”
    // 的情况。磁吸必须按真实占位校验返回的落点，否则自由队列会把胶囊叠在一起。
    private static void NewQueueDropAvoidsOverlap()
    {
        const double slot = 50;
        const double height = 46;
        const double merge = EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip;
        const double detach = EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip;
        const double half = height / 2;

        EdgeCapsuleQueueMagnetPlan Drop(
            double centreTop,
            double centreLeft,
            EdgeCapsuleQueueMagnetCandidate[] candidates) =>
            EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
                centreTop, "other", candidates, slot, height, merge, detach, "", centreLeft));

        // 单槽队列的真实占位是 [top, top + height]。
        EdgeCapsuleQueueMagnetCandidate QueueAt(string key, double top) =>
            new(key, 1, top, slot, 0, 200, 1, top);

        bool OverlapsAny(EdgeCapsuleQueueMagnetPlan plan, EdgeCapsuleQueueMagnetCandidate[] candidates)
        {
            var top = plan.NewQueueStartTopDip;
            var bottom = top + height;
            foreach (var candidate in candidates)
            {
                var low = candidate.FirstSlotTopDip;
                var high = low + height;
                if (top < high && bottom > low)
                {
                    return true;
                }
            }

            return false;
        }

        // 横向 19 DIP 已把总距离推出 18 DIP 半径，但中心 120 仍与队列 [100, 146] 重叠。
        // 落点应被推到更近的一侧：队列上方 54。
        EdgeCapsuleQueueMagnetCandidate[] single = [QueueAt("a", 100)];
        var above = Drop(120, 219, single);
        Check(above.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A diagonal drop outside the merge radius is still its own queue");
        Check(!OverlapsAny(above, single),
            "The new-queue drop is pushed clear of the queue it would have covered");
        Check(Math.Abs(above.NewQueueStartTopDip - 54) < 0.001,
            "A drop overlapping the top half resolves above the queue");

        // 中心 130 更靠近队列下沿，应解析到队列下方 146。
        var below = Drop(130, 219, single);
        Check(!OverlapsAny(below, single),
            "A drop overlapping the bottom half is pushed clear below it");
        Check(Math.Abs(below.NewQueueStartTopDip - 146) < 0.001,
            "The side chosen follows the side the capsule actually came from");

        // 无重叠时保持原落点（中心 - 半胶囊），避让是恒等变换。
        var clear = Drop(400, 100, single);
        Check(clear.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue, "A clear drop stays independent");
        Check(Math.Abs(clear.NewQueueStartTopDip - (400 - half)) < 0.001,
            "A non-overlapping drop keeps its exact drop height");

        // 相邻队列：“a”占 [100,146]，“b”占 [146,192]。中心 190、横向 19 超出半径，
        // 贴 a 下方会压住 b，所以最终必须落到 b 下方 192。
        EdgeCapsuleQueueMagnetCandidate[] adjacent = [QueueAt("a", 100), QueueAt("b", 146)];
        var squeezed = Drop(190, 219, adjacent);
        Check(squeezed.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A diagonal drop into a narrow gap is still independent");
        Check(!OverlapsAny(squeezed, adjacent),
            "Avoidance re-checks the position it slid to against every candidate, not just the first");
        Check(Math.Abs(squeezed.NewQueueStartTopDip - 192) < 0.001,
            "The resolved drop clears both adjacent queues");

        // 候选顺序不得影响落点。
        var reversed = Drop(190, 219, [QueueAt("b", 146), QueueAt("a", 100)]);
        Check(Math.Abs(squeezed.NewQueueStartTopDip - reversed.NewQueueStartTopDip) < 0.001,
            "The resolved drop does not depend on the order candidates were enumerated in");

        // Ctrl 整队搬移：3 个成员时整块高度是 146 DIP。即使第一颗不重叠，后续成员也可能压住
        // 目标队列，因此避让必须按整队高度计算。
        var stack = EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
            54, "other", [QueueAt("a", 100)], slot, height, merge, detach, "", 100,
            EdgeCapsuleLayout.TopMargin, 446, 3));
        Check(stack.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A whole-queue drop outside the radius is still its own queue");
        Check(Math.Abs(stack.NewQueueStartTopDip - 146) < 0.001,
            "A multi-member block clears the target by its whole stack height");

        // 被排除的唯一成员没有占位，不触发避让。
        var solo = new EdgeCapsuleQueueMagnetCandidate("solo", 0, 100, slot, 0, 200, 0, 100);
        var soloDrop = EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
            400, "solo", [solo], slot, height, merge, detach, "", 100));
        Check(Math.Abs(soloDrop.NewQueueStartTopDip - (400 - half)) < 0.001,
            "A candidate that excluded its only member occupies nothing and never forces avoidance");

    }

    // 满边或整队过高时，“新建队列”没有合法落点。磁吸必须返回 None 让调用方保持原布局，
    // 而不是退回一个必然与现有队列重叠的位置。

    // The commit path clamps a new queue's top back into the work area, so the magnet has to
    // validate the drop in committed coordinates. Validating the raw pointer position instead lets
    // an off-screen drop pass as "clear" and then clamp straight down onto a wall queue.

    // The magnet measures to the queue's wall-anchored rectangle, so a vertically aligned but
    // horizontally distant capsule must not snap. Before the horizontal term existed, dragging to
    // the middle of the screen with Y lined up against a queue merged into it anyway.

    // FQ-02：held 队列仍保留死区，但更近且已进入 merge 半径的候选可以接管。

    // The drag loop feeds each frame's target back in as the next frame's held queue. If that
    // round trip were unstable the outline and the avoidance animation would strobe while the
    // pointer merely wobbles, so the hold behaviour is pinned across a simulated frame sequence.

    // A mini-view is placed by a pure relative displacement, so the same number applies to any
    // queue on that edge (D-058). These checks pin the scope rule: everything below the owner's
    // card is pushed, everything above is left alone, and a member that already clears the gap
    // stays put rather than the whole stack closing up.

    // 自由队列静止避让：同一 live edge 上，后一条队列按前一条队列的底边整体下推。
    // 输入 top 已包含 preview 等附加偏移，delta 只是需要追加到 TopOffsetDip 上的增量。

    // 贴底队列扩容时插入预览的 top 会上移；磁吸距离必须使用同一插入几何，
    // 但 NewQueue 避让仍按当前真实占位。
    private static void BottomQueueInsertionMagnet()
    {
        const double slot = 50;
        const double height = 46;
        const double bandLeft = 0;
        const double bandWidth = 200;
        const double centreLeft = 100;

        EdgeCapsuleQueueMagnetPlan Evaluate(
            double center,
            string held,
            EdgeCapsuleQueueMagnetCandidate candidate,
            int newQueueSlotCount = 1,
            double horizontal = centreLeft) =>
            EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
                center, "", [candidate], slot, height,
                EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
                EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
                held, horizontal,
                NewQueueSlotCount: newQueueSlotCount));

        // 贴底单成员：当前 [986, 1032]；插入 3 个 carried 后 [836, 1032]。
        var currentOnly = new EdgeCapsuleQueueMagnetCandidate(
            "|right#qtgt", 1, 986, slot, bandLeft, bandWidth, 1, 986);
        var withInsertion = currentOnly with
        {
            InsertionSlotCount = 4,
            InsertionFirstSlotTopDip = 836,
        };

        const double headCenter = 836 + height / 2; // 859
        Check(Evaluate(headCenter, "", currentOnly).Outcome != EdgeCapsuleQueueMagnetOutcome.Merge,
            "The current-only geometry cannot reach the preview's head slot");
        Check(Evaluate(headCenter, "|right#qtgt", currentOnly).Outcome !=
                  EdgeCapsuleQueueMagnetOutcome.Merge,
            "The current-only geometry drops a held queue at the preview head");
        Check(Evaluate(headCenter, "", withInsertion).Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.Merge,
            "The insertion geometry reaches the preview head slot");
        Check(Math.Abs(Evaluate(headCenter, "", withInsertion).ScoreDip) < 1e-9,
            "The preview head slot is at zero distance");

        // 底边仍锚定在工作区底部：1055 是零距离带的下边界，1074 已超出 merge 半径。
        Check(Evaluate(1055, "", withInsertion).ScoreDip == 0 &&
              Evaluate(1074, "", withInsertion).Outcome != EdgeCapsuleQueueMagnetOutcome.Merge,
            "The insertion geometry keeps the queue's anchored bottom edge");

        // NewQueue 避让继续使用当前占位：desired top 900 在插入几何内，但在当前队列上方。
        var newQueue = Evaluate(923, "", withInsertion, newQueueSlotCount: 1, horizontal: 500);
        Check(newQueue.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue &&
              Math.Abs(newQueue.NewQueueStartTopDip - 900) < 1e-9,
            "NewQueue avoidance must ignore the insertion-only span");
    }

    // 第 3 项回归：Feasible 必须兑现“整边不重叠”。删除提前 break 还不够，保持原位的队列
    // 也必须推进扫描前沿，否则近端已让位的队列会挡住更远处的重叠队列。
    private static void EdgeCapsuleQueueInsertionSolverChecks()
    {
        const double gap = 4;
        const double area = 1080;
        const double topMargin = 8;

        EdgeCapsuleQueueInsertSolveResult Solve(
            double gapDip,
            params EdgeCapsuleQueueInsertBox[] boxes) =>
            EdgeCapsuleQueueInsertionSolver.Solve(boxes, "t", area, gapDip, topMargin);

        static void RequireNoOverlap(
            EdgeCapsuleQueueInsertSolveResult result,
            EdgeCapsuleQueueInsertBox[] boxes)
        {
            var heights = boxes.ToDictionary(
                box => box.QueueKey,
                box => box.HeightDip,
                StringComparer.Ordinal);
            var ordered = result.TopsDip
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (current.Value < previous.Value + heights[previous.Key] - 1e-6)
                {
                    throw new Exception("A feasible layout must not contain overlapping queues");
                }
            }
        }

        EdgeCapsuleQueueInsertBox[] stable =
        [
            new("t", 48, 96),
            new("b", 400, 46),
            new("c", 450, 46)
        ];
        var stableResult = Solve(gap, stable);
        Check(stableResult.Feasible, "A non-overlapping layout stays feasible");
        Check(Math.Abs(stableResult.TotalDisplacementDip) < 1e-6,
            "A non-overlapping insertion does not move any queue");
        Check(Math.Abs(stableResult.TopsDip["b"] - 400) < 1e-6 &&
              Math.Abs(stableResult.TopsDip["c"] - 450) < 1e-6,
            "Untouched queues keep their input tops");

        // B 已避开锚点，但 C 与 B 相交：前沿必须从 B 的底边继续推进，把 C 推开。
        EdgeCapsuleQueueInsertBox[] overlappedBelow =
        [
            new("t", 48, 96),
            new("b", 400, 46),
            new("c", 420, 46)
        ];
        var belowResult = Solve(gap, overlappedBelow);
        Check(belowResult.Feasible, "Overlapping input below the anchor can still be solved");
        Check(belowResult.TopsDip["c"] >= belowResult.TopsDip["b"] + 46 - 1e-6,
            "The farther lower queue is pushed clear of the nearer one");
        RequireNoOverlap(belowResult, overlappedBelow);

        // 上方链同理。
        EdgeCapsuleQueueInsertBox[] overlappedAbove =
        [
            new("a", 400, 46),
            new("b", 420, 46),
            new("t", 600, 96)
        ];
        var aboveResult = Solve(gap, overlappedAbove);
        Check(aboveResult.Feasible, "Overlapping input above the anchor can still be solved");
        Check(aboveResult.TopsDip["a"] + 46 <= aboveResult.TopsDip["b"] + 1e-6,
            "The farther upper queue is pushed clear of the nearer one");
        RequireNoOverlap(aboveResult, overlappedAbove);

        // 0-gap 接触合法；相邻校验只拒绝真实重叠。
        var touching = Solve(
            0,
            new EdgeCapsuleQueueInsertBox("t", 100, 46),
            new EdgeCapsuleQueueInsertBox("b", 146, 46));
        Check(touching.Feasible, "Touching queues at zero gap are feasible");

        // 输入顺序不影响结果。
        var reversed = EdgeCapsuleQueueInsertionSolver.Solve(
            [new("c", 420, 46), new("b", 400, 46), new("t", 48, 96)],
            "t", area, gap, topMargin);
        Check(reversed.Feasible &&
              Math.Abs(reversed.TopsDip["b"] - belowResult.TopsDip["b"]) < 1e-6 &&
              Math.Abs(reversed.TopsDip["c"] - belowResult.TopsDip["c"]) < 1e-6,
            "Candidate order does not change the solved layout");

        // 越界仍不可行。
        var overflows = Solve(gap, new EdgeCapsuleQueueInsertBox("t", 100, 1200));
        Check(!overflows.Feasible, "A queue that cannot fit the work area is infeasible");
    }

    // 第 2 项回归：静止避让只下推；展开卡片的完整底边必须继续传递给下方队列。
    private static void EdgeCapsuleRestingLayoutChecks()
    {
        const double gap = 4;

        static EdgeCapsuleRestingQueue Queue(
            string key,
            string edge,
            double top,
            double bottom,
            params string[] paperIds) =>
            new(
                edge,
                key,
                paperIds
                    .Select(paperId => new EdgeCapsuleRestingQueueMember(paperId))
                    .ToArray(),
                top,
                bottom);

        var queues = new[]
        {
            Queue("a", "|right", 48, 194, "a1", "a2", "a3"),
            Queue("b", "|right", 100, 146, "b1"),
            Queue("c", "|right", 320, 366, "c1")
        };
        var deltas = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(queues, gap);
        Check(Math.Abs(deltas.GetValueOrDefault("b1") - 98) < 1e-6,
            "A colliding queue is pushed below the previous compact bottom");
        Check(deltas.GetValueOrDefault("c1") == 0,
            "A queue that already clears the previous bottom stays put");

        // B 展开 200 高卡片：输入底边是 100+200=300，B 自身下推 98 后底边 398，C 应推到 402。
        var withCard = new[]
        {
            Queue("a", "|right", 48, 194, "a1", "a2", "a3"),
            Queue("b", "|right", 100, 300, "b1"),
            Queue("c", "|right", 320, 366, "c1")
        };
        var cardDeltas = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(withCard, gap);
        Check(Math.Abs(cardDeltas.GetValueOrDefault("b1") - 98) < 1e-6,
            "The card owner is still pushed by the queue above it");
        Check(Math.Abs(cardDeltas.GetValueOrDefault("c1") - 82) < 1e-6,
            "The expanded card's full bottom pushes the queue below it to 402");

        var splitEdges = new[]
        {
            Queue("a", "|right", 48, 194, "a1"),
            Queue("b", "|left", 100, 146, "b1")
        };
        var splitDeltas = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(splitEdges, gap);
        Check(splitDeltas.GetValueOrDefault("b1") == 0,
            "Queues on different physical edges do not push each other");
    }

    private static void CycleAndUnicode()    {
        var cycle = new[] { EdgeCapsuleTitleLimit.Unlimited, EdgeCapsuleTitleLimit.Hidden }
            .Concat(Enumerable.Range(1, PaperTitles.MaxConfigurableTitleLength)).ToArray();
        for (var i = 0; i < cycle.Length; i++)
        {
            Check(EdgeCapsuleTitleLimit.Step(cycle[i], true) == cycle[(i + 1) % cycle.Length], "Forward cycle");
            Check(EdgeCapsuleTitleLimit.Step(cycle[i], false) == cycle[(i + cycle.Length - 1) % cycle.Length], "Backward cycle");
        }
        const string text = "中👨‍👩‍👧‍👦e\u0301文";
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 0) == text, "Legacy unlimited retains full title");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, -1) == "", "Zero measures no title");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 2) == "中👨‍👩‍👧‍👦", "Do not split emoji clusters");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 3) == "中👨‍👩‍👧‍👦e\u0301", "Do not split combining characters");
        Check(EdgeCapsuleTitleLimit.TextForMeasure("", -1) == "", "Empty title");
        Check(EdgeCapsuleTitleLimit.Normalize(int.MinValue) == 0, "Invalid old negatives remain unlimited");
        Check(EdgeCapsuleTitleLimit.Normalize(int.MaxValue) == 20, "Upper bound");
    }

    private static void Persistence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "papertodo-edge-title-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new StateStore(dir, DurableAtomicFileWriter.Shared);
            foreach (var value in new[] { 0, -1, 1, 20 })
            {
                File.WriteAllText(store.FilePath, $"{{\"papers\":[],\"deepCapsuleTitleMeasureCharacterLimit\":{value}}}");
                var state = store.Load();
                Check(state.DeepCapsuleTitleMeasureCharacterLimit == value, "Load/normalization retains semantics");
                File.WriteAllText(store.FilePath, store.SerializeState(state));
                Check(store.Load().DeepCapsuleTitleMeasureCharacterLimit == value, "Round-trip retains zero/unlimited");
            }
            File.WriteAllText(store.FilePath, "{\"papers\":[]}");
            Check(store.Load().DeepCapsuleTitleMeasureCharacterLimit == 0, "Missing legacy field remains unlimited");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void HostContentVisibility()
    {
        using var host = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            WindowChromeMargin: 4, ChromeCornerRadius: 16, InnerCornerRadius: 15,
            OutlineThickness: 2, OutlineOverlap: 1, BodyHeight: 32,
            LeftPadding: 6, IconGap: 4, IconText: "✓", IconFontSize: 13,
            LabelFontSize: 12, LabelFontWeight: FontWeights.Normal, CloseToolTip: "Close",
            PaperBrush: Brushes.White, PaperBorderBrush: Brushes.Gray, OutlineBrush: Brushes.Blue,
            HoverBrush: Brushes.LightGray, IconBrush: Brushes.Gray,
            StrongTextBrush: Brushes.Black, TextBrush: Brushes.Gray,
            UiFontFamily: new FontFamily("Segoe UI"), SymbolFontFamily: new FontFamily("Segoe UI Symbol"),
            Language: XmlLanguage.GetLanguage("en-US"), Topmost: false, DiagnosticId: "title-checks"));
        T HostPart<T>(string name) where T : class => (T)typeof(EdgeCapsuleHost)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var label = HostPart<TextBlock>("Label");
        var icon = HostPart<TextBlock>("Icon");
        var contentGrid = HostPart<Grid>("ContentGrid");
        var window = HostPart<Window>("Window");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "Host test monitor");
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Right, 40, 10,
            40, 28, 40, 178, 40, false, 1, null, ExpandedWidthDip: 150);
        var model = EdgeCapsuleModel.Initial with
        {
            State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                EdgeCapsuleVisualState.Resting, EdgeCapsuleGestureState.Idle, EdgeCapsuleOpenOrigin.Normal),
            Placement = new EdgeCapsulePlacement(0, 0, 1)
        };
        var pluginContent = new TextBlock { Text = "Plugin clock", Background = Brushes.Transparent };
        var material = (SkinBorder)HostPart<Border>("Chrome");
        var compact = EdgeCapsuleTargetPlanner.Calculate(model, layout).Docked.ToFrame();
        Check(host.Apply(compact with { Surface = EdgeCapsuleSurfaceKind.DockedPreview }) && material.UseLightweightMaterial,
            "Presented preview frame selects lightweight material before any geometry animation");
        host.RefreshSkin();
        Check(material.UseLightweightMaterial, "Theme refresh must not re-enable heavy preview material");
        Check(host.Apply(compact) && !material.UseLightweightMaterial,
            "Presented compact frame restores normal material without changing HWND authority");

        foreach (var hideTitle in new[] { false, true })
        foreach (var hovered in new[] { false, true })
        {
            var frame = EdgeCapsuleTargetPlanner.Calculate(
                model with { State = model.State with { Visual = hovered
                    ? EdgeCapsuleVisualState.Hovered : EdgeCapsuleVisualState.Resting } },
                layout with { HideRestingTitle = hideTitle }).Docked.ToFrame();
            var expectedTitle = !hideTitle || hovered ? Visibility.Visible : Visibility.Collapsed;
            host.SetLabel("Ordinary title", "Stored title");
            host.SetPluginContent(pluginContent, "Plugin tooltip");
            Check(host.Apply(frame), "Apply real host frame with plugin content");
            Check(label.Visibility == Visibility.Collapsed && icon.Visibility == Visibility.Collapsed,
                "Frame application must not reveal defaults beneath plugin content");
            Check(pluginContent.IsVisible, "Custom plugin content remains visible");

            host.SetPluginContent(null, null);
            Check(label.Visibility == expectedTitle && icon.Visibility == Visibility.Visible,
                "Removing plugin content restores defaults according to the applied title state");
            Check(host.Apply(frame), "Apply ordinary host frame");
            Check(label.Visibility == expectedTitle, "Ordinary frame honors title visibility");
            // Theme/title refresh uses this sequence. There is deliberately no Apply afterwards:
            // an unchanged target makes the presenter skip reapplying the frame.
            host.SetLabel("Refreshed title", "Refreshed tooltip");
            host.SetPluginContent(null, null);
            Check(label.Visibility == expectedTitle && label.Text == "Refreshed title" &&
                icon.Visibility == Visibility.Visible,
                "Refreshing ordinary content preserves hidden titles without a new frame");
        }

        // The fixed slot must affect the real WPF layout, not only the outer width calculation.
        // Measure the following label after switching between the two default glyphs.
        var visibleFrame = EdgeCapsuleTargetPlanner.Calculate(
            model with { State = model.State with { Visual = EdgeCapsuleVisualState.Hovered } },
            layout with { HideRestingTitle = false }).Docked.ToFrame();
        host.SetPluginContent(null, null);
        host.SetDefaultIconSlotWidth(40);
        Check(Math.Abs(host.DefaultIconSlotWidthForChecks - 40) < 0.01,
            "Host accepts a real default icon slot width");
        Check(host.Apply(visibleFrame), "Apply host frame for icon-slot alignment");
        host.SetLabel("Title", "Title");
        icon.Text = "✓";
        window.UpdateLayout();
        var todoLabelX = label.TranslatePoint(new Point(0, 0), contentGrid).X;
        icon.Text = "✎";
        window.UpdateLayout();
        var noteLabelX = label.TranslatePoint(new Point(0, 0), contentGrid).X;
        Check(Math.Abs(todoLabelX - noteLabelX) < 0.01,
            "Todo and note glyphs share the same real layout slot");
        host.SetDefaultIconSlotWidth(0);
        Check(host.DefaultIconSlotWidthForChecks < 0.01,
            "Script/natural icon layout can release the default slot");
    }

    private static void Geometry()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        foreach (var hiddenClose in new[] { false, true })
        {
            var monitor = new MonitorGeometry("test", new DeviceScreenRect(-1920, -100, 0, 980), scale, scale);
            var layout = new EdgeCapsuleLayoutSnapshot(monitor, edge, 40, 10,
                40, 28, 40, 260, 180, hiddenClose, 1, null,
                ExpandedWidthDip: 150, HideRestingTitle: true);
            var model = EdgeCapsuleModel.Initial with
            {
                State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                    EdgeCapsuleVisualState.Resting, EdgeCapsuleGestureState.Idle, EdgeCapsuleOpenOrigin.Normal),
                Placement = new EdgeCapsulePlacement(0, 0, 1)
            };
            EdgeCapsuleTargetPresentation Plan(EdgeCapsuleModel m, EdgeCapsuleLayoutSnapshot l) =>
                EdgeCapsuleTargetPlanner.Calculate(m, l).Docked;
            var resting = Plan(model, layout);
            var hoverModel = model with { State = model.State with { Visual = EdgeCapsuleVisualState.Hovered } };
            var hovered = Plan(hoverModel, layout);
            Check(!resting.TitleVisible && hovered.TitleVisible, "Zero title appears only while expanded");
            Check(resting.HostBounds == hovered.HostBounds, "Reserve full title capacity before hover");
            var expectedCloseWidth = hiddenClose ? 0 : 28;
            var expected = EdgeCapsuleGeometry.Calculate(new(monitor, edge, 40, 150, expectedCloseWidth, 40));
            Check(hovered.Bounds == expected.Bounds && hovered.InteractiveBounds == expected.InteractiveBounds,
                "Visible width and hit region remove the hidden close strip");
            Check(!hovered.CloseSegmentActsAsContent,
                "Hidden close mode leaves no invisible content segment");
            var transition = new EdgeCapsuleTransition(resting.ToFrame(), hovered, 0, 100,
                EdgeCapsuleTransitionReason.Pointer);
            var previousWidth = resting.Bounds.Width;
            for (var tick = 0; tick <= 100; tick += 5)
            {
                var frame = EdgeCapsuleTransitionPolicy.Sample(transition, tick).Frame;
                Check(frame.IsUsable && frame.HostBounds == resting.HostBounds, "Every animation frame stays inside stable host");
                Check(frame.Bounds.Width >= previousWidth, "No width reversal during hover");
                previousWidth = frame.Bounds.Width;
            }
            var retract = new EdgeCapsuleTransition(hovered.ToFrame(), resting, 0, 100,
                EdgeCapsuleTransitionReason.Pointer);
            Check(!EdgeCapsuleTransitionPolicy.Sample(retract, 100).Frame.TitleVisible,
                "Mouse leave restores hidden title");
            Check(EdgeCapsuleTransitionPolicy.Create(resting.ToFrame(), hovered,
                EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Pointer), false, 0, 1000) == null,
                "Animation disabled remains an immediate apply");
            var explicitPreserve = EdgeCapsuleTransitionPolicy.Create(
                resting.ToFrame(),
                hovered,
                EdgeCapsuleMotion.Preserve(EdgeCapsuleTransitionReason.Measure, 60),
                transitionAlreadyActive: true,
                nowTimestamp: 0,
                timestampFrequency: 1000);
            Check(explicitPreserve?.DurationTimestampTicks == 60,
                "Preserve transition uses the explicit caller-provided duration");
            var previewsEnabled = layout with { ExpandedWidthDip = layout.RestingWidthDip };
            var ordinaryHover = Plan(hoverModel, previewsEnabled);
            Check(!ordinaryHover.TitleVisible && ordinaryHover.BodyWindowWidthDevice == resting.BodyWindowWidthDevice,
                "Preview-enabled compact hover retains the configured title limit");
            var preview = Plan(hoverModel with { Preview = EdgeCapsulePreviewState.Open }, layout);
            Check(preview.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
                preview.Bounds.Width == (int)Math.Round(260 * scale),
                "Preview card keeps its independent close segment");
            var active = hoverModel with { State = hoverModel.State with { Visual = EdgeCapsuleVisualState.Active } };
            var handoff = Plan(active with { State = active.State with { Gesture = EdgeCapsuleGestureState.DockingHandoff } }, layout);
            var reveal = Plan(active with { State = active.State with { Gesture = EdgeCapsuleGestureState.DockingReveal } }, layout);
            Check(handoff.Bounds == reveal.Bounds && reveal.Bounds == Plan(active, layout).Bounds,
                "Docking handoff/reveal/active retain identical title geometry");
        }
    }
}
