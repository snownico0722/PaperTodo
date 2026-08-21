from __future__ import annotations

import re
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

OLD_BASE = "fe6fa1ac14dbe3cf16a8f51e756a2359ec72c960"
FORMAL_HEAD = "fd7437c9021ebfe14331dfd1c03e97bff904f77e"

CODE_PATHS = [
    "AppController.Settings.cs",
    "AppController.cs",
    "Models.cs",
    "PaperWindow.Todo.cs",
    "PaperWindow.TodoEnhancements.cs",
    "PaperWindow.cs",
    "StateStore.cs",
    "TodoRules.cs",
]
RESX_PATHS = [
    "Resources/Strings.en.resx",
    "Resources/Strings.ja.resx",
    "Resources/Strings.ko.resx",
    "Resources/Strings.resx",
]
CHANGELOG = "CHANGELOG.md"
DATA_BLOCK = re.compile(
    r'^  <data name="([^"]+)"[^>]*>\r?\n.*?^  </data>\r?\n?',
    re.MULTILINE | re.DOTALL,
)


def git_text(ref: str, path: str) -> str:
    result = subprocess.run(
        ["git", "show", f"{ref}:{path}"],
        check=True,
        stdout=subprocess.PIPE,
    )
    return result.stdout.decode("utf-8-sig")


def write_text(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="")


def blocks(text: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for match in DATA_BLOCK.finditer(text):
        key = match.group(1)
        if key in result:
            raise RuntimeError(f"duplicate resource key before merge: {key}")
        result[key] = match.group(0)
    return result


def semantic_merge_resx(path: str) -> None:
    base = git_text(OLD_BASE, path)
    formal = git_text(FORMAL_HEAD, path)
    current = Path(path).read_text(encoding="utf-8-sig")

    base_blocks = blocks(base)
    formal_blocks = blocks(formal)
    changed_keys = sorted(
        key for key in (set(base_blocks) | set(formal_blocks))
        if base_blocks.get(key) != formal_blocks.get(key)
    )

    for key in changed_keys:
        key_pattern = re.compile(
            r'^  <data name="' + re.escape(key) + r'"[^>]*>\r?\n.*?^  </data>\r?\n?',
            re.MULTILINE | re.DOTALL,
        )
        replacement = formal_blocks.get(key)
        matches = list(key_pattern.finditer(current))
        if replacement is None:
            if len(matches) > 1:
                raise RuntimeError(f"duplicate current resource key: {path}: {key}")
            if matches:
                current = key_pattern.sub("", current, count=1)
            continue

        if len(matches) > 1:
            raise RuntimeError(f"duplicate current resource key: {path}: {key}")
        if matches:
            current = key_pattern.sub(lambda _: replacement, current, count=1)
        else:
            marker = "</root>"
            if marker not in current:
                raise RuntimeError(f"missing </root>: {path}")
            block = replacement if replacement.endswith("\n") else replacement + "\n"
            current = current.replace(marker, block + marker, 1)

    write_text(path, current)

    # Validate structure and duplicate keys after semantic merge.
    ET.parse(path)
    merged_keys = [m.group(1) for m in DATA_BLOCK.finditer(current)]
    if len(merged_keys) != len(set(merged_keys)):
        raise RuntimeError(f"duplicate resource key after merge: {path}")

    for key in changed_keys:
        expected = formal_blocks.get(key)
        actual = blocks(current).get(key)
        if actual != expected:
            raise RuntimeError(f"resource delta not preserved: {path}: {key}")


def section(text: str, heading: str) -> str:
    start = text.find(heading)
    if start < 0:
        raise RuntimeError(f"missing heading: {heading}")
    next_heading = text.find("\n### ", start + len(heading))
    return text[start:] if next_heading < 0 else text[start:next_heading]


def merge_changelog() -> None:
    base = section(git_text(OLD_BASE, CHANGELOG), "### v3.3")
    formal = section(git_text(FORMAL_HEAD, CHANGELOG), "### v3.3")
    current = Path(CHANGELOG).read_text(encoding="utf-8-sig")
    current_section = section(current, "### v3.3")

    base_lines = set(base.splitlines())
    added_bullets = [
        line for line in formal.splitlines()
        if line.startswith("- ") and line not in base_lines
    ]
    if not added_bullets:
        raise RuntimeError("expected PR 125 changelog bullets")

    missing = [line for line in added_bullets if line not in current_section.splitlines()]
    if missing:
        anchor = "**优化和修复**\n\n"
        section_start = current.find("### v3.3")
        anchor_at = current.find(anchor, section_start)
        if anchor_at < 0:
            raise RuntimeError("missing v3.3 optimization anchor")
        insert_at = anchor_at + len(anchor)
        current = current[:insert_at] + "\n".join(missing) + "\n" + current[insert_at:]
        write_text(CHANGELOG, current)

    merged_section = section(Path(CHANGELOG).read_text(encoding="utf-8-sig"), "### v3.3")
    for line in added_bullets:
        if line not in merged_section.splitlines():
            raise RuntimeError(f"missing changelog delta after merge: {line}")


def apply_code_delta() -> None:
    patch = Path("pr125-code.patch")
    with patch.open("wb") as stream:
        subprocess.run(
            ["git", "diff", f"{OLD_BASE}..{FORMAL_HEAD}", "--", *CODE_PATHS],
            check=True,
            stdout=stream,
        )
    subprocess.run(["git", "apply", "--3way", "--index", str(patch)], check=True)
    patch.unlink(missing_ok=True)


def main() -> None:
    apply_code_delta()
    for path in RESX_PATHS:
        semantic_merge_resx(path)
    merge_changelog()
    subprocess.run(["git", "add", "--", *RESX_PATHS, CHANGELOG], check=True)

    # The reviewed self-link suppression must survive the replay.
    controller = Path("AppController.cs").read_text(encoding="utf-8")
    required = "string.Equals(paperId, sourceNote.Id, StringComparison.Ordinal)"
    if required not in controller:
        raise RuntimeError("self-link drop-target suppression missing after replay")

    for path in RESX_PATHS:
        if "<<<<<<<" in Path(path).read_text(encoding="utf-8"):
            raise RuntimeError(f"conflict marker remains: {path}")
    if "<<<<<<<" in Path(CHANGELOG).read_text(encoding="utf-8"):
        raise RuntimeError("conflict marker remains: CHANGELOG.md")

    print("PR #125 formal delta semantically replayed on latest 3.2.")


if __name__ == "__main__":
    main()
