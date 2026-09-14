using System.IO;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void CheckNoteBackgroundToggle()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "custom", "note");
        Require(!Directory.Exists(directory), "background fixture must not replace existing files");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "background.png"), "not an image");
            using var editor = new Editor("body stays editable");
            NoteBackground.Apply(editor.Box);
            Require(ReferenceEquals(editor.Box.Background, Brushes.Transparent), "bad image keeps the plain background");
            Equal("body stays editable", editor.Box.Text, "bad background does not alter note content");

            NoteBackground.SetEnabled(false);
            Require(!NoteBackground.IsEnabled, "disabled marker is saved");
            using (var locked = new FileStream(Path.Combine(directory, "background.disabled"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var enabled in new[] { false, true })
                {
                    var failed = false;
                    try { NoteBackground.SetEnabled(enabled); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                    Require(failed && !NoteBackground.IsEnabled, "failed marker write/delete is reported and stays disabled");
                }
            }
            NoteBackground.SetEnabled(true);
            Require(NoteBackground.IsEnabled, "enabling removes the marker");
            NoteBackground.SetEnabled(false);
            File.Delete(Path.Combine(directory, "background.png"));
            NoteBackground.SetEnabled(true);
            Require(!File.Exists(Path.Combine(directory, "background.disabled")), "restore clears a stale marker without an image");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
