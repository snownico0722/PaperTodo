using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static bool IsExportablePaper(PaperData paper) =>
        paper.Type == PaperTypes.Todo ||
        (paper.Type == PaperTypes.Note &&
         string.Equals(paper.BodyProviderId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal));

    private bool _paperExportRunning;
    private int _paperExportCompleted;
    private int _paperExportTotal;

    private enum ExportScope { Current, Notes, Todos, All }

    private async void ExportPapers(ExportScope scope)
    {
        if (_paperExportRunning || !IsExportablePaper(_paper))
            return;

        var all = scope != ExportScope.Current;
        var caption = Strings.Get(scope switch
        {
            ExportScope.Current => _paper.Type == PaperTypes.Todo ? "TodoExportCurrent" : "NoteExportCurrent",
            ExportScope.Notes => "MenuExportAllNotes",
            ExportScope.Todos => "MenuExportAllTodos",
            _ => "NoteExportAll"
        });

        try
        {
            // Capture live text and detached Todo items on the UI thread.
            // Sort and render Todo Markdown only after the worker starts.
            IEnumerable<PaperData> selected = scope switch
            {
                ExportScope.Current => [_paper],
                ExportScope.Notes => _controller.State.Papers.Where(p => p.Type == PaperTypes.Note),
                ExportScope.Todos => _controller.State.Papers.Where(p => p.Type == PaperTypes.Todo),
                _ => _controller.State.Papers
            };
            var snapshots = selected.Where(IsExportablePaper)
                .Select(paper => (
                    paper.Id,
                    Title: _controller.PaperTitleText(paper),
                    Markdown: paper.Type == PaperTypes.Note
                        ? _controller.CurrentMarkdownContentForExternalRead(paper) : null,
                    TodoItems: paper.Type == PaperTypes.Todo
                        ? TodoRules.CloneAll(paper.Items) : null))
                .ToArray();

            if (snapshots.Length == 0)
            {
                MessageBox.Show(this, Strings.Get("NoteExportNoNotes"), caption,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = caption,
                Filter = all
                    ? Strings.Get("NoteExportMarkdownZipType") + " (*.zip)|*.zip|" +
                      Strings.Get("NoteExportHtmlZipType") + " (*.zip)|*.zip"
                    : Strings.Get("NoteExportMarkdownType") + " (*.md)|*.md|" +
                      Strings.Get("NoteExportHtmlType") + " (*.html)|*.html",
                DefaultExt = all ? ".zip" : ".md",
                FilterIndex = 1,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = all ? DateTime.Now.ToString("yyyy-MM-dd") + "_PaperTodo"
                    : NotePortableExport.SafeName(snapshots[0].Title)
            };
            if (dialog.ShowDialog(this) != true) return;

            var format = dialog.FilterIndex == 2 ? NoteExportFormat.Html : NoteExportFormat.Markdown;
            var destination = dialog.FileName;
            var missingLabel = Strings.Get("NoteExportMissingImageText");
            _paperExportRunning = true;
            _paperExportCompleted = 0;
            _paperExportTotal = snapshots.Length;
            var progress = new Progress<int>(value => _paperExportCompleted = value);
            var result = await Task.Run(() =>
            {
                var documents = snapshots.Select(snapshot => snapshot.TodoItems is { } items
                    ? NotePortableExport.FromTodoItems(snapshot.Id, snapshot.Title, items)
                    : new NoteExportDocument(snapshot.Id, snapshot.Title, snapshot.Markdown!))
                    .ToArray();
                return NotePortableExport.Export(
                    destination, documents, format, all, _controller.ImageStore, missingLabel, progress);
            });

            var message = Strings.Format("NoteExportSummary", result.Exported, snapshots.Length,
                result.MissingImages, result.NotesWithMissingImages, destination);
            if (result.FailedNotes.Count > 0)
                message += Environment.NewLine +
                    Strings.Format("NoteExportFailedNotes",
                        string.Join(Environment.NewLine, result.FailedNotes.Take(5)));
            if (!IsClosed)
                MessageBox.Show(this, message, caption, MessageBoxButton.OK,
                    result.MissingImages > 0 || result.FailedNotes.Count > 0
                        ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (!IsClosed)
                MessageBox.Show(this, Strings.Format("NoteExportFailure", ex.Message),
                    caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _paperExportRunning = false;
            _paperExportCompleted = 0;
            _paperExportTotal = 0;
        }
    }
}
