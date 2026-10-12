using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi
{
    // Native and local Web plugins are trusted. This is the same explicit reload operation as
    // the watcher/MCP entry, not a new permission wall or an alternate state writer.
    public DataReloadResult ReloadData() =>
        Invoke(() => _commands.ReloadData(PaperOperationContext.Plugin(_providerId)));

    public DataReloadStatus GetDataReloadStatus() =>
        Invoke(_commands.GetDataReloadStatus);
}

internal sealed partial class PaperPluginRuntimeWorkspaceApi
{
    public DataReloadResult ReloadData() => OnUi(_inner.ReloadData);
    public DataReloadStatus GetDataReloadStatus() => OnUi(_inner.GetDataReloadStatus);
}
