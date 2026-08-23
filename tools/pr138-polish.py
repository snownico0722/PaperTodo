from pathlib import Path
import json


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, found {count}")
    return text.replace(old, new, 1)


# 1) Advanced settings are explicit opt-in. Any sample that demonstrates the new
#    layout metadata must say so; samples without it remain on the old inline path.
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
            raise RuntimeError(f"{path}: advanced sample has no insertion point")
        index = min(candidates)
        text = text[:index] + '  "advancedSettings": true,\n' + text[index:]
        write(path, text)

# 2) The host is 2.0-only now. Remove the remaining miniEntry-era 1.8 gate/message.
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
text = replace_once(text, old, new, "remove 1.8 miniEntry gate")
if 'host supports 1.8 compatibility' in text:
    raise RuntimeError("old 1.8 compatibility message still exists")
write(registry, text)

# 3) Sample READMEs should describe their current 2.0 contract, not old protocol archaeology.
replacements = {
    Path("plugin-samples/PaperTodo.Plugin.FocusTimer/README.md"): [
        ("当前按 PaperTodo 协议 1.8 构建；待办读写/监听来自 1.3 数据能力，胶囊展示使用 1.6 宿主模板，边缘快速浏览使用专属 WPF 迷你界面。",
         "当前按 PaperTodo 协议 2.0 构建，包含待办读写/监听、宿主胶囊展示和专属 WPF 边缘迷你界面。"),
        ("- 1.6 胶囊显示运行/暂停状态点、当前待办/倒计时，并遵循“显示进度”设置决定是否显示阶段进度条；胶囊宽度按当前标题和可见组件自动适配；",
         "- 胶囊显示运行/暂停状态点、当前待办/倒计时，并遵循“显示进度”设置决定是否显示阶段进度条；胶囊宽度按当前标题和可见组件自动适配；"),
        ("- 1.8 迷你界面以 `300 × 210 DIP` 显示阶段、倒计时、关联待办和进度，并可直接开始、继续或暂停；",
         "- 迷你界面以 `300 × 210 DIP` 显示阶段、倒计时、关联待办和进度，并可直接开始、继续或暂停；"),
    ],
    Path("plugin-samples/PaperTodo.Plugin.SampleClock/README.md"): [
        ("这是一个完全由 WPF 控件构成的 PaperTodo 原生主示例。除正文外，它实现协议 1.7 `IPaperCapsuleViewProvider` 和协议 1.8 `IPaperMiniViewProvider`，并持续保留协议 1.6 模板作为启动、拖动交接和失败回退：",
         "这是一个完全由 WPF 控件构成的 PaperTodo 2.0 原生主示例。除正文外，它实现 `IPaperCapsuleViewProvider` 和 `IPaperMiniViewProvider`，并保留宿主胶囊模板作为启动、拖动交接和失败回退："),
        ("- 1.6 胶囊模板在启用日进度时显示进度环 + 当前标题，并按时间、日期、时区或自定义标题的实际内容自动适配宽度；",
         "- 胶囊模板在启用日进度时显示进度环 + 当前标题，并按时间、日期、时区或自定义标题的实际内容自动适配宽度；"),
        ("- 1.7 普通/贴边胶囊分别持有独立 WPF View，使用完整 `Width × Height` 内容槽，并在主题变化时原地刷新；",
         "- 普通/贴边胶囊分别持有独立 WPF View，使用完整 `Width × Height` 内容槽，并在主题变化时原地刷新；"),
        ("- 1.8 边缘快速浏览使用 `300 × 190 DIP` 专属 WPF 迷你时钟，与正文共享时间、设置和主题，但创建独立控件实例；",
         "- 边缘快速浏览使用 `300 × 190 DIP` 专属 WPF 迷你时钟，与正文共享时间、设置和主题，但创建独立控件实例；"),
    ],
    Path("plugin-samples/PaperTodo.Plugin.CloudGenshin/README.md"): [
        ("这是一个**完全独立的协议 1.8 原生正文插件**。",
         "这是一个**完全独立的协议 2.0 原生正文插件**。"),
        ("- 1.6 胶囊状态点区分加载、运行、重启和错误，并按当前状态文字自动适配宽度；不把 WebView2 塞进胶囊",
         "- 胶囊状态点区分加载、运行、重启和错误，并按当前状态文字自动适配宽度；不把 WebView2 塞进胶囊"),
        ("- 1.8 边缘快速浏览使用 `240 × 140 DIP` 的纯 WPF 状态面板；不启动第二个 WebView2，也不迁移完整云游戏画面",
         "- 边缘快速浏览使用 `240 × 140 DIP` 的纯 WPF 状态面板；不启动第二个 WebView2，也不迁移完整云游戏画面"),
    ],
    Path("plugin-samples/PaperTodo.Plugin.ReviewArchive/README.md"): [
        ("这是按 PaperTodo 协议 1.8 构建的完整原生插件；核心数据能力来自 1.3 的待办事件监听与只读接口，胶囊展示使用 1.6 宿主模板。它不提供专属迷你界面，用于演示宿主自动放大结构化胶囊的 1.8 回退路径。",
         "这是按 PaperTodo 协议 2.0 构建的完整原生插件；它使用待办事件监听与只读接口，并通过宿主胶囊模板展示。它不提供专属迷你界面，用于演示宿主自动放大结构化胶囊的回退路径。"),
        ("- 1.6 胶囊可显示累计完成、今日完成、连续完成日或进行中数量；开启“显示复盘指标”时附带进行中数量，并按当前可见指标自动适配宽度；",
         "- 胶囊可显示累计完成、今日完成、连续完成日或进行中数量；开启“显示复盘指标”时附带进行中数量，并按当前可见指标自动适配宽度；"),
    ],
}
for path, items in replacements.items():
    text = read(path)
    for old, new in items:
        if old in text:
            text = text.replace(old, new)
    write(path, text)

shortcut_doc = Path("plugin-samples/PROTOCOL-2.0-SHORTCUTS.md")
text = read(shortcut_doc)
text = text.replace(
    "用户在**插件自己的设置卡片或“更多设置”页**中录制或修改快捷键。它不会作为第三方动作塞进 PaperTodo 的全局快捷键设置页。",
    "用户在插件自己的设置区域中录制或修改快捷键；启用 `advancedSettings` 的插件也可以在独立“更多设置”页中修改。它不会作为第三方动作塞进 PaperTodo 的全局快捷键设置页。")
write(shortcut_doc, text)

# Sanity checks for the intended split.
for path in Path("plugin-samples").rglob("plugin.json"):
    data = json.loads(read(path))
    if data.get("settingCategories") and data.get("advancedSettings") is not True:
        raise RuntimeError(f"{path}: categories without advancedSettings opt-in")

print("PR138 final polish applied.")
