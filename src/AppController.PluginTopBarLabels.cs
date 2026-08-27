using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed record PluginTopBarLabelBinding(
    Guid OwnerId,
    string ProviderId,
    PaperTopBarLabel Label);

public sealed partial class AppController
{
    private sealed class PluginTopBarLabelRegistration
    {
        public required Guid OwnerId { get; init; }
        public required string ProviderId { get; init; }
        public required Func<bool> IsActive { get; set; }
        public required long Order { get; init; }
        public Dictionary<string, PaperTopBarLabel[]> Labels { get; } =
            new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, PluginTopBarLabelRegistration>
        _pluginTopBarLabelRegistrations = new(StringComparer.Ordinal);
    private long _pluginTopBarLabelRegistrationOrder;

    internal void SetPluginTopBarLabels(
        Guid ownerId,
        string providerId,
        string paperId,
        IReadOnlyList<PaperTopBarLabel> labels,
        Func<bool> isActive)
    {
        EnsurePluginApiAtLeast(providerId, 2, 1, "topbar_labels_require_api_2_1");
        var normalizedPaperId = RequirePluginLabelTarget(paperId).Id;
        var normalized = PluginContributionPolicy.NormalizeTopBarLabels(labels);

        if (_pluginTopBarLabelRegistrations.TryGetValue(providerId, out var current) &&
            current.OwnerId != ownerId)
        {
            throw new PaperTodoPluginException(
                "topbar_label_runtime_conflict",
                "A provider can have only one active Runtime top-bar-label owner.");
        }

        if (current == null)
        {
            current = new PluginTopBarLabelRegistration
            {
                OwnerId = ownerId,
                ProviderId = providerId,
                IsActive = isActive,
                Order = ++_pluginTopBarLabelRegistrationOrder
            };
            _pluginTopBarLabelRegistrations.Add(providerId, current);
        }
        else
        {
            current.IsActive = isActive;
        }

        if (normalized.Length == 0)
        {
            current.Labels.Remove(normalizedPaperId);
        }
        else
        {
            current.Labels[normalizedPaperId] = normalized;
            PaperWindow.EnsurePluginTopBarLabelsLoadedHandler();
        }
        RefreshPluginTopBarLabels(normalizedPaperId);
    }

    internal void ClearPluginTopBarLabels(
        Guid ownerId,
        string providerId,
        string paperId)
    {
        if (!_pluginTopBarLabelRegistrations.TryGetValue(providerId, out var registration) ||
            registration.OwnerId != ownerId)
        {
            return;
        }

        var normalizedPaperId = paperId?.Trim() ?? string.Empty;
        if (normalizedPaperId.Length > 0 && registration.Labels.Remove(normalizedPaperId))
        {
            RefreshPluginTopBarLabels(normalizedPaperId);
        }
    }

    internal void RemovePluginTopBarLabelsOwner(Guid ownerId, string providerId)
    {
        if (!_pluginTopBarLabelRegistrations.TryGetValue(providerId, out var registration) ||
            registration.OwnerId != ownerId)
        {
            return;
        }

        var affectedPaperIds = registration.Labels.Keys.ToArray();
        _pluginTopBarLabelRegistrations.Remove(providerId);
        foreach (var paperId in affectedPaperIds)
        {
            RefreshPluginTopBarLabels(paperId);
        }
    }

    internal IReadOnlyList<PluginTopBarLabelBinding> GetPluginTopBarLabels(string paperId)
    {
        var normalizedPaperId = paperId?.Trim() ?? string.Empty;
        if (normalizedPaperId.Length == 0 ||
            !State.Papers.Any(paper => string.Equals(paper.Id, normalizedPaperId, StringComparison.Ordinal)))
        {
            return [];
        }

        var candidates = new List<(
            PluginTopBarLabelRegistration Registration,
            PaperTopBarLabel Label,
            int DeclarationOrder)>();
        foreach (var registration in _pluginTopBarLabelRegistrations.Values)
        {
            if (!IsActive(registration.IsActive) ||
                !registration.Labels.TryGetValue(normalizedPaperId, out var labels))
            {
                continue;
            }

            for (var index = 0; index < labels.Length; index++)
            {
                if (labels[index].Visible)
                {
                    candidates.Add((registration, labels[index], index));
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Label.Priority)
            .ThenBy(candidate => candidate.Registration.Order)
            .ThenBy(candidate => candidate.DeclarationOrder)
            .Select(candidate => new PluginTopBarLabelBinding(
                candidate.Registration.OwnerId,
                candidate.Registration.ProviderId,
                candidate.Label))
            .ToArray();
    }

    private PaperData RequirePluginLabelTarget(string paperId)
    {
        var normalized = paperId?.Trim() ?? string.Empty;
        var paper = State.Papers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, normalized, StringComparison.Ordinal));
        return paper ?? throw new PaperTodoPluginException(
            "paper_not_found",
            "The requested Paper does not exist.");
    }

    private void RefreshPluginTopBarLabels(string paperId)
    {
        if (_windows.TryGetValue(paperId, out var window) && !window.IsClosed)
        {
            window.RefreshPluginTopBarLabels();
        }
    }
}
