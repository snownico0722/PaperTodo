using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    internal IReadOnlyList<PaperPluginRuntimePaper> GetPluginRuntimePapers(string providerId) =>
        State.Papers
            .Where(paper =>
                paper.Type == PaperTypes.Note &&
                string.Equals(
                    PluginRuntimeProviderId(paper.BodyProviderId),
                    providerId,
                    StringComparison.Ordinal))
            .Select(paper => new PaperPluginRuntimePaper(paper.Id))
            .ToArray();

    internal PaperPluginRuntimePaper? GetPluginRuntimePaper(
        string providerId,
        string paperId)
    {
        var paper = FindPluginRuntimePaper(providerId, paperId);
        return paper == null ? null : new PaperPluginRuntimePaper(paper.Id);
    }

    internal bool HasPluginRuntimeOwnership(string paperId, string providerId) =>
        FindPluginRuntimePaper(providerId, paperId) != null &&
        PaperBodyPlugins.TryGet(providerId, out var descriptor) &&
        DeclaresPluginAppRuntime(descriptor);

    internal bool CanPostBodyMessageToPluginRuntime(string paperId, string providerId) =>
        _pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) &&
        slot.State == PluginAppRuntimeState.Running &&
        slot.Lease?.Papers != null &&
        FindPluginRuntimePaper(providerId, paperId) != null;

    internal bool PostBodyMessageToPluginRuntime(
        string paperId,
        string providerId,
        JsonElement payload)
    {
        if (!_pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) ||
            slot.State != PluginAppRuntimeState.Running ||
            slot.Lease?.Papers == null ||
            FindPluginRuntimePaper(providerId, paperId) == null)
        {
            return false;
        }
        return slot.Lease.Papers.PublishMessage(paperId, payload);
    }

    internal bool PostPluginRuntimeMessageToBody(
        string providerId,
        string paperId,
        JsonElement payload)
    {
        _ = RequirePluginRuntimePaper(providerId, paperId);
        return _windows.TryGetValue(paperId, out var window) &&
            !window.IsClosed &&
            window.ReceivePluginRuntimeMessage(providerId, payload);
    }

    internal void ApplyPluginRuntimePresentationToWindow(
        PaperWindow window,
        string paperId,
        string providerId)
    {
        if (!_pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) ||
            slot.State != PluginAppRuntimeState.Running ||
            slot.Lease?.Papers == null ||
            FindPluginRuntimePaper(providerId, paperId) == null)
        {
            return;
        }
        if (slot.Lease.Papers.TryGetCapsulePresentation(paperId, out var presentation))
        {
            window.ApplyPluginRuntimeCapsule(providerId, presentation);
        }
    }

    internal void SetPluginRuntimePaperTitle(
        string providerId,
        string paperId,
        string title)
    {
        var paper = RequirePluginRuntimePaper(providerId, paperId);
        UpdatePaperTitleFromPlugin(paper, title, providerId);
    }

    internal void SetPluginRuntimePaperHeader(
        string providerId,
        string paperId,
        string text)
    {
        var paper = RequirePluginRuntimePaper(providerId, paperId);
        var normalized = PaperWindow.NormalizePluginDisplayText(text);
        paper.BodyHeaderText = normalized;
        if (_windows.TryGetValue(paperId, out var window) && !window.IsClosed)
        {
            window.ApplyPluginRuntimeHeader(providerId, normalized);
        }
        NotifyPaperDisplayTitleChanged(paperId);
    }

    internal void SetPluginRuntimePaperCapsule(
        string providerId,
        string paperId,
        PaperCapsulePresentation? presentation)
    {
        var paper = RequirePluginRuntimePaper(providerId, paperId);
        var normalized = PaperWindow.NormalizePluginCapsulePresentation(presentation);
        paper.BodyCapsuleText = normalized == null
            ? string.Empty
            : PaperWindow.CapsulePresentationFallbackText(normalized);
        if (_windows.TryGetValue(paperId, out var window) && !window.IsClosed)
        {
            window.ApplyPluginRuntimeCapsule(providerId, normalized);
        }
    }

    private PaperData? FindPluginRuntimePaper(string providerId, string paperId) =>
        State.Papers.FirstOrDefault(paper =>
            paper.Type == PaperTypes.Note &&
            string.Equals(paper.Id, paperId, StringComparison.Ordinal) &&
            string.Equals(
                PluginRuntimeProviderId(paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal));

    private static string PluginRuntimeProviderId(string? providerId) =>
        providerId?.Trim() ?? string.Empty;

    private PaperData RequirePluginRuntimePaper(string providerId, string paperId) =>
        FindPluginRuntimePaper(providerId, paperId)
        ?? throw new PaperTodoPluginException(
            "paper_not_owned",
            "The requested Paper is not an instance of this plugin Runtime.");
}
