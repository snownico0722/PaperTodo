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
        HasEntityPluginPaper(descriptor.Id) ||
        (DeclaresPluginBottomBar(descriptor) && State.Papers.Any(paper => HasBottomBarBinding(paper, descriptor.Id)));

    // Existing global chrome still needs an entity paper, even when a bottom-bar target
    // keeps the same Runtime alive. Other providers retain their existing lifetime check.
    private bool AllowsPluginGlobalChrome(string providerId) =>
        !_pluginBottomBars.ContainsKey(providerId) || HasEntityPluginPaper(providerId);

    internal static bool HasBottomBarBinding(PaperData paper, string providerId) =>
        paper.BottomBarPluginIds?.Contains(providerId, StringComparer.Ordinal) == true;

    internal IReadOnlyList<PaperBottomBarTarget> GetBottomBarTargets(string providerId) =>
        State.Papers.Where(paper => HasBottomBarBinding(paper, providerId))
            .Select(paper => new PaperBottomBarTarget(paper.Id, paper.Type)).ToArray();

    internal void TogglePaperBottomBar(PaperData paper, string providerId)
    {
        if (IsExiting || IsDataReloading || !State.Papers.Contains(paper)) return;
        if (HasBottomBarBinding(paper, providerId))
        {
            var remaining = paper.BottomBarPluginIds!.Where(id => id != providerId).ToArray();
            paper.BottomBarPluginIds = remaining.Length == 0 ? null : remaining;
        }
        else
        {
            if (!IsPluginEnabled(providerId) || !PaperBodyPlugins.TryGet(providerId, out var descriptor) ||
                !DeclaresPluginBottomBar(descriptor)) return;
            paper.BottomBarPluginIds = [.. paper.BottomBarPluginIds ?? [], providerId];
        }

        MarkDirty();
        ReconcilePluginRuntimes();
        RefreshPaperBottomBar(paper.Id);
    }

    private PaperPluginRuntimeBottomBarApi? CreatePluginBottomBar(
        PaperBodyPluginDescriptor descriptor, Func<bool> isActive)
    {
        if (!DeclaresPluginBottomBar(descriptor)) return null;
        var api = new PaperPluginRuntimeBottomBarApi(this, descriptor.Id, isActive);
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
        foreach (var providerId in paper.BottomBarPluginIds ?? [])
        {
            if (!_pluginBottomBars.TryGetValue(providerId, out var api)) continue;
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
    AppController controller, string providerId, Func<bool> isActive) : IPaperPluginBottomBarApi, IDisposable
{
    private readonly Dictionary<string, PaperBottomBarItem[]> _items = new(StringComparer.Ordinal);
    private IReadOnlyList<PaperBottomBarTarget> _targets = controller.GetBottomBarTargets(providerId);
    private Action<PaperActionInvocation>? _action;
    public event Action? TargetsChanged;
    internal string ProviderId => providerId;

    public IReadOnlyList<PaperBottomBarTarget> ListTargets() =>
        OnUi(() => controller.GetBottomBarTargets(providerId));

    public void SetItems(string paperId, IReadOnlyList<PaperBottomBarItem> items) =>
        OnUi(() =>
        {
            var id = paperId?.Trim();
            if (id == null || !controller.GetBottomBarTargets(providerId).Any(target => target.PaperId == id))
                throw new PaperTodoPluginException("bottom_bar_not_bound", "This plugin is not enabled on that paper's bottom bar.");
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
        var current = controller.GetBottomBarTargets(providerId);
        if (current.SequenceEqual(_targets)) return;
        _targets = current;
        var bound = current.Select(target => target.PaperId).ToHashSet(StringComparer.Ordinal);
        foreach (var paperId in _items.Keys.Where(id => !bound.Contains(id)).ToArray())
        {
            _items.Remove(paperId);
            controller.RefreshPaperBottomBar(paperId);
        }

        if (TargetsChanged is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); }
                catch (Exception ex) { Trace.TraceWarning("Bottom bar targets callback failed ({0}): {1}", providerId, ex.GetBaseException()); }
    }

    internal void Invoke(PaperData paper, PaperBottomBarItem item, PaperPopupPosition? position = null)
    {
        if (!item.Clickable || !AppController.HasBottomBarBinding(paper, providerId) ||
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
