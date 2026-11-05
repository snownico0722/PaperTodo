#if !DEBUG
using System.Diagnostics;

namespace PaperTodo;

// Release keeps only the compile-time entry points. The collector and observers live in
// tools/PaperTodo.EdgeDiagnostics and are source-linked only by the Debug application build.
internal static class EdgeDiagnosticJournal
{
    internal static bool Enabled => false;

    [Conditional("DEBUG")]
    internal static void AppendText(string fileName, string message) { }

    [Conditional("DEBUG")]
    internal static void Event(string eventName, long correlationId = 0,
        long value1 = 0, long value2 = 0, long value3 = 0, long value4 = 0,
        double number1 = 0, double number2 = 0, string? detail = null) { }

    [Conditional("DEBUG")]
    internal static void Complete(string reason) { }
}
#endif
