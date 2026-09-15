from pathlib import Path

path = Path("tests/PaperTodo.EdgeTitleChecks/ProxyMaximumCapacityChecks.cs")
text = path.read_text(encoding="utf-8-sig")
old = '''        var controller = GetCapacityCheckField<AppController>(fixture.Window, "_controller");
        var windows = GetCapacityCheckField<Dictionary<string, PaperWindow>>(controller, "_windows");
        windows[fixture.Paper.Id] = fixture.Window;
        foreach (var fieldName in new[]
'''
new = '''        var controller = GetCapacityCheckField<AppController>(fixture.Window, "_controller");
        var windows = GetCapacityCheckField<Dictionary<string, PaperWindow>>(controller, "_windows");
        windows[fixture.Paper.Id] = fixture.Window;
        // MaximumCapacityFixture intentionally bypasses PaperWindow field initializers. The H7
        // layout path reaches Markdown preload invalidation, so restore its real per-window source
        // instead of treating the fixture-only null as a product failure.
        SetCapacityCheckField(fixture.Window, "_edgeCapsulePreviewInvalidationSource",
            new EdgeCapsulePreviewInvalidationSource());
        foreach (var fieldName in new[]
'''
if text.count(old) != 1:
    raise SystemExit(f"focused fixture insertion mismatch: {text.count(old)}")
path.write_text(text.replace(old, new), encoding="utf-8", newline="")
