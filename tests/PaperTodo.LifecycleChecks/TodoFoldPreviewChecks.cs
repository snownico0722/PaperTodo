using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static class TodoFoldPreviewChecks
{
    internal static async Task Run()
    {
        const string source = "first line  \r\nsecond line  \r\nthird line  \r\nfourth line";
        var editor = new TodoTextBox
        {
            Text = source,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            Padding = new Thickness(2)
        };
        var row = new Grid { Width = 240, VerticalAlignment = VerticalAlignment.Top };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        var fold = new TodoFoldPreview(editor, row);
        var preview = row.Children.OfType<TextBlock>().Single();
        var window = new Window
        {
            Content = row, Width = 300, Height = 240,
            ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            await Idle();
            foreach (var (lines, expected) in new[]
            {
                (2, "first line  \nsecond line…"),
                (1, "first line…"),
                (3, "first line  \nsecond line  \nthird line…"),
                (2, "first line  \nsecond line…")
            })
            {
                editor.MaxLines = lines;
                fold.SetFolded(true);
                row.UpdateLayout();
                await Idle();
                Require(editor.LineCount == 4, "fixture text should occupy four visual lines");
                Require(preview.Visibility == Visibility.Visible && editor.Opacity == 0,
                    $"{lines}-line fold did not show the ellipsis preview");
                Require(preview.Text == expected,
                    $"{lines}-line fold lost a line boundary or placed the ellipsis on the wrong line");
                Require(preview.Height <= editor.ActualHeight,
                    $"{lines}-line preview exceeded the editor's visible height");
                Require(editor.Text == source, "folding changed the source text");
            }

            // If the changed cap exposes the whole text, an older short preview must disappear.
            editor.MaxLines = 6;
            fold.SetFolded(true);
            row.UpdateLayout();
            await Idle();
            Require(preview.Visibility == Visibility.Collapsed && editor.Opacity == 1,
                "a no-longer-truncated todo retained its previous ellipsis overlay");
        }
        finally
        {
            window.Close();
        }
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher
        .InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
