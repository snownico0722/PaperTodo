"""Render GitHub release notes with collapsed Chinese first, followed by English."""

import argparse
from pathlib import Path
import re
from urllib.parse import quote


def release_section(markdown, tag):
    """Read one exact version heading, allowing a trailing human-readable label."""
    heading = re.compile(r"^###\s+" + re.escape(tag) + r"(?:\s|$)")
    lines = markdown.splitlines()
    starts = [i for i, line in enumerate(lines) if heading.match(line)]
    if len(starts) != 1:
        raise ValueError(f"Expected exactly one changelog section for {tag}; found {len(starts)}.")
    start = starts[0] + 1
    end = next((i for i in range(start, len(lines)) if re.match(r"^###\s+", lines[i])), len(lines))
    section = lines[start:end]
    while section and section[0].strip() in ("", "---"):
        section.pop(0)
    while section and section[-1].strip() in ("", "---"):
        section.pop()
    notes = "\n".join(section).strip()
    if not notes:
        raise ValueError(f"Changelog section for {tag} is empty.")
    return notes


def render_notes(english, chinese, repository, tag, assets):
    if not english.strip() or not chinese.strip():
        raise ValueError("Both English and Chinese release notes are required.")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Repository must be in owner/name format.")
    if not re.fullmatch(r"v[0-9][A-Za-z0-9_.-]*", tag):
        raise ValueError("Expected a version tag beginning with v.")
    if not assets:
        raise ValueError("At least one download asset is required.")

    downloads = []
    for name in assets:
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", name):
            raise ValueError(f"Unsupported asset filename: {name}")
        url = f"https://github.com/{repository}/releases/download/{quote(tag, safe='')}/{quote(name, safe='')}"
        if "win7BestEffort" in name:
            label = "Windows 7 SP1 x64, best effort, runtime included / 尽力兼容版，含 .NET 运行库"
        elif "self-contained" in name:
            label = "Full version, runtime included / 完整版，含 .NET 运行库"
        elif "no-runtime" in name:
            label = "Smaller download, .NET Desktop Runtime required / 精简版，需自行安装 .NET 桌面运行库"
        elif name == "SHA256SUMS.txt":
            label = "SHA-256 checksums / 文件校验值"
        else:
            label = "Download / 下载"
        downloads.append(f"- **{label}**: [{name}]({url})")

    return (
        "<details>\n<summary>简体中文更新日志</summary>\n\n"
        + chinese.strip()
        + "\n\n</details>\n\n"
        + english.strip()
        + "\n\n## Downloads / 下载\n\n"
        + "\n".join(downloads)
        + "\n"
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--english", type=Path, default=Path("CHANGELOG.md"))
    parser.add_argument("--chinese", type=Path, default=Path("CHANGELOG.zh.md"))
    parser.add_argument("--assets", nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    english = release_section(args.english.read_text(encoding="utf-8-sig"), args.tag)
    chinese = release_section(args.chinese.read_text(encoding="utf-8-sig"), args.tag)
    notes = render_notes(english, chinese, args.repository, args.tag, args.assets)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(notes, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
