from pathlib import Path

path = Path("CHANGELOG.md")
text = path.read_text(encoding="utf-8")
bullet = "- Markdown 裸 `http://` / `https://` 地址可直接点击打开；行内代码和围栏代码中的示例地址保持普通文本，句末标点、括号和成对强调标记不会混入实际网址。"
if bullet not in text:
    section = text.find("### v3.3")
    if section < 0:
        raise RuntimeError("missing v3.3 section")
    anchor = "**优化和修复**\n\n"
    at = text.find(anchor, section)
    if at < 0:
        raise RuntimeError("missing v3.3 optimization anchor")
    at += len(anchor)
    text = text[:at] + bullet + "\n" + text[at:]
    path.write_text(text, encoding="utf-8", newline="")
print("PR #126 changelog entry present.")
