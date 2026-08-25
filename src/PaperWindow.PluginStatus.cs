namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal bool HasFailedPluginBody(string providerId)
    {
        return _paper.Type == PaperTypes.Note &&
            _bodyFailed &&
            string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal);
    }

    internal bool HasRunningPluginBody(string providerId)
    {
        return _paper.Type == PaperTypes.Note &&
            !_bodyFailed &&
            _paperBodyHost.HasCurrent &&
            _bodyRuntimeVisible &&
            string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal);
    }
}
