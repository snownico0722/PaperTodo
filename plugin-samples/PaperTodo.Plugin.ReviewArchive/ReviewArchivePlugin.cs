using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ReviewArchive;

public sealed class ReviewArchivePlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) =>
        new ReviewArchiveSession(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new Runtime(context);

    private sealed class Runtime : IPaperPluginRuntime
    {
        private readonly PaperPluginRuntimeContext _context;
        private readonly IDisposable _workspaceSubscription;
        private readonly IDisposable _settingsSubscription;
        private readonly IDisposable _papersSubscription;
        private bool _disposed;

        public Runtime(PaperPluginRuntimeContext context)
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
                "today" => PluginText.Format("今日完成 {0}", todayCount),
                "streak" => streak > 0 ? PluginText.Format("连续 {0} 天", streak) : PluginText.T("等待今日完成"),
                "open" => PluginText.Format("进行中 {0}", openCount),
                "fixed" => string.IsNullOrWhiteSpace(settings.FixedTitle)
                    ? PluginText.T("复盘记录")
                    : settings.FixedTitle,
                _ => PluginText.Format("复盘 · {0} 项", completedRecords)
            };

            var presentation = new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = title,
                ToolTip = PluginText.Format("{0} · 进行中 {1}", title, openCount),
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
                            Text = PluginText.Format("{0} 未完", openCount),
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
