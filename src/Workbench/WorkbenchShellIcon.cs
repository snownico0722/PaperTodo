using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PaperTodo;

// Reads the same file/folder icons as Windows Explorer. Called only on a worker thread.
internal static class WorkbenchShellIcon
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint attributes, out ShellFileInfo info, uint infoSize, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    internal static BitmapSource? Load(string source, bool link)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var path = link ? ".url" : source;
        var folder = !link && Directory.Exists(path);
        var file = !link && File.Exists(path);
        var flags = ShgfiIcon | ShgfiLargeIcon;
        if (link || !file && !folder)
        {
            flags |= ShgfiUseFileAttributes;
            path = link ? ".url" : Path.GetExtension(source);
            if (string.IsNullOrEmpty(path)) path = ".bin";
        }
        if (SHGetFileInfo(path, folder ? FileAttributeDirectory : FileAttributeNormal,
                out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags) == IntPtr.Zero ||
            info.Icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally { DestroyIcon(info.Icon); }
    }
}
