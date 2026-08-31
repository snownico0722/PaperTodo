namespace PaperTodo;

public enum StartupCommandKind
{
    None,
    Show,
    Hide,
    Toggle,
    NewTodo,
    NewNote,
    Exit
}

public sealed class StartupCommand
{
    private static readonly string[] DefaultLanguageOptionNames =
    [
        "language",
        "lang",
        "default-language"
    ];

    public StartupCommand(StartupCommandKind kind, string? defaultLanguage = null)
    {
        Kind = kind;
        DefaultLanguage = defaultLanguage;
    }

    public StartupCommandKind Kind { get; }
    public string? DefaultLanguage { get; }

    public bool CreatesPaper => Kind is StartupCommandKind.NewTodo or StartupCommandKind.NewNote;

    public static StartupCommand Parse(
        IReadOnlyList<string> args,
        StartupCommandKind defaultWhenEmpty = StartupCommandKind.None)
    {
        var kind = defaultWhenEmpty;
        var hasMeaningfulArgument = false;
        var hasCommandCandidate = false;
        string? defaultLanguage = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = (args[index] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            hasMeaningfulArgument = true;
            if (TryParseDefaultLanguageOption(argument, out var inlineLanguage, out var needsSeparateValue))
            {
                if (!string.IsNullOrWhiteSpace(inlineLanguage))
                {
                    defaultLanguage = inlineLanguage.Trim();
                }
                else if (needsSeparateValue &&
                    index + 1 < args.Count &&
                    IsSeparateOptionValue(args[index + 1]))
                {
                    defaultLanguage = args[++index].Trim();
                }

                continue;
            }

            if (!hasCommandCandidate)
            {
                kind = ParseKind(Normalize(argument));
                hasCommandCandidate = true;
            }
        }

        // A language-only invocation is meaningful but is not a window command. In particular,
        // forwarding it to an already-running instance must not fall through to the no-argument
        // default of Show.
        if (!hasCommandCandidate && hasMeaningfulArgument)
        {
            kind = StartupCommandKind.None;
        }

        return new StartupCommand(kind, defaultLanguage);
    }

    private static StartupCommandKind ParseKind(string command)
    {
        return command switch
        {
            "show" or "open" => StartupCommandKind.Show,
            "hide" => StartupCommandKind.Hide,
            "toggle" => StartupCommandKind.Toggle,
            "new-todo" or "todo" => StartupCommandKind.NewTodo,
            "new-note" or "note" or "paper" => StartupCommandKind.NewNote,
            "exit" or "quit" => StartupCommandKind.Exit,
            _ => StartupCommandKind.None
        };
    }

    private static bool TryParseDefaultLanguageOption(
        string argument,
        out string? inlineLanguage,
        out bool needsSeparateValue)
    {
        var option = argument.Trim().TrimStart('-', '/');
        foreach (var name in DefaultLanguageOptionNames)
        {
            if (string.Equals(option, name, StringComparison.OrdinalIgnoreCase))
            {
                inlineLanguage = null;
                needsSeparateValue = true;
                return true;
            }

            foreach (var separator in new[] { '=', ':' })
            {
                var prefix = name + separator;
                if (option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    inlineLanguage = option[prefix.Length..];
                    needsSeparateValue = false;
                    return true;
                }
            }
        }

        inlineLanguage = null;
        needsSeparateValue = false;
        return false;
    }

    private static bool IsSeparateOptionValue(string? argument)
    {
        var value = (argument ?? "").Trim();
        return !string.IsNullOrWhiteSpace(value) &&
            !value.StartsWith("-", StringComparison.Ordinal) &&
            !value.StartsWith("/", StringComparison.Ordinal);
    }

    private static string Normalize(string arg)
    {
        return arg.Trim().TrimStart('-', '/').ToLowerInvariant();
    }
}
