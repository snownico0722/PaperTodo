using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace PaperTodo;

/// <summary>
/// First-run bridge from the historic portable data folder into the stable Store data folder.
/// It never copies telemetry queues/markers. Existing Store data is never overwritten.
/// </summary>
internal static class StoreDataMigration
{
    private static readonly string[] OptionalPortableFiles =
    {
        "data.backup.json",
        "note-assets.lmdb",
        "PaperTodo.ico",
        "papertodo.ttf",
        "papertodo.otf",
        "papertodo_bold.ttf",
        "papertodo_bold.otf",
        "papertodo-bold.ttf",
        "papertodo-bold.otf",
        "PaperTodo_Bold.ttf",
        "PaperTodo_Bold.otf",
        "PaperTodo-Bold.ttf",
        "PaperTodo-Bold.otf"
    };

    public static bool TryMigrateBeforeController()
    {
#if !PAPERTODO_STORE_BUILD
        return false;
#else
        if (!AppDataDirectory.IsPackaged)
        {
            return false;
        }

        var destinationDirectory = AppDataDirectory.Current;
        if (HasExistingStoreData(destinationDirectory))
        {
            return false;
        }

        var text = MigrationText.ForCurrentCulture();
        var detectedDirectory = SystemSettingsHelper.TryGetLegacyStartupDirectory();
        var hasDetectedData = IsPortableDataDirectory(detectedDirectory);
        var prompt = hasDetectedData ? text.DetectedPrompt : text.SelectPrompt;
        if (MessageBox.Show(
                prompt,
                text.Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return false;
        }

        var sourceDirectory = detectedDirectory;
        if (!hasDetectedData)
        {
            var dialog = new OpenFileDialog
            {
                Title = text.PickerTitle,
                Filter = "data.json|data.json",
                FileName = "data.json",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            sourceDirectory = Path.GetDirectoryName(dialog.FileName);
        }

        if (!TryMigrateDirectory(sourceDirectory, destinationDirectory, out var error))
        {
            MessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, text.Failure, error),
                text.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        SystemSettingsHelper.TryMigrateLegacyStartupRegistration(sourceDirectory!);
        return true;
#endif
    }

    private static bool HasExistingStoreData(string directory)
    {
        return File.Exists(Path.Combine(directory, "data.json")) ||
            File.Exists(Path.Combine(directory, "data.backup.json")) ||
            File.Exists(Path.Combine(directory, "note-assets.lmdb"));
    }

    private static bool IsPortableDataDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(directory, "data.json")) ||
                File.Exists(Path.Combine(directory, "data.backup.json"));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMigrateDirectory(
        string? sourceDirectory,
        string destinationDirectory,
        out string error)
    {
        error = "";
        if (!IsPortableDataDirectory(sourceDirectory))
        {
            error = MigrationText.ForCurrentCulture().MissingData;
            return false;
        }

        try
        {
            sourceDirectory = Path.GetFullPath(sourceDirectory!);
            destinationDirectory = Path.GetFullPath(destinationDirectory);
            if (string.Equals(
                    sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ValidateStateJson(sourceDirectory);
            Directory.CreateDirectory(destinationDirectory);

            var sourceFiles = CollectPortableFiles(sourceDirectory);
            var staged = new List<(string TempPath, string DestinationPath)>();
            var committed = new List<string>();
            try
            {
                foreach (var sourcePath in sourceFiles)
                {
                    var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
                    if (File.Exists(destinationPath))
                    {
                        continue;
                    }

                    var tempPath = destinationPath + ".store-migrate-" + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(sourcePath, tempPath, overwrite: false);
                    staged.Add((tempPath, destinationPath));
                }

                foreach (var item in staged)
                {
                    File.Move(item.TempPath, item.DestinationPath, overwrite: false);
                    committed.Add(item.DestinationPath);
                }
            }
            catch
            {
                foreach (var item in staged)
                {
                    try
                    {
                        if (File.Exists(item.TempPath))
                        {
                            File.Delete(item.TempPath);
                        }
                    }
                    catch
                    {
                        // Best-effort cleanup only.
                    }
                }

                foreach (var path in committed)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                    catch
                    {
                        // Best-effort rollback only.
                    }
                }

                throw;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ValidateStateJson(string sourceDirectory)
    {
        var statePath = Path.Combine(sourceDirectory, "data.json");
        if (!File.Exists(statePath))
        {
            statePath = Path.Combine(sourceDirectory, "data.backup.json");
        }

        using var stream = File.Open(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var _ = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
    }

    private static IReadOnlyList<string> CollectPortableFiles(string sourceDirectory)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfExists(files, Path.Combine(sourceDirectory, "data.json"));
        foreach (var fileName in OptionalPortableFiles)
        {
            AddIfExists(files, Path.Combine(sourceDirectory, fileName));
        }

        foreach (var pattern in new[]
                 {
                     "data.failed_load.*.json",
                     "data.backup.used_for_recovery.*.json"
                 })
        {
            foreach (var path in Directory.EnumerateFiles(sourceDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                files.Add(path);
            }
        }

        return files.ToList();
    }

    private static void AddIfExists(ISet<string> files, string path)
    {
        if (File.Exists(path))
        {
            files.Add(path);
        }
    }

    private sealed record MigrationText(
        string Title,
        string DetectedPrompt,
        string SelectPrompt,
        string PickerTitle,
        string Failure,
        string MissingData)
    {
        public static MigrationText ForCurrentCulture()
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
            {
                "zh" => new MigrationText(
                    "导入现有 PaperTodo 数据",
                    "检测到现有 PaperTodo 便携版数据。是否导入到 Microsoft Store 版？\n\n将复制纸片、待办、笔记图片和自定义图标/字体；不会复制遥测数据。",
                    "Microsoft Store 版尚无数据。是否从现有 PaperTodo 便携版导入？\n\n选择“是”后，请选择旧版目录中的 data.json。",
                    "选择旧版 PaperTodo 的 data.json",
                    "导入失败：{0}\n\n商店版现有数据未被覆盖。",
                    "所选目录中没有可用的 data.json 或 data.backup.json。"),
                "ja" => new MigrationText(
                    "既存の PaperTodo データをインポート",
                    "既存のポータブル版 PaperTodo データが見つかりました。Microsoft Store 版へインポートしますか？\n\n紙片、ToDo、ノート画像、カスタムアイコン/フォントをコピーします。テレメトリーデータはコピーしません。",
                    "Microsoft Store 版にはまだデータがありません。既存のポータブル版 PaperTodo からインポートしますか？\n\n「はい」を選んだ後、旧版フォルダーの data.json を選択してください。",
                    "旧版 PaperTodo の data.json を選択",
                    "インポートに失敗しました: {0}\n\nStore 版の既存データは上書きされていません。",
                    "選択したフォルダーに data.json または data.backup.json がありません。"),
                "ko" => new MigrationText(
                    "기존 PaperTodo 데이터 가져오기",
                    "기존 휴대용 PaperTodo 데이터를 찾았습니다. Microsoft Store 버전으로 가져오시겠습니까?\n\n메모, 할 일, 노트 이미지, 사용자 지정 아이콘/글꼴을 복사하며 원격 측정 데이터는 복사하지 않습니다.",
                    "Microsoft Store 버전에 아직 데이터가 없습니다. 기존 휴대용 PaperTodo에서 가져오시겠습니까?\n\n‘예’를 선택한 뒤 이전 폴더의 data.json을 선택하세요.",
                    "이전 PaperTodo의 data.json 선택",
                    "가져오기에 실패했습니다: {0}\n\nStore 버전의 기존 데이터는 덮어쓰지 않았습니다.",
                    "선택한 폴더에 사용할 수 있는 data.json 또는 data.backup.json이 없습니다."),
                _ => new MigrationText(
                    "Import existing PaperTodo data",
                    "Existing portable PaperTodo data was detected. Import it into the Microsoft Store version?\n\nPapers, todos, note images, and custom icons/fonts will be copied. Telemetry data will not be copied.",
                    "The Microsoft Store version has no data yet. Import from an existing portable PaperTodo installation?\n\nAfter choosing Yes, select data.json from the old PaperTodo folder.",
                    "Select the old PaperTodo data.json",
                    "Import failed: {0}\n\nExisting Store data was not overwritten.",
                    "The selected folder does not contain a usable data.json or data.backup.json.")
            };
        }
    }
}
