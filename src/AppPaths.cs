using System.IO;

namespace PaperTodo;

internal static class AppPaths
{
    internal static string ExecutableDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            var directory = string.IsNullOrWhiteSpace(processPath)
                ? null
                : Path.GetDirectoryName(processPath);
            return string.IsNullOrWhiteSpace(directory)
                ? AppContext.BaseDirectory
                : Path.GetFullPath(directory);
        }
    }

    internal static string File(string name) => Path.Combine(ExecutableDirectory, name);
}
