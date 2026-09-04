namespace PaperTodo;

internal static class VisibilityShortcutSnapshot
{
    // An empty snapshot is meaningful: every linked paper was already hidden. Repeated Hide
    // commands must retain it as well as non-empty snapshots until the existing owner clears it.
    internal static HashSet<string> Capture(
        HashSet<string>? existing,
        IEnumerable<string> visibleLinkedPaperIds) =>
        existing ?? visibleLinkedPaperIds.ToHashSet(StringComparer.Ordinal);
}
