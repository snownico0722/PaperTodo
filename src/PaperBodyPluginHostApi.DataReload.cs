using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi
{
    // Native and local Web plugins share one explicit import entry with MCP.
    // It does not create a second state writer or add a new permission gate.
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
