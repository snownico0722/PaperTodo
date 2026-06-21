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
                    Normalize(state);
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

                    Normalize(state);
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

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _latestWrittenVersion = 0;

    public string SerializeState(AppState state)
    {
        Normalize(state);
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

    private static void Normalize(AppState state)
    {
        state.Papers ??= new List<PaperData>();
        state.Papers = state.Papers
            .Where(paper => paper != null)
            .Cast<PaperData>()
            .ToList();
        if (state.Theme is not ("system" or "light" or "dark"))
        {
            state.Theme = "system";
        }

        state.ColorScheme = ColorSchemes.Normalize(state.ColorScheme);
        if (state.ColorScheme == ColorSchemes.Custom || state.CustomColorPalette != null)
        {
            state.CustomColorPalette = Theme.NormalizeCustomPalette(state.CustomColorPalette, ColorSchemes.Warm);
        }

        if (!MarkdownRenderModes.IsValid(state.MarkdownRenderMode))
        {
            state.MarkdownRenderMode = MarkdownRenderModes.Enhanced;
        }

        state.ExternalMarkdownExtension = ExternalMarkdownFileExtensions.Normalize(state.ExternalMarkdownExtension);
        state.FullscreenTopmostMode = FullscreenTopmostModes.Normalize(state.FullscreenTopmostMode);
        state.DeepCapsuleSide = DeepCapsuleSides.Normalize(state.DeepCapsuleSide);
        state.DeepCapsuleMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(state.DeepCapsuleMonitorDeviceName);
        state.TodoVisualSize = TodoVisualSizes.Normalize(state.TodoVisualSize);
        state.TopBarHeight = 0;

        if (state.ShowTopBarNewPaperButtons is bool showTopBarNewPaperButtons)
        {
            state.ShowTopBarNewTodoButton = showTopBarNewPaperButtons;
            state.ShowTopBarNewNoteButton = showTopBarNewPaperButtons;
            state.ShowTopBarNewPaperButtons = null;
        }

        state.Zoom = NormalizeScalePercent(state.Zoom);
        state.CapsuleZoom = NormalizeScalePercent(state.CapsuleZoom);

        if (!state.UseCapsuleMode)
        {
            state.UseDeepCapsuleMode = false;
        }

        state.MaxTitleLength = PaperTitles.NormalizeMaxTitleLength(state.MaxTitleLength);

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
            : DeepCapsuleLayout.StartTopMargin;

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
            paper.CapsulePlacement = CapsulePlacements.Normalize(paper.CapsulePlacement);
            if (!IsValidNullableCoordinate(paper.CapsuleX) || !IsValidNullableCoordinate(paper.CapsuleY))
            {
                paper.CapsuleX = null;
                paper.CapsuleY = null;
            }

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
            paper.Items = paper.Items
                .Where(item => item != null)
                .Cast<PaperItem>()
                .ToList();
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

    private static double NormalizeScalePercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            value = 1.0;
        }

        return Math.Round(Math.Clamp(value, 0.5, 1.5) / 0.05, MidpointRounding.AwayFromZero) * 0.05;
    }

    private static bool IsValidNullableCoordinate(double? value)
    {
        return !value.HasValue || (!double.IsNaN(value.Value) && !double.IsInfinity(value.Value));
    }

    private static double NormalizeDeepCapsuleStartTopMargin(double value, string monitorDeviceName)
    {
        var area = DeepCapsuleLayout.WorkAreaForQueue(monitorDeviceName);
        return DeepCapsuleLayout.NormalizeStartTopMargin(value, area, 1);
    }
}

/*
=== 修改记录 ===
[修改编号]: 1
[修改日期]: 2026-06-20
[修改类型]: 新增功能
[主要内容]:
- 新增整体缩放、胶囊缩放的百分比规范化。
- 新增胶囊布局状态与自由悬浮坐标的兼容校验。

[修改目的]:
- 保证旧配置加载后能安全使用新增的缩放和自由悬浮字段。

[影响范围]:
- data.json 加载、保存前序列化规范化和旧版本配置兼容。

[修改编号]: 2
[修改日期]: 2026-06-21
[修改类型]: 新增功能
[主要内容]:
- 新增自定义色盘的加载和保存前规范化。
- 对非法或缺失的 HEX 颜色按默认色盘逐项回退。

[修改目的]:
- 保证旧配置和手动修改过的 data.json 在启用自定义主题时仍能安全加载。

[影响范围]:
- data.json 主题字段兼容、自定义色盘保存格式和主题初始化。
*/
