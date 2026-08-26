using System.Text.Json;

namespace PaperTodo.Plugin;

/// <summary>
/// Host-managed persistent state for one plugin Runtime. Runtime state is provider-scoped rather
/// than paper-scoped; a multi-instance plugin stores its own instances keyed by PaperId inside this
/// single JSON document. Body/Mini state remains separate and belongs to the frontend session.
/// </summary>
public interface IPaperPluginRuntimeState
{
    string Json { get; }
    int StateVersion { get; }
    int TargetStateVersion { get; }
    void Save(string json);
}

/// <summary>
/// One real Paper currently using the plugin provider. Runtime is single-instance per provider;
/// these snapshots are logical frontend instances, not separate background runtimes.
/// </summary>
public sealed record PaperPluginRuntimePaper(string PaperId);

public enum PaperPluginRuntimeEventKind
{
    PaperAdded,
    PaperRemoved,
    Message
}

/// <summary>
/// Provider-runtime event. Message is populated only for Message events and is cloned by the host,
/// so handlers may retain it beyond the callback.
/// </summary>
public sealed record PaperPluginRuntimeEvent(
    PaperPluginRuntimeEventKind Kind,
    string PaperId,
    JsonElement? Message = null);

/// <summary>
/// Provider-scoped view of the plugin's own Paper instances. It deliberately does not create one
/// runtime per Paper: presentation is addressed by PaperId while one plugin Runtime owns the
/// backend. Plugins needing extra workers/processes create and manage those internally.
/// </summary>
public interface IPaperPluginRuntimePapers
{
    IReadOnlyList<PaperPluginRuntimePaper> List();
    PaperPluginRuntimePaper? Get(string paperId);

    void SetTitle(string paperId, string title);
    void SetHeaderText(string paperId, string text);
    void SetCapsulePresentation(string paperId, PaperCapsulePresentation? presentation);

    bool PostToBody(string paperId, JsonElement message);
    IDisposable Subscribe(Action<PaperPluginRuntimeEvent> handler);
}

/// <summary>
/// Body/Mini-to-Runtime command endpoint for the Paper carrying this frontend. IsAvailable is false
/// when the provider does not declare runtime or its Runtime is not currently able to accept a
/// command. Post returns false rather than inventing a second fallback backend.
/// </summary>
public interface IPaperPluginRuntimeClient
{
    bool IsAvailable { get; }
    bool Post(JsonElement message);
}
