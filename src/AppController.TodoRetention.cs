using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    // Controller owns scheduling/removal of active rows. TodoRules owns date policy;
    // existing plugins keep ownership of their own completion history.
    private DispatcherTimer? _todoRetentionTimer;
    private bool _removingCompletedTodos;

    private void RefreshTodoRetentionSchedule()
    {
        _todoRetentionTimer?.Stop();
        if (IsExiting || TodoRules.RemovalMode(State) is not (TodoRules.RemoveNextDay or TodoRules.RemoveNextWeek)) return;
        _todoRetentionTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _todoRetentionTimer.Tick -= OnTodoRetentionTick;
        _todoRetentionTimer.Tick += OnTodoRetentionTick;
        _todoRetentionTimer.Start();
    }

    private void OnTodoRetentionTick(object? sender, EventArgs e) =>
        RemoveDueCompletedTodos(DateTimeOffset.Now, PaperOperationContext.User());

    private void RequestTodoRetentionCheck()
    {
        if (IsExiting) return;
        _ = Application.Current.Dispatcher.BeginInvoke((Action)(() =>
        {
            RemoveDueCompletedTodos(DateTimeOffset.Now, PaperOperationContext.User());
            RefreshTodoRetentionSchedule();
        }), DispatcherPriority.Background);
    }

    internal void RemoveDueCompletedTodos(DateTimeOffset now, PaperOperationContext context)
    {
        if (IsExiting || _removingCompletedTodos) return;
        var mode = TodoRules.RemovalMode(State);
        if (mode is not (TodoRules.RemoveNextDay or TodoRules.RemoveNextWeek)) return;
        _removingCompletedTodos = true;
        try
        {
            var due = State.Papers.Where(p => p.Type == PaperTypes.Todo)
                .Select(p => (Paper: p, Ids: p.Items.Where(i => TodoRules.IsRemovalDue(i, mode, now))
                    .Select(i => i.Id).ToArray()))
                .Where(p => p.Ids.Length > 0).ToArray();
            if (due.Length == 0) return;
            // Deliver pending completion events through the existing plugin event boundary.
            PrepareExternalPaperOperation();

            var snapshots = due.Select(p => (p.Paper, Items: p.Paper.Items.ToList(),
                Orders: p.Paper.Items.Select(i => i.Order).ToArray())).ToArray();
            using (SuppressPaperPluginEventScans())
            {
                foreach (var entry in due)
                    TodoRules.ApplyCompletionPolicy(entry.Paper.Items, entry.Ids, true, true, false);
                if (!TryCommitExternalMutation())
                {
                    foreach (var old in snapshots)
                    {
                        old.Paper.Items = old.Items;
                        for (var i = 0; i < old.Items.Count; i++) old.Items[i].Order = old.Orders[i];
                    }
                    ResetPaperPluginEventBaseline();
                    return;
                }
                foreach (var entry in due)
                    RunExternalPostCommitUi(() => RefreshExternalTodoPaper(entry.Paper));
                RunExternalPostCommitUi(() => RefreshCapsuleEligibilityForLinkedPapers(
                    snapshots.SelectMany(old => old.Items).Select(item => item.LinkedPaperId)));
            }
            // Publish deletion only after the core save succeeds.
            PublishExternalPaperOperation(context);
            NotifyTodoReminderCollectionChanged();
        }
        catch (Exception ex) { HandleSaveFailure(ex); }
        finally { _removingCompletedTodos = false; }
    }
}
