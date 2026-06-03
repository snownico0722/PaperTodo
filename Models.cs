namespace PaperTodo;

public static class PaperTypes
{
    public const string Todo = "todo";
    public const string Note = "note";
}

public static class PaperLayoutDefaults
{
    public const double MinWidth = 220;
    public const double MinHeight = 160;

    public const double CapsuleWidth = 108; // 包含阴影边框边距
    public const double CapsuleHeight = 46;

    public const double TodoDefaultWidth = 280;
    public const double TodoDefaultHeight = 340;

    public const double NoteDefaultWidth = 320;
    public const double NoteDefaultHeight = 360;
}

public sealed class AppState
{
    public List<PaperData> Papers { get; set; } = new();
    public string Theme { get; set; } = "system";
    public bool UseCapsuleMode { get; set; } = false;
    public bool UseDeepCapsuleMode { get; set; } = false;
}

public sealed class PaperData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = PaperTypes.Todo;
    public string Title { get; set; } = "";

    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 360;

    public bool IsVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool IsCollapsed { get; set; } = false;

    public List<PaperItem> Items { get; set; } = new();
    public string Content { get; set; } = "";
}

public sealed class PaperItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Order { get; set; }
}

