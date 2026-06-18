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

    public AppState Load()
    {
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
                    Normalize(state);
                    return state;
                }
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

        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);

        try
        {
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, BackupPath, overwrite: true);
            }
        }
        catch
        {
            // Backup failure should not block normal saving.
        }

        File.Move(tempPath, FilePath, overwrite: true);
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

        if (!MarkdownRenderModes.IsValid(state.MarkdownRenderMode))
        {
            state.MarkdownRenderMode = MarkdownRenderModes.Enhanced;
        }

        state.ExternalMarkdownExtension = ExternalMarkdownFileExtensions.Normalize(state.ExternalMarkdownExtension);
        state.FullscreenTopmostMode = FullscreenTopmostModes.Normalize(state.FullscreenTopmostMode);
        state.DeepCapsuleSide = DeepCapsuleSides.Normalize(state.DeepCapsuleSide);
        state.DeepCapsuleMonitorDeviceName = (state.DeepCapsuleMonitorDeviceName ?? "").Trim();
        state.TodoVisualSize = TodoVisualSizes.Normalize(state.TodoVisualSize);
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

        if (!state.UseCapsuleMode || !state.UseDeepCapsuleMode)
        {
            state.UseCapsuleCollapseAll = false;
        }

        if (!state.UseCapsuleCollapseAll)
        {
            state.CapsuleCollapseAllActive = false;
        }

        state.DeepCapsuleStartTopMargin = state.UseCapsuleMode && state.UseDeepCapsuleMode && state.UseCapsuleCollapseAll
            ? NormalizeDeepCapsuleStartTopMargin(state.DeepCapsuleStartTopMargin)
            : DeepCapsuleLayout.StartTopMargin;

        // Per-queue margins: drop NaN/inf; final clamping against each queue's live work area is
        // done at layout time (monitor set can change between sessions, so we don't over-normalize
        // here). A null dict (older config) becomes empty => every queue falls back to the global.
        state.DeepCapsuleQueueStartTopMargins ??= new Dictionary<string, double>();
        foreach (var key in state.DeepCapsuleQueueStartTopMargins.Keys.ToList())
        {
            var v = state.DeepCapsuleQueueStartTopMargins[key];
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                state.DeepCapsuleQueueStartTopMargins.Remove(key);
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
            paper.CapsuleMonitorDeviceName = string.IsNullOrWhiteSpace(paper.CapsuleMonitorDeviceName)
                ? (state.DeepCapsuleMonitorDeviceName ?? "")
                : paper.CapsuleMonitorDeviceName.Trim();

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

    private static double NormalizeDeepCapsuleStartTopMargin(double value) => DeepCapsuleLayout.NormalizeStartTopMargin(value);
}
