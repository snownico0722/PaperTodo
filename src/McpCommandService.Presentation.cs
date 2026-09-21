using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class McpCommandService
{
    private PaperPresentationResult PresentPaper(JsonElement parameters, PaperPresentationAction action)
    {
        var paperId = RequiredString(parameters, "paper_id", 64);
        var activate = OptionalBoolean(parameters, "activate") ?? true;
        return _controller.PresentWorkspacePaper(paperId, action, activate, PaperOperationContext.Mcp());
    }
}
