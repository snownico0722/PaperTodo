using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PaperTodo;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Tolerate unknown properties so configs written by older / experimental builds (e.g.
        // retired deepCapsuleDock* fields) still load instead of crashing on startup. The Strict
        // preset otherwise sets Disallow, which makes any removed/renamed field fatal.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "data.json");
    public string BackupPath { get; } = Path.Combine(AppContext.BaseDirectory, "data.backup.json");
    private bool _preserveRecoveredLoadFilesOnNextSave;
    private string? _preservedFailedPrimaryPath;
    private string? _preservedRecoveryBackupPath;

    public AppState Load()
    {
        ClearRecoveredLoadPreservationState();

        bool mainExists = File.Exists(FilePath);
        bool backupExists = File.Exists(BackupPath);

        if (!mainExists && !backupExists)
        {
            return new AppState();
        }

        Exception? mainEx = null;
        if (mainExists)
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                if (state != null)
                {
                    NormalizeAfterLoad(state);
                    return state;
                }

                mainEx = new InvalidDataException($"{Path.GetFileName(FilePath)} deserialized to null.");
            }
            catch (Exception ex)
            {
                mainEx = ex;
            }
        }

        Exception? backupEx = null;
        if (backupExists)
        {
            try
            {
                var json = File.ReadAllText(BackupPath);
                var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                if (state != null)
                {
                    if (mainExists)
                    {
                        _preserveRecoveredLoadFilesOnNextSave = true;
                    }

                    NormalizeAfterLoad(state);
                    return state;
                }

                backupEx = new InvalidDataException($"{Path.GetFileName(BackupPath)} deserialized to null.");
            }
            catch (Exception ex)
            {
                backupEx = ex;
            }
        }

        var innerEx = mainEx ?? backupEx;
        throw new InvalidOperationException(
            Strings.Get("StateLoadFailed"),
            innerEx);
    }

    internal bool TryCollectProtectedImageIds(
        AppState currentState,
        out HashSet<string> protectedImageIds)
    {
        protectedImageIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // The failed primary has not been copied to its recovery path yet. Until the first
            // successful save completes, no image can be proven unreachable.
            if (_preserveRecoveredLoadFilesOnNextSave ||
                !TryAddImageReferences(currentState, protectedImageIds))
            {
                protectedImageIds.Clear();
                return false;
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasPersistedState = FileExistsForCollection(FilePath);
            AddIfExists(paths, BackupPath);
            AddIfExists(paths, FilePath + ".tmp");

            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = AppContext.BaseDirectory;
            }

            AddIfExists(paths, Path.Combine(directory, "data.crash_recovery.json"));
            foreach (var path in Directory.EnumerateFiles(directory, "data.failed_load.*.json"))
            {
                paths.Add(path);
            }
            foreach (var path in Directory.EnumerateFiles(directory, "data.backup.used_for_recovery.*.json"))
            {
                paths.Add(path);
            }

            hasPersistedState |= paths.Count > 0;
            if (!hasPersistedState)
            {
                protectedImageIds.Clear();
                return false;
            }

            foreach (var path in paths)
            {
                var json = File.ReadAllText(path);
                var snapshot = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                if (snapshot == null || !TryAddImageReferences(snapshot, protectedImageIds))
                {
                    protectedImageIds.Clear();
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // Image collection is destructive. An unreadable or malformed recovery snapshot
            // therefore disables the whole collection pass instead of guessing reachability.
            protectedImageIds.Clear();
            return false;
        }
    }

    private static void AddIfExists(HashSet<string> paths, string path)
    {
        if (FileExistsForCollection(path))
        {
            paths.Add(path);
        }
    }

    private static bool FileExistsForCollection(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException($"Expected a state file at '{path}'.");
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool TryAddImageReferences(AppState state, HashSet<string> imageIds)
    {
        if (state.Papers == null)
        {
            return false;
        }

        foreach (var paper in state.Papers)
        {
            if (paper == null)
            {
                return false;
            }

            var content = MarkdownImageReferences.StripRenderMarkers(paper.Content ?? "");
            foreach (var imageId in MarkdownImageReferences.CollectImageIds(content))
            {
                imageIds.Add(imageId);
            }
        }

        return true;
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _latestWrittenVersion = 0;

    public string SerializeState(AppState state)
    {
        PrepareForSave(state);
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    public void SaveJsonSync(string json, long version)
    {
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
            {
                return;
            }
            WriteJsonInternal(json);
            _latestWrittenVersion = version;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveJsonAsync(string json, long version)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (version < _latestWrittenVersion)
            {
                return;
            }
            await Task.Run(() => WriteJsonInternal(json));
            _latestWrittenVersion = version;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void WriteJsonInternal(string json)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var skipBackupRotation = PreserveRecoveredLoadFilesIfNeeded();
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);

        try
        {
            if (!skipBackupRotation && File.Exists(FilePath))
            {
                File.Copy(FilePath, BackupPath, overwrite: true);
            }
        }
        catch
        {
            // Backup failure should not block normal saving.
        }

        File.Move(tempPath, FilePath, overwrite: true);

        if (skipBackupRotation)
        {
            ClearRecoveredLoadPreservationState();
        }
    }

    private bool PreserveRecoveredLoadFilesIfNeeded()
    {
        if (!_preserveRecoveredLoadFilesOnNextSave)
        {
            return false;
        }

        if (File.Exists(FilePath) && string.IsNullOrWhiteSpace(_preservedFailedPrimaryPath))
        {
            _preservedFailedPrimaryPath = CopyRecoverySource(FilePath, "failed_load");
        }

        if (File.Exists(BackupPath) && string.IsNullOrWhiteSpace(_preservedRecoveryBackupPath))
        {
            _preservedRecoveryBackupPath = CopyRecoverySource(BackupPath, "used_for_recovery");
        }

        return true;
    }

    private static string CopyRecoverySource(string sourcePath, string suffix)
    {
        var targetPath = UniqueRecoveryCopyPath(sourcePath, suffix);
        File.Copy(sourcePath, targetPath, overwrite: false);
        return targetPath;
    }

    private static string UniqueRecoveryCopyPath(string sourcePath, string suffix)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var candidate = Path.Combine(directory, $"{stem}.{suffix}.{stamp}{extension}");
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{stem}.{suffix}.{stamp}.{i}{extension}");
        }

        return candidate;
    }

    private void ClearRecoveredLoadPreservationState()
    {
        _preserveRecoveredLoadFilesOnNextSave = false;
        _preservedFailedPrimaryPath = null;
        _preservedRecoveryBackupPath = null;
    }

    private static void PrepareForSave(AppState state)
    {
        // Runtime commands own business invariants. Saving only repairs values that could make
        // serialization fail; it must not rebuild collections, migrate settings, or change links.
        state.Papers ??= new List<PaperData>();
        RemoveNullEntriesInPlace(state.Papers);

        state.CapsuleCollapseAllActiveQueues ??= new Dictionary<string, bool>();
        state.GlobalHotkeys ??= new Dictionary<string, string>();
        state.GlobalHotkeyEnabled ??= new Dictionary<string, bool>();
        state.DeepCapsuleQueueStartTopMargins ??= new Dictionary<string, double>();
        RemoveNonFiniteValues(state.DeepCapsuleQueueStartTopMargins);

        if (!IsFinite(state.Zoom))
        {
            state.Zoom = 1.0;
        }
        if (!IsFinite(state.DeepCapsuleStartTopMargin))
        {
            state.DeepCapsuleStartTopMargin = EdgeCapsuleLayout.StartTopMargin;
        }

        foreach (var paper in state.Papers)
        {
            paper.Items ??= new List<PaperItem>();
            RemoveNullEntriesInPlace(paper.Items);

            paper.Content ??= "";
            paper.X = NormalizeCoordinate(paper.X, 120);
            paper.Y = NormalizeCoordinate(paper.Y, 120);
            if (!IsFinite(paper.TextZoom))
            {
                paper.TextZoom = 1.0;
            }

            var defaultWidth = paper.Type == PaperTypes.Note
                ? PaperLayoutDefaults.NoteDefaultWidth
                : PaperLayoutDefaults.TodoDefaultWidth;
            var defaultHeight = paper.Type == PaperTypes.Note
                ? PaperLayoutDefaults.NoteDefaultHeight
                : PaperLayoutDefaults.TodoDefaultHeight;
            if (!IsFinite(paper.Width))
            {
                paper.Width = defaultWidth;
            }
            if (!IsFinite(paper.Height))
            {
                paper.Height = defaultHeight;
            }

            if ((paper.DeepCapsuleExpandedX.HasValue && !IsFinite(paper.DeepCapsuleExpandedX.Value)) ||
                (paper.DeepCapsuleExpandedY.HasValue && !IsFinite(paper.DeepCapsuleExpandedY.Value)) ||
                (paper.DeepCapsuleExpandedWidth.HasValue && !IsFinite(paper.DeepCapsuleExpandedWidth.Value)) ||
                (paper.DeepCapsuleExpandedHeight.HasValue && !IsFinite(paper.DeepCapsuleExpandedHeight.Value)))
            {
                ClearDeepCapsuleExpandedGeometry(paper);
            }

            foreach (var item in paper.Items)
            {
                item.Text ??= "";
            }
        }
    }

    private static void RemoveNonFiniteValues(Dictionary<string, double> values)
    {
        List<string>? invalidKeys = null;
        foreach (var (key, value) in values)
        {
            if (!IsFinite(value))
            {
                invalidKeys ??= new List<string>();
                invalidKeys.Add(key);
            }
        }

        if (invalidKeys == null)
        {
            return;
        }

        foreach (var key in invalidKeys)
        {
            values.Remove(key);
        }
    }

    private static void NormalizeAfterLoad(AppState state)
    {
        state.Papers ??= new List<PaperData>();
        RemoveNullEntriesInPlace(state.Papers);

        NormalizeGlobalState(state);
        NormalizePapers(state);
        NormalizeLinks(state);
    }

    private static void NormalizeGlobalState(AppState state)
    {
        if (state.Theme is not ("system" or "light" or "dark"))
        {
            state.Theme = "system";
        }

        state.ColorScheme = ColorSchemes.Normalize(state.ColorScheme);

        if (!MarkdownRenderModes.IsValid(state.MarkdownRenderMode))
        {
            state.MarkdownRenderMode = MarkdownRenderModes.Enhanced;
        }

        state.ExternalMarkdownExtension = ExternalMarkdownFileExtensions.Normalize(state.ExternalMarkdownExtension);
        state.FullscreenTopmostMode = FullscreenTopmostModes.Normalize(state.FullscreenTopmostMode);
        state.DeepCapsuleSide = DeepCapsuleSides.Normalize(state.DeepCapsuleSide);
        state.DeepCapsuleMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(state.DeepCapsuleMonitorDeviceName);
        state.TodoVisualSize = TodoVisualSizes.Normalize(state.TodoVisualSize);
        state.UiFontPreset = UiFontPresets.Normalize(state.UiFontPreset);
        state.TopBarHeight = 0;

        if (state.ShowTopBarNewPaperButtons is bool showTopBarNewPaperButtons)
        {
            state.ShowTopBarNewTodoButton = showTopBarNewPaperButtons;
            state.ShowTopBarNewNoteButton = showTopBarNewPaperButtons;
            state.ShowTopBarNewPaperButtons = null;
        }

        if (double.IsNaN(state.Zoom) || double.IsInfinity(state.Zoom) || state.Zoom <= 0)
        {
            state.Zoom = 1.0;
        }
        state.Zoom = Math.Clamp(state.Zoom, 0.5, 1.5);

        if (!state.UseCapsuleMode)
        {
            state.UseDeepCapsuleMode = false;
        }

        state.MaxTitleLength = PaperTitles.NormalizeMaxTitleLength(state.MaxTitleLength);
        state.DeepCapsuleTitleMeasureCharacterLimit = Math.Clamp(state.DeepCapsuleTitleMeasureCharacterLimit, 0, PaperTitles.MaxConfigurableTitleLength);
        state.GlobalHotkeys = GlobalShortcutCatalog.NormalizeBindings(state.GlobalHotkeys);
        state.GlobalHotkeyEnabled = GlobalShortcutCatalog.NormalizeEnabled(
            state.GlobalHotkeyEnabled,
            state.GlobalHotkeys);

        if (!state.UseCapsuleMode || !state.UseDeepCapsuleMode)
        {
            state.UseCapsuleCollapseAll = false;
        }

        if (!state.UseCapsuleCollapseAll)
        {
            state.CapsuleCollapseAllActive = false;
        }
        state.CapsuleCollapseAllActiveQueues ??= new Dictionary<string, bool>();
        state.CapsuleCollapseAllActiveQueues = NormalizeCollapseAllActiveQueues(state.CapsuleCollapseAllActiveQueues);
        if (!state.UseCapsuleCollapseAll)
        {
            state.CapsuleCollapseAllActiveQueues.Clear();
        }
        else
        {
            foreach (var key in state.CapsuleCollapseAllActiveQueues.Keys.ToList())
            {
                if (!state.CapsuleCollapseAllActiveQueues[key])
                {
                    state.CapsuleCollapseAllActiveQueues.Remove(key);
                }
            }
            if (state.CapsuleCollapseAllActiveQueues.Count > 0)
            {
                state.CapsuleCollapseAllActive = true;
            }
        }

        var keepDeepCapsuleStartTopMargins = state.UseCapsuleMode && state.UseDeepCapsuleMode && state.UseCapsuleCollapseAll;
        state.DeepCapsuleStartTopMargin = keepDeepCapsuleStartTopMargins
            ? NormalizeDeepCapsuleStartTopMargin(state.DeepCapsuleStartTopMargin, state.DeepCapsuleMonitorDeviceName)
            : EdgeCapsuleLayout.StartTopMargin;

        // Per-queue margins: drop NaN/inf; final clamping against each queue's live work area is
        // done at layout time (monitor set can change between sessions, so we don't over-normalize
        // here). A null dict (older config) becomes empty => every queue falls back to the global.
        state.DeepCapsuleQueueStartTopMargins ??= new Dictionary<string, double>();
        state.DeepCapsuleQueueStartTopMargins = NormalizeQueueStartTopMargins(state.DeepCapsuleQueueStartTopMargins);
        if (!keepDeepCapsuleStartTopMargins)
        {
            state.DeepCapsuleQueueStartTopMargins.Clear();
        }
        else
        {
            foreach (var key in state.DeepCapsuleQueueStartTopMargins.Keys.ToList())
            {
                var v = state.DeepCapsuleQueueStartTopMargins[key];
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    state.DeepCapsuleQueueStartTopMargins.Remove(key);
                }
            }
        }
    }

    private static void NormalizePapers(AppState state)
    {
        var usedPaperIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paper in state.Papers)
        {
            if (string.IsNullOrWhiteSpace(paper.Id) || !usedPaperIds.Add(paper.Id))
            {
                paper.Id = NewUniqueId(usedPaperIds);
            }

            if (paper.Type != PaperTypes.Note && paper.Type != PaperTypes.Todo)
            {
                paper.Type = PaperTypes.Todo;
            }

            // Per-paper queue identity. Migration: a capsule with no explicit side inherits the
            // legacy single global anchor, so existing docked capsules keep their current edge /
            // monitor. New papers default to the global side until dragged to a queue.
            paper.CapsuleSide = string.IsNullOrWhiteSpace(paper.CapsuleSide)
                ? state.DeepCapsuleSide
                : DeepCapsuleSides.Normalize(paper.CapsuleSide);
            var capsuleMonitorDeviceName = string.IsNullOrWhiteSpace(paper.CapsuleMonitorDeviceName)
                ? (state.DeepCapsuleMonitorDeviceName ?? "")
                : paper.CapsuleMonitorDeviceName.Trim();
            paper.CapsuleMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(capsuleMonitorDeviceName);
            NormalizeDeepCapsuleExpandedGeometry(paper);

            paper.Title = PaperTitles.CleanCustomTitle(paper.Title, state.MaxTitleLength);
            paper.X = NormalizeCoordinate(paper.X, 120);
            paper.Y = NormalizeCoordinate(paper.Y, 120);
            if (double.IsNaN(paper.TextZoom) || double.IsInfinity(paper.TextZoom) || paper.TextZoom <= 0)
            {
                paper.TextZoom = 1.0;
            }
            paper.TextZoom = Math.Clamp(Math.Round(paper.TextZoom, 1), 0.5, 1.5);

            paper.Width = NormalizePaperDimension(
                paper.Width,
                paper.Type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultWidth : PaperLayoutDefaults.TodoDefaultWidth,
                PaperLayoutDefaults.MinWidth);
            paper.Height = NormalizePaperDimension(
                paper.Height,
                paper.Type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultHeight : PaperLayoutDefaults.TodoDefaultHeight,
                PaperLayoutDefaults.MinHeight);

            paper.Items ??= new List<PaperItem>();
            RemoveNullEntriesInPlace(paper.Items);
            paper.Content ??= "";
            if (!state.UseCapsuleMode)
            {
                paper.IsCollapsed = false;
            }

            var usedItemIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < paper.Items.Count; i++)
            {
                var item = paper.Items[i];
                if (string.IsNullOrWhiteSpace(item.Id) || !usedItemIds.Add(item.Id))
                {
                    item.Id = NewUniqueId(usedItemIds);
                }

                item.Order = i;
                item.Text ??= "";
            }
        }
    }

    private static void NormalizeLinks(AppState state)
    {
        var noteIds = state.Papers
            .Where(p => p.Type == PaperTypes.Note)
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in state.Papers.SelectMany(p => p.Items))
        {
            if (string.IsNullOrWhiteSpace(item.LinkedNoteId) ||
                !noteIds.Contains(item.LinkedNoteId))
            {
                item.LinkedNoteId = null;
            }
        }

        if (state.EnableTodoNoteLinks && state.HideLinkedNotesFromCapsules)
        {
            var linkedNoteIds = state.Papers
                .Where(p => p.Type == PaperTypes.Todo)
                .SelectMany(p => p.Items)
                .Select(item => item.LinkedNoteId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var note in state.Papers.Where(p => p.Type == PaperTypes.Note && linkedNoteIds.Contains(p.Id)))
            {
                note.IsCollapsed = false;
            }
        }
    }

    private static void RemoveNullEntriesInPlace<T>(List<T> items)
        where T : class
    {
        var writeIndex = 0;
        for (var readIndex = 0; readIndex < items.Count; readIndex++)
        {
            var item = items[readIndex];
            if (item == null)
            {
                continue;
            }

            if (writeIndex != readIndex)
            {
                items[writeIndex] = item;
            }

            writeIndex++;
        }

        if (writeIndex < items.Count)
        {
            items.RemoveRange(writeIndex, items.Count - writeIndex);
        }
    }

    private static string NewUniqueId(HashSet<string> usedIds)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (!usedIds.Add(id));

        return id;
    }

    private static double NormalizeCoordinate(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : value;
    }

    private static double NormalizePaperDimension(double value, double fallback, double min)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < min
            ? fallback
            : value;
    }

    private static void NormalizeDeepCapsuleExpandedGeometry(PaperData paper)
    {
        if (!paper.DeepCapsuleExpandedX.HasValue ||
            !paper.DeepCapsuleExpandedY.HasValue ||
            !paper.DeepCapsuleExpandedWidth.HasValue ||
            !paper.DeepCapsuleExpandedHeight.HasValue ||
            !IsFinite(paper.DeepCapsuleExpandedX.Value) ||
            !IsFinite(paper.DeepCapsuleExpandedY.Value))
        {
            ClearDeepCapsuleExpandedGeometry(paper);
            return;
        }

        var fallbackWidth = paper.Type == PaperTypes.Note
            ? PaperLayoutDefaults.NoteDefaultWidth
            : PaperLayoutDefaults.TodoDefaultWidth;
        var fallbackHeight = paper.Type == PaperTypes.Note
            ? PaperLayoutDefaults.NoteDefaultHeight
            : PaperLayoutDefaults.TodoDefaultHeight;
        paper.DeepCapsuleExpandedWidth = NormalizePaperDimension(
            paper.DeepCapsuleExpandedWidth.Value,
            fallbackWidth,
            PaperLayoutDefaults.MinWidth);
        paper.DeepCapsuleExpandedHeight = NormalizePaperDimension(
            paper.DeepCapsuleExpandedHeight.Value,
            fallbackHeight,
            PaperLayoutDefaults.MinHeight);
        paper.DeepCapsuleExpandedSide = string.IsNullOrWhiteSpace(paper.DeepCapsuleExpandedSide)
            ? paper.CapsuleSide
            : DeepCapsuleSides.Normalize(paper.DeepCapsuleExpandedSide);
        var monitor = string.IsNullOrWhiteSpace(paper.DeepCapsuleExpandedMonitorDeviceName)
            ? paper.CapsuleMonitorDeviceName
            : paper.DeepCapsuleExpandedMonitorDeviceName.Trim();
        paper.DeepCapsuleExpandedMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitor);
    }

    private static void ClearDeepCapsuleExpandedGeometry(PaperData paper)
    {
        paper.DeepCapsuleExpandedX = null;
        paper.DeepCapsuleExpandedY = null;
        paper.DeepCapsuleExpandedWidth = null;
        paper.DeepCapsuleExpandedHeight = null;
        paper.DeepCapsuleExpandedSide = "";
        paper.DeepCapsuleExpandedMonitorDeviceName = "";
    }

    private static bool IsFinite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);

    private static Dictionary<string, bool> NormalizeCollapseAllActiveQueues(Dictionary<string, bool> source)
    {
        var normalized = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeQueueKey(key);
            if (!normalized.ContainsKey(normalizedKey) || string.Equals(key, normalizedKey, StringComparison.Ordinal))
            {
                normalized[normalizedKey] = value;
            }
        }

        return normalized;
    }

    private static Dictionary<string, double> NormalizeQueueStartTopMargins(Dictionary<string, double> source)
    {
        var normalized = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeQueueKey(key);
            if (!normalized.ContainsKey(normalizedKey) || string.Equals(key, normalizedKey, StringComparison.Ordinal))
            {
                normalized[normalizedKey] = value;
            }
        }

        return normalized;
    }

    private static string NormalizeQueueKey(string? key)
    {
        var value = (key ?? "").Trim();
        var separator = value.LastIndexOf('|');
        if (separator < 0)
        {
            return QueueKey("", value);
        }

        var monitorDeviceName = value[..separator];
        var side = value[(separator + 1)..];
        return QueueKey(monitorDeviceName, side);
    }

    private static string QueueKey(string? monitorDeviceName, string? side)
        => $"{WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName)}|{(side == DeepCapsuleSides.Left ? DeepCapsuleSides.Left : DeepCapsuleSides.Right)}";

    private static double NormalizeDeepCapsuleStartTopMargin(double value, string monitorDeviceName)
    {
        var area = EdgeCapsuleLayout.LocalWorkAreaForQueue(monitorDeviceName);
        return EdgeCapsuleLayout.NormalizeStartTopMargin(value, area, 1);
    }
}
