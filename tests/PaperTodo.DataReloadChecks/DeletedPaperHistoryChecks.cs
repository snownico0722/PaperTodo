using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void CheckEmptyPaperDeletionConfirmation(AppController controller)
    {
        var windows = Field<Dictionary<string, PaperWindow>>(controller, "_windows");
        foreach (var type in new[] { PaperTypes.Note, PaperTypes.Todo })
        {
            var paper = controller.CreatePaper(type)!;
            var window = windows[paper.Id];
            window.EnsureShellBuilt();
            var menu = Field<FrameworkElement>(window, "_paperChrome").ContextMenu;
            var delete = menu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, Strings.Get("MenuDelete")));

            void Choose(string action)
            {
                var response = window.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>()
                        .Single(w => w.IsVisible && w.Title == Strings.Get("DeletePaperTitle"));
                    try { ClickFailureAction(dialog, action); }
                    catch { dialog.Close(); throw; }
                }, DispatcherPriority.ContextIdle);

                delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (response.Abort())
                    throw new InvalidOperationException($"Deleting an empty {type} paper must open confirmation.");
                response.Task.GetAwaiter().GetResult();
            }

            Choose("CommonCancel");
            Require(controller.State.Papers.Contains(paper) && !window.IsClosed,
                $"Cancel did not preserve the empty {type} paper.");
            Choose("MenuDelete");
            Require(!controller.State.Papers.Contains(paper) && window.IsClosed,
                $"Confirm did not remove the empty {type} paper.");
        }
        Console.WriteLine("PASS deleting an empty paper requires confirmation");
    }

    private static async Task DeletedPaperHistoryChecks(AppController controller)
    {
        var commands = controller.PaperCommands;
        var origin = PaperOperationContext.Mcp();
        var previousLinks = controller.State.EnableTodoPaperLinks;
        controller.State.EnableTodoPaperLinks = true;
        try
        {
            foreach (var merge in new[] { false, true })
            {
                var targetId = commands.CreatePaper(new CreatePaperRequest
                {
                    Type = PaperTypes.Note, Show = false
                }, origin).PaperId;
                var todoId = commands.CreatePaper(new CreatePaperRequest
                {
                    Type = PaperTypes.Todo, Show = true,
                    Todos = [new TodoCreateItem { Text = "history original", LinkedPaperId = targetId }]
                }, origin).PaperId;
                try
                {
                    var todo = controller.State.Papers.Single(p => p.Id == todoId);
                    var window = Field<Dictionary<string, PaperWindow>>(controller, "_windows")[todoId];
                    window.EnsureShellBuilt();
                    var itemId = todo.Items[0].Id;
                    void History(string operation) => typeof(PaperWindow).GetMethod(operation, Private)!.Invoke(window, null);
                    commands.UpdateTodo(new UpdateTodoRequest
                    {
                        PaperId = todoId, TodoId = itemId, UpdateLinkedPaper = true, LinkedPaperId = null
                    }, origin);
                    commands.UpdateTodo(new UpdateTodoRequest
                    {
                        PaperId = todoId, TodoId = itemId, UpdateLinkedPaper = true, LinkedPaperId = targetId
                    }, origin);
                    commands.UpdateTodo(new UpdateTodoRequest
                    {
                        PaperId = todoId, TodoId = itemId, Text = "history edited"
                    }, origin);
                    controller.SaveNow(sync: true);
                    Require(LivePaper(ReadLive(), todoId)["items"]![0]!["linkedNoteId"]?.GetValue<string>() == targetId,
                        "Deletion fixture did not persist its link to the target paper.");
                    History("Undo");
                    History("Undo");
                    var undo = Field<List<List<PaperItem>>>(window, "_undoStack");
                    var redo = Field<List<List<PaperItem>>>(window, "_redoStack");
                    Require(todo.Items.All(item => item.LinkedPaperId != targetId) &&
                        undo.SelectMany(items => items).Any(item => item.LinkedPaperId == targetId) &&
                        redo.SelectMany(items => items).Any(item => item.LinkedPaperId == targetId),
                        "Deletion fixture must retain links in both histories, but not in the current todo.");
                    var undoCount = undo.Count;
                    var redoCount = redo.Count;
                    controller.SaveNow(sync: true);
                    var external = ReadLive();
                    var baseline = external.ToJsonString();
                    external["papers"]!.AsArray().Remove(LivePaper(external, targetId));
                    WriteLive(external);
                    var result = merge ? commands.MergeData(origin, baseline) : commands.ReloadData(origin);
                    Require(merge ? result.Outcome == "pending" : result.Applied,
                        "External deletion was not accepted.");
                    await Until(() => !controller.IsDataReloading, "deleted-paper history cleanup");
                    Require(commands.GetDataReloadStatus().LastResult?.Applied == true &&
                        !controller.State.Papers.Any(p => p.Id == targetId), "External deletion did not finish.");
                    Require(undo.Count == undoCount && redo.Count == redoCount &&
                        undo.Concat(redo).SelectMany(items => items).All(item => item.LinkedPaperId != targetId),
                        "Accepted deletion must prune both histories without discarding unrelated undo/redo steps.");
                    foreach (var operation in new[] { "Undo", "Redo", "Redo" })
                    {
                        History(operation);
                        Require(todo.Items.All(item => item.LinkedPaperId != targetId),
                            "Undo/redo restored a link to an externally deleted paper.");
                    }
                    controller.SaveNow(sync: true);
                    Require(LivePaper(ReadLive(), todoId)["items"]!.AsArray().All(item =>
                        item!["linkedNoteId"]?.GetValue<string>() != targetId),
                        "Undo/redo persisted an externally deleted paper link.");
                    Console.WriteLine($"PASS {(merge ? "merge" : "reload")} deletion prunes undo and redo links while preserving history");
                }
                finally
                {
                    commands.DeletePaper(todoId, origin);
                    if (controller.State.Papers.Any(p => p.Id == targetId)) commands.DeletePaper(targetId, origin);
                }
            }
        }
        finally { controller.State.EnableTodoPaperLinks = previousLinks; }
    }
}
