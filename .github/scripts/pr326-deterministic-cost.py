"""Exact main/PR326 same-runner deterministic HWND and sampled-material audit."""
import json
import os
from pathlib import Path
import shutil
import statistics
import subprocess
import time

ROOT = Path(os.environ["GITHUB_WORKSPACE"])
OUT = Path(os.environ["RUNNER_TEMP"]) / "pr326-deterministic-cost"
OUT.mkdir(parents=True,exist_ok=True)
REVS={"main":"39292c739901b74a0d718c4d697ecdc1183bdcc0",
      "pr326":"a1b296fba72ea575de268b36ff6cc84737d55a16"}
SEQUENCE=["main","pr326","pr326","main"]
TOOL=Path("tools/PaperTodo.MaterialBenchmarks")
data={"base":REVS["main"],"candidate":REVS["pr326"],"runs":[],"errors":[],
      "scope":"Deterministic WPF host/native HWND movement, synchronous preparation, final recapture; not injected mouse input or scanout"}
def save():
    (OUT/"results.json").write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding="utf-8")
def run(cmd,cwd=ROOT,log=None,timeout=300,env=None,required=True):
    cmd=list(map(str,cmd))
    t=time.monotonic()
    print("RUN",cmd,"cwd",cwd,flush=True)
    try:
        p=subprocess.run(cmd,cwd=cwd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,
                         encoding="utf-8",errors="replace",timeout=timeout,env=env)
        code,text=p.returncode,p.stdout
    except subprocess.TimeoutExpired as e:
        code,text=124,str(e)
    if log:(OUT/(log+".log")).write_text(text,encoding="utf-8")
    print("EXIT",code,"secs",round(time.monotonic()-t,3),
          "\n".join(text.splitlines()[-12:]),flush=True)
    if code and required:raise RuntimeError(str(cmd)+" failed; see log "+str(log))
    return code,text
def checkout(label):
    dst=Path(os.environ["RUNNER_TEMP"])/("pr326-deterministic-"+label)
    run(["git","worktree","add","--detach",dst,REVS[label]],log="checkout-"+label)
    run(["git","submodule","update","--init","--recursive"],cwd=dst,log="submodule-"+label)
    return dst
def patch(worktree):
    f=worktree/TOOL/"Program.cs"
    s=f.read_text(encoding="utf-8")
    needle='            Console.Error.WriteLine("Unknown arguments. Use --help.");'
    insert='''            if (args is ["--deterministic-cost", var deterministicOutput])
            {
                using var controller = new AppController();
                Pr326DeterministicCost.Run(controller, deterministicOutput);
                return 0;
            }
'''
    if s.count(needle)!=1:raise RuntimeError("Unexpected material runner entry point")
    f.write_text(s.replace(needle,insert+needle),encoding="utf-8",newline="\n")
    shutil.copy2(ROOT/".github/scripts/pr326-deterministic-cost.cs",
                 worktree/TOOL/"Pr326DeterministicCost.cs")
    code,s=run(["git","diff","--check"],cwd=worktree,log="git-diff-"+worktree.name)
def exe(worktree):
    return worktree/TOOL/"bin/Release/net10.0-windows10.0.17763.0/PaperTodo.MaterialBenchmarks.exe"
try:
    trees={label:checkout(label) for label in REVS}
    for label in REVS:
        patch(trees[label])
        run(["dotnet","build",TOOL/"PaperTodo.MaterialBenchmarks.csproj","-c","Release"],
            cwd=trees[label],log="build-"+label,timeout=360)
    for idx,label in enumerate(SEQUENCE):
        dst=OUT/f"sample-{idx:02d}-{label}.json"
        env=os.environ.copy();env["PAPER_BENCH_REVISION"]=label
        code,_=run([exe(trees[label]),"--deterministic-cost",dst],cwd=trees[label],
                   log=f"sample-{idx:02d}-{label}",env=env,required=False,timeout=260)
        record={"variant":label,"sequence":idx,"exit":code,"file":dst.name}
        if dst.exists():
            obj=json.loads(dst.read_text(encoding="utf-8"))
            record["samples"]=obj.get("Samples",[])
            record["count"]=len(record["samples"])
        if code!=0 or record.get("count",0)!=12:
            data["errors"].append({"variant":label,"sequence":idx,"status":code,
                                   "samples":record.get("count",0)})
        data["runs"].append(record);save()
    for label in REVS:
        samples=[s for run_entry in data["runs"] if run_entry["variant"]==label
                 for s in run_entry.get("samples",[])]
        byskin={}
        for item in samples:byskin.setdefault(item["Skin"],[]).append(item)
        summary={}
        for skin,arr in byskin.items():
            summary[skin]={"count":len(arr)}
            for name in ["UiStartMs","FirstNativeMoveMs","SnapshotAwaitMs",
                         "NativeMoveMedianMs","NativeMoveP95Ms",
                         "ReleaseCallMs","BackgroundRecoveryMs","BackgroundFramesDuringMove",
                         "ProjectedPositions"]:
                vals=[float(x[name]) for x in arr if x.get(name) is not None]
                summary[skin][name]={"n":len(vals),"median":round(statistics.median(vals),5),
                                     "p95":round(sorted(vals)[int((len(vals)-1)*.95)],5),
                                     "max":round(max(vals),5)} if vals else None
            summary[skin]["snapshot_ok"]=sum(1 for x in arr if x["FrozenDragSnapshot"])
            summary[skin]["affinity_restored"]=sum(1 for x in arr if x["NativeAffinityRestored"])
            summary[skin]["material_active_at_end"]=sum(1 for x in arr if x["MaterialActiveAtEnd"])
        data.setdefault("aggregate",{})[label]=summary
    save()
    print("DETERMINISTIC_COST_RESULT",json.dumps(data.get("aggregate"),ensure_ascii=False),flush=True)
finally:
    save()
