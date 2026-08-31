using System.IO;
using System.Runtime.InteropServices;

namespace PaperTodo;

/// <summary>
/// Writable application-data root used by the MSIX build. Normal portable builds keep using
/// AppContext.BaseDirectory; the MSIX workflow rewrites those references to this property only
/// inside the CI workspace before compilation.
/// </summary>
internal static class MsixDataDirectory
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        if (!IsPackagedProcess())
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Local application data directory is unavailable for the packaged PaperTodo process.");
        }

        var directory = Path.Combine(localAppData, "PaperTodo");
        Directory.CreateDirectory(directory);
        return directory;
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
            return result is ErrorSuccess or ErrorInsufficientBuffer;
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
