using System.IO;
using System.Text.Json;

namespace PaperTodo;

internal static class TelemetryCrashMarkerMigration
{
    private static readonly string CrashPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PaperTodo",
        "telemetry-crash.json");

    private static readonly JsonSerializerOptions DiskJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static void MigrateIfNeeded()
    {
        try
        {
            if (!File.Exists(CrashPath))
            {
                return;
            }

            var json = File.ReadAllText(CrashPath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var legacy = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    return;
                }

                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out var count))
                {
                    return;
                }

                legacy[property.Name] = Math.Clamp(count, 0, 1000);
            }

            if (legacy.Count == 0)
            {
                return;
            }

            var migrated = legacy.ToDictionary(
                pair => pair.Key,
                pair => new CrashMarkerWire { Count = pair.Value },
                StringComparer.Ordinal);

            var tempPath = CrashPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(migrated, DiskJsonOptions));
            File.Move(tempPath, CrashPath, overwrite: true);
        }
        catch
        {
            // Leave an unreadable marker untouched; telemetry must never affect startup.
        }
    }

    private sealed class CrashMarkerWire
    {
        public int Count { get; set; }
        public string ExceptionType { get; set; } = "";
        public string StackHash { get; set; } = "";
        public string Module { get; set; } = "";
    }
}
