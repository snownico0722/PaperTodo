"""Temporary real-drag comparison. Writes ONLY audit artifacts; never updates PR/main refs."""
import json
import os
from pathlib import Path
import shutil
import statistics
import subprocess
import sys
import time

ROOT = Path(os.environ["GITHUB_WORKSPACE"])
OUT = Path(os.environ["RUNNER_TEMP"]) / "pr326-drag-cost"
OUT.mkdir(parents=True, exist_ok=True)
REVS = {
    "main": "39292c739901b74a0d718c4d697ecdc1183bdcc0",
    "pr326": "a1b296fba72ea575de268b36ff6cc84737d55a16"
}
TOOL = Path("tools/PaperTodo.MaterialBenchmarks")
IMPL = TOOL / "MaterialDragBenchmarks.cs"
SEQUENCE = ["main", "pr326", "pr326", "main", "main", "pr326"]
summary = {
    "baseline": REVS["main"], "candidate": REVS["pr326"],
    "scope": "Real injected mouse input, actual production capsule HWND movement; not DWM pixels or monitor scanout",
    "runs": [], "checks": [], "errors": [],
    "measured_cases": ["paper/plain capsule", "acrylic/full capsule", "aero/full capsule",
                       "tracingPaper/full capsule", "acrylic/expanded paper"],
    "note": "Identical temporary benchmark patch in both detached worktrees; unchanged product files."
}

def save():
    (OUT / "results.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")

def run(argv, cwd=ROOT, logname=None, env=None, timeout=700, required=True):
    print("RUN", " ".join(map(str, argv)), "in", cwd, flush=True)
    started=time.monotonic()
    try:
        p = subprocess.run(list(map(str, argv)), cwd=cwd, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, encoding="utf-8", errors="replace",
                           env=env, timeout=timeout)
        s=p.stdout
        code=p.returncode
    except subprocess.TimeoutExpired as e:
        s=str(e)
        code=124
    if logname:
        (OUT / (logname+".log")).write_text(s, encoding="utf-8")
    print("EXIT", code, "elapsed_s", round(time.monotonic()-started,2),
          "\n".join(s.splitlines()[-8:]), flush=True)
    if code and required:
        raise RuntimeError("Command failed: "+str(argv)+"; log="+str(logname))
    return code, s

def replace_one(s, before, after):
    found=s.count(before)
    if found!=1:
        raise RuntimeError(f"Expected 1 replacement, got {found}: {before[:90]!r}")
    return s.replace(before,after)

def instrument(path):
    s=path.read_text(encoding="utf-8")
    s=replace_one(s,
        'new Case(PaperSkins.Paper, false), new Case(PaperSkins.Mica, false),\n'
        '            new Case(PaperSkins.Acrylic, false), new Case(PaperSkins.ClearAcrylic, false),\n'
        '            new Case(PaperSkins.TracingPaper, false), new Case(PaperSkins.Aero, false),\n'
        '            new Case(PaperSkins.Paper, true), new Case(PaperSkins.Acrylic, true),\n'
        '            new Case(PaperSkins.Aero, true), new Case(PaperSkins.Aero, false, false)',
        'new Case(PaperSkins.Paper, true), new Case(PaperSkins.Acrylic, true),\n'
        '            new Case(PaperSkins.Aero, true), new Case(PaperSkins.TracingPaper, true),\n'
        '            new Case(PaperSkins.Acrylic, false)')
    s=replace_one(s,
        'for (var round = -1; round < 2; round++)',
        'for (var round = -1; round < 4; round++)')
    s=replace_one(s,
        'var sawDragSnapshotWhilePressed = false;\n        var watch = Stopwatch.StartNew();',
        'var sawDragSnapshotWhilePressed = false;\n'
        '        long firstCursorCommandAt = 0, firstHwndMoveAt = 0, releaseAt = 0;\n'
        '        var watch = Stopwatch.StartNew();')
    s=replace_one(s,
        'if (bounds.Left != previous.Left || bounds.Top != previous.Top)\n                {\n'
        '                    GetCursorPos(out var cursor);\n',
        'if (bounds.Left != previous.Left || bounds.Top != previous.Top)\n'
        '                {\n'
        '                    Interlocked.CompareExchange(ref firstHwndMoveAt, Stopwatch.GetTimestamp(), 0);\n'
        '                    GetCursorPos(out var cursor);\n')
    s=replace_one(s,
        'Thread.Sleep(30);\n                SetCursorPos((int)start.X + 12, (int)start.Y + 4);',
        'Thread.Sleep(30);\n'
        '                Interlocked.Exchange(ref firstCursorCommandAt, Stopwatch.GetTimestamp());\n'
        '                if (!SetCursorPos((int)start.X + 12, (int)start.Y + 4))\n'
        '                    throw new InvalidOperationException("First cursor move failed");')
    s=replace_one(s,
        'finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }',
        'finally { Interlocked.Exchange(ref releaseAt, Stopwatch.GetTimestamp());\n'
        '                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }')
    s=replace_one(s,
        'Wait(40);\n            var elapsed = watch.Elapsed.TotalMilliseconds;',
        'Wait(40);\n'
        '            double? recoveredMs = null;\n'
        '            if (c.Capsule && PaperSkins.UsesSampledAuxiliary(c.Skin))\n'
        '            {\n'
        '                var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;\n'
        '                bool Recovered() => surfaces.Where(s => s.IsCapsule)\n'
        '                    .All(s => s.IsBackgroundActive && !s.HasBackgroundCapture &&\n'
        '                      s.BackgroundSessionState?.GetType().GetField("_dragSnapshotActive", Private)?\n'
        '                          .GetValue(s.BackgroundSessionState) is not true);\n'
        '                while (!Recovered() && Stopwatch.GetTimestamp() < deadline) Wait(10);\n'
        '                if (Recovered() && Interlocked.Read(ref releaseAt) > 0)\n'
        '                    recoveredMs = Stopwatch.GetElapsedTime(Interlocked.Read(ref releaseAt)).TotalMilliseconds;\n'
        '            }\n'
        '            var firstMotionCommand = Interlocked.Read(ref firstCursorCommandAt);\n'
        '            var firstWindowMotion = Interlocked.Read(ref firstHwndMoveAt);\n'
        '            double? firstFollowMs = firstMotionCommand > 0 && firstWindowMotion >= firstMotionCommand\n'
        '                ? (firstWindowMotion-firstMotionCommand)*1000.0/Stopwatch.Frequency : null;\n'
        '            var elapsed = watch.Elapsed.TotalMilliseconds;')
    s=replace_one(s,
        'NativeSizeMessages = nativeSizeMessages, EnterSizeMove = enters, ExitSizeMove = exits,',
        'NativeSizeMessages = nativeSizeMessages, EnterSizeMove = enters, ExitSizeMove = exits,\n'
        '                FirstFollowMs = firstFollowMs, RecoveredMs = recoveredMs,')
    path.write_text(s,encoding="utf-8",newline="\n")

def checkout(label):
    directory=Path(os.environ["RUNNER_TEMP"])/("pr326-bench-"+label)
    run(["git","worktree","add","--detach",directory,REVS[label]],logname="checkout-"+label,timeout=100)
    run(["git","submodule","update","--init","--recursive"],cwd=directory,
        logname="submodule-"+label,timeout=150)
    return directory

def benchmark_binary(worktree):
    return worktree / TOOL / "bin/Release/net10.0-windows10.0.17763.0/PaperTodo.MaterialBenchmarks.exe"

try:
    worktrees={label:checkout(label) for label in REVS}
    # Same test-only harness; versions differ ONLY in product binaries.
    s1=(worktrees["main"]/IMPL).read_bytes()
    s2=(worktrees["pr326"]/IMPL).read_bytes()
    if s1!=s2: raise RuntimeError("Shared drag harness changed between main and PR; need an independent common harness")
    for label,worktree in worktrees.items():
        instrument(worktree/IMPL)
        code,s=run(["git","diff","--name-only"],cwd=worktree,logname="worktree-diff-"+label)
        if [line.strip() for line in s.splitlines() if line.strip().startswith("tools/")] != [str(IMPL).replace("\\","/")]:
            raise RuntimeError("Unexpected benchmark source changes: "+s)
        run(["dotnet","build",TOOL/"PaperTodo.MaterialBenchmarks.csproj","-c","Release"],
            cwd=worktree,logname="build-"+label,timeout=360)
    hashes=[(worktrees[label]/IMPL).read_bytes() for label in REVS]
    if hashes[0]!=hashes[1]:raise RuntimeError("Instrumented harnesses are not byte-identical")
    for index,label in enumerate(SEQUENCE):
        destination=OUT/f"drag-{index:02d}-{label}.json"
        env=os.environ.copy(); env["PAPER_BENCH_REVISION"]=label
        code,s=run([benchmark_binary(worktrees[label]),"--drag-benchmark",destination],
                   cwd=worktrees[label],env=env,logname=f"drag-{index:02d}-{label}",timeout=220,
                   required=False)
        entry={"variant":label,"sequence":index,"exit_code":code,"artifact":destination.name}
        if destination.exists():
            doc=json.loads(destination.read_text(encoding="utf-8"))
            entry["rows"]=len(doc.get("Results",[]))
            entry["data"]=doc.get("Results",[])
        if code!=0 or entry.get("rows",0)!=20:
            summary["errors"].append({"test":"real_drag","variant":label,
                                      "sequence":index,"exit":code,"rows":entry.get("rows",0)})
        summary["runs"].append(entry)
        save()
    # Functional checks are complementary to timing: rapid master queue retraction
    # and exact target hit/click behavior using the existing real DComp fixture.
    for label in ("main","pr326"):
        env=os.environ.copy();env["PAPER_BENCH_REVISION"]=label
        cmd=["dotnet","run","--project","tests/PaperTodo.LifecycleChecks/PaperTodo.LifecycleChecks.csproj",
             "-c","Release","--","--case","master-queue-material-handoff"]
        code,s=run(cmd,cwd=worktrees[label],env=env,timeout=220,
                   logname="rapid-handoff-"+label,required=False)
        summary["checks"].append({"variant":label,"case":"master-queue-material-handoff",
             "exit_code":code,"passed":"PASS full-material master handoff" in s})
        if code!=0: summary["errors"].append({"test":"rapid-handoff","variant":label,"exit":code})
        save()
    for label in ("main","pr326"):
        env=os.environ.copy();env["PAPER_BENCH_REVISION"]=label
        destination=OUT/("snapshot-"+label+".json")
        code,s=run([benchmark_binary(worktrees[label]),"--drag-snapshot-timing",destination],
                   cwd=worktrees[label],env=env,timeout=110,
                   logname="snapshot-"+label,required=False)
        check={"variant":label,"case":"real-input snapshot before mouse release","exit_code":code}
        if destination.exists():check.update(json.loads(destination.read_text(encoding="utf-8")))
        summary["checks"].append(check)
        if code!=0:summary["errors"].append({"test":"drag-snapshot","variant":label,"exit":code})
        save()
    for label in REVS:
        sets=[row for run in summary["runs"] if run["variant"]==label and run.get("rows")==20
              for row in run["data"]]
        group={}
        for row in sets:
            key=f'{row["Skin"]}/{("capsule" if row["Capsule"] else "expanded")}'
            group.setdefault(key,[]).append(row)
        stats={}
        for key,rows in group.items():
            metrics=["FirstFollowMs","MovementIntervalP95Ms","MovementIntervalMedianMs",
                     "ProcessCpuMs","NativeMoves","NativeSizeMessages","RecoveredMs"]
            stats[key]={"n":len(rows)}
            for metric in metrics:
                values=[float(x[metric]) for x in rows if x.get(metric)!=None]
                stats[key][metric]={"n":len(values),"median":round(statistics.median(values),4),
                                   "min":round(min(values),4),"max":round(max(values),4)} if values else None
        summary.setdefault("aggregates",{})[label]=stats
    save()
    print("DRAG_REVIEW_AGGREGATES",json.dumps(summary["aggregates"],ensure_ascii=False),flush=True)
finally:
    save()
