from pathlib import Path

def replace(path, old, new):
    p = Path(path)
    source = p.read_text(encoding="utf-8-sig")
    if source.count(old) != 1:
        raise ValueError(f"Anchor no longer unique in {p}: {old}")
    p.write_text(source.replace(old, new), encoding="utf-8")

paper = "src/PaperWindow.cs"
bar = "src/PaperWindow.PluginTopBar.cs"
bench = "tools/PaperTodo.MaterialBenchmarks/MaterialDragBenchmarks.cs"
replace(bench, "internal static class MaterialDragBenchmarks", "internal static partial class MaterialDragBenchmarks")
replace(bench, "        typeof(AppController).GetProperty", '''        if (Environment.GetEnvironmentVariable("PAPERTODO_RESIZE_PROBE") == "1")
        {
            RunResize(controller, output);
            return;
        }
        typeof(AppController).GetProperty''')
replace(paper, "    private void UpdateTopBarResponsiveLayout()", '''    internal static bool ProbeWidthOnly;
    internal static long ProbeCalls, ProbeTicks;

    private void UpdateTopBarResponsiveLayout()''')
replace(paper, "        _updatingTopBarResponsiveLayout = true;", '''        var probeBegin = System.Diagnostics.Stopwatch.GetTimestamp();
        ProbeCalls++;
        _updatingTopBarResponsiveLayout = true;''')
replace(paper, "            _updatingTopBarResponsiveLayout = false;", '''            ProbeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - probeBegin;
            _updatingTopBarResponsiveLayout = false;''')
replace(paper, '''        SizeChanged += (_, _) =>
        {
            HandleWindowGeometryChanged();
            UpdateTopBarResponsiveLayout();''', '''        SizeChanged += (_, e) =>
        {
            HandleWindowGeometryChanged();
            if (!ProbeWidthOnly || e.WidthChanged) UpdateTopBarResponsiveLayout();''')
replace(bar, "        _topBar.SizeChanged += (_, _) => UpdateTopBarResponsiveLayout();",
        "        _topBar.SizeChanged += (_, e) => { if (!ProbeWidthOnly || e.WidthChanged) UpdateTopBarResponsiveLayout(); };")
