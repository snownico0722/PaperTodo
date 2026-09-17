from __future__ import annotations

from pathlib import Path

ROOT = Path('.')


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding='utf-8')


def write(path: str, text: str) -> None:
    (ROOT / path).write_text(text, encoding='utf-8')


def replace(path: str, old: str, new: str, count: int = 1) -> None:
    text = read(path)
    if old in text:
        write(path, text.replace(old, new, count))
        return
    if new in text:
        return
    raise SystemExit(f'missing replacement in {path}: {old[:140]!r}')

# Expose PaperTodo's resolved UI culture to all Web plugin surfaces.
replace(
    'src/WebPaperBodySession.cs',
    '            apiVersion = _context.ApiVersion,\n            state = ParseState(_stateJson),',
    '            apiVersion = _context.ApiVersion,\n            uiLanguage = _context.UiLanguage,\n            state = ParseState(_stateJson),')
replace(
    'src/WebPaperBodySession.Mini.cs',
    '            apiVersion = _owner._context.ApiVersion,\n            state = ParseState(_owner._stateJson),',
    '            apiVersion = _owner._context.ApiVersion,\n            uiLanguage = _owner._context.UiLanguage,\n            state = ParseState(_owner._stateJson),')
replace(
    'src/WebPluginRuntime.cs',
    '            apiVersion = _descriptor.ApiVersion,\n            permissions = _workspace.GrantedPermissions.OrderBy(value => value).ToArray(),',
    '            apiVersion = _descriptor.ApiVersion,\n            uiLanguage = UiLanguages.EffectiveUiCulture.Name,\n            permissions = _workspace.GrantedPermissions.OrderBy(value => value).ToArray(),')

# Document the Web initialize field next to the Native guidance.
replace(
    'plugin-samples/PROTOCOL-2.1-LOCALIZATION.md',
    '`locales` 只覆盖宿主绘制的 manifest/settings 文案；Native/Web 插件自己绘制的内容仍由插件自行组织翻译资源。\n',
    '''`locales` 只覆盖宿主绘制的 manifest/settings 文案；Native/Web 插件自己绘制的内容仍由插件自行组织翻译资源。\n\n## Web 插件读取当前语言\n\nBody、Mini 和 provider Runtime 的 `initialize` 消息都会包含 `uiLanguage`，值与 Native 的 `context.UiLanguage` 一致：\n\n```js\nwindow.addEventListener('papertodo', event => {\n  const message = event.detail || {};\n  if (message.type !== 'initialize') return;\n  const uiLanguage = message.uiLanguage || 'en-US';\n});\n```\n\n示例插件采用“中文 + 英文回退”：`zh-*` 使用中文，其余语言使用英文。这样 Web 自绘界面不会错误跟随 WebView/系统浏览器语言。\n''')

# Official Web Clock body.
path = 'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/web/index.html'
replace(path, '<html lang="zh-CN">', '<html lang="en">')
replace(path,
'''    const zoneLabels = Object.freeze({
      local: '本地时间',
      utc: 'UTC',
      beijing: '北京时间',
      tokyo: '东京时间',
      london: '伦敦时间',
      newYork: '纽约时间',
      losAngeles: '洛杉矶时间'
    });

    function applyTheme(theme) {''',
'''    const zoneLabels = Object.freeze({
      local: ['本地时间', 'Local time'],
      utc: ['UTC', 'UTC'],
      beijing: ['北京时间', 'Beijing time'],
      tokyo: ['东京时间', 'Tokyo time'],
      london: ['伦敦时间', 'London time'],
      newYork: ['纽约时间', 'New York time'],
      losAngeles: ['洛杉矶时间', 'Los Angeles time']
    });
    let uiLanguage = 'en-US';
    const isChinese = () => /^zh(?:-|$)/i.test(uiLanguage);
    const locale = () => isChinese() ? 'zh-CN' : 'en-US';
    const t = (zh, en) => isChinese() ? zh : en;
    const zoneLabel = value => (zoneLabels[value] || zoneLabels.local)[isChinese() ? 0 : 1];
    function applyLanguage(value) {
      uiLanguage = String(value || 'en-US');
      document.documentElement.lang = locale();
    }

    function applyTheme(theme) {''')
replace(path, '      const parts = new Intl.DateTimeFormat(undefined, {', '      const parts = new Intl.DateTimeFormat(locale(), {')
replace(path,
'''          : settings.dateFormat === 'eu' ? `${d}/${m}/${y}`
          : `${y}年${Number(m)}月${Number(d)}日`;''',
'''          : settings.dateFormat === 'eu' ? `${d}/${m}/${y}`
          : isChinese()
            ? `${y}年${Number(m)}月${Number(d)}日`
            : new Intl.DateTimeFormat(locale(), {
                timeZone: zoneMap[settings.timeZone],
                year: 'numeric', month: 'long', day: 'numeric'
              }).format(date);''')
replace(path, "      zoneElement.textContent = zoneLabels[settings.timeZone] || '本地时间';", '      zoneElement.textContent = zoneLabel(settings.timeZone);')
replace(path, '      dayPercentElement.textContent = `今日 ${percent.toFixed(1)}%`;', "      dayPercentElement.textContent = `${t('今日', 'Today')} ${percent.toFixed(1)}%`;")
replace(path, '      weekElement.textContent = `第 ${isoWeek(parts.year, parts.month, parts.day)} 周`;', "      weekElement.textContent = isChinese()\n        ? `第 ${isoWeek(parts.year, parts.month, parts.day)} 周`\n        : `Week ${isoWeek(parts.year, parts.month, parts.day)}`;")
replace(path,
'''      if (message.type === 'initialize') {
        visible = message.visible !== false;''',
'''      if (message.type === 'initialize') {
        applyLanguage(message.uiLanguage);
        visible = message.visible !== false;''')

# Official Web Clock mini.
path = 'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/web/mini.html'
replace(path, '<html lang="zh-CN">', '<html lang="en">')
replace(path,
'''    const zoneLabels = Object.freeze({
      local: '本地时间', utc: 'UTC', beijing: '北京时间', tokyo: '东京时间',
      london: '伦敦时间', newYork: '纽约时间', losAngeles: '洛杉矶时间'
    });
    let settings = { ...defaults };''',
'''    const zoneLabels = Object.freeze({
      local: ['本地时间', 'Local time'], utc: ['UTC', 'UTC'],
      beijing: ['北京时间', 'Beijing time'], tokyo: ['东京时间', 'Tokyo time'],
      london: ['伦敦时间', 'London time'], newYork: ['纽约时间', 'New York time'],
      losAngeles: ['洛杉矶时间', 'Los Angeles time']
    });
    let uiLanguage = 'en-US';
    const isChinese = () => /^zh(?:-|$)/i.test(uiLanguage);
    const locale = () => isChinese() ? 'zh-CN' : 'en-US';
    const zoneLabel = value => (zoneLabels[value] || zoneLabels.local)[isChinese() ? 0 : 1];
    let settings = { ...defaults };''')
replace(path, '      const parts = Object.fromEntries(new Intl.DateTimeFormat(undefined, {', '      const parts = Object.fromEntries(new Intl.DateTimeFormat(locale(), {')
replace(path, "      $('zone').textContent = zoneLabels[settings.timeZone] || '本地时间';", "      $('zone').textContent = zoneLabel(settings.timeZone);")
replace(path,
'''      if (message.type === 'initialize') {
        settings = { ...defaults, ...(message.settings || {}) };''',
'''      if (message.type === 'initialize') {
        uiLanguage = String(message.uiLanguage || 'en-US');
        document.documentElement.lang = locale();
        settings = { ...defaults, ...(message.settings || {}) };''')

# Official Web Clock Runtime.
path = 'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/web/runtime.html'
replace(path, '<html lang="zh-CN">', '<html lang="en">')
replace(path,
'''  const zoneLabels = Object.freeze({
    local: '本地时间',
    utc: 'UTC',
    beijing: '北京时间',
    tokyo: '东京时间',
    london: '伦敦时间',
    newYork: '纽约时间',
    losAngeles: '洛杉矶时间'
  });

  let settings = { ...defaults };''',
'''  const zoneLabels = Object.freeze({
    local: ['本地时间', 'Local time'], utc: ['UTC', 'UTC'],
    beijing: ['北京时间', 'Beijing time'], tokyo: ['东京时间', 'Tokyo time'],
    london: ['伦敦时间', 'London time'], newYork: ['纽约时间', 'New York time'],
    losAngeles: ['洛杉矶时间', 'Los Angeles time']
  });
  let uiLanguage = 'en-US';
  const isChinese = () => /^zh(?:-|$)/i.test(uiLanguage);
  const locale = () => isChinese() ? 'zh-CN' : 'en-US';
  const zoneLabel = value => (zoneLabels[value] || zoneLabels.local)[isChinese() ? 0 : 1];

  let settings = { ...defaults };''')
replace(path, '    const parts = new Intl.DateTimeFormat(undefined, {', '    const parts = new Intl.DateTimeFormat(locale(), {')
replace(path,
'''      : settings.dateFormat === 'eu' ? `${d}/${m}/${y}`
      : `${y}年${Number(m)}月${Number(d)}日`;''',
'''      : settings.dateFormat === 'eu' ? `${d}/${m}/${y}`
      : isChinese() ? `${y}年${Number(m)}月${Number(d)}日` : `${m}/${d}/${y}`;''')
replace(path, "      return `${zoneLabels[settings.timeZone] || '本地时间'} · ${timeText}`;", '      return `${zoneLabel(settings.timeZone)} · ${timeText}`;')
replace(path, "    if (settings.titleMode === 'fixed') return '时钟';", "    if (settings.titleMode === 'fixed') return isChinese() ? '时钟' : 'Clock';")
replace(path,
'''    if (message.type === 'initialize') {
      applySettings(message.settings);''',
'''    if (message.type === 'initialize') {
      uiLanguage = String(message.uiLanguage || 'en-US');
      document.documentElement.lang = locale();
      applySettings(message.settings);''')

# Protocol 2.1 Web Body.
path = 'plugin-samples/PaperTodo.Plugin.Protocol21Web/web/index.html'
replace(path, '<html lang="zh-CN">', '<html lang="en">')
replace(path, '<title>Protocol 2.1 示例</title>', '<title>Protocol 2.1 Sample</title>')
replace(path,
'''  <p>这个 Paper 只负责让 provider Runtime 保持存在。</p>
  <p>Runtime 会向当前 Todo 发布 <code>SVG + 文字</code> 的行内/右键动作，并向任意 Workspace Paper 发布静态顶栏标签。</p>
  <p>可用任意 Paper 顶栏的“刷新 2.1”动作重新扫描当前 Workspace。</p>
  <script>
    papertodo.paper.setHeaderText('Protocol 2.1');''',
'''  <p id="keepalive">This paper only keeps the provider Runtime alive.</p>
  <p id="runtime-info">Runtime publishes host-native <code>SVG + text</code> inline/context-menu actions to todos and static top-bar labels to Workspace papers.</p>
  <p id="refresh-info">Use “Refresh 2.1” from any paper top bar to rescan the current Workspace.</p>
  <script>
    let uiLanguage = 'en-US';
    const isChinese = () => /^zh(?:-|$)/i.test(uiLanguage);
    const t = (zh, en) => isChinese() ? zh : en;
    function applyLanguage(value) {
      uiLanguage = String(value || 'en-US');
      document.documentElement.lang = isChinese() ? 'zh-CN' : 'en';
      document.title = t('Protocol 2.1 示例', 'Protocol 2.1 Sample');
      document.querySelector('#keepalive').textContent = t(
        '这个 Paper 只负责让 provider Runtime 保持存在。',
        'This paper only keeps the provider Runtime alive.');
      document.querySelector('#runtime-info').innerHTML = t(
        'Runtime 会向当前 Todo 发布 <code>SVG + 文字</code> 的行内/右键动作，并向任意 Workspace Paper 发布静态顶栏标签。',
        'Runtime publishes host-native <code>SVG + text</code> inline/context-menu actions to todos and static top-bar labels to Workspace papers.');
      document.querySelector('#refresh-info').textContent = t(
        '可用任意 Paper 顶栏的“刷新 2.1”动作重新扫描当前 Workspace。',
        'Use “Refresh 2.1” from any paper top bar to rescan the current Workspace.');
    }
    window.addEventListener('papertodo', event => {
      const message = event.detail || {};
      if (message.type === 'initialize') applyLanguage(message.uiLanguage);
    });
    papertodo.paper.setHeaderText('Protocol 2.1');''')

# Protocol 2.1 Web Runtime.
path = 'plugin-samples/PaperTodo.Plugin.Protocol21Web/web/runtime.html'
replace(path, '<html lang="zh-CN">', '<html lang="en">')
replace(path,
'''  const todoKeys = new Set();
  const labeledPapers = new Set();''',
'''  const todoKeys = new Set();
  const labeledPapers = new Set();
  let uiLanguage = 'en-US';
  const isChinese = () => /^zh(?:-|$)/i.test(uiLanguage);
  const t = (zh, en) => isChinese() ? zh : en;''')
replace(path, "          text: '绑定',\n          toolTip: '查看插件收到的当前 Todo 与绑定信息',", "          text: t('绑定', 'Binding'),\n          toolTip: t('查看插件收到的当前 Todo 与绑定信息', 'Inspect the Todo and binding information received by the plugin'),")
replace(path, "          toolTip: 'Protocol 2.1 Runtime 发布的宿主原生静态标签',", "          toolTip: t('Protocol 2.1 Runtime 发布的宿主原生静态标签', 'Host-native static label published by Protocol 2.1 Runtime'),")
replace(path, "        toolTip: '重新发布 Protocol 2.1 Todo 动作与顶栏标签',", "        toolTip: t('重新发布 Protocol 2.1 Todo 动作与顶栏标签', 'Republish Protocol 2.1 todo actions and top-bar labels'),")
replace(path,
'''    if (message.type !== 'initialize') return;

    registerGlobalTopBar()''',
'''    if (message.type !== 'initialize') return;
    uiLanguage = String(message.uiLanguage || 'en-US');
    document.documentElement.lang = isChinese() ? 'zh-CN' : 'en';

    registerGlobalTopBar()''')

# Top Bar sample body: manifest already localizes four supported host languages; self-rendered UI uses zh + English fallback.
path = 'plugin-samples/PaperTodo.Plugin.TopBarWeb/web/index.html'
replace(path, '<html lang="zh-CN">', '<html lang="en">')
replace(path, '<p id="status">等待宿主初始化…</p>', '<p id="status">Waiting for host initialization…</p>')
replace(path, '<button id="toggle-collapsed">折叠 / 展开自身纸片</button>', '<button id="toggle-collapsed">Collapse / expand this paper</button>')
replace(path, '<button id="hide-paper">隐藏自身纸片</button>', '<button id="hide-paper">Hide this paper</button>')
replace(path,
'''    const status = document.querySelector('#status');

    async function registerPaperTopBar() {''',
'''    const status = document.querySelector('#status');
    const toggleButton = document.querySelector('#toggle-collapsed');
    const hideButton = document.querySelector('#hide-paper');
    let uiLanguage = 'en-US';
    const isChinese = () => /^zh(?:-|$)/i.test(uiLanguage);
    const t = (zh, en) => isChinese() ? zh : en;
    function applyLanguage(value) {
      uiLanguage = String(value || 'en-US');
      document.documentElement.lang = isChinese() ? 'zh-CN' : 'en';
      toggleButton.textContent = t('折叠 / 展开自身纸片', 'Collapse / expand this paper');
      hideButton.textContent = t('隐藏自身纸片', 'Hide this paper');
    }

    async function registerPaperTopBar() {''')
replace(path, "            toolTip: '显示这张插件纸片的信息'", "            toolTip: t('显示这张插件纸片的信息', 'Show information about this plugin paper')")
replace(path, '        status.textContent = `切换失败：${error?.message || error}`;', "        status.textContent = `${t('切换失败：', 'Toggle failed: ')}${error?.message || error}`;")
replace(path, '        status.textContent = `隐藏失败：${error?.message || error}`;', "        status.textContent = `${t('隐藏失败：', 'Hide failed: ')}${error?.message || error}`;")
replace(path,
'''      if (message.type !== 'initialize') return;


      registerPaperTopBar()''',
'''      if (message.type !== 'initialize') return;
      applyLanguage(message.uiLanguage);

      registerPaperTopBar()''')
replace(path, "          status.textContent = 'Paper 顶栏已注册；隐藏后可用插件设置中的快捷键重新显示。';", "          status.textContent = t(\n            'Paper 顶栏已注册；隐藏后可用插件设置中的快捷键重新显示。',\n            'Paper top bar registered; use the plugin shortcut to show the paper again after hiding it.');")
replace(path, '        .catch(error => { status.textContent = `注册失败：${error?.message || error}`; });', "        .catch(error => { status.textContent = `${t('注册失败：', 'Registration failed: ')}${error?.message || error}`; });")

# Top Bar sample runtime.
path = 'plugin-samples/PaperTodo.Plugin.TopBarWeb/web/runtime.html'
replace(path, '<html lang="zh-CN">', '<html lang="en">')
replace(path,
'''  const paperIds = new Set();

  async function registerGlobalTopBar() {''',
'''  const paperIds = new Set();
  let uiLanguage = 'en-US';
  const isChinese = () => /^zh(?:-|$)/i.test(uiLanguage);
  const t = (zh, en) => isChinese() ? zh : en;

  async function registerGlobalTopBar() {''')
replace(path, "        toolTip: '读取当前纸片信息'", "        toolTip: t('读取当前纸片信息', 'Inspect the current paper')")
replace(path, "      toolTip: 'Protocol 2.1 Top Bar 示例',", "      toolTip: t('Protocol 2.1 Top Bar 示例', 'Protocol 2.1 Top Bar sample'),")
replace(path,
'''    if (message.type !== 'initialize') return;
    replacePapers(message.papers);''',
'''    if (message.type !== 'initialize') return;
    uiLanguage = String(message.uiLanguage || 'en-US');
    document.documentElement.lang = isChinese() ? 'zh-CN' : 'en';
    replacePapers(message.papers);''')
