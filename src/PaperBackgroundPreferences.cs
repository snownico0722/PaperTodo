using System.IO;
using System.Text.Json;

namespace PaperTodo;

internal sealed record PaperBackgroundPreferences
{
    public bool BlendWithTheme { get; init; } = true;
    public bool Stretch { get; init; }
    public string Layout { get; init; } = PaperBackgroundLayouts.Center;

    internal static PaperBackgroundPreferences Load(string path, string legacyPath)
    {
        try
        {
            try
            {
                return Read(path) ?? new PaperBackgroundPreferences();
            }
            catch (FileNotFoundException)
            {
                // Only a missing portable file can import the former per-user preferences.
            }

            var legacy = Read(legacyPath);
            if (legacy == null)
                return new PaperBackgroundPreferences();

            try { legacy.Save(path, overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the imported settings usable if this folder cannot be written yet.
            }
            return legacy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PaperBackgroundPreferences();
        }
    }

    private static PaperBackgroundPreferences? Read(string path)
    {
        var preferences = JsonSerializer.Deserialize<PaperBackgroundPreferences>(File.ReadAllText(path));
        return preferences == null
            ? null
            : preferences with { Layout = PaperBackgroundLayouts.Normalize(preferences.Layout) };
    }

    internal void Save(string path, bool overwrite = true)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
            File.Move(temporaryPath, path, overwrite);
        }
        catch
        {
            try { File.Delete(temporaryPath); }
            catch { /* Preserve the original settings write failure. */ }
            throw;
        }
    }
}
