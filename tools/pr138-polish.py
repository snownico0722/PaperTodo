from pathlib import Path
import json


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8", newline="\n")


for base in (Path("plugin-samples"), Path("plugins")):
    if not base.exists():
        continue
    for path in base.rglob("plugin.json"):
        text = read(path)
        data = json.loads(text)
        settings = data.get("settings") or []
        needs_advanced = (
            "primarySettings" in data
            or bool(data.get("settingCategories"))
            or any(bool(item.get("category")) for item in settings if isinstance(item, dict))
        )
        if not needs_advanced or data.get("advancedSettings") is True:
            continue
        candidates = []
        for marker in ('  "primarySettings":', '  "settingCategories":', '  "settings":'):
            index = text.find(marker)
            if index >= 0:
                candidates.append(index)
        if not candidates:
            raise RuntimeError(f"{path}: no advancedSettings insertion point")
        index = min(candidates)
        text = text[:index] + '  "advancedSettings": true,\n' + text[index:]
        write(path, text)

registry = Path("src/PaperBodyPluginRegistry.cs")
text = read(registry)
old = '''        if (!string.IsNullOrWhiteSpace(manifest.MiniEntry))
        {
            if (!ApiAtLeast(manifest.ApiVersion, 1, 8))
            {
                throw new InvalidDataException(
                    "miniEntry requires apiVersion 1.8 or newer.");
            }
            if (kind != PaperBodyPluginKind.Web)
'''
new = '''        if (!string.IsNullOrWhiteSpace(manifest.MiniEntry))
        {
            if (kind != PaperBodyPluginKind.Web)
'''
if old in text:
    text = text.replace(old, new, 1)
elif "miniEntry requires apiVersion 1.8 or newer." in text:
    raise RuntimeError("unexpected residual 1.8 miniEntry gate")
write(registry, text)

replacements = {
    Path("plugin-samples/PaperTodo.Plugin.FocusTimer/README.md"): [
        ("当前按 PaperTodo 协议 1.8 构建；待办读写/监听来自 1.3 数据能力，胶囊展示使用 1.6 宿主模板，边缘快速浏览使用专属 WPF 迷你界面。",
         "当前按 PaperTodo 协议 2.0 构建，包含待办读写/监听、宿主胶囊展示和专属 WPF 边缘迷你界面。"),
        ("- 1.6 胶囊显示", "- 胶囊显示"),
        ("- 1.8 迷你界面", "- 迷你界面"),
    ],
    Path("plugin-samples/PaperTodo.Plugin.SampleClock/README.md"): [
        ("这是一个完全由 WPF 控件构成的 PaperTodo 原生主示例。除正文外，它实现协议 1.7 `IPaperCapsuleViewProvider` 和协议 1.8 `IPaperMiniViewProvider`，并持续保留协议 1.6 模板作为启动、拖动交接和失败回退：",
         "这是一个完全由 WPF 控件构成的 PaperTodo 2.0 原生主示例。除正文外，它实现 `IPaperCapsuleViewProvider` 和 `IPaperMiniViewProvider`，并保留宿主胶囊模板作为启动、拖动交接和失败回退："),
        ("- 1.6 胶囊模板", "- 胶囊模板"),
        ("- 1.7 普通/贴边胶囊", "- 普通/贴边胶囊"),
        ("- 1.8 边缘快速浏览", "- 边缘快速浏览"),
    ],
    Path("plugin-samples/PaperTodo.Plugin.CloudGenshin/README.md"): [
        ("这是一个**完全独立的协议 1.8 原生正文插件**。", "这是一个**完全独立的协议 2.0 原生正文插件**。"),
        ("- 1.6 胶囊状态点", "- 胶囊状态点"),
        ("- 1.8 边缘快速浏览", "- 边缘快速浏览"),
    ],
    Path("plugin-samples/PaperTodo.Plugin.ReviewArchive/README.md"): [
        ("这是按 PaperTodo 协议 1.8 构建的完整原生插件；核心数据能力来自 1.3 的待办事件监听与只读接口，胶囊展示使用 1.6 宿主模板。它不提供专属迷你界面，用于演示宿主自动放大结构化胶囊的 1.8 回退路径。",
         "这是按 PaperTodo 协议 2.0 构建的完整原生插件；它使用待办事件监听与只读接口，并通过宿主胶囊模板展示。它不提供专属迷你界面，用于演示宿主自动放大结构化胶囊的回退路径。"),
        ("- 1.6 胶囊可显示", "- 胶囊可显示"),
    ],
}
for path, pairs in replacements.items():
    text = read(path)
    for old, new in pairs:
        text = text.replace(old, new)
    write(path, text)

shortcut_doc = Path("plugin-samples/PROTOCOL-2.0-SHORTCUTS.md")
text = read(shortcut_doc)
text = text.replace(
    "用户在**插件自己的设置卡片或“更多设置”页**中录制或修改快捷键。它不会作为第三方动作塞进 PaperTodo 的全局快捷键设置页。",
    "用户在插件自己的设置区域中录制或修改快捷键；启用 `advancedSettings` 的插件也可以在独立“更多设置”页中修改。它不会作为第三方动作塞进 PaperTodo 的全局快捷键设置页。")
write(shortcut_doc, text)

for base in (Path("plugin-samples"), Path("plugins")):
    if not base.exists():
        continue
    for path in base.rglob("plugin.json"):
        data = json.loads(read(path))
        settings = data.get("settings") or []
        uses_advanced_metadata = (
            "primarySettings" in data
            or bool(data.get("settingCategories"))
            or any(bool(item.get("category")) for item in settings if isinstance(item, dict))
        )
        if uses_advanced_metadata and data.get("advancedSettings") is not True:
            raise RuntimeError(f"{path}: advanced metadata without advancedSettings: true")

print("PR138 semantic polish applied")
