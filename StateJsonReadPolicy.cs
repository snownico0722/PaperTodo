using System.Text.Json;

namespace PaperTodo;

internal static class StateJsonReadPolicy
{
    public const JsonCommentHandling CommentHandling = JsonCommentHandling.Skip;
    public const bool AllowTrailingCommas = true;

    public static JsonDocumentOptions DocumentOptions => new()
    {
        CommentHandling = CommentHandling,
        AllowTrailingCommas = AllowTrailingCommas
    };
}
