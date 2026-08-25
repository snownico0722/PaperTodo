using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool HasWebPaperRuntimePresentationOwner =>
        _controller.HasWebPaperRuntimeOwnership(
            _paper.Id,
            NormalizeBodyProviderId(_paper.BodyProviderId));

    internal void ApplyWebPaperRuntimePresentation(
        string providerId,
        bool hasHeaderValue,
        string headerText,
        bool hasCapsuleValue,
        PaperCapsulePresentation? capsulePresentation)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (hasHeaderValue)
        {
            ApplyWebPaperRuntimeHeader(providerId, headerText);
        }
        if (hasCapsuleValue)
        {
            ApplyWebPaperRuntimeCapsule(providerId, capsulePresentation);
        }
    }

    internal void ApplyWebPaperRuntimeHeader(string providerId, string headerText)
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

    internal void ApplyWebPaperRuntimeCapsule(
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

    internal void ApplyExternalWebPaperRuntimeState(
        string providerId,
        string stateJson)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }
        if (_paperBodyHost.Current is WebPaperBodySession body)
        {
            body.ApplyExternalState(stateJson);
        }
    }

    internal void ReceiveWebPaperRuntimeMessage(
        string providerId,
        JsonElement payload)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }
        if (_paperBodyHost.Current is WebPaperBodySession body)
        {
            body.ReceiveRuntimeMessage(payload);
        }
    }

    internal void ClearWebPaperRuntimePresentation(string providerId)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        var hadHeader = !string.IsNullOrEmpty(_pluginDisplayTitle) ||
            !string.IsNullOrEmpty(_paper.BodyHeaderText);
        var hadCapsule = _pluginCapsulePresentation != null ||
            !string.IsNullOrEmpty(_paper.BodyCapsuleText);
        _pluginDisplayTitle = string.Empty;
        _pluginCapsulePresentation = null;
        _paper.BodyHeaderText = string.Empty;
        _paper.BodyCapsuleText = string.Empty;
        ResetPluginCapsuleCustomViews();
        if (_isShellBuilt)
        {
            if (hadHeader)
            {
                RefreshPaperTitle();
            }
            if (hadCapsule)
            {
                RefreshCapsuleLabel();
                ApplyCurrentCollapsedCapsuleWidth();
            }
        }
    }
}
