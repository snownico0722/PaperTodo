using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi
{
    // A data import can replace an entire workspace. Both plugin routes need the
    // dedicated high-risk grant; MCP retains its existing full-write permission gate.
    public DataReloadResult ReloadData()
    {
        Require(PaperTodoPermissionNames.DataImport);
        return Invoke(() => _commands.ReloadData(PaperOperationContext.Plugin(_providerId)));
    }

    public DataReloadResult MergeData(string baselineJson)
    {
        Require(PaperTodoPermissionNames.DataImport);
        return Invoke(() => _commands.MergeData(PaperOperationContext.Plugin(_providerId), baselineJson));
    }

    public DataReloadStatus GetDataReloadStatus() =>
        Invoke(_commands.GetDataReloadStatus);
}

internal sealed partial class PaperPluginRuntimeWorkspaceApi
{
    public DataReloadResult ReloadData() => OnUi(_inner.ReloadData);
    public DataReloadResult MergeData(string baselineJson) => OnUi(() => _inner.MergeData(baselineJson));
    public DataReloadStatus GetDataReloadStatus() => OnUi(_inner.GetDataReloadStatus);
}
