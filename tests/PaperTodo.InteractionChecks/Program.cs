using PaperTodo;

var checks = new (string Name, Action Run)[]
{
    ("preview-bounded-prefix", () =>
    {
        var result = Capture(Enumerable.Range(0, 30).Select(i => new Item(i)), false);
        Assert(result.Items.Count == 12 && result.Total == 30 && result.RemainingCount == 18);
    }),
    ("preview-filter-scans-beyond-first-twelve", () =>
    {
        var items = Enumerable.Range(0, 40).Select(i => new Item(i, Done: i < 25)).ToArray();
        var result = Capture(items, true);
        Assert(result.Items.Select(i => i.Order).SequenceEqual(Enumerable.Range(25, 12)));
        Assert(result.Total == 40 && result.Done == 25 && result.MatchingCount == 15 && result.RemainingCount == 3);
    }),
    ("preview-empty-rows-excluded-but-linked-rows-kept", () =>
    {
        var result = Capture([new Item(0, HasContent: false), new Item(1, Text: "")], false);
        Assert(result.Total == 1 && result.Items.Count == 1 && result.Items[0].Order == 1);
    }),
    ("preview-all-completed", () =>
    {
        var result = Capture([new Item(0, Done: true), new Item(1, Done: true)], true);
        Assert(result.Total == 2 && result.Done == 2 && result.Items.Count == 0 && result.RemainingCount == 0);
    }),
    ("preview-stable-order-and-no-source-mutation", () =>
    {
        Item[] items = [new(2, Text: "last"), new(1, Text: "first"), new(1, Text: "second")];
        var before = items.ToArray();
        var result = Capture(items, false);
        Assert(result.Items.Select(i => i.Text).SequenceEqual(["first", "second", "last"]));
        Assert(items.SequenceEqual(before) && ReferenceEquals(result.Items[0], items[1]));
    }),
    ("preview-zero-limit-still-counts", () =>
    {
        var result = Capture([new Item(0), new Item(1, Done: true)], false, 0);
        Assert(result.Items.Count == 0 && result.Total == 2 && result.RemainingCount == 2);
    }),
    ("preview-negative-limit-rejected", () =>
    {
        try { Capture([], false, -1); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Negative limit was accepted.");
    }),
    ("preview-randomized-differential", () =>
    {
        var random = new Random(20260905);
        for (var run = 0; run < 200; run++)
        {
            var items = Enumerable.Range(0, random.Next(0, 120))
                .Select(i => new Item(random.Next(-5, 20), random.Next(2) == 0,
                    random.Next(4) != 0, i.ToString())).ToArray();
            foreach (var filtered in new[] { false, true })
            foreach (var limit in new[] { 0, 1, 12, 25 })
            {
                var result = Capture(items, filtered, limit);
                var matching = items.Where(i => i.HasContent && (!filtered || !i.Done)).ToArray();
                Assert(result.Items.SequenceEqual(matching.OrderBy(i => i.Order).Take(limit)));
                Assert(result.Total == items.Count(i => i.HasContent));
                Assert(result.Done == items.Count(i => i.HasContent && i.Done));
                Assert(result.MatchingCount == matching.Length);
                Assert(result.RemainingCount == Math.Max(0, matching.Length - limit));
            }
        }
    }),
    ("visibility-repeated-hide-keeps-first-snapshot", () =>
    {
        var first = VisibilityShortcutSnapshot.Capture(null, ["A"]);
        var second = VisibilityShortcutSnapshot.Capture(first, []);
        var third = VisibilityShortcutSnapshot.Capture(second, []);
        Assert(ReferenceEquals(first, third) && third.SetEquals(["A"]));
    }),
    ("visibility-empty-snapshot-is-not-uninitialized", () =>
    {
        var first = VisibilityShortcutSnapshot.Capture(null, []);
        var second = VisibilityShortcutSnapshot.Capture(first, ["B"]);
        Assert(ReferenceEquals(first, second) && second.Count == 0);
    }),
    ("visibility-new-cycle-captures-new-state", () =>
    {
        HashSet<string>? snapshot = VisibilityShortcutSnapshot.Capture(null, ["A"]);
        snapshot = null; // Existing controller clears on Show / external invalidation.
        snapshot = VisibilityShortcutSnapshot.Capture(snapshot, ["B"]);
        Assert(snapshot.SetEquals(["B"]));
    }),
    ("visibility-does-not-reenumerate-on-repeat", () =>
    {
        var first = VisibilityShortcutSnapshot.Capture(null, ["A"]);
        Assert(ReferenceEquals(first, VisibilityShortcutSnapshot.Capture(first, FailOnEnumeration())));
    }),
    ("markdown-completion-and-order", () =>
    {
        Assert(TodoClipboardFormatter.ToMarkdown([("买牛奶", false), ("提交报告", true)]) ==
            "- [ ] 买牛奶" + Environment.NewLine + "- [x] 提交报告");
    }),
    ("markdown-multiline-normalizes-line-endings", () =>
    {
        Assert(TodoClipboardFormatter.ToMarkdown([("one\r\ntwo\rthree\nfour", false)]) ==
            "- [ ] one" + Environment.NewLine + "    two" + Environment.NewLine +
            "    three" + Environment.NewLine + "    four");
    }),
    ("markdown-literal-punctuation-and-paths", () =>
    {
        Assert(TodoClipboardFormatter.ToMarkdown([(@"[a] **b** C:\notes <x>", false)]) ==
            @"- [ ] \[a\] \*\*b\*\* C:\\notes \<x\>");
    }),
    ("markdown-entities-and-setext-remain-literal", () =>
    {
        Assert(TodoClipboardFormatter.ToMarkdown([("&copy;\n===", false)]) ==
            @"- [ ] \&copy;" + Environment.NewLine + @"    \=\=\=");
    }),
    ("markdown-empty-selection", () => Assert(TodoClipboardFormatter.ToMarkdown([]) == "")),
    ("markdown-preserves-blank-items", () => Assert(TodoClipboardFormatter.ToMarkdown([("", false)]) == "- [ ] ")),
    ("markdown-export-does-not-change-input", () =>
    {
        (string Text, bool Done)[] items = [("保留🙂", true), ("- literal", false)];
        var before = items.ToArray();
        _ = TodoClipboardFormatter.ToMarkdown(items);
        Assert(items.SequenceEqual(before));
    })
};

var failures = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"Interaction checks: {checks.Length - failures}/{checks.Length} passed.");
return failures == 0 ? 0 : 1;

static TodoPreviewSelection<Item> Capture(IEnumerable<Item> items, bool filtered, int limit = 12) =>
    TodoPreviewSelection.Capture(items, i => i.HasContent, i => i.Done, i => i.Order, filtered, limit);

static void Assert(bool condition)
{
    if (!condition) throw new Exception("Assertion failed.");
}

static IEnumerable<string> FailOnEnumeration() =>
    Enumerable.Range(0, 1).Select<int, string>(_ =>
        throw new Exception("Repeated Hide must not enumerate the now-hidden windows."));

internal sealed record Item(int Order, bool Done = false, bool HasContent = true, string Text = "task");
