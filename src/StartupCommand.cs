namespace PaperTodo;

public enum StartupCommandKind
{
    None,
    Show,
    Hide,
    Toggle,
    NewTodo,
    NewNote,
    Exit,
    EnableMcpForCodex,
    Studio,
    Canvas
}

public sealed class StartupCommand
{
    public StartupCommand(StartupCommandKind kind, bool standalone = false, string? filePath = null)
    {
        Kind = kind;
        Standalone = kind == StartupCommandKind.Studio && standalone;
        FilePath = filePath;
    }

    public StartupCommandKind Kind { get; }
    public bool Standalone { get; }
    public string? FilePath { get; }

    public bool CreatesPaper => Kind is StartupCommandKind.NewTodo or StartupCommandKind.NewNote;

    public static StartupCommand Parse(
        IReadOnlyList<string> args,
        StartupCommandKind defaultWhenEmpty = StartupCommandKind.None)
    {
        string? command = null;
        string? filePath = null;
        var standalone = false;
        for (var index = 0; index < args.Count; index++)
        {
            var option = Normalize(args[index]);
            if (string.IsNullOrWhiteSpace(option)) continue;
            if (option == "standalone")
            {
                standalone = true;
                continue;
            }
            if (option == "file")
            {
                // Only switches are normalized. The following argument is an opaque
                // path, including its case, spaces and any leading punctuation.
                if (index + 1 < args.Count) filePath = args[++index];
                continue;
            }
            command ??= option;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return new StartupCommand(args.Count == 0 ? defaultWhenEmpty : StartupCommandKind.None);
        }

        return new StartupCommand(command switch
        {
            "show" or "open" => StartupCommandKind.Show,
            "hide" => StartupCommandKind.Hide,
            "toggle" => StartupCommandKind.Toggle,
            "new-todo" or "todo" => StartupCommandKind.NewTodo,
            "new-note" or "note" or "paper" => StartupCommandKind.NewNote,
            "exit" or "quit" => StartupCommandKind.Exit,
            "enable-mcp-for-codex" => StartupCommandKind.EnableMcpForCodex,
            "studio" => StartupCommandKind.Studio,
            "canvas" => StartupCommandKind.Canvas,
            _ => StartupCommandKind.None
        }, standalone, filePath);
    }

    private static string Normalize(string arg)
    {
        return arg.Trim().TrimStart('-', '/').ToLowerInvariant();
    }
}
