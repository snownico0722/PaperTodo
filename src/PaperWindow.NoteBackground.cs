namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal void RefreshNoteBackground()
    {
        NoteBackground.Apply(_markdownBodySession?.NoteBox);
    }
}
