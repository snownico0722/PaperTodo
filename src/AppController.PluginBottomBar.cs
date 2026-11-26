using System.Diagnostics;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    internal const string PluginBottomBarCapability = "bottomBar";
    private readonly Dictionary<string, PaperPluginRuntimeBottomBarApi> _pluginBottomBars =
        new(StringComparer.Ordinal);

    internal static bool DeclaresPluginBottomBar(PaperBodyPluginDescriptor descriptor) =>
        descriptor.Manifest?.Capabilities.Contains(PluginBottomBarCapability, StringComparer.Ordinal) == true;

    private bool HasPluginRuntimeOwner(PaperBodyPluginDescriptor descriptor) =>
        DeclaresPluginBottomBar(descriptor) || HasEntityPluginPaper(descriptor.Id);

    // Existing global chrome still needs an entity paper, even when global bottom-bar enablement
    // keeps the same Runtime alive. Other providers retain their existing lifetime check.
    private bool AllowsPluginGlobalChrome(string providerId) =>
        !_pluginBottomBars.ContainsKey(providerId) || HasEntityPluginPaper(providerId);

    private PaperPluginRuntimeBottomBarApi? CreatePluginBottomBar(
        PaperBodyPluginDescriptor descriptor, Func<bool> isActive)
    {
        if (!DeclaresPluginBottomBar(descriptor)) return null;
        var api = new PaperPluginRuntimeBottomBarApi(this, descriptor.Id, descriptor.Manifest!.BottomBarTargets, isActive);
        _pluginBottomBars[descriptor.Id] = api;
        return api;
    }

    internal void RemovePluginBottomBar(PaperPluginRuntimeBottomBarApi api)
    {
        if (_pluginBottomBars.TryGetValue(api.ProviderId, out var current) && ReferenceEquals(current, api))
            _pluginBottomBars.Remove(api.ProviderId);
    }

    internal IEnumerable<(PaperPluginRuntimeBottomBarApi Runtime, PaperBottomBarItem Item)> GetPaperBottomBarItems(
        PaperData paper)
    {
        foreach (var api in _pluginBottomBars.Values)
        {
            if (!api.Supports(paper.Type)) continue;
            foreach (var item in api.GetItems(paper.Id))
                if (item.Visible) yield return (api, item);
        }
    }

    internal void RefreshPaperBottomBar(string paperId)
    {
        if (_windows.TryGetValue(paperId, out var window)) window.RefreshPaperBottomBar();
    }
}

internal sealed class PaperPluginRuntimeBottomBarApi(
    AppController controller, string providerId, string[] targetTypes, Func<bool> isActive) : IPaperPluginBottomBarApi, IDisposable
{
    private readonly Dictionary<string, PaperBottomBarItem[]> _items = new(StringComparer.Ordinal);
    private IReadOnlyList<PaperBottomBarTarget> _targets = CollectTargets(controller, targetTypes);
    internal bool Supports(string type) => targetTypes.Contains(type, StringComparer.Ordinal);
    private static PaperBottomBarTarget[] CollectTargets(AppController host, string[] types) =>
        host.State.Papers.Where(p => types.Contains(p.Type, StringComparer.Ordinal))
            .Select(p => new PaperBottomBarTarget(p.Id, p.Type)).ToArray();
    private Action<PaperActionInvocation>? _action;
    public event Action? TargetsChanged;
    internal string ProviderId => providerId;

    public IReadOnlyList<PaperBottomBarTarget> ListTargets() =>
        OnUi(() => CollectTargets(controller, targetTypes));

    public void SetItems(string paperId, IReadOnlyList<PaperBottomBarItem> items) =>
        OnUi(() =>
        {
            var id = paperId?.Trim();
            if (id == null || !controller.State.Papers.Any(p => p.Id == id && Supports(p.Type)))
                throw new PaperTodoPluginException("bottom_bar_target_unavailable", "No current paper of a declared bottom-bar type has this ID.");
            var normalized = PluginContributionPolicy.NormalizeBottomBarItems(items);
            if (_items.TryGetValue(id, out var previous) && previous.SequenceEqual(normalized)) return true;
            if (normalized.Length == 0) _items.Remove(id);
            else _items[id] = normalized;
            controller.RefreshPaperBottomBar(id);
            return true;
        });

    public void SetActionHandler(Action<PaperActionInvocation>? handler) =>
        OnUi(() => { _action = handler; return true; });

    private T OnUi<T>(Func<T> action)
    {
        T Invoke()
        {
            if (!isActive()) throw new PaperTodoPluginException("runtime_closed", "The plugin Runtime has ended.");
            return action();
        }

        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? Invoke() : dispatcher.Invoke(Invoke);
    }

    internal IReadOnlyList<PaperBottomBarItem> GetItems(string paperId) =>
        isActive() && _items.TryGetValue(paperId, out var items) ? items : [];

    internal void ReconcileTargets()
    {
        if (!isActive()) return;
        var current = CollectTargets(controller, targetTypes);
        if (current.SequenceEqual(_targets)) return;
        var previous = _targets;
        _targets = current;
        foreach (var departed in previous.Except(current))
            if (_items.Remove(departed.PaperId)) controller.RefreshPaperBottomBar(departed.PaperId);

        if (TargetsChanged is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); }
                catch (Exception ex) { Trace.TraceWarning("Bottom bar targets callback failed ({0}): {1}", providerId, ex.GetBaseException()); }
    }

    internal void Invoke(PaperData paper, PaperBottomBarItem item, PaperPopupPosition? position = null)
    {
        if (!item.Clickable || !Supports(paper.Type) || !controller.State.Papers.Contains(paper) ||
            !GetItems(paper.Id).Contains(item)) return;
        try
        {
            _action?.Invoke(new PaperActionInvocation(item.Id, controller.CapturePaperSnapshot(paper),
                position ?? PluginPopupHost.CapturePosition()));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Bottom bar action failed ({0}/{1}): {2}", providerId, item.Id, ex.GetBaseException());
        }
    }

    internal void Clear()
    {
        var targets = _items.Keys.ToArray();
        _items.Clear();
        _action = null;
        foreach (var id in targets) controller.RefreshPaperBottomBar(id);
    }

    public void Dispose()
    {
        TargetsChanged = null;
        controller.RemovePluginBottomBar(this);
        Clear();
    }
}
