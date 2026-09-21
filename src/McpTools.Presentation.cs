using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace PaperTodo;

internal sealed partial class McpTools
{
    [McpServerTool(Name = "show_paper", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("Show an existing paper without changing its collapsed state. Success is logical state, not animation completion.")]
    public Task<JsonElement> ShowPaper(
        [Description("Exact existing paper ID from list_papers.")] string paper_id,
        [Description("Whether to request activation when showing or expanding.")] bool activate = true,
        CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("show_paper", new { paper_id, activate }, cancellationToken);

    [McpServerTool(Name = "hide_paper", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("Hide an existing paper without deleting it or its content. Success is logical state, not animation completion.")]
    public Task<JsonElement> HidePaper(
        [Description("Exact existing paper ID from list_papers.")] string paper_id,
        CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("hide_paper", new { paper_id }, cancellationToken);

    [McpServerTool(Name = "toggle_paper_visibility", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false)]
    [Description("Toggle visibility of an existing paper. Not safe to retry blindly. Success is logical state, not animation completion.")]
    public Task<JsonElement> TogglePaperVisibility(
        [Description("Exact existing paper ID from list_papers.")] string paper_id,
        [Description("Whether to request activation when showing or expanding.")] bool activate = true,
        CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("toggle_paper_visibility", new { paper_id, activate }, cancellationToken);

    [McpServerTool(Name = "expand_paper", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("Expand and show an existing paper. Success is logical state, not animation completion.")]
    public Task<JsonElement> ExpandPaper(
        [Description("Exact existing paper ID from list_papers.")] string paper_id,
        [Description("Whether to request activation when showing or expanding.")] bool activate = true,
        CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("expand_paper", new { paper_id, activate }, cancellationToken);

    [McpServerTool(Name = "collapse_paper", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("Collapse an existing paper without showing a hidden paper. Requires capsule eligibility. Success is logical state, not animation completion.")]
    public Task<JsonElement> CollapsePaper(
        [Description("Exact existing paper ID from list_papers.")] string paper_id,
        CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("collapse_paper", new { paper_id }, cancellationToken);

    [McpServerTool(Name = "toggle_paper_collapsed", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false)]
    [Description("Toggle collapsed state. Expanding shows the paper; collapsing preserves visibility. Not safe to retry blindly. Success is logical state, not animation completion.")]
    public Task<JsonElement> TogglePaperCollapsed(
        [Description("Exact existing paper ID from list_papers.")] string paper_id,
        [Description("Whether to request activation when showing or expanding.")] bool activate = true,
        CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("toggle_paper_collapsed", new { paper_id, activate }, cancellationToken);

    [McpServerTool(Name = "activate_paper", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("Show and activate an existing paper without forcing it to expand. Success is logical state, not animation completion.")]
    public Task<JsonElement> ActivatePaper(
        [Description("Exact existing paper ID from list_papers.")] string paper_id,
        CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("activate_paper", new { paper_id }, cancellationToken);
}
