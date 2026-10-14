using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperCommandService
{
    internal DataReloadResult ReloadData(PaperOperationContext origin) =>
        _controller.ReloadDataCore(origin);

    // Experimental #349-style three-way merge, opt-in; ordinary ReloadData remains authoritative.
    internal DataReloadResult MergeData(PaperOperationContext origin) =>
        _controller.ReloadDataCore(origin, merge: true);

    internal DataReloadStatus GetDataReloadStatus() =>
        _controller.GetDataReloadStatusCore();
}
