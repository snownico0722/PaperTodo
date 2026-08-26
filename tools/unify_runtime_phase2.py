from pathlib import Path
import json
import re


def read(path):
    return Path(path).read_text(encoding='utf-8')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8')


def replace(path, old, new, count=None):
    text = read(path)
    actual = text.count(old)
    if count is not None and actual != count:
        raise SystemExit(f'{path}: expected {count} occurrences, found {actual}: {old[:120]!r}')
    if actual == 0:
        raise SystemExit(f'{path}: replacement target missing: {old[:120]!r}')
    write(path, text.replace(old, new))


def regex_replace(path, pattern, replacement, count=0, flags=re.S):
    text = read(path)
    text2, actual = re.subn(pattern, replacement, text, count=count, flags=flags)
    if actual == 0:
        raise SystemExit(f'{path}: regex target missing: {pattern[:120]!r}')
    write(path, text2)


# ---------------------------------------------------------------------------
# Manifest/protocol: one provider Runtime only. paperRuntime is retired.
# ---------------------------------------------------------------------------
registry = 'src/PaperBodyPluginRegistry.cs'
text = read(registry)
text = text.replace('    public string PaperRuntime { get; set; } = "";\n', '')
text = text.replace('    public string PaperRuntimePath { get; internal set; } = "";\n', '')

# Remove the old per-Paper Web runtime validation/resolution block and reject the obsolete Web
# backgroundUpdates requirement explicitly instead of silently inventing a second backend mode.
pattern = re.compile(
    r'''        var requiresBackgroundUpdates =\n.*?        return manifest;''',
    re.S)
match = pattern.search(text)
if not match:
    raise SystemExit('registry: background/paperRuntime block not found')
replacement = '''        var requiresBackgroundUpdates =
            (ParseRuntimeRequirements(manifest.Requires) &
             PaperBodyRuntimeRequirements.BackgroundUpdates) != 0;
        if (kind == PaperBodyPluginKind.Web && requiresBackgroundUpdates)
        {
            throw new InvalidDataException(
                "Web plugins use the single provider appRuntime/runtime backend; " +
                "requires: backgroundUpdates is not supported.");
        }

        return manifest;'''
text = text[:match.start()] + replacement + text[match.end():]

# Web discovery fingerprint no longer includes a second runtime entry.
text = re.sub(
    r'''\n\s*string\.IsNullOrWhiteSpace\(manifest\.PaperRuntimePath\)\n\s*\? null\n\s*: manifest\.PaperRuntimePath,''',
    '',
    text)
write(registry, text)

# ---------------------------------------------------------------------------
# Controller lifecycle/status: remove the second per-Paper runtime manager.
# ---------------------------------------------------------------------------
replace('src/AppController.PluginApi.cs',
'''        // Deletion is committed before this cleanup pass. Reconcile from the final entity-paper
        // set so provider-level and per-paper runtimes lose deleted owners promptly.
        ReconcilePluginAppRuntimes();
        ReconcileWebPaperRuntimes();''',
'''        // Deletion is committed before this cleanup pass. Reconcile from the final entity-paper
        // set so the provider Runtime loses deleted logical Paper instances promptly.
        ReconcilePluginAppRuntimes();''', count=1)
replace('src/AppController.PluginApi.cs',
'''    internal void DisposePaperPluginHostRuntime()
    {
        DisposeWebPaperRuntimes();
        DisposePluginAppRuntimes();''',
'''    internal void DisposePaperPluginHostRuntime()
    {
        DisposePluginAppRuntimes();''', count=1)

status = 'src/AppController.PluginStatus.cs'
text = read(status)
text = text.replace('            HasWebPaperRuntimeFailure(descriptor.Id) ||\n', '')
text = text.replace('        return IsPluginAppRuntimeRunning(descriptor.Id) ||\n               HasRunningWebPaperRuntime(descriptor.Id) ||\n',
                    '        return IsPluginAppRuntimeRunning(descriptor.Id) ||\n')
text = text.replace('        ReconcilePluginAppRuntimes();\n        ReconcileWebPaperRuntimes();\n',
                    '        ReconcilePluginAppRuntimes();\n')
write(status, text)

plugins = 'src/AppController.Plugins.cs'
replace(plugins,
'''        var settingsJson = _paperBodyPlugins.DataStore.GetSettingsJson(descriptor);
        RetryFailedPluginAppRuntimeAfterSettingsChanged(providerId);
        RetryFailedWebPaperRuntimesAfterSettingsChanged(providerId);
        NotifyWebPaperRuntimeSettingsChanged(providerId, settingsJson);''',
'''        var settingsJson = _paperBodyPlugins.DataStore.GetSettingsJson(descriptor);
        RetryFailedPluginAppRuntimeAfterSettingsChanged(providerId);
        NotifyPluginAppRuntimeSettingsChanged(providerId, settingsJson);''', count=1)

# Generic removal of direct old manager calls in controller/window partials. Remaining references are
# intentionally caught by the validation grep/build below.
for path in Path('src').glob('*.cs'):
    if path.name in {'AppController.WebPaperRuntime.cs', 'WebPaperRuntime.cs',
                     'PaperWindow.WebPaperRuntimePresentation.cs'}:
        continue
    text = path.read_text(encoding='utf-8')
    text = text.replace('        EnableWebPaperRuntimeReconciliation();\n', '')
    text = text.replace('        ReconcileWebPaperRuntimes();\n', '')
    text = text.replace('        DisposeWebPaperRuntimes();\n', '')
    text = text.replace('HasWebPaperRuntimePresentationOwner', 'HasPluginRuntimePresentationOwner')
    path.write_text(text, encoding='utf-8')

# ---------------------------------------------------------------------------
# Body/Mini are frontends of the provider Runtime. Their own UI state stays per-Paper writable.
# ---------------------------------------------------------------------------
window = 'src/PaperWindow.PluginBodies.cs'
text = read(window)
old = '''                var hasPaperRuntime =
                    (descriptor.RuntimeRequirements &
                     PaperBodyRuntimeRequirements.BackgroundUpdates) != 0 &&
                    !string.IsNullOrWhiteSpace(
                        descriptor.Manifest.PaperRuntimePath);
                return new WebPaperBodySession(
                    context,
                    descriptor.Manifest,
                    payload => _controller.PostBodyMessageToWebPaperRuntime(
                        _paper.Id,
                        descriptor.Id,
                        payload),
                    paperRuntimeOwnsPresentation: hasPaperRuntime,
                    paperRuntimeOwnsState: hasPaperRuntime);'''
new = '''                var runtimeOwnsPresentation =
                    descriptor.Manifest.Capabilities.Contains(
                        "appRuntime",
                        StringComparer.Ordinal);
                return new WebPaperBodySession(
                    context,
                    descriptor.Manifest,
                    runtimeOwnsPresentation);'''
if old not in text:
    raise SystemExit('PaperWindow.PluginBodies: Web session construction block not found')
text = text.replace(old, new)
# Per-Paper plugin UI state no longer has a hidden runtime peer to notify.
text = re.sub(
    r'''\n\s*_controller\.NotifyWebPaperRuntimeStateChanged\(\n\s*_paper\.Id,\n\s*providerId,\n\s*normalized\);''',
    '',
    text)
# Old presentation cache/manager calls are retired.
text = re.sub(r'^\s*_controller\.ReconcileWebPaperRuntimes\(\);\n', '', text, flags=re.M)
text = re.sub(r'^\s*ClearWebPaperRuntimePresentation\([^\n]*\);\n', '', text, flags=re.M)
write(window, text)

body = 'src/WebPaperBodySession.cs'
text = read(body)
text = text.replace('    private readonly Func<JsonElement, bool>? _postRuntimeMessage;\n', '')
text = text.replace('    private readonly bool _paperRuntimeOwnsPresentation;\n',
                    '    private readonly bool _runtimeOwnsPresentation;\n')
text = text.replace('    private readonly bool _paperRuntimeOwnsState;\n', '')
text = text.replace(
'''        PaperBodyContext context,
        PaperBodyPluginManifest manifest,
        Func<JsonElement, bool>? postRuntimeMessage = null,
        bool paperRuntimeOwnsPresentation = false,
        bool paperRuntimeOwnsState = false)''',
'''        PaperBodyContext context,
        PaperBodyPluginManifest manifest,
        bool runtimeOwnsPresentation = false)''')
text = text.replace('        _postRuntimeMessage = postRuntimeMessage;\n', '')
text = text.replace('        _paperRuntimeOwnsPresentation = paperRuntimeOwnsPresentation;\n',
                    '        _runtimeOwnsPresentation = runtimeOwnsPresentation;\n')
text = text.replace('        _paperRuntimeOwnsState = paperRuntimeOwnsState;\n', '')
text = text.replace('persistentStateWritable: !_paperRuntimeOwnsState',
                    'persistentStateWritable: true')
text = text.replace('Persistent paper state is owned by paperRuntime; send a runtime command instead.',
                    'Persistent frontend state is unavailable.')
text = text.replace('Persistent paper state providers belong to paperRuntime for this plugin.',
                    'Persistent frontend state providers are unavailable.')
# Body state is frontend state and always writable.
text = text.replace(
'''                case "saveState":
                    if (!_paperRuntimeOwnsState)
                    {
                        UpdateStateFromWebSurface(payload, sourceMini: null);
                    }
                    break;''',
'''                case "saveState":
                    UpdateStateFromWebSurface(payload, sourceMini: null);
                    break;''')
# Long-lived presentation has one writer whenever a provider Runtime exists.
text = text.replace(
'''                case "setTitle":
                    _context.SetTitle(ReadPayloadString(payload));
                    break;
                case "setHeaderText":
                    _context.Paper.SetHeaderText(ReadPayloadString(payload));
                    break;
                case "setCapsulePresentation":
                    _context.Paper.SetCapsulePresentation(
                        payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                            ? null
                            : JsonSerializer.Deserialize<PaperCapsulePresentation>(
                                payload.GetRawText(),
                                BridgeJsonOptions));
                    break;''',
'''                case "setTitle":
                    if (!_runtimeOwnsPresentation)
                    {
                        _context.SetTitle(ReadPayloadString(payload));
                    }
                    break;
                case "setHeaderText":
                    if (!_runtimeOwnsPresentation)
                    {
                        _context.Paper.SetHeaderText(ReadPayloadString(payload));
                    }
                    break;
                case "setCapsulePresentation":
                    if (!_runtimeOwnsPresentation)
                    {
                        _context.Paper.SetCapsulePresentation(
                            payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                                ? null
                                : JsonSerializer.Deserialize<PaperCapsulePresentation>(
                                    payload.GetRawText(),
                                    BridgeJsonOptions));
                    }
                    break;''')
# runtime.post now targets the one provider backend through the common Body context.
text = text.replace(
'''            if (_postRuntimeMessage == null ||
                !_postRuntimeMessage(
                    message.ValueKind == JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement<object?>(null)
                        : message.Clone()))''',
'''            if (!_context.Runtime.Post(
                    message.ValueKind == JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement<object?>(null)
                        : message.Clone()))''')
text = text.replace('The paper runtime is not ready to accept this message.',
                    'The plugin Runtime is not ready to accept this message.')
text = text.replace('_paperRuntimeOwnsPresentation', '_runtimeOwnsPresentation')
write(body, text)

mini_requests = 'src/WebPaperBodySession.MiniRequests.cs'
text = read(mini_requests)
text = text.replace(
'''            if (_postRuntimeMessage == null ||
                !_postRuntimeMessage(
                    message.ValueKind == JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement<object?>(null)
                        : message.Clone()))''',
'''            if (!_context.Runtime.Post(
                    message.ValueKind == JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement<object?>(null)
                        : message.Clone()))''')
text = text.replace('The paper runtime is not ready to accept this message.',
                    'The plugin Runtime is not ready to accept this message.')
write(mini_requests, text)

mini = 'src/WebPaperBodySession.Mini.cs'
text = read(mini)
# Mini and Body share their per-Paper frontend state; backend state is separate.
text = text.replace('persistentStateWritable: !_owner._paperRuntimeOwnsState',
                    'persistentStateWritable: true')
text = text.replace('Persistent paper state is owned by paperRuntime; send a runtime command instead.',
                    'Persistent frontend state is unavailable.')
text = text.replace('Persistent paper state providers belong to paperRuntime for this plugin.',
                    'Persistent frontend state providers are unavailable.')
text = text.replace('        if (_paperRuntimeOwnsState)\n        {\n            return;\n        }\n\n', '')
text = text.replace('        if (_owner._paperRuntimeOwnsState)\n        {\n            return;\n        }\n\n', '')
write(mini, text)

# ---------------------------------------------------------------------------
# Official Web clock: provider Runtime manages logical Paper ids, no per-Paper hidden WebView.
# ---------------------------------------------------------------------------
for manifest_path in [
    'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/plugin.json',
    'plugins/official.clock.web/plugin.json'
]:
    data = json.loads(read(manifest_path))
    data.pop('paperRuntime', None)
    data['runtime'] = 'web/runtime.html'
    caps = list(data.get('capabilities') or [])
    if 'appRuntime' not in caps:
        caps.append('appRuntime')
    data['capabilities'] = caps
    requires = [value for value in (data.get('requires') or []) if value != 'backgroundUpdates']
    if requires:
        data['requires'] = requires
    else:
        data.pop('requires', None)
    write(manifest_path, json.dumps(data, ensure_ascii=False, indent=2) + '\n')

clock_runtime = r'''<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <title>PaperTodo Clock Runtime</title>
</head>
<body>
<script>
  const defaults = Object.freeze({
    showDayProgress: true,
    hourCycle: '24',
    dateFormat: 'long',
    timeZone: 'local',
    titleMode: 'time',
    customTitle: ''
  });
  const zoneMap = Object.freeze({
    local: undefined,
    utc: 'UTC',
    beijing: 'Asia/Shanghai',
    tokyo: 'Asia/Tokyo',
    london: 'Europe/London',
    newYork: 'America/New_York',
    losAngeles: 'America/Los_Angeles'
  });
  const zoneLabels = Object.freeze({
    local: '本地时间',
    utc: 'UTC',
    beijing: '北京时间',
    tokyo: '东京时间',
    london: '伦敦时间',
    newYork: '纽约时间',
    losAngeles: '洛杉矶时间'
  });

  let settings = { ...defaults };
  const paperIds = new Set();
  let timer = 0;
  let lastSignature = '';

  function applySettings(value) {
    settings = { ...defaults, ...(value || {}) };
  }

  function replacePapers(values) {
    paperIds.clear();
    for (const value of Array.isArray(values) ? values : []) {
      const id = String(value?.paperId || '').trim();
      if (id) paperIds.add(id);
    }
  }

  function zonedParts(date) {
    const timeZone = zoneMap[settings.timeZone];
    const parts = new Intl.DateTimeFormat(undefined, {
      timeZone,
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit',
      weekday: 'long', hour12: settings.hourCycle === '12'
    }).formatToParts(date);
    return Object.fromEntries(parts.map(part => [part.type, part.value]));
  }

  function formatDate(parts) {
    const y = parts.year;
    const m = parts.month;
    const d = parts.day;
    return settings.dateFormat === 'short' ? `${y}-${m}-${d}`
      : settings.dateFormat === 'slash' ? `${y}/${m}/${d}`
      : settings.dateFormat === 'us' ? `${m}/${d}/${y}`
      : settings.dateFormat === 'eu' ? `${d}/${m}/${y}`
      : `${y}年${Number(m)}月${Number(d)}日`;
  }

  function zonedClockParts(date) {
    const parts = new Intl.DateTimeFormat('en-US', {
      timeZone: zoneMap[settings.timeZone],
      hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23'
    }).formatToParts(date);
    return Object.fromEntries(parts.map(part => [part.type, part.value]));
  }

  function titleText(timeText, dateText) {
    if (settings.titleMode === 'date') return dateText || timeText;
    if (settings.titleMode === 'zone') {
      return `${zoneLabels[settings.timeZone] || '本地时间'} · ${timeText}`;
    }
    if (settings.titleMode === 'fixed') return '时钟';
    if (settings.titleMode === 'custom' && String(settings.customTitle || '').trim()) {
      return String(settings.customTitle).trim();
    }
    return timeText;
  }

  function publish(force = false) {
    const api = window.papertodo?.papers;
    if (!api || paperIds.size === 0) return;
    const now = new Date();
    const parts = zonedParts(now);
    const timeText = `${parts.hour}:${parts.minute}`;
    const title = titleText(timeText, formatDate(parts));
    const clock = zonedClockParts(now);
    const elapsed = Number(clock.hour) * 3600 + Number(clock.minute) * 60 + Number(clock.second);
    const percent = Math.min(100, Math.max(0, elapsed / 864));
    const progressStep = Math.round(percent * 10);
    const signature = `${title}\u001f${progressStep}\u001f${settings.showDayProgress ? 1 : 0}`;
    if (!force && signature === lastSignature) return;
    lastSignature = signature;

    const presentation = {
      preferredWidth: 0,
      plainText: title,
      toolTip: title,
      components: settings.showDayProgress
        ? [
            { kind: 'progressRing', value: percent / 100, tone: 'accent' },
            { kind: 'text', text: title, fill: true }
          ]
        : [{ kind: 'text', text: title, fill: true }]
    };
    for (const paperId of paperIds) {
      void api.setHeaderText(paperId, title);
      void api.setCapsulePresentation(paperId, presentation);
    }
  }

  function restartTimer() {
    clearInterval(timer);
    timer = setInterval(() => publish(false), 1000);
    publish(true);
  }

  window.addEventListener('papertodo', event => {
    const message = event.detail || {};
    if (message.type === 'initialize') {
      applySettings(message.settings);
      replacePapers(message.papers);
      restartTimer();
      return;
    }
    if (message.type === 'settingsChanged') {
      applySettings(message.settings);
      lastSignature = '';
      restartTimer();
      return;
    }
    if (message.type === 'paperEvent') {
      const item = message.event || {};
      const paperId = String(item.paperId || '').trim();
      if (!paperId) return;
      if (item.kind === 'paperAdded') paperIds.add(paperId);
      if (item.kind === 'paperRemoved') paperIds.delete(paperId);
      publish(true);
    }
  });
</script>
</body>
</html>
'''
for runtime_path in [
    'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/web/runtime.html',
    'plugins/official.clock.web/web/runtime.html'
]:
    write(runtime_path, clock_runtime)

for old_clock_runtime in [
    'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/web/paper-runtime.html',
    'plugins/official.clock.web/web/paper-runtime.html'
]:
    p = Path(old_clock_runtime)
    if p.exists():
        p.unlink()

# Delete host-managed per-Paper backend implementation completely.
for old in [
    'src/WebPaperRuntime.cs',
    'src/AppController.WebPaperRuntime.cs',
    'src/PaperWindow.WebPaperRuntimePresentation.cs'
]:
    p = Path(old)
    if p.exists():
        p.unlink()

print('phase2 transformations complete')
