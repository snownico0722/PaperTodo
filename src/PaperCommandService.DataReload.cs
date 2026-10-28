using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperCommandService
{
    internal DataReloadResult ReloadData(PaperOperationContext origin) =>
        _controller.ReloadDataCore(origin);

    // Opt-in three-way merge with the caller's original snapshot; ReloadData remains authoritative.
    internal DataReloadResult MergeData(PaperOperationContext origin, string? baselineJson = null) =>
        _controller.ReloadDataCore(origin, merge: true, baselineJson: baselineJson);

    internal DataReloadStatus GetDataReloadStatus() =>
        _controller.GetDataReloadStatusCore();
}
