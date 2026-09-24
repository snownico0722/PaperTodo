using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PaperTodo;

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
    // A failed fast flush gets one delayed retry. A second failure remains dirty until the next
    // real mutation or normal disposal instead of creating a background retry loop.
    private const int ForceSaveMilliseconds = 10_000;

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
    private readonly HashSet<string> _dirtyProviderIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _saveFailureAttempts = new(StringComparer.Ordinal);
    private readonly Timer _saveTimer;
    private readonly Timer _forceSaveTimer;
    private readonly IDurableAtomicFileWriter _atomicWriter;
    private readonly int _saveDebounceMilliseconds;
    private readonly int _forceSaveMilliseconds;
    private bool _suppressFinalFlushOnDispose;
    private bool _disposed;

    public PaperBodyPluginDataStore(string pluginRoot)
        : this(pluginRoot, DurableAtomicFileWriter.Shared)
    {
    }

    internal PaperBodyPluginDataStore(
        string pluginRoot,
        IDurableAtomicFileWriter atomicWriter)
        : this(
            pluginRoot,
            atomicWriter,
            SaveDebounceMilliseconds,
            ForceSaveMilliseconds)
    {
    }

    internal PaperBodyPluginDataStore(
        string pluginRoot,
        IDurableAtomicFileWriter atomicWriter,
        int saveDebounceMilliseconds,
        int forceSaveMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);
        ArgumentNullException.ThrowIfNull(atomicWriter);
        ArgumentOutOfRangeException.ThrowIfNegative(saveDebounceMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(forceSaveMilliseconds);

        DataRoot = Path.Combine(pluginRoot, "data");
        _atomicWriter = atomicWriter;
        _saveDebounceMilliseconds = saveDebounceMilliseconds;
        _forceSaveMilliseconds = forceSaveMilliseconds;
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
        _ = NormalizeStateJson(
            json,
            MaximumPluginRuntimeStateBytes,
            "Plugin Runtime state",
            out var value);

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

            // The 10 MiB limit is a write contract. Existing on-disk state is still readable even
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
        _ = NormalizeStateJson(
            json,
            MaximumPaperStateBytes,
            "Plugin paper state",
            out var value);

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
        RejectActionSettingValue(setting);
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
        RejectActionSettingValue(setting);
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

    private static void RejectActionSettingValue(PaperBodyPluginSettingManifest setting)
    {
        if (setting.Type == "action")
        {
            throw new InvalidOperationException("Action settings are commands, not stored values.");
        }
    }

    public string GetSettingsJson(PaperBodyPluginDescriptor descriptor)
    {
        var settings = (descriptor.Manifest?.Settings ?? [])
            .Where(setting => setting.Type != "action")
            .ToArray();
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
                        _saveFailureAttempts.Remove(providerId);
                    }
                    catch
                    {
                        // One direct cleanup write already failed. Keep it dirty and allow one
                        // delayed retry; another real mutation will reset the retry budget.
                        _dirtyProviderIds.Add(providerId);
                        _saveFailureAttempts[providerId] = 1;
                    }
                }
                catch
                {
                    // One plugin's unreadable data must not block deletion of the paper itself or
                    // cleanup of other plugins. Reads of that plugin still report the original failure.
                }
            }

            UpdateSaveTimersAfterFlush();
        }
    }

    public static string NormalizeStateJson(string? json) =>
        NormalizeStateJson(
            json,
            MaximumPaperStateBytes,
            "Plugin paper state",
            out _);

    internal static string NormalizePluginRuntimeStateJson(string? json) =>
        NormalizeStateJson(
            json,
            MaximumPluginRuntimeStateBytes,
            "Plugin Runtime state",
            out _);

    private static string NormalizeStateJson(
        string? json,
        int maximumBytes,
        string stateName,
        out JsonElement value)
    {
        var normalized = string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > maximumBytes)
        {
            throw new InvalidOperationException(
                $"{stateName} cannot exceed {maximumBytes} UTF-8 bytes.");
        }

        using var parsed = JsonDocument.Parse(normalized);
        value = parsed.RootElement.Clone();
        return normalized;
    }

    private PluginDataDocument Load(string providerId)
    {
        if (_cache.TryGetValue(providerId, out var cached))
        {
            return cached;
        }

        var path = DataPath(providerId);
        PluginDataDocument document;
        try
        {
            document = ReadDocument(path);
        }
        catch (FileNotFoundException)
        {
            document = NewDocument();
        }
        catch (DirectoryNotFoundException)
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
        return providerIds;
    }

    private void ScheduleSave(string providerId)
    {
        var startForceTimer = _dirtyProviderIds.Count == 0;
        _dirtyProviderIds.Add(providerId);
        _saveFailureAttempts[providerId] = 0;
        if (_suppressFinalFlushOnDispose)
        {
            return;
        }

        _saveTimer.Change(_saveDebounceMilliseconds, Timeout.Infinite);
        if (startForceTimer)
        {
            _forceSaveTimer.Change(_forceSaveMilliseconds, Timeout.Infinite);
        }
    }

    private void FlushDirty()
    {
        lock (_gate)
        {
            if (_disposed ||
                _suppressFinalFlushOnDispose ||
                _dirtyProviderIds.Count == 0)
            {
                return;
            }

            foreach (var providerId in _dirtyProviderIds.ToArray())
            {
                var failures = _saveFailureAttempts.GetValueOrDefault(providerId);
                if (failures >= 2)
                {
                    continue;
                }

                try
                {
                    SaveNow(providerId, Load(providerId));
                    _dirtyProviderIds.Remove(providerId);
                    _saveFailureAttempts.Remove(providerId);
                }
                catch
                {
                    // Keep the provider dirty, but spend only one delayed retry without requiring
                    // another mutation. A second failure waits for the next real mutation or exit.
                    _saveFailureAttempts[providerId] = failures + 1;
                }
            }

            UpdateSaveTimersAfterFlush();
        }
    }

    private void UpdateSaveTimersAfterFlush()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_suppressFinalFlushOnDispose ||
            _dirtyProviderIds.Count == 0 ||
            !_dirtyProviderIds.Any(providerId =>
                _saveFailureAttempts.GetValueOrDefault(providerId) < 2))
        {
            _forceSaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        _forceSaveTimer.Change(_forceSaveMilliseconds, Timeout.Infinite);
    }

    private void SaveNow(string providerId, PluginDataDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        _atomicWriter.Write(DataPath(providerId), bytes);
    }

    internal void SuppressFinalFlushOnDispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Waiting for the gate lets any write already inside FlushDirty/SaveNow finish first.
            // Once this flag is set, queued timer callbacks and disposal must not start new writes.
            _suppressFinalFlushOnDispose = true;
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _forceSaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private string DataPath(string providerId) =>
        Path.Combine(DataRoot, providerId + ".json");

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
            if (!_suppressFinalFlushOnDispose)
            {
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
            }
            _disposed = true;
        }
        _saveTimer.Dispose();
        _forceSaveTimer.Dispose();
    }
}
