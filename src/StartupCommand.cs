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
    EnableMcpForCodex
}

public sealed class StartupCommand
{
    public StartupCommand(StartupCommandKind kind)
    {
        Kind = kind;
    }

    public StartupCommandKind Kind { get; }

    public bool CreatesPaper => Kind is StartupCommandKind.NewTodo or StartupCommandKind.NewNote;

    public static StartupCommand Parse(
        IReadOnlyList<string> args,
        StartupCommandKind defaultWhenEmpty = StartupCommandKind.None)
    {
        var command = args
            .Select(Normalize)
            .FirstOrDefault(arg => !string.IsNullOrWhiteSpace(arg));

        if (string.IsNullOrWhiteSpace(command))
        {
            return new StartupCommand(defaultWhenEmpty);
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
            _ => StartupCommandKind.None
        });
    }

    private static string Normalize(string arg)
    {
        return arg.Trim().TrimStart('-', '/').ToLowerInvariant();
    }
}
