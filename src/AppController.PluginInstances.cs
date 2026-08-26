namespace PaperTodo;

public sealed partial class AppController
{
    internal bool CanAssignPluginProvider(
        PaperData paper,
        PaperBodyPluginDescriptor descriptor)
    {
        var limit = descriptor.Manifest?.MaxPaperInstances ?? 1;
        if (limit == 0)
        {
            return true;
        }

        var otherInstances = State.Papers.Count(candidate =>
            !ReferenceEquals(candidate, paper) &&
            candidate.Type == PaperTypes.Note &&
            string.Equals(
                candidate.BodyProviderId,
                descriptor.Id,
                StringComparison.Ordinal));
        return otherInstances < limit;
    }

    internal bool CanCreatePluginPaper(PaperBodyPluginDescriptor descriptor)
    {
        var limit = descriptor.Manifest?.MaxPaperInstances ?? 1;
        return limit == 0 || State.Papers.Count(candidate =>
            candidate.Type == PaperTypes.Note &&
            string.Equals(
                candidate.BodyProviderId,
                descriptor.Id,
                StringComparison.Ordinal)) < limit;
    }
}
