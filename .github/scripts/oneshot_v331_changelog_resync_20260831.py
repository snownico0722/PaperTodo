from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path.cwd()
path = root / "CHANGELOG.md"
text = path.read_text(encoding="utf-8")

new_section = """### v3.31

**bug修复和优化**
- **高级设置视觉区分**：开启高级设置后，高级选项会以轻微染色背景与边框分组显示，更容易与常用设置区分。
- **修复含图笔记静置时的持续刷新问题**：修复含图片的笔记在前台静置时反复触发图片刷新，导致 CPU / GPU 占用持续偏高的问题。
- **含图笔记缩放性能优化**：调整纸片大小时复用已显示的图片内容，并减少缩放过程中的重复刷新与处理，降低含图笔记连续缩放时的额外开销。
- **修复自定义字体增强加粗的 Markdown 正文适配**：启用增强加粗后，笔记中的 Markdown 标题、粗体和 `<b>` / `<strong>` 会正确使用自定义粗体字形。
- 修复开启“已完成待办自动置底”后，通过底部 ＋、Enter 或多行粘贴新增待办时，已完成事项可能不再保持置底的问题。
- 修复部分输入法状态下录制全局快捷键时，按键可能被错误识别、导致快捷键无法正常保存或触发的问题。
"""

start = text.find("### v3.31")
if start < 0:
    raise SystemExit("main CHANGELOG has no v3.31 section")
next_section = text.find("\n### ", start + len("### v3.31"))
end = len(text) if next_section < 0 else next_section + 1
text = text[:start] + new_section + text[end:]

# If the newly released 3.31 item was also listed under Unreleased, remove the duplicate there.
unreleased_start = text.find("### Unreleased")
unreleased_end = text.find("\n### v0.1", unreleased_start)
if unreleased_start >= 0 and unreleased_end > unreleased_start:
    bullet = "- **高级设置视觉区分**：开启高级设置后，高级选项会以轻微染色背景与边框分组显示，更容易与常用设置区分。\n"
    block = text[unreleased_start:unreleased_end]
    block = block.replace(bullet, "")
    text = text[:unreleased_start] + block + text[unreleased_end:]

path.write_text(text, encoding="utf-8", newline="\n")
print("v3.31 changelog synced")
