"""Sync the full Chinese README into the English homepage's collapsed section."""

import argparse
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
START = "<!-- BEGIN GENERATED CHINESE README -->"
END = "<!-- END GENERATED CHINESE README -->"


def render_readme(readme, chinese):
    if not chinese.strip():
        raise ValueError("README.zh.md is empty.")
    block = (
        START
        + "\n<details>\n<summary>简体中文（点击展开完整 README）</summary>\n\n"
        + chinese
        + ("\n" if chinese.endswith("\n") else "\n\n")
        + "</details>\n"
        + END
    )
    counts = (readme.count(START), readme.count(END))
    if counts == (0, 0):
        return readme + ("\n" if readme.endswith("\n") else "\n\n") + "---\n\n" + block + "\n"
    if counts != (1, 1) or readme.index(START) >= readme.index(END):
        raise ValueError("Expected one ordered pair of generated Chinese README markers.")
    start = readme.index(START)
    end = readme.index(END) + len(END)
    return readme[:start] + block + readme[end:]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Fail if the generated section is out of date.")
    args = parser.parse_args()
    readme_path = ROOT / "README.md"
    readme = readme_path.read_text(encoding="utf-8")
    chinese = (ROOT / "README.zh.md").read_text(encoding="utf-8")
    try:
        updated = render_readme(readme, chinese)
    except ValueError as error:
        parser.error(str(error))
    if updated == readme:
        print("README.md Chinese section is up to date.")
        return
    if args.check:
        parser.exit(1, "README.md is out of date; run python .github/scripts/sync_readme.py\n")
    readme_path.write_text(updated, encoding="utf-8", newline="\n")
    print("Synced the full README.zh.md into README.md.")


if __name__ == "__main__":
    main()
