"""Resolve only evidenced PR326 / main three-way conflicts.

Runs in a real git merge --no-commit worktree, preserving automatic merges.
Does not modify or push product branches.
"""
from pathlib import Path
import re
import subprocess

CONFLICT_FILES = {
    "CHANGELOG.md",
    "CHANGELOG.zh.md",
    "tests/PaperTodo.LifecycleChecks/Program.cs",
}
CONTROLLER_CASES = [
    "controller-hide-create-reentry",
    "controller-hide-show-reentry",
    "controller-zorder-create-reentry",
    "controller-close-delete-reentry",
]
TODO_CASES = ["todo-visibility", "todo-context-menu", "todo-history"]

def git(*args):
    result = subprocess.run(["git", *args], stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                            errors="replace", check=True)
    return result.stdout

unmerged = set(git("diff", "--name-only", "--diff-filter=U").splitlines())
if unmerged != CONFLICT_FILES:
    raise RuntimeError(f"Unreviewed merge conflicts: {sorted(unmerged)}; expected {sorted(CONFLICT_FILES)}")

pattern = re.compile(
    r"^<<<<<<< HEAD\n(?P<ours>.*?)^\|\|\|\|\|\|\| [^\n]*\n"
    r"(?P<base>.*?)^=======\n(?P<theirs>.*?)^>>>>>>> origin/main(?:\n|$)",
    re.MULTILINE | re.DOTALL,
)

for path in sorted(unmerged):
    p = Path(path)
    before = p.read_text(encoding="utf-8")
    matches = list(pattern.finditer(before))
    expected_count = 2 if path.endswith("/Program.cs") else 1
    if len(matches) != expected_count:
        raise RuntimeError(f"{path}: expected {expected_count} conflict blocks, got {len(matches)}")

    out = []
    end = 0
    for i, match in enumerate(matches):
        ours, theirs = match.group("ours"), match.group("theirs")
        out.append(before[end:match.start()])
        if path in ("CHANGELOG.md", "CHANGELOG.zh.md"):
            marker = "rapid switching between edge previews" if path == "CHANGELOG.md" else "快速连续切换边缘预览"
            if marker not in ours or (("Todo" not in theirs) if path == "CHANGELOG.md" else ("待办" not in theirs)):
                raise RuntimeError(f"{path}: conflict content differs from reviewed evidence")
            resolution = ours.rstrip("\n") + "\n" + theirs
        elif i == 0:
            if "private static readonly string[] Cases" not in theirs or not all(
                '"' + key + '"' in ours for key in CONTROLLER_CASES
            ) or not all('"' + key + '"' in theirs for key in TODO_CASES):
                raise RuntimeError("Case list was not the reviewed original")
            # Retain main's 324/325 cases and add the 326 four controller scenarios.
            anchor = '"master-queue-material-handoff",'
            if theirs.count(anchor) != 1:
                raise RuntimeError("Unexpected master material handoff list position")
            insert = " ".join(f'"{name}",' for name in CONTROLLER_CASES)
            resolution = theirs.replace(anchor, anchor + " " + insert, 1)
        else:
            if 'if (name.StartsWith("controller-"))' not in ours or \
                    'TodoContextMenuChecks.Prepare(state)' not in theirs:
                raise RuntimeError("Lifecycle fixture preparation changed unexpectedly")
            # Controller re-entry uses a collapsed queue; Todo context-menu test
            # needs its separate state preparation. Preserve both.
            resolution = ours + theirs
        out.append(resolution)
        end = match.end()
    out.append(before[end:])
    resolved = "".join(out)
    if any(tag in resolved for tag in ("<<<<<<< HEAD", ">>>>>>> origin/main", "||||||| 39292c7")):
        raise RuntimeError(f"{path}: conflict markers remain")
    if path.endswith("/Program.cs"):
        first = next(x for x in resolved.splitlines() if "private static readonly string[] Cases" in x)
        for key in CONTROLLER_CASES + TODO_CASES:
            if first.count('"' + key + '"') != 1:
                raise RuntimeError(f"Lost or duplicate lifecycle case {key}")
        for fragment in (
            'if (name.StartsWith("controller-"))',
            'TodoContextMenuChecks.Prepare(state)',
            'ControllerReentrancyChecks.HideDuringCreate',
            'TodoContextMenuChecks.Run',
            'TodoHistoryChecks.Run',
        ):
            if fragment not in resolved:
                raise RuntimeError(f"Merged fixture lost {fragment}")
    p.write_text(resolved, encoding="utf-8", newline="\n")
    print(f"RESOLVED {path}: {len(matches)} block(s)")

git("add", *sorted(unmerged))
remaining = git("ls-files", "--unmerged")
if remaining.strip():
    raise RuntimeError("Unmerged paths still present: " + remaining[:3000])
git("diff", "--cached", "--check")
changed = git("diff", "--cached", "--name-only").splitlines()
for needed in CONFLICT_FILES | {"src/AppController.cs", "src/PaperWindow.cs", "doc/ARCHITECTURE.md"}:
    if needed not in changed:
        raise RuntimeError(f"Expected merged source file absent: {needed}")
print("MERGE_RESOLUTION_OK", "changed_files=", len(changed))
print("FULL_MERGED_FILES\n", "\n".join(changed))
