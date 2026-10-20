using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

public sealed partial class StateStore
{
    // Bound to a session, never to AppController's mutable "current store" field.
    // Null for the original single-workspace path and for independent workspaces.
    internal Func<AppState, AppState>? PersistenceProjection { get; set; }
    internal WorkspaceSettingsSnapshot? InheritedSettings { get; set; }
    internal int WorkspaceNumber { get; }
    internal string DocumentStem => Path.GetFileNameWithoutExtension(FilePath);
    internal string ConflictFilePrefix => DocumentStem + "conflict-";

    internal AppState ProjectForPersistence(AppState state) => PersistenceProjection?.Invoke(state) ?? state;

    internal string SerializeRawState(AppState state)
    {
        if (WorkspaceNumber > 1) state.Workspaces = null;
        PrepareForSave(state);
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    private void NormalizeWorkspaceDocument(AppState state)
    {
        if (InheritedSettings == null)
        {
            NormalizeAfterLoad(state);
            return;
        }
        var ownSettings = new AppState();
        WorkspaceSettingsSnapshot.Capture(state).ApplyTo(ownSettings);
        NormalizeGlobalState(ownSettings);
        var independent = WorkspaceSettingsSnapshot.Capture(ownSettings);
        InheritedSettings.ApplyTo(state);
        NormalizeAfterLoad(state);
        // The reader returns a persistable document; the session applies the effective overlay.
        // Its layout was nevertheless normalized against the settings the user will actually use.
        independent.ApplyTo(state);
    }

    /// <summary>
    /// While a numbered workspace is active, update ONLY the main catalog under the main
    /// store's existing writer lock/version fence. Never serialize the numbered AppState here.
    /// Preserve unknown main-document properties as well as its complete paper content.
    /// </summary>
    internal void UpdateWorkspaceConfiguration(WorkspaceConfiguration options, long version)
    {
        if (WorkspaceNumber != 1) throw new InvalidOperationException("Only data.json owns workspace configuration.");
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
                throw new InvalidOperationException("A newer main-workspace save already completed.");
            WriteLifetime.ThrowIfStopped();
            if (OrdinaryWritesPaused) throw new StateWriteBlockedException();
            var bytes = File.ReadAllBytes(FilePath);
            var json = DecodeStateBytes(bytes);
            var document = JsonNode.Parse(json, documentOptions: StateJsonReadPolicy.DocumentOptions)
                as JsonObject ?? throw new JsonException("The main workspace must contain an object.");
            ValidateExternalDocument(document);
            document["workspaces"] = JsonSerializer.SerializeToNode(options.Normalized(), JsonOptions);
            var sourceHash = System.Security.Cryptography.SHA256.HashData(bytes);
            WriteJsonInternal(document.ToJsonString(JsonOptions),
                beforeReplace: () => ValidatePrimaryHash(sourceHash), acceptExternal: true);
            _latestWrittenVersion = version;
        }
        finally { _writeLock.Release(); }
    }
}
