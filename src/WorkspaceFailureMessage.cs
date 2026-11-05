using System.IO;
using System.Text.Json;

namespace PaperTodo;

// Keep recovery reasons typed, so presentation can use the restored workspace's
// language without matching English exception text.
internal sealed class StateRecoveryException(string messageKey, Exception? cause = null)
    : IOException(Strings.Get(messageKey), cause)
{
    internal string MessageKey { get; } = messageKey;
}

internal static class WorkspaceFailureMessage
{
    internal static string For(Exception error)
    {
        var cause = error;
        while (cause is not StateRecoveryException && cause.InnerException is { } inner)
            cause = inner;
        var key = cause switch
        {
            StateRecoveryException recovery => recovery.MessageKey,
            UnauthorizedAccessException => "WorkspaceFailureAccessDenied",
            FileNotFoundException or DirectoryNotFoundException => "WorkspaceFailureMissing",
            InvalidDataException or JsonException => "WorkspaceFailureInvalidData",
            IOException io when (io.HResult & 0xffff) is 32 or 33 => "WorkspaceFailureBusy",
            IOException io when (io.HResult & 0xffff) is 39 or 112 => "WorkspaceFailureDiskFull",
            IOException => "WorkspaceFailureIo",
            _ => "WorkspaceFailureUnknown"
        };
        return Strings.Get(key);
    }

    internal static string ForStartup(Exception error)
    {
        var cause = error.GetBaseException();
        var key = cause switch
        {
            IOException io when (io.HResult & 0xffff) is 32 or 33 => "AppStartupFailureBusy",
            JsonException => "AppStartupFailureCorrupt",
            _ => "AppStartupFailureUnknown"
        };
        return Strings.Format("AppStartupFailureMessage", Strings.Get(key), cause.Message);
    }
}
