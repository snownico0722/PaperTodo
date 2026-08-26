using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool HasPluginRuntimePresentationOwner =>
        _controller.HasPluginRuntimeOwnership(
            _paper.Id,
            NormalizeBodyProviderId(_paper.BodyProviderId));

    private void ReplayPluginRuntimePresentation()
    {
        var providerId = NormalizeBodyProviderId(_paper.BodyProviderId);
        if (providerId.Length == 0)
        {
            return;
        }
        _controller.ApplyPluginRuntimePresentationToWindow(
            this,
            _paper.Id,
            providerId);
    }

    internal void ApplyPluginRuntimeHeader(string providerId, string headerText)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        _pluginDisplayTitle = headerText;
        _paper.BodyHeaderText = headerText;
        if (_isShellBuilt)
        {
            RefreshPaperTitle();
        }
    }

    internal void ApplyPluginRuntimeCapsule(
        string providerId,
        PaperCapsulePresentation? presentation)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!_isShellBuilt)
        {
            _pluginCapsulePresentation = presentation;
            return;
        }
        SetPluginCapsulePresentation(presentation);
    }

    internal bool ReceivePluginRuntimeMessage(
        string providerId,
        JsonElement payload)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return false;
        }
        return _paperBodyHost.Current?.OnRuntimeMessage(payload) == true;
    }
}
