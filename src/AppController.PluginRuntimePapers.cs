using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    // Volatile presentation fallback that outlives one Runtime lease. A Web Runtime can enter
    // Backoff and dispose its lease while the owning Papers still exist; keeping the last rich
    // capsule snapshot here lets a rebuilt PaperWindow replay the previous presentation until the
    // Runtime recovers. Final Failed clears this cache together with the persisted fallback text.
    private readonly Dictionary<string, Dictionary<string, PaperCapsulePresentation>>
        _pluginRuntimePresentationCache = new(StringComparer.Ordinal);

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
        DeclaresPluginRuntime(descriptor);

    internal bool CanPostBodyMessageToPluginRuntime(string paperId, string providerId) =>
        _pluginRuntimeSlots.TryGetValue(providerId, out var slot) &&
        slot.State == PluginRuntimeState.Running &&
        slot.Lease?.Papers != null &&
        CanPluginRuntimeAcceptMessages(slot) &&
        FindPluginRuntimePaper(providerId, paperId) != null;

    internal bool PostBodyMessageToPluginRuntime(
        string paperId,
        string providerId,
        JsonElement payload)
    {
        if (!_pluginRuntimeSlots.TryGetValue(providerId, out var slot) ||
            slot.State != PluginRuntimeState.Running ||
            slot.Lease?.Papers == null ||
            !CanPluginRuntimeAcceptMessages(slot) ||
            FindPluginRuntimePaper(providerId, paperId) == null)
        {
            return false;
        }
        return slot.Lease.Papers.PublishMessage(paperId, payload);
    }

    private static bool CanPluginRuntimeAcceptMessages(PluginRuntimeSlot slot) =>
        slot.Lease?.Runtime is not WebPluginRuntime webRuntime ||
        webRuntime.CanAcceptPaperMessages;

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
        if (!_pluginRuntimeSlots.TryGetValue(providerId, out var slot) ||
            slot.State is PluginRuntimeState.Failed or PluginRuntimeState.Disposing ||
            FindPluginRuntimePaper(providerId, paperId) == null)
        {
            return;
        }

        if (slot.State == PluginRuntimeState.Running &&
            slot.Lease?.Papers != null &&
            slot.Lease.Papers.TryGetCapsulePresentation(paperId, out var livePresentation))
        {
            window.ApplyPluginRuntimeCapsule(providerId, livePresentation);
            return;
        }

        // A failed Web Runtime disposes its lease before entering Backoff. Keep replaying the last
        // published rich capsule while the replacement Runtime starts so a PaperWindow rebuild does
        // not temporarily collapse to BodyCapsuleText-only presentation.
        if (slot.State is PluginRuntimeState.Starting or
            PluginRuntimeState.Backoff or
            PluginRuntimeState.Running &&
            TryGetPluginRuntimePresentationCache(
                providerId,
                paperId,
                out var retainedPresentation))
        {
            window.ApplyPluginRuntimeCapsule(providerId, retainedPresentation);
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
        CachePluginRuntimePaperCapsule(providerId, paperId, normalized);
        paper.BodyCapsuleText = normalized == null
            ? string.Empty
            : PaperWindow.CapsulePresentationFallbackText(normalized);
        if (_windows.TryGetValue(paperId, out var window) && !window.IsClosed)
        {
            window.ApplyPluginRuntimeCapsule(providerId, normalized);
        }
    }

    internal void RemovePluginRuntimePresentationCache(
        string providerId,
        string paperId)
    {
        if (!_pluginRuntimePresentationCache.TryGetValue(providerId, out var papers))
        {
            return;
        }

        papers.Remove(paperId);
        if (papers.Count == 0)
        {
            _pluginRuntimePresentationCache.Remove(providerId);
        }
    }

    internal void ClearPluginRuntimePresentation(string providerId)
    {
        _pluginRuntimePresentationCache.Remove(providerId);

        var changed = false;
        foreach (var paper in State.Papers.Where(paper =>
                     paper.Type == PaperTypes.Note &&
                     string.Equals(
                         PluginRuntimeProviderId(paper.BodyProviderId),
                         providerId,
                         StringComparison.Ordinal)))
        {
            var hadHeader = !string.IsNullOrEmpty(paper.BodyHeaderText);
            var hadCapsule = !string.IsNullOrEmpty(paper.BodyCapsuleText);
            changed |= hadHeader || hadCapsule;

            if (_windows.TryGetValue(paper.Id, out var window) && !window.IsClosed)
            {
                window.ClearPluginRuntimePresentation(providerId);
            }
            else
            {
                paper.BodyHeaderText = string.Empty;
                paper.BodyCapsuleText = string.Empty;
            }
        }

        if (changed)
        {
            MarkDirty();
        }
    }

    private void CachePluginRuntimePaperCapsule(
        string providerId,
        string paperId,
        PaperCapsulePresentation? presentation)
    {
        if (presentation == null)
        {
            RemovePluginRuntimePresentationCache(providerId, paperId);
            return;
        }

        if (!_pluginRuntimePresentationCache.TryGetValue(providerId, out var papers))
        {
            papers = new Dictionary<string, PaperCapsulePresentation>(StringComparer.Ordinal);
            _pluginRuntimePresentationCache.Add(providerId, papers);
        }
        papers[paperId] = presentation;
    }

    private bool TryGetPluginRuntimePresentationCache(
        string providerId,
        string paperId,
        out PaperCapsulePresentation? presentation)
    {
        if (_pluginRuntimePresentationCache.TryGetValue(providerId, out var papers) &&
            papers.TryGetValue(paperId, out var value))
        {
            presentation = value;
            return true;
        }

        presentation = null;
        return false;
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
