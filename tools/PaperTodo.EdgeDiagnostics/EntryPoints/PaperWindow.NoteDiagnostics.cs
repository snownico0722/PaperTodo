namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static readonly object NoteRenderTraceLock = new();

    private void TraceNoteRender(string message)
    {
#if DEBUG
        if (EdgeDiagnosticJournal.Enabled)
        {
            EdgeDiagnosticJournal.AppendText("md-render-trace.log", $"paper={_paper.Id[..Math.Min(6, _paper.Id.Length)]} {message}");
            return;
        }
        // edgeJournal: default non-memory diagnostics preserve their original behavior.
#endif

#if DEBUG
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "md-render-trace.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} paper={_paper.Id[..Math.Min(6, _paper.Id.Length)]} {message}{Environment.NewLine}";
            lock (NoteRenderTraceLock)
            {
                System.IO.File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Test-only diagnostics must never affect note interaction.
        }
#endif
    }
}
