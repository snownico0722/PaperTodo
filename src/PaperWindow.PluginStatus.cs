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
        if (_paper.Type != PaperTypes.Note ||
            !string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return false;
        }

        return HasLiveWebPaperRuntime(providerId) ||
            (!_bodyFailed && _paperBodyHost.HasCurrent && _bodyRuntimeVisible);
    }
}
