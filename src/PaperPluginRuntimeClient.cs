using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// Thin frontend-to-backend command route. It never creates a fallback per-Paper runtime.
/// </summary>
internal sealed class PaperPluginRuntimeClient : IPaperPluginRuntimeClient
{
    private readonly AppController _controller;
    private readonly string _paperId;
    private readonly string _providerId;
    private readonly Func<bool> _isCurrent;

    public PaperPluginRuntimeClient(
        AppController controller,
        string paperId,
        string providerId,
        Func<bool> isCurrent)
    {
        _controller = controller;
        _paperId = paperId;
        _providerId = providerId;
        _isCurrent = isCurrent;
    }

    public bool IsAvailable =>
        _isCurrent() &&
        _controller.CanPostBodyMessageToPluginRuntime(_paperId, _providerId);

    public bool Post(JsonElement message) =>
        _isCurrent() &&
        _controller.PostBodyMessageToPluginRuntime(
            _paperId,
            _providerId,
            message.Clone());
}
