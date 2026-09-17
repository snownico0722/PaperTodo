from __future__ import annotations

from pathlib import Path
import copy
import json
import re

ROOT = Path('.')

EN = {
    '云·原神（实验）': 'Cloud Genshin (Experimental)',
    '在 PaperTodo 纸片中直接打开云·原神网页版。': 'Open the Cloud Genshin web app directly inside a PaperTodo paper.',
    '专注计时器': 'Focus Timer',
    '可关联 PaperTodo 待办的 WPF 番茄钟，支持完成联动、自动选择下一项和折叠后台计时。': 'A WPF Pomodoro timer that can link to PaperTodo todos, complete linked tasks, select the next item, and keep timing while collapsed.',
    'Web 时钟': 'Web Clock',
    '完整的 Web 插件示例：主题、时区、日期格式、标题和日进度均可配置。': 'A complete Web plugin sample with configurable theme integration, time zones, date formats, title, and day progress.',
    'Protocol 2.1 示例': 'Protocol 2.1 Sample',
    '演示 Runtime 向普通 Todo 发布宿主原生行内/右键动作，以及向任意 Paper 发布静态顶栏标签。': 'Demonstrates Runtime publishing host-native inline/context-menu actions to todos and static top-bar labels to arbitrary papers.',
    '待办复盘记录池': 'Todo Review Archive',
    '实时记录待办生命周期、提醒变化和完成趋势，长期保存并导出 Excel 可直接打开的 CSV。': 'Tracks todo lifecycle, reminder changes, and completion trends, keeps a long-lived archive, and exports Excel-friendly CSV.',
    '原生时钟': 'Native Clock',
    '完整的 WPF 时钟示例：时区、日期格式、标题和日进度均可配置。': 'A complete WPF clock sample with configurable time zones, date formats, title, and day progress.',
    '联动': 'Integration',
    '显示': 'Display',
    '计时': 'Timer',
    '时间与标题': 'Time & title',
    '启动': 'Startup',
    '记录': 'Recording',
    '导出': 'Export',
    '显示秒数': 'Show seconds',
    '显示日期': 'Show date',
    '显示日进度': 'Show day progress',
    '在底部显示今天已经过去的比例。': 'Show how much of the current day has elapsed.',
    '显示星期': 'Show weekday',
    '时间制式': 'Hour cycle',
    '24 小时': '24-hour',
    '12 小时': '12-hour',
    '日期格式': 'Date format',
    '2026年8月1日': 'August 1, 2026',
    '时区': 'Time zone',
    '本地时间': 'Local time',
    '北京时间': 'Beijing time',
    '东京时间': 'Tokyo time',
    '伦敦时间': 'London time',
    '纽约时间': 'New York time',
    '洛杉矶时间': 'Los Angeles time',
    '胶囊标题': 'Capsule title',
    '当前时间': 'Current time',
    '当前日期': 'Current date',
    '时区和时间': 'Time zone and time',
    '固定为“时钟”': 'Fixed “Clock”',
    '自定义文字': 'Custom text',
    '自定义标题': 'Custom title',
    '仅在标题模式为自定义时使用': 'Used only when the title mode is custom.',
    '时钟字号倍率': 'Clock text scale',
    '自动开始下一阶段': 'Auto-start next phase',
    '专注结束后自动开始休息，休息结束后自动开始专注。': 'Start the break automatically after focus, and start focus automatically after the break.',
    '显示关联待办': 'Show linked todo',
    '在计时器中显示可实时同步的 PaperTodo 待办选择器。': 'Show a PaperTodo todo picker that stays synchronized with the workspace.',
    '专注结束后完成待办': 'Complete todo after focus',
    '完整完成一轮专注时，将当前关联待办标记为完成；跳过不触发。': 'Mark the linked todo complete after a full focus session; skipping does not trigger it.',
    '完成后选择下一项': 'Select next after completion',
    '自动完成关联待办后，按纸片和顺序选择下一条未完成待办。': 'After completing the linked todo, select the next unfinished todo by paper and order.',
    '显示进度条': 'Show progress bar',
    '显示完成统计': 'Show completion stats',
    '默认专注时长': 'Default focus duration',
    '默认休息时长': 'Default break duration',
    '加减步长': 'Adjustment step',
    '分钟': 'min',
    '每日目标': 'Daily goal',
    '0 表示只显示完成数，不显示目标。': '0 shows only the completed count without a goal.',
    '轮': 'sessions',
    '结束提示音': 'Completion sound',
    '静音': 'Silent',
    '蜂鸣': 'Beep',
    '提示': 'Asterisk',
    '警示': 'Exclamation',
    '待办与倒计时': 'Todo and countdown',
    '阶段与倒计时': 'Phase and countdown',
    '仅阶段状态': 'Phase status only',
    '固定标题': 'Fixed title',
    '重置前确认': 'Confirm before reset',
    '启动后自动创建时钟胶囊': 'Create clock capsule on startup',
    'PaperTodo 启动稳定后创建或恢复同一个时钟胶囊，不会重复创建。': 'Create or restore the same clock capsule after PaperTodo startup without creating duplicates.',
    '显示周数': 'Show week number',
    '自动启动示例': 'Auto-start sample',
    '保持一张示例 Paper，使 provider Runtime 可以持续发布 2.1 contribution。': 'Keep one sample paper alive so the provider Runtime can continue publishing Protocol 2.1 contributions.',
    '保留已删除来源': 'Keep deleted sources',
    '删除原待办或整张待办纸后仍保留复盘记录。': 'Keep review records after the original todo or entire todo paper is deleted.',
    '显示进行中待办': 'Show open todos',
    '在“全部记录”和“进行中”筛选中显示尚未完成的项目。': 'Show unfinished items in the All records and Open filters.',
    '显示复盘指标': 'Show review metrics',
    '显示今日、近 7 天、连续完成日和进行中数量。': 'Show today, last 7 days, completion streak, and open-count metrics.',
    '记录创建时间': 'Record creation time',
    '为新建待办保存精确创建时刻。': 'Store the exact creation time for newly created todos.',
    '显示提醒变化': 'Show reminder changes',
    '记录并显示提醒设置、取消和调整次数。': 'Record and show reminder creation, removal, and adjustment counts.',
    '高亮近期提醒': 'Highlight upcoming reminders',
    '高亮未来 24 小时内和已经到期的待办提醒。': 'Highlight reminders due within 24 hours or already overdue.',
    '首次导入现有待办': 'Initial import of existing todos',
    '不自动导入': 'Do not import automatically',
    '仅已完成': 'Completed only',
    '全部待办': 'All todos',
    '默认筛选': 'Default filter',
    '已完成': 'Completed',
    '今天完成': 'Completed today',
    '近 7 天': 'Last 7 days',
    '近 30 天': 'Last 30 days',
    '进行中': 'Open',
    '重新打开': 'Reopened',
    '有提醒': 'Has reminder',
    '源已删除': 'Source deleted',
    '全部记录': 'All records',
    '保留天数': 'Retention days',
    '0 表示永久保留。': '0 keeps records forever.',
    '天': 'days',
    '最大记录数': 'Maximum records',
    '条': 'records',
    'CSV 编码': 'CSV encoding',
    'UTF-8 BOM（推荐 Excel）': 'UTF-8 BOM (recommended for Excel)',
    '时间格式': 'Time format',
    '本地时间（到分钟）': 'Local time (minutes)',
    'ISO 风格（到秒）': 'ISO style (seconds)',
    '包含所属纸片': 'Include paper title',
    '标记来源已删除': 'Mark deleted sources',
    '清空前确认': 'Confirm before clearing',
    '累计完成项目': 'Completed items',
    '今日完成数': 'Today’s completed count',
    '连续完成日': 'Completion streak',
    '进行中数量': 'Open count',
    '固定文字': 'Fixed text',
    '复盘记录': 'Review Archive',
}

MANIFESTS = [
    'plugin-samples/PaperTodo.Plugin.CloudGenshin/plugin.json',
    'plugin-samples/PaperTodo.Plugin.FocusTimer/plugin.json',
    'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/plugin.json',
    'plugin-samples/PaperTodo.Plugin.Protocol21Web/plugin.json',
    'plugin-samples/PaperTodo.Plugin.ReviewArchive/plugin.json',
    'plugin-samples/PaperTodo.Plugin.SampleClock/plugin.json',
]

def translate(value):
    return EN.get(value, value) if isinstance(value, str) else value

def localize_manifest(path: str) -> None:
    file = ROOT / path
    doc = json.loads(file.read_text(encoding='utf-8'))
    original = copy.deepcopy(doc)

    for key in ('name', 'description'):
        if key in doc:
            doc[key] = translate(doc[key])

    category_names: dict[str, str] = {}
    for category, old_category in zip(doc.get('settingCategories', []), original.get('settingCategories', [])):
        old_name = old_category.get('name', '')
        new_name = translate(old_name)
        category['name'] = new_name
        category_names[old_name] = new_name

    for setting in doc.get('settings', []):
        for key in ('name', 'description', 'suffix', 'placeholder'):
            if key in setting:
                setting[key] = translate(setting[key])
        if 'category' in setting:
            setting['category'] = category_names.get(setting['category'], translate(setting['category']))
        for option in setting.get('options', []):
            option['name'] = translate(option.get('name', ''))

    if 'startupPaper' in doc and 'title' in doc['startupPaper']:
        doc['startupPaper']['title'] = translate(doc['startupPaper']['title'])

    if doc.get('id') == 'sample.review-archive.native':
        for setting in doc.get('settings', []):
            if setting.get('id') == 'fixedTitle':
                setting['default'] = ''

    zh: dict[str, object] = {}
    if original.get('name'):
        zh['name'] = original['name']
    if original.get('description'):
        zh['description'] = original['description']
    if original.get('settingCategories'):
        zh['settingCategories'] = {
            category_names.get(item['name'], translate(item['name'])): item['name']
            for item in original['settingCategories']
        }

    localized_settings: dict[str, object] = {}
    for setting in original.get('settings', []):
        localized: dict[str, object] = {}
        for key in ('name', 'description', 'suffix', 'placeholder'):
            if key in setting and str(setting[key]).strip():
                localized[key] = setting[key]
        if setting.get('options'):
            localized['options'] = {option['value']: option['name'] for option in setting['options']}
        if localized:
            localized_settings[setting['id']] = localized
    if localized_settings:
        zh['settings'] = localized_settings
    if original.get('startupPaper', {}).get('title'):
        zh['startupPaper'] = {'title': original['startupPaper']['title']}

    doc['locales'] = {'zh': zh}

    probes = [doc.get('name', ''), doc.get('description', '')]
    probes.extend(category.get('name', '') for category in doc.get('settingCategories', []))
    for setting in doc.get('settings', []):
        probes.extend(setting.get(key, '') for key in ('name', 'description', 'suffix', 'placeholder', 'category'))
        probes.extend(option.get('name', '') for option in setting.get('options', []))
    leftovers = sorted({value for value in probes if isinstance(value, str) and re.search(r'[\u3400-\u9fff]', value)})
    if leftovers:
        raise SystemExit(f'untranslated host strings in {path}: {leftovers}')

    file.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

for manifest in MANIFESTS:
    localize_manifest(manifest)
