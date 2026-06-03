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
        AllowTrailingCommas = true
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
        if (state.Theme is not ("system" or "light" or "dark"))
        {
            state.Theme = "system";
        }

        if (!state.UseCapsuleMode)
        {
            state.UseDeepCapsuleMode = false;
        }

        foreach (var paper in state.Papers)
        {
            if (string.IsNullOrWhiteSpace(paper.Id))
            {
                paper.Id = Guid.NewGuid().ToString("N");
            }

            if (paper.Type != PaperTypes.Note && paper.Type != PaperTypes.Todo)
            {
                paper.Type = PaperTypes.Todo;
            }

            if (paper.Width < PaperLayoutDefaults.MinWidth)
            {
                paper.Width = paper.Type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultWidth : PaperLayoutDefaults.TodoDefaultWidth;
            }
            if (paper.Height < PaperLayoutDefaults.MinHeight)
            {
                paper.Height = paper.Type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultHeight : PaperLayoutDefaults.TodoDefaultHeight;
            }

            paper.Items ??= new List<PaperItem>();
            paper.Content ??= "";
            if (!state.UseCapsuleMode)
            {
                paper.IsCollapsed = false;
            }

            for (var i = 0; i < paper.Items.Count; i++)
            {
                var item = paper.Items[i];
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    item.Id = Guid.NewGuid().ToString("N");
                }

                item.Order = i;
                item.Text ??= "";
            }
        }
    }
}
