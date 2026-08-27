using System.Diagnostics;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed record PluginTopBarActionBinding(
    Guid OwnerId,
    string ProviderId,
    PaperTopBarActionScope Scope,
    PaperTopBarAction Action);

internal sealed record PluginTopBarRenderState(
    IReadOnlyList<PluginTopBarActionBinding> Actions,
    PaperHostTopBarActions HiddenHostActions);

public sealed partial class AppController
{
    private const int MaximumPaperTopBarActions = 4;
    private const int MaximumGlobalTopBarActions = 256;
    private const int MaximumTopBarActionIdLength = 64;
    private const int MaximumTopBarToolTipLength = 160;
    private const int MaximumTopBarCharacterLength = 8;
    private const int MaximumTopBarSvgPathLength = 4096;
    private const double MinimumTopBarSvgStrokeWidth = 0.1;
    private const double MaximumTopBarSvgStrokeWidth = 4.0;

    private sealed class PluginPaperTopBarRegistration
    {
        public required Guid SessionId { get; init; }
        public required string ProviderId { get; init; }
        public required string HostPaperId { get; init; }
        public required Func<bool> IsActive { get; set; }
        public required Action<PaperTopBarActionInvocation> Invoke { get; set; }
        public PaperTopBarAction[] Actions { get; set; } = [];
        public PaperHostTopBarActions HiddenHostActions { get; set; }
    }

    private sealed class PluginGlobalTopBarRegistration
    {
        public required Guid RuntimeId { get; init; }
        public required string ProviderId { get; init; }
        public required Func<bool> IsActive { get; set; }
        public required Action<PaperTopBarActionInvocation> Invoke { get; set; }
        public required long Order { get; init; }
        public PaperTopBarAction[] Actions { get; set; } = [];
    }

    private readonly Dictionary<Guid, PluginPaperTopBarRegistration>
        _pluginPaperTopBars = new();
    private readonly Dictionary<string, PluginGlobalTopBarRegistration>
        _pluginGlobalTopBars = new(StringComparer.Ordinal);
    private long _pluginGlobalTopBarOrder;

    internal void SetPluginPaperTopBarActions(
        Guid sessionId,
        string providerId,
        string hostPaperId,
        IReadOnlyList<PaperTopBarAction> actions,
        PaperHostTopBarActions hiddenHostActions,
        Func<bool> isActive,
        Action<PaperTopBarActionInvocation> invoke)
    {
        EnsurePluginTopBarProtocol(providerId);
        var normalized = NormalizePluginTopBarActions(
            actions,
            MaximumPaperTopBarActions,
            "paper");
        const PaperHostTopBarActions supportedHidden =
            PaperHostTopBarActions.NewTodoPaper |
            PaperHostTopBarActions.NewNotePaper;
        if ((hiddenHostActions & ~supportedHidden) != 0)
        {
            throw new PaperTodoPluginException(
                "invalid_topbar_host_action",
                "Only the host's new-Todo and new-Note actions can be hidden by a plugin paper.");
        }

        if (!_pluginPaperTopBars.TryGetValue(sessionId, out var registration))
        {
            registration = new PluginPaperTopBarRegistration
            {
                SessionId = sessionId,
                ProviderId = providerId,
                HostPaperId = hostPaperId,
                IsActive = isActive,
                Invoke = invoke
            };
            _pluginPaperTopBars.Add(sessionId, registration);
        }
        else
        {
            registration.IsActive = isActive;
            registration.Invoke = invoke;
        }

        registration.Actions = normalized;
        registration.HiddenHostActions = hiddenHostActions;
        if (normalized.Length > 0 || hiddenHostActions != PaperHostTopBarActions.None)
        {
            PaperWindow.EnsurePluginTopBarLoadedHandler();
        }
        RefreshPluginTopBarForPaper(hostPaperId);
    }

    internal void SetPluginGlobalTopBarActions(
        Guid runtimeId,
        string providerId,
        IReadOnlyList<PaperTopBarAction> actions,
        Func<bool> isActive,
        Action<PaperTopBarActionInvocation> invoke)
    {
        // Runtime creation already validated an accepted protocol version. The protocol accepts a
        // broad descriptor set, while the window still materializes only the fitting prefix after
        // current-paper actions have taken first claim on plugin space.
        var normalized = NormalizePluginTopBarActions(
            actions,
            MaximumGlobalTopBarActions,
            "global");

        if (_pluginGlobalTopBars.TryGetValue(providerId, out var current) &&
            current.RuntimeId != runtimeId)
        {
            throw new PaperTodoPluginException(
                "global_topbar_runtime_conflict",
                "A provider can have only one active app-runtime global top-bar owner.");
        }

        if (current == null)
        {
            current = new PluginGlobalTopBarRegistration
            {
                RuntimeId = runtimeId,
                ProviderId = providerId,
                IsActive = isActive,
                Invoke = invoke,
                Order = ++_pluginGlobalTopBarOrder
            };
            _pluginGlobalTopBars.Add(providerId, current);
        }
        else
        {
            current.IsActive = isActive;
            current.Invoke = invoke;
        }

        current.Actions = normalized;
        if (normalized.Length > 0)
        {
            PaperWindow.EnsurePluginTopBarLoadedHandler();
        }
        RefreshAllPluginTopBars();
    }

    private void EnsurePluginTopBarProtocol(string providerId)
    {
        if (PaperBodyPlugins.TryGet(providerId, out var descriptor) &&
            PluginContributionPolicy.ApiAtLeast(descriptor.ApiVersion, 2, 0))
        {
            return;
        }

        throw new PaperTodoPluginException(
            "topbar_requires_api_2_0",
            "Plugin top-bar extensions require apiVersion 2.0 or newer.");
    }

    internal void RemovePluginPaperTopBarSession(Guid sessionId)
    {
        if (_pluginPaperTopBars.Remove(sessionId, out var removed))
        {
            RefreshPluginTopBarForPaper(removed.HostPaperId);
        }
    }

    internal void RemovePluginGlobalTopBarRuntime(Guid runtimeId, string providerId)
    {
        if (_pluginGlobalTopBars.TryGetValue(providerId, out var current) &&
            current.RuntimeId == runtimeId)
        {
            _pluginGlobalTopBars.Remove(providerId);
            RefreshAllPluginTopBars();
        }
    }

    internal PluginTopBarRenderState GetPluginTopBarRenderState(string paperId)
    {
        // Rendering is a read-only query. Ownership teardown is the only place that mutates these
        // registries so a stale registration cannot disappear here before its owner gets a chance
        // to refresh every window through RemovePlugin*TopBar*.
        var paperRegistration = _pluginPaperTopBars.Values
            .FirstOrDefault(item =>
                string.Equals(item.HostPaperId, paperId, StringComparison.Ordinal) &&
                IsActive(item.IsActive));

        var actions = new List<PluginTopBarActionBinding>();
        if (paperRegistration != null)
        {
            foreach (var action in paperRegistration.Actions)
            {
                actions.Add(new PluginTopBarActionBinding(
                    paperRegistration.SessionId,
                    paperRegistration.ProviderId,
                    PaperTopBarActionScope.Paper,
                    action));
            }
        }

        var globalActions = _pluginGlobalTopBars.Values
            .Where(item => IsActive(item.IsActive))
            .SelectMany(registration =>
                registration.Actions.Select((action, index) => new
                {
                    Registration = registration,
                    Action = action,
                    DeclarationOrder = index
                }))
            .OrderByDescending(item => item.Action.Priority)
            .ThenBy(item => item.Registration.Order)
            .ThenBy(item => item.DeclarationOrder);
        foreach (var item in globalActions)
        {
            actions.Add(new PluginTopBarActionBinding(
                item.Registration.RuntimeId,
                item.Registration.ProviderId,
                PaperTopBarActionScope.Global,
                item.Action));
        }

        return new PluginTopBarRenderState(
            actions,
            paperRegistration?.HiddenHostActions ?? PaperHostTopBarActions.None);
    }

    internal void InvokePluginTopBarAction(
        PluginTopBarActionBinding binding,
        string targetPaperId,
        string targetPaperType,
        string targetBodyProviderId)
    {
        Action<PaperTopBarActionInvocation>? invoke = null;
        IReadOnlyList<PaperTopBarAction>? actions = null;

        if (binding.Scope == PaperTopBarActionScope.Global)
        {
            if (!_pluginGlobalTopBars.TryGetValue(binding.ProviderId, out var registration) ||
                registration.RuntimeId != binding.OwnerId ||
                !IsActive(registration.IsActive))
            {
                RemovePluginGlobalTopBarRuntime(binding.OwnerId, binding.ProviderId);
                return;
            }
            invoke = registration.Invoke;
            actions = registration.Actions;
        }
        else
        {
            if (!_pluginPaperTopBars.TryGetValue(binding.OwnerId, out var registration) ||
                !IsActive(registration.IsActive))
            {
                RemovePluginPaperTopBarSession(binding.OwnerId);
                return;
            }
            invoke = registration.Invoke;
            actions = registration.Actions;
        }

        if (!actions.Any(action =>
                string.Equals(action.Id, binding.Action.Id, StringComparison.Ordinal) &&
                action.Visible &&
                action.Enabled))
        {
            return;
        }

        try
        {
            invoke(new PaperTopBarActionInvocation(
                binding.Action.Id,
                binding.Scope,
                targetPaperId,
                targetPaperType,
                targetBodyProviderId));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Plugin top-bar action failed. Provider={0}; Action={1}; Exception={2}",
                binding.ProviderId,
                binding.Action.Id,
                ex.GetBaseException());
        }
    }

    private static PaperTopBarAction[] NormalizePluginTopBarActions(
        IReadOnlyList<PaperTopBarAction>? actions,
        int? maximumCount,
        string scope)
    {
        actions ??= [];
        if (maximumCount is { } limit && actions.Count > limit)
        {
            throw new PaperTodoPluginException(
                "too_many_topbar_actions",
                $"A plugin can contribute at most {limit} {scope} top-bar actions.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PaperTopBarAction>(actions.Count);
        foreach (var source in actions)
        {
            if (source == null)
            {
                throw new PaperTodoPluginException(
                    "invalid_topbar_action",
                    "Top-bar actions cannot contain null entries.");
            }

            var id = source.Id?.Trim() ?? "";
            if (id.Length is 0 or > MaximumTopBarActionIdLength ||
                id.Any(ch =>
                    !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')) ||
                !seen.Add(id))
            {
                throw new PaperTodoPluginException(
                    "invalid_topbar_action_id",
                    "Top-bar action ids must be unique 1-64 character ASCII identifiers using letters, digits, '.', '_' or '-'.");
            }

            var tooltip = source.ToolTip?.Trim() ?? "";
            if (tooltip.Length > MaximumTopBarToolTipLength)
            {
                throw new PaperTodoPluginException(
                    "invalid_topbar_tooltip",
                    $"Top-bar tooltips cannot exceed {MaximumTopBarToolTipLength} characters.");
            }

            var icon = source.Icon ?? new PaperTopBarIcon();
            var value = icon.Value?.Trim() ?? "";
            switch (icon.Kind)
            {
                case PaperTopBarIconKind.Character:
                    if (value.Length is 0 or > MaximumTopBarCharacterLength ||
                        value.Any(char.IsControl))
                    {
                        throw new PaperTodoPluginException(
                            "invalid_topbar_icon",
                            $"Character top-bar icons must contain 1-{MaximumTopBarCharacterLength} UTF-16 characters and no control characters.");
                    }
                    break;

                case PaperTopBarIconKind.SvgPath:
                    if (value.Length is 0 or > MaximumTopBarSvgPathLength)
                    {
                        throw new PaperTodoPluginException(
                            "invalid_topbar_icon",
                            $"SVG path data must contain 1-{MaximumTopBarSvgPathLength} characters.");
                    }
                    if (!Enum.IsDefined(icon.RenderMode))
                    {
                        throw new PaperTodoPluginException(
                            "invalid_topbar_icon",
                            "Unknown SVG top-bar render mode.");
                    }
                    if (icon.RenderMode == PaperTopBarSvgRenderMode.Stroke &&
                        (!double.IsFinite(icon.StrokeWidth) ||
                         icon.StrokeWidth < MinimumTopBarSvgStrokeWidth ||
                         icon.StrokeWidth > MaximumTopBarSvgStrokeWidth))
                    {
                        throw new PaperTodoPluginException(
                            "invalid_topbar_icon",
                            $"SVG strokeWidth must be between {MinimumTopBarSvgStrokeWidth} and {MaximumTopBarSvgStrokeWidth}.");
                    }
                    try
                    {
                        _ = Geometry.Parse(value);
                    }
                    catch (Exception ex)
                    {
                        throw new PaperTodoPluginException(
                            "invalid_topbar_icon",
                            $"SVG path data is invalid: {ex.GetBaseException().Message}");
                    }
                    break;

                default:
                    throw new PaperTodoPluginException(
                        "invalid_topbar_icon",
                        "Unknown top-bar icon kind.");
            }

            result.Add(source with
            {
                Id = id,
                ToolTip = tooltip,
                Icon = icon with { Value = value }
            });
        }
        return result.ToArray();
    }

    private static bool IsActive(Func<bool> predicate)
    {
        try
        {
            return predicate();
        }
        catch
        {
            return false;
        }
    }

    private void RefreshPluginTopBarForPaper(string paperId)
    {
        if (_windows.TryGetValue(paperId, out var window) && !window.IsClosed)
        {
            window.RefreshPluginTopBarActions();
        }
    }

    private void RefreshAllPluginTopBars()
    {
        foreach (var window in _windows.Values.ToArray())
        {
            if (!window.IsClosed)
            {
                window.RefreshPluginTopBarActions();
            }
        }
    }
}
