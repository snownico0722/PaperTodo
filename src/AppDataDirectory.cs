using System.IO;
using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>
/// PaperTodo's application-relative data root.
/// Portable builds keep the historic behavior (data beside the executable). Store/MSIX builds
/// use a stable writable LocalAppData directory when running with package identity.
/// </summary>
internal static class AppDataDirectory
{
    public static string Current { get; } = Resolve();

    public static bool IsPackaged
    {
        get
        {
#if PAPERTODO_STORE_BUILD
            return IsPackagedProcess();
#else
            return false;
#endif
        }
    }

    private static string Resolve()
    {
#if PAPERTODO_STORE_BUILD
        if (IsPackagedProcess())
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException(
                    "Local application data directory is unavailable for the packaged PaperTodo process.");
            }

            var directory = Path.Combine(localAppData, "PaperTodo");
            Directory.CreateDirectory(directory);
            return directory;
        }
#endif

        return System.AppContext.BaseDirectory;
    }

    private static bool IsPackagedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return result is 0 or 122; // ERROR_SUCCESS / ERROR_INSUFFICIENT_BUFFER
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        IntPtr packageFullName);
}

/// <summary>
/// Compatibility surface for the existing 3.x app-relative paths. Keeping the name local to the
/// PaperTodo namespace means existing AppContext.BaseDirectory call sites resolve here without a
/// build-time source rewrite. System.AppContext remains available by its fully qualified name.
/// </summary>
internal static class AppContext
{
    public static string BaseDirectory => AppDataDirectory.Current;
}
