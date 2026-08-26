using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PaperTodo;

internal sealed record PaperBodyPluginDataReadIssue(
    string ActivePath,
    bool RecoveredFileExists,
    bool UsingEmptyState,
    string Details);

internal sealed class PaperBodyStoredState
{
    public int Version { get; set; } = 1;
    public string Json { get; set; } = "{}";
}

/// <summary>
/// PaperTodo-managed plugin persistence. One JSON file stores one plugin's global settings and
/// all of its per-paper state. These files are deliberately separate from data.json and its backup.
/// </summary>
internal sealed class PaperBodyPluginDataStore : IDisposable
{
    internal const int MaximumPaperStateBytes = 10 * 1024 * 1024;
    internal const int MaximumPluginRuntimeStateBytes = 20 * 1024 * 1024;
    private const int StorageVersion = 1;
    private const int SaveDebounceMilliseconds = 750;
    private const int ForceSaveMilliseconds = 10_000;
    private const string RecoveredSuffix = ".json.recovered";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class PluginDataDocument
    {
        public int StorageVersion { get; set; } = PaperBodyPluginDataStore.StorageVersion;
        public Dictionary<string, JsonElement> Settings { get; set; } =
            new(StringComparer.Ordinal);
        public PaperDataState? Runtime { get; set; }
        public Dictionary<string, PaperDataState> Papers { get; set; } =
            new(StringComparer.Ordinal);
    }

    private sealed class PaperDataState
    {
        public int StateVersion { get; set; } = 1;
        public JsonElement Data { get; set; } =
            JsonSerializer.SerializeToElement(new { });
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PluginDataDocument> _cache =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _recoveredProviderIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperBodyPluginDataReadIssue> _readIssues =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyProviderIds = new(StringComparer.Ordinal);
    private readonly Timer _saveTimer;
    private readonly Timer _forceSaveTimer;
    private bool _disposed;

    public PaperBodyPluginDataStore(string pluginRoot)
    {
        DataRoot = Path.Combine(pluginRoot, "data");
        _saveTimer = new Timer(
            _ => FlushDirty(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        _forceSaveTimer = new Timer(
            _ => FlushDirty(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public string DataRoot { get; }

    public PaperBodyStoredState ReadRuntimeState(string providerId) =>
        TryReadRuntimeState(providerId, out var state)
            ? state
            : new PaperBodyStoredState();

    public bool TryReadRuntimeState(
        string providerId,
        out PaperBodyStoredState state)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(providerId);
            var stored = document.Runtime;
            if (stored == null || stored.Data.ValueKind == JsonValueKind.Undefined)
            {
                state = new PaperBodyStoredState();
                return false;
            }
            state = new PaperBodyStoredState
            {
                Version = Math.Max(1, stored.StateVersion),
                Json = stored.Data.GetRawText()
            };
            return true;
        }
    }

    public void SaveRuntimeState(
        string providerId,
        int stateVersion,
        string? json)
    {
        var normalized = NormalizePluginRuntimeStateJson(json);
        using var parsed = JsonDocument.Parse(normalized);
        var value = parsed.RootElement.Clone();

        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(providerId);
            stateVersion = Math.Max(1, stateVersion);
            if (document.Runtime is { } existing &&
                existing.StateVersion == stateVersion &&
                JsonElementEquals(existing.Data, value))
            {
                return;
            }
            document.Runtime = new PaperDataState
            {
                StateVersion = stateVersion,
                Data = value
            };
            ScheduleSave(providerId);
        }
    }

    public PaperBodyStoredState ReadPaperState(string providerId, string paperId) =>
        TryReadPaperState(providerId, paperId, out var state)
            ? state
            : new PaperBodyStoredState();

    public bool TryReadPaperState(
        string providerId,
        string paperId,
        out PaperBodyStoredState state)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(providerId);
            if (!document.Papers.TryGetValue(paperId, out var stored) ||
                stored == null ||
                stored.Data.ValueKind == JsonValueKind.Undefined)
            {
                state = new PaperBodyStoredState();
                return false;
            }

            // The 1 MiB limit is a write contract. Existing on-disk state is still readable even
            // when it came from an older host or was edited outside PaperTodo.
            state = new PaperBodyStoredState
            {
                Version = Math.Max(1, stored.StateVersion),
                Json = stored.Data.GetRawText()
            };
            return true;
        }
    }

    public void SavePaperState(
        string providerId,
        string paperId,
        int stateVersion,
        string? json)
    {
        var normalized = NormalizeStateJson(json);
        using var parsed = JsonDocument.Parse(normalized);
        var value = parsed.RootElement.Clone();

        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(providerId);
            stateVersion = Math.Max(1, stateVersion);
            if (document.Papers.TryGetValue(paperId, out var existing) &&
                existing != null &&
                existing.StateVersion == stateVersion &&
                JsonElementEquals(existing.Data, value))
            {
                return;
            }

            document.Papers[paperId] = new PaperDataState
            {
                StateVersion = stateVersion,
                Data = value
            };
            ScheduleSave(providerId);
        }
    }

    public JsonElement GetSettingValue(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(descriptor.Id);
            if (document.Settings.TryGetValue(setting.Id, out var stored))
            {
                return PaperBodyPluginRegistry.NormalizeSettingValue(setting, stored);
            }
            return PaperBodyPluginRegistry.DefaultSettingValue(setting);
        }
    }

    public JsonElement SetSettingValue(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginSettingManifest setting,
        JsonElement value)
    {
        var normalized = PaperBodyPluginRegistry.NormalizeSettingValue(setting, value);
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(descriptor.Id);
            if (document.Settings.TryGetValue(setting.Id, out var existing) &&
                JsonElementEquals(existing, normalized))
            {
                return normalized;
            }

            document.Settings[setting.Id] = normalized.Clone();
            ScheduleSave(descriptor.Id);
            return normalized;
        }
    }

    public string GetSettingsJson(PaperBodyPluginDescriptor descriptor)
    {
        var settings = descriptor.Manifest?.Settings ?? [];
        if (settings.Length == 0)
        {
            return "{}";
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var document = Load(descriptor.Id);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var setting in settings)
            {
                values[setting.Id] = document.Settings.TryGetValue(setting.Id, out var stored)
                    ? PaperBodyPluginRegistry.NormalizeSettingValue(setting, stored)
                    : PaperBodyPluginRegistry.DefaultSettingValue(setting);
            }
            return JsonSerializer.Serialize(values, JsonOptions);
        }
    }

    public bool TryGetReadIssue(
        string providerId,
        out PaperBodyPluginDataReadIssue issue)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _ = Load(providerId);
            return _readIssues.TryGetValue(providerId, out issue!);
        }
    }

    public void RemovePaperStateEverywhere(string paperId)
    {
        if (string.IsNullOrWhiteSpace(paperId))
        {
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var providerId in EnumerateStoredProviderIds())
            {
                try
                {
                    var document = Load(providerId);
                    if (!document.Papers.Remove(paperId))
                    {
                        continue;
                    }

                    try
                    {
                        SaveNow(providerId, document);
                        _dirtyProviderIds.Remove(providerId);
                    }
                    catch
                    {
                        // Keep the in-memory deletion dirty so the normal retry path can finish it.
                        _dirtyProviderIds.Add(providerId);
                    }
                }
                catch
                {
                    // One plugin's unreadable data must not block deletion of the paper itself or
                    // cleanup of other plugins. Load records the problem for the plugin page.
                }
            }

            UpdateSaveTimersAfterFlush();
        }
    }

    public static string NormalizeStateJson(string? json) =>
        NormalizeStateJson(
            json,
            MaximumPaperStateBytes,
            "Plugin paper state");

    internal static string NormalizePluginRuntimeStateJson(string? json) =>
        NormalizeStateJson(
            json,
            MaximumPluginRuntimeStateBytes,
            "Plugin Runtime state");

    private static string NormalizeStateJson(
        string? json,
        int maximumBytes,
        string stateName)
    {
        var normalized = string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > maximumBytes)
        {
            throw new InvalidOperationException(
                $"{stateName} cannot exceed {maximumBytes} UTF-8 bytes.");
        }

        using (JsonDocument.Parse(normalized))
        {
        }
        return normalized;
    }

    private PluginDataDocument Load(string providerId)
    {
        if (_cache.TryGetValue(providerId, out var cached))
        {
            return cached;
        }

        PluginDataDocument document;
        var primaryPath = DataPath(providerId);
        var recoveredPath = RecoveredDataPath(providerId);
        if (File.Exists(recoveredPath))
        {
            _recoveredProviderIds.Add(providerId);
            try
            {
                document = ReadDocument(recoveredPath);
                _readIssues[providerId] = new PaperBodyPluginDataReadIssue(
                    recoveredPath,
                    RecoveredFileExists: true,
                    UsingEmptyState: false,
                    Details: "");
            }
            catch (Exception ex)
            {
                document = NewDocument();
                _readIssues[providerId] = new PaperBodyPluginDataReadIssue(
                    recoveredPath,
                    RecoveredFileExists: true,
                    UsingEmptyState: true,
                    ex.GetBaseException().Message);
            }
        }
        else if (File.Exists(primaryPath))
        {
            try
            {
                document = ReadDocument(primaryPath);
            }
            catch (Exception ex)
            {
                // Preserve the unreadable original. This process runs from an empty document and
                // all later writes go to the single stable .recovered file.
                document = NewDocument();
                _recoveredProviderIds.Add(providerId);
                _readIssues[providerId] = new PaperBodyPluginDataReadIssue(
                    recoveredPath,
                    RecoveredFileExists: false,
                    UsingEmptyState: true,
                    ex.GetBaseException().Message);
            }
        }
        else
        {
            document = NewDocument();
        }

        _cache.Add(providerId, document);
        return document;
    }

    private static PluginDataDocument ReadDocument(string path)
    {
        var document = JsonSerializer.Deserialize<PluginDataDocument>(
            File.ReadAllText(path),
            JsonOptions)
            ?? throw new InvalidDataException("Plugin data deserialized to null.");
        if (document.StorageVersion != StorageVersion)
        {
            throw new InvalidDataException(
                $"Unsupported plugin data storage version {document.StorageVersion}; expected {StorageVersion}.");
        }

        document.Settings ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        document.Papers ??= new Dictionary<string, PaperDataState>(StringComparer.Ordinal);
        return document;
    }

    private static PluginDataDocument NewDocument() => new();

    private IEnumerable<string> EnumerateStoredProviderIds()
    {
        var providerIds = new HashSet<string>(_cache.Keys, StringComparer.Ordinal);
        if (!Directory.Exists(DataRoot))
        {
            return providerIds;
        }

        foreach (var path in Directory.EnumerateFiles(
                     DataRoot,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            providerIds.Add(Path.GetFileNameWithoutExtension(path));
        }
        foreach (var path in Directory.EnumerateFiles(
                     DataRoot,
                     "*" + RecoveredSuffix,
                     SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(RecoveredSuffix, StringComparison.OrdinalIgnoreCase))
            {
                providerIds.Add(fileName[..^RecoveredSuffix.Length]);
            }
        }
        return providerIds;
    }

    private void ScheduleSave(string providerId)
    {
        var startForceTimer = _dirtyProviderIds.Count == 0;
        _dirtyProviderIds.Add(providerId);
        _saveTimer.Change(SaveDebounceMilliseconds, Timeout.Infinite);
        if (startForceTimer)
        {
            _forceSaveTimer.Change(ForceSaveMilliseconds, Timeout.Infinite);
        }
    }

    private void FlushDirty()
    {
        lock (_gate)
        {
            if (_disposed || _dirtyProviderIds.Count == 0)
            {
                return;
            }

            foreach (var providerId in _dirtyProviderIds.ToArray())
            {
                try
                {
                    SaveNow(providerId, Load(providerId));
                    _dirtyProviderIds.Remove(providerId);
                }
                catch
                {
                    // Keep the provider dirty. The timers below retry without requiring another
                    // plugin mutation.
                }
            }

            UpdateSaveTimersAfterFlush();
        }
    }

    private void UpdateSaveTimersAfterFlush()
    {
        if (_dirtyProviderIds.Count == 0)
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _forceSaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        _saveTimer.Change(SaveDebounceMilliseconds, Timeout.Infinite);
        _forceSaveTimer.Change(ForceSaveMilliseconds, Timeout.Infinite);
    }

    private void SaveNow(string providerId, PluginDataDocument document)
    {
        Directory.CreateDirectory(DataRoot);
        var useRecovered = _recoveredProviderIds.Contains(providerId);
        var path = useRecovered
            ? RecoveredDataPath(providerId)
            : DataPath(providerId);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(tempPath, path, overwrite: true);

        if (useRecovered)
        {
            _readIssues.TryGetValue(providerId, out var existingIssue);
            _readIssues[providerId] = new PaperBodyPluginDataReadIssue(
                path,
                RecoveredFileExists: true,
                UsingEmptyState: false,
                existingIssue?.Details ?? "");
        }
    }

    private string DataPath(string providerId) =>
        Path.Combine(DataRoot, providerId + ".json");

    private string RecoveredDataPath(string providerId) =>
        Path.Combine(DataRoot, providerId + RecoveredSuffix);

    private static bool JsonElementEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Undefined ||
            right.ValueKind == JsonValueKind.Undefined)
        {
            return left.ValueKind == right.ValueKind;
        }

        return left.ValueKind == right.ValueKind &&
            string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _forceSaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            foreach (var providerId in _dirtyProviderIds.ToArray())
            {
                try
                {
                    SaveNow(providerId, Load(providerId));
                    _dirtyProviderIds.Remove(providerId);
                }
                catch
                {
                }
            }
            _disposed = true;
        }
        _saveTimer.Dispose();
        _forceSaveTimer.Dispose();
    }
}
