from __future__ import annotations

from pathlib import Path
import copy
import json

# Trigger the temporary branch build after the workflow file itself exists.
ROOT = Path('.')

# Normalize TopBarWeb to English base + Chinese locale while preserving its existing ja/ko packs.
path = ROOT / 'plugin-samples/PaperTodo.Plugin.TopBarWeb/plugin.json'
doc = json.loads(path.read_text(encoding='utf-8'))
base_zh = copy.deepcopy(doc)
en = copy.deepcopy(doc.get('locales', {}).get('en-US', {}))
if not en:
    raise SystemExit('TopBarWeb en-US locale missing')

for key in ('name', 'description'):
    if key in en:
        doc[key] = en[key]
for setting in doc.get('settings', []):
    localized = en.get('settings', {}).get(setting['id'], {})
    for key in ('name', 'description', 'suffix', 'placeholder'):
        if key in localized:
            setting[key] = localized[key]
    if 'options' in localized:
        names = localized['options']
        for option in setting.get('options', []):
            if option['value'] in names:
                option['name'] = names[option['value']]

zh = {
    'name': base_zh['name'],
    'description': base_zh['description'],
    'settings': {
        setting['id']: {
            key: setting[key]
            for key in ('name', 'description', 'suffix', 'placeholder')
            if key in setting and str(setting[key]).strip()
        }
        for setting in base_zh.get('settings', [])
    }
}
for setting in base_zh.get('settings', []):
    if setting.get('options'):
        zh['settings'][setting['id']]['options'] = {
            option['value']: option['name'] for option in setting['options']
        }
locales = doc.setdefault('locales', {})
locales['zh'] = zh
path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

# Make ReviewArchive corrupt/missing settings use the same language-aware fixed-title fallback.
settings_reader = ROOT / 'plugin-samples/PaperTodo.Plugin.ReviewArchive/ReviewArchiveSettingsReader.cs'
text = settings_reader.read_text(encoding='utf-8')
old = '                Text(root, "fixedTitle", "复盘记录", 40));'
new = '                Text(root, "fixedTitle", PluginText.T("复盘记录"), 40));'
if old in text:
    text = text.replace(old, new, 1)
elif new not in text:
    raise SystemExit('ReviewArchive fixedTitle reader fallback not found')
old = '                "utf8bom", "local", true, true, true, "summary", "复盘记录");'
new = '                "utf8bom", "local", true, true, true, "summary", PluginText.T("复盘记录"));'
if old in text:
    text = text.replace(old, new, 1)
elif new not in text:
    raise SystemExit('ReviewArchive corrupt-settings fallback not found')
settings_reader.write_text(text, encoding='utf-8')
