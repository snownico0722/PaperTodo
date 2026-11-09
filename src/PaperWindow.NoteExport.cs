using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static bool IsExportablePaper(PaperData paper) =>
        paper.Type == PaperTypes.Todo ||
        (paper.Type == PaperTypes.Note &&
         string.Equals(paper.BodyProviderId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal));

    private bool _paperExportRunning;

    private enum ExportScope { Current, Notes, Todos, All }

    private async void ExportPapers(ExportScope scope)
    {
        if (_paperExportRunning || _controller.IsDataReloading || !IsExportablePaper(_paper))
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
            var images = _controller.ImageStore;
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
            if (dialog.ShowDialog(this) != true || IsClosed || _controller.IsDataReloading ||
                !ReferenceEquals(images, _controller.ImageStore)) return;

            var format = dialog.FilterIndex == 2 ? NoteExportFormat.Html : NoteExportFormat.Markdown;
            var destination = dialog.FileName;
            var missingLabel = Strings.Get("NoteExportMissingImageText");
            var result = await RunExportWithProgressAsync(caption, snapshots.Length, (progress, token) =>
            {
                var documents = new List<NoteExportDocument>(snapshots.Length);
                foreach (var snapshot in snapshots)
                {
                    token.ThrowIfCancellationRequested();
                    documents.Add(snapshot.TodoItems is { } items
                        ? NotePortableExport.FromTodoItems(snapshot.Id, snapshot.Title, items)
                        : new NoteExportDocument(snapshot.Id, snapshot.Title, snapshot.Markdown!));
                }
                return NotePortableExport.Export(destination, documents, format, all, images,
                    missingLabel, progress, token);
            });
            if (result == null) return;

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
    }

    private async Task<NoteExportResult?> RunExportWithProgressAsync(string caption, int total,
        Func<IProgress<int>, CancellationToken, NoteExportResult> export)
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new PaperExportProgressWindow(this, caption, total, cancellation);
        var progress = new Progress<int>(dialog.Report);
        _paperExportRunning = true;
        try
        {
            _controller.SetPaperExportActive(true);
            dialog.Show();
            return await Task.Run(() => export(progress, cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _paperExportRunning = false;
            try { dialog.Finish(); }
            finally { _controller.SetPaperExportActive(false); }
        }
    }
}

internal sealed class PaperExportProgressWindow : Window
{
    private readonly TextBlock _status = new();
    private readonly ProgressBar _progress;
    private readonly Button _cancel;
    private readonly CancellationTokenSource _cancellation;
    private readonly int _total;
    private bool _finished;

    internal PaperExportProgressWindow(Window owner, string caption, int total, CancellationTokenSource cancellation)
    {
        _total = total;
        _cancellation = cancellation;
        Title = caption;
        if (owner.IsVisible) Owner = owner;
        WindowStartupLocation = Owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = Owner == null;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 320;
        var panel = new StackPanel { Margin = new Thickness(16) };
        _progress = new ProgressBar { Height = 14, Maximum = total, IsIndeterminate = total == 1,
            Margin = new Thickness(0, 8, 0, 12) };
        _cancel = new Button { Content = Strings.Get("CommonCancel"), MinWidth = 72,
            Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Right };
        _cancel.Click += (_, _) => RequestCancellation();
        panel.Children.Add(_status);
        panel.Children.Add(_progress);
        panel.Children.Add(_cancel);
        Content = panel;
        Report(0);
    }

    internal void Report(int completed)
    {
        if (_finished || _cancellation.IsCancellationRequested) return;
        _status.Text = Strings.Format("NoteExportProgress", completed, _total);
        _progress.Value = completed;
    }

    private void RequestCancellation()
    {
        if (_finished || _cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        _cancel.IsEnabled = false;
        _status.Text = Strings.Get("NoteExportCancelling");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        RequestCancellation();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            RequestCancellation();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_finished) _cancellation.Cancel();
        base.OnClosed(e);
    }

    internal void Finish()
    {
        _finished = true;
        if (IsLoaded) Close();
    }
}
