using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// Provider-scoped backend state. This is deliberately separate from per-Paper Body state: one
/// Runtime owns one document and may keep N logical Paper instances inside it.
/// </summary>
internal sealed class PaperAppRuntimeStateApi : IPaperPluginRuntimeState
{
    private readonly PaperBodyPluginDataStore _dataStore;
    private readonly PaperBodyPluginDescriptor _descriptor;
    private readonly Func<bool> _isActive;

    public PaperAppRuntimeStateApi(
        PaperBodyPluginDataStore dataStore,
        PaperBodyPluginDescriptor descriptor,
        Func<bool> isActive)
    {
        _dataStore = dataStore;
        _descriptor = descriptor;
        _isActive = isActive;
    }

    public string Json => Read().Json;
    public int StateVersion => Read().Version;
    public int TargetStateVersion => _descriptor.StateVersion;

    public void Save(string json)
    {
        EnsureActive();
        _dataStore.SaveRuntimeState(
            _descriptor.Id,
            _descriptor.StateVersion,
            json);
    }

    private PaperBodyStoredState Read()
    {
        EnsureActive();
        return _dataStore.ReadRuntimeState(_descriptor.Id);
    }

    private void EnsureActive()
    {
        if (!_isActive())
        {
            throw new PaperTodoPluginException(
                "runtime_closed",
                "The plugin Runtime is no longer active.");
        }
    }
}
