from pathlib import Path
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


# ---------------------------------------------------------------------------
# One presentation writer when a provider Runtime exists, for Native and Web alike.
# Body/Mini still own their visible UI and per-Paper frontend state.
# ---------------------------------------------------------------------------
path = 'src/PaperWindow.PluginBodies.cs'
replace(path,
'''        var controls = _bodyControls ??= new PaperBodyControls(this);
        var theme = CurrentPaperBodyTheme();
        Action<string> setTitle = title => InvokePluginContext(
            generation,
            providerId,
            () => _controller.UpdatePaperTitleFromPlugin(
                _paper,
                title,
                providerId));
        Action<string> setHeaderText = text => InvokePluginContext(
            generation,
            providerId,
            () => SetPluginHeaderText(text));
        Action<PaperCapsulePresentation?> setCapsulePresentation = presentation => InvokePluginContext(
            generation,
            providerId,
            () => SetPluginCapsulePresentation(presentation));''',
'''        var controls = _bodyControls ??= new PaperBodyControls(this);
        var theme = CurrentPaperBodyTheme();
        var runtimeOwnsPresentation =
            descriptor.Manifest?.Capabilities.Contains(
                "appRuntime",
                StringComparer.Ordinal) == true;
        Action<string> setTitle = runtimeOwnsPresentation
            ? _ => { }
            : title => InvokePluginContext(
                generation,
                providerId,
                () => _controller.UpdatePaperTitleFromPlugin(
                    _paper,
                    title,
                    providerId));
        Action<string> setHeaderText = runtimeOwnsPresentation
            ? _ => { }
            : text => InvokePluginContext(
                generation,
                providerId,
                () => SetPluginHeaderText(text));
        Action<PaperCapsulePresentation?> setCapsulePresentation = runtimeOwnsPresentation
            ? _ => { }
            : presentation => InvokePluginContext(
                generation,
                providerId,
                () => SetPluginCapsulePresentation(presentation));''')

# ---------------------------------------------------------------------------
# Native ReviewArchive: provider Runtime owns global recording + all Paper presentation.
# Body becomes a pure frontend/view-state session.
# ---------------------------------------------------------------------------
review_plugin = r'''using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ReviewArchive;

public sealed class ReviewArchivePlugin : IPaperBodyPlugin, IPaperAppRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) =>
        new ReviewArchiveSession(context);

    public IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context) =>
        new Runtime(context);

    private sealed class Runtime : IPaperAppRuntime
    {
        private readonly PaperAppRuntimeContext _context;
        private readonly IDisposable _workspaceSubscription;
        private readonly IDisposable _settingsSubscription;
        private readonly IDisposable _papersSubscription;
        private bool _disposed;

        public Runtime(PaperAppRuntimeContext context)
        {
            _context = context;
            ReviewArchiveStore.EnsureLoaded();
            var settings = CurrentSettings();
            _ = ReviewArchiveStore.ImportCurrent(
                context.Workspace,
                settings,
                manual: false);
            ReviewArchiveStore.ApplyRetention(settings);

            ReviewArchiveStore.Changed += OnArchiveChanged;
            _settingsSubscription = context.Settings.Subscribe(OnSettingsChanged);
            _papersSubscription = context.Papers.Subscribe(OnPaperEvent);
            _workspaceSubscription = context.Workspace.Subscribe(
                new PaperTodoEventFilter
                {
                    Kinds = new HashSet<PaperTodoEventKind>
                    {
                        PaperTodoEventKind.PaperChanged,
                        PaperTodoEventKind.PaperDeleted,
                        PaperTodoEventKind.TodoCreated,
                        PaperTodoEventKind.TodoChanged,
                        PaperTodoEventKind.TodoDeleted
                    }
                },
                value => ReviewArchiveStore.Apply(value, CurrentSettings()));

            PublishPresentation();
        }

        private ReviewArchiveSettings CurrentSettings() =>
            ReviewArchiveSettingsReader.ReadSettings(_context.Settings.Json);

        private void OnArchiveChanged()
        {
            if (!_disposed)
            {
                PublishPresentation();
            }
        }

        private void OnSettingsChanged(string json)
        {
            if (_disposed)
            {
                return;
            }
            var settings = ReviewArchiveSettingsReader.ReadSettings(json);
            ReviewArchiveStore.ApplyRetention(settings);
            PublishPresentation(settings);
        }

        private void OnPaperEvent(PaperPluginRuntimeEvent value)
        {
            if (!_disposed && value.Kind == PaperPluginRuntimeEventKind.PaperAdded)
            {
                PublishPresentation(CurrentSettings(), value.PaperId);
            }
        }

        private void PublishPresentation(
            ReviewArchiveSettings? settings = null,
            string? onlyPaperId = null)
        {
            if (_disposed)
            {
                return;
            }

            settings ??= CurrentSettings();
            var all = ReviewArchiveStore.Snapshot();
            var now = DateTimeOffset.Now;
            var completionEvents = all
                .SelectMany(item => item.Events.Where(value => value.Kind == "completed"))
                .ToArray();
            var completedRecords = all.Count(item =>
                item.Events.Any(value => value.Kind == "completed"));
            var todayCount = completionEvents.Count(value =>
                value.At.ToLocalTime().Date == now.Date);
            var openCount = all.Count(item => !item.Done && !item.SourceDeleted);
            var streak = CompletionStreakDays(completionEvents);

            var title = settings.TitleMode switch
            {
                "today" => $"今日完成 {todayCount}",
                "streak" => streak > 0 ? $"连续 {streak} 天" : "等待今日完成",
                "open" => $"进行中 {openCount}",
                "fixed" => string.IsNullOrWhiteSpace(settings.FixedTitle)
                    ? "复盘记录"
                    : settings.FixedTitle,
                _ => $"复盘 · {completedRecords} 项"
            };

            var presentation = new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = title,
                ToolTip = $"{title} · 进行中 {openCount}",
                Components = settings.ShowInsights
                    ? new PaperCapsuleComponent[]
                    {
                        new()
                        {
                            Kind = PaperCapsuleComponentKind.Text,
                            Text = title,
                            Fill = true
                        },
                        new()
                        {
                            Kind = PaperCapsuleComponentKind.Text,
                            Text = $"{openCount} 未完",
                            Tone = PaperCapsuleTone.Muted
                        }
                    }
                    : new PaperCapsuleComponent[]
                    {
                        new()
                        {
                            Kind = PaperCapsuleComponentKind.Text,
                            Text = title,
                            Fill = true
                        }
                    }
            };

            var paperIds = onlyPaperId == null
                ? _context.Papers.List().Select(value => value.PaperId)
                : new[] { onlyPaperId };
            foreach (var paperId in paperIds)
            {
                _context.Papers.SetHeaderText(paperId, title);
                _context.Papers.SetCapsulePresentation(paperId, presentation);
            }
        }

        private static int CompletionStreakDays(
            IEnumerable<ReviewArchiveEvent> completionEvents)
        {
            var days = completionEvents
                .Select(value => value.At.ToLocalTime().Date)
                .ToHashSet();
            if (days.Count == 0)
            {
                return 0;
            }

            var cursor = DateTime.Now.Date;
            if (!days.Contains(cursor))
            {
                cursor = cursor.AddDays(-1);
            }

            var streak = 0;
            while (days.Contains(cursor))
            {
                streak++;
                cursor = cursor.AddDays(-1);
            }
            return streak;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ReviewArchiveStore.Changed -= OnArchiveChanged;
            _workspaceSubscription.Dispose();
            _settingsSubscription.Dispose();
            _papersSubscription.Dispose();
            ReviewArchiveStore.Flush();
        }
    }
}
'''
write('plugin-samples/PaperTodo.Plugin.ReviewArchive/ReviewArchivePlugin.cs', review_plugin)

session = 'plugin-samples/PaperTodo.Plugin.ReviewArchive/ReviewArchiveSession.cs'
text = read(session)
text = text.replace('    private string _lastDisplayTitle = "";\n', '')
text = text.replace('    private string _lastCapsuleSignature = "";\n', '')
# Remove the Body-owned presentation calculation/call after its UI metrics are already calculated.
text = re.sub(
    r'''\n        var title = _settings\.TitleMode switch\n        \{.*?        SetPaperStatus\(title, openCount\);''',
    '',
    text,
    count=1,
    flags=re.S)
# Remove SetPaperStatus entirely.
text, removed = re.subn(
    r'''\n    private void SetPaperStatus\(string title, int openCount\)\n    \{.*?\n    \}\n\n    private void SaveViewState''',
    '\n    private void SaveViewState',
    text,
    count=1,
    flags=re.S)
if removed != 1:
    raise SystemExit('ReviewArchiveSession: SetPaperStatus block not found')
write(session, text)

# ---------------------------------------------------------------------------
# Web TopBar sample: Body demonstrates Paper-scoped UI/actions only; Runtime publishes persistent
# header/capsule to every logical Paper and owns Global Top Bar.
# ---------------------------------------------------------------------------
topbar_index = 'plugin-samples/PaperTodo.Plugin.TopBarWeb/web/index.html'
text = read(topbar_index)
text, removed = re.subn(
    r'''\n      papertodo\.paper\.setHeaderText\('Top Bar 2\.0'\);\n      papertodo\.paper\.setCapsulePresentation\(\{.*?\n      \}\);\n''',
    '\n',
    text,
    count=1,
    flags=re.S)
if removed != 1:
    raise SystemExit('TopBarWeb index: Body presentation block not found')
write(topbar_index, text)

topbar_runtime = r'''<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <title>PaperTodo Top Bar Runtime</title>
</head>
<body>
<script>
  const paperIds = new Set();

  async function registerGlobalTopBar() {
    await papertodo.globalTopBar.setActions([
      {
        id: 'inspect-current',
        icon: {
          kind: 'svgPath',
          value: 'M3,3 L13,3 13,13 3,13 Z M6,8 L10,8',
          renderMode: 'stroke',
          strokeWidth: 1.5
        },
        toolTip: '读取当前纸片信息'
      }
    ]);
  }

  function publishPaper(paperId) {
    if (!paperId) return;
    void papertodo.papers.setHeaderText(paperId, 'Top Bar 2.0');
    void papertodo.papers.setCapsulePresentation(paperId, {
      preferredWidth: 0,
      plainText: 'Top Bar 2.0',
      toolTip: 'Protocol 2.0 Top Bar 示例',
      components: [
        { kind: 'glyph', text: 'T' },
        { kind: 'text', text: 'Top Bar 2.0', fill: true }
      ]
    });
  }

  function replacePapers(values) {
    paperIds.clear();
    for (const value of Array.isArray(values) ? values : []) {
      const paperId = String(value?.paperId || '').trim();
      if (!paperId) continue;
      paperIds.add(paperId);
      publishPaper(paperId);
    }
  }

  papertodo.onEvent(async message => {
    if (message.type === 'shortcutInvoked' &&
        message.actionId === 'runtime.ping') {
      console.log('PaperTodo custom shortcut', message.settingId, message.actionId);
      return;
    }

    if (message.type === 'paperEvent') {
      const item = message.event || {};
      const paperId = String(item.paperId || '').trim();
      if (item.kind === 'paperAdded' && paperId) {
        paperIds.add(paperId);
        publishPaper(paperId);
      } else if (item.kind === 'paperRemoved' && paperId) {
        paperIds.delete(paperId);
      }
      return;
    }

    if (message.type !== 'topBarActionInvoked' ||
        message.action?.actionId !== 'inspect-current') {
      return;
    }

    try {
      const paper = await papertodo.workspace.request('papers.get', {
        paperId: message.action.targetPaperId
      });
      console.log('PaperTodo Global Top Bar target', paper || message.action);
    } catch (error) {
      console.error('PaperTodo Global Top Bar workspace read failed', error);
    }
  });

  window.addEventListener('papertodo', event => {
    const message = event.detail || {};
    if (message.type !== 'initialize') return;
    replacePapers(message.papers);
    registerGlobalTopBar().catch(error => {
      console.error('PaperTodo Global Top Bar registration failed', error);
    });
  });
</script>
</body>
</html>
'''
write('plugin-samples/PaperTodo.Plugin.TopBarWeb/web/runtime.html', topbar_runtime)

print('phase3 transformations complete')
