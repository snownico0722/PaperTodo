using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperCommandService
{
    internal DataReloadResult ReloadData(PaperOperationContext origin) =>
        _controller.ReloadDataCore(origin);

    internal DataReloadStatus GetDataReloadStatus() =>
        _controller.GetDataReloadStatusCore();
}
