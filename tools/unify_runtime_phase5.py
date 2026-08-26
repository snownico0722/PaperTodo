from pathlib import Path
import json
import re


def read(path):
    return Path(path).read_text(encoding='utf-8')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8')


def replace(path, old, new, count=1):
    text = read(path)
    actual = text.count(old)
    if actual != count:
        raise SystemExit(f'{path}: expected {count}, found {actual}: {old[:120]!r}')
    write(path, text.replace(old, new, count))


# Public contract: Body is a frontend. There is no host-level "keep this Body alive as a backend"
# requirement anymore; guaranteed background work belongs to the one provider Runtime.
path = 'PaperTodo.Plugin.Abstractions/PaperBodyPluginContracts.cs'
text = read(path)
text, n = re.subn(
    r'''\n\[Flags\]\npublic enum PaperBodyRuntimeRequirements\n\{\n    None = 0,\n    BackgroundUpdates = 1 << 0\n\}\n''',
    '\n',
    text,
    count=1)
if n != 1:
    raise SystemExit('PaperBodyRuntimeRequirements enum not found')
write(path, text)

# Registry: delete requires/RuntimeRequirements from the canonical descriptor and manifest.
path = 'src/PaperBodyPluginRegistry.cs'
text = read(path)
text = text.replace('    PaperBodyRuntimeRequirements RuntimeRequirements,\n', '')
text = text.replace('    public string[] Requires { get; set; } = [];\n', '')
text = text.replace('            PaperBodyRuntimeRequirements.None,\n', '')
text = text.replace('            ParseRuntimeRequirements(manifest.Requires),\n', '')
text, n = re.subn(
    r'''\n        var requiresBackgroundUpdates =\n            \(ParseRuntimeRequirements\(manifest\.Requires\) &\n             PaperBodyRuntimeRequirements\.BackgroundUpdates\) != 0;\n        if \(kind == PaperBodyPluginKind\.Web && requiresBackgroundUpdates\)\n        \{\n            throw new InvalidDataException\(\n                "Web plugins use the single provider appRuntime/runtime backend; " \+\n                "requires: backgroundUpdates is not supported\."\);\n        \}\n''',
    '\n',
    text,
    count=1)
if n != 1:
    raise SystemExit('registry backgroundUpdates validation block not found')
text, n = re.subn(
    r'''\n    private static PaperBodyRuntimeRequirements ParseRuntimeRequirements\(\n        IEnumerable<string>\? values\)\n    \{.*?\n    \}\n''',
    '\n',
    text,
    count=1,
    flags=re.S)
if n != 1:
    raise SystemExit('ParseRuntimeRequirements method not found')
write(path, text)

# Window: visible frontend lifecycle is now literal; no Native-specific hidden background mode.
path = 'src/PaperWindow.PluginBodies.cs'
text = read(path)
text, n = re.subn(
    r'''\n    private bool BodyRequires\(PaperBodyRuntimeRequirements requirement\) =>\n        _bodyDescriptor != null &&\n        \(_bodyDescriptor\.RuntimeRequirements & requirement\) == requirement;\n''',
    '\n',
    text,
    count=1)
if n != 1:
    raise SystemExit('BodyRequires helper not found')
replace(path,
'''        var keepNativeBodyRuntimeAlive =
            _bodyDescriptor?.Kind == PaperBodyPluginKind.Native &&
            BodyRequires(PaperBodyRuntimeRequirements.BackgroundUpdates);
        var runtimeVisible = _paper.IsVisible &&
            (visible || keepNativeBodyRuntimeAlive);''',
'''        var runtimeVisible = _paper.IsVisible && visible;''')

# All sample/runtime manifests stop declaring the removed lifecycle feature.
for root in (Path('plugin-samples'), Path('plugins')):
    for manifest in root.rglob('plugin.json'):
        data = json.loads(manifest.read_text(encoding='utf-8'))
        if 'requires' in data:
            data.pop('requires', None)
            manifest.write_text(
                json.dumps(data, ensure_ascii=False, indent=2) + '\n',
                encoding='utf-8')

# Native clock keeps its own UI timer alive while its session exists. If it ever needs guaranteed
# work with no Body session at all, it should declare provider Runtime instead of asking the host to
# reinterpret Body visibility.
path = 'plugin-samples/PaperTodo.Plugin.SampleClock/SampleClockPlugin.cs'
text = read(path)
text = text.replace('        private bool _runtimeVisible;\n', '')
text = text.replace(
'''            if (_runtimeVisible && !_timer.IsEnabled)
            {
                _timer.Start();
            }''',
'''            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }''')
text = text.replace(
'''        public void OnVisibilityChanged(bool visible)
        {
            _runtimeVisible = visible;
            if (visible)
            {
                if (!_timer.IsEnabled) _timer.Start();
                Refresh();
            }
            else
            {
                _timer.Stop();
            }
        }''',
'''        public void OnVisibilityChanged(bool visible)
        {
            if (!_timer.IsEnabled) _timer.Start();
            if (visible) Refresh();
        }''')
write(path, text)

# FocusTimer likewise owns its in-session scheduler. Collapsing the UI no longer needs a host
# background-lifetime flag; the timer continues until the session itself is disposed.
path = 'plugin-samples/PaperTodo.Plugin.FocusTimer/FocusTimerPlugin.cs'
text = read(path)
text = text.replace('        private bool _runtimeVisible;\n', '')
text = text.replace(
'''        private void StartTimer()
        {
            if (_runtimeVisible && _state.IsRunning && !_timer.IsEnabled)
            {
                _timer.Start();
            }
        }''',
'''        private void StartTimer()
        {
            if (_state.IsRunning && !_timer.IsEnabled)
            {
                _timer.Start();
            }
        }''')
text = text.replace(
'''        public void OnVisibilityChanged(bool visible)
        {
            _runtimeVisible = visible;
            if (!visible)
            {
                _timer.Stop();
                _hostRefreshTimer.Stop();
                return;
            }

            if (CompleteExpiredPhase())
            {
                SaveState();
            }
            RefreshTodoOptions(saveIfMissing: true);
            StartTimer();
            UpdateView();
        }''',
'''        public void OnVisibilityChanged(bool visible)
        {
            if (!visible)
            {
                _hostRefreshTimer.Stop();
                StartTimer();
                return;
            }

            if (CompleteExpiredPhase())
            {
                SaveState();
            }
            RefreshTodoOptions(saveIfMissing: true);
            StartTimer();
            UpdateView();
        }''')
# A restored running timer should resume as soon as the session exists, not wait for the host to
# call the old special visibility path.
text = text.replace(
'''                if (stateChanged)
                {
                    SaveState();
                }
            }
        }

        public FrameworkElement View => _root;''',
'''                if (stateChanged)
                {
                    SaveState();
                }
            }
            StartTimer();
        }

        public FrameworkElement View => _root;''')
write(path, text)

# Policy check: the deleted host feature must stay deleted.
path = 'tests/PaperTodo.ProtocolPolicyChecks/Program.cs'
text = read(path)
needle = '''        Assert(manifest.GetProperty("PaperRuntime") == null &&
               manifest.GetProperty("PaperRuntimePath") == null,
            "paperRuntime manifest fields must not return.");'''
replacement = needle + '''
        Assert(manifest.GetProperty("Requires") == null,
            "Body requires/backgroundUpdates must not return as a host lifecycle mode.");
        Assert(abstractions.GetType("PaperTodo.Plugin.PaperBodyRuntimeRequirements", throwOnError: false) == null,
            "PaperBodyRuntimeRequirements must stay deleted; guaranteed background work belongs to Runtime.");'''
if needle not in text:
    raise SystemExit('policy unified runtime insertion point not found')
text = text.replace(needle, replacement, 1)
write(path, text)

print('phase5 background lifetime cleanup complete')
