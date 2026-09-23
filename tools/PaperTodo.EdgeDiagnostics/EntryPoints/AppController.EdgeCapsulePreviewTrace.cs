namespace PaperTodo;

public sealed partial class AppController
{
    private static string EdgeCapsulePreviewTraceId(string? paperId)
    {
        if (string.IsNullOrEmpty(paperId))
        {
            return "<none>";
        }

        return paperId[..Math.Min(6, paperId.Length)];
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void TraceEdgeCapsulePreview(string message) =>
        EdgeCapsulePerformanceDiagnostics.TraceInteraction(message);
}
