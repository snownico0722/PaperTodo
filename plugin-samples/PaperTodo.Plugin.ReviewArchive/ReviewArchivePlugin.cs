using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ReviewArchive;

public sealed class ReviewArchivePlugin : IPaperBodyPlugin, IPaperAppRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) =>
        new ReviewArchiveSession(context);

    public IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context) =>
        new Runtime(context);

    private sealed class Runtime : IPaperAppRuntime
    {
        private readonly IDisposable _subscription;

        public Runtime(PaperAppRuntimeContext context)
        {
            ReviewArchiveStore.EnsureLoaded();
            var settings = ReviewArchiveSettingsReader.ReadSettings(context.Settings.Json);
            _ = ReviewArchiveStore.ImportCurrent(
                context.Workspace,
                settings,
                manual: false);
            ReviewArchiveStore.ApplyRetention(settings);

            _subscription = context.Workspace.Subscribe(
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
                value =>
                {
                    var current = ReviewArchiveSettingsReader.ReadSettings(
                        context.Settings.Json);
                    ReviewArchiveStore.Apply(value, current);
                });
        }

        public void Dispose()
        {
            _subscription.Dispose();
            ReviewArchiveStore.Flush();
        }
    }
}
