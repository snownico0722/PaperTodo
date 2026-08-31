using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PaperTodo;

internal static class LmdbNative
{
    private const string LibraryName = "papertodo_lmdb";

    internal const int Success = 0;
    internal const int KeyExists = -30799;
    internal const int NotFound = -30798;

    internal const uint NoSubdirectory = 0x4000;
    internal const uint ReadOnly = 0x20000;
    internal const uint NoLock = 0x400000;
    internal const uint CreateDatabase = 0x40000;
    internal const uint NoOverwrite = 0x10;

    internal const int CursorFirst = 0;
    internal const int CursorNext = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Value
    {
        internal nuint Size;
        internal IntPtr Data;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_env_create(out IntPtr environment);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_env_set_mapsize(IntPtr environment, nuint size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_env_set_maxdbs(IntPtr environment, uint databases);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_env_open(
        IntPtr environment,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint flags,
        int mode);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mdb_env_close(IntPtr environment);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_txn_begin(
        IntPtr environment,
        IntPtr parent,
        uint flags,
        out IntPtr transaction);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_txn_commit(IntPtr transaction);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mdb_txn_abort(IntPtr transaction);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_dbi_open(
        IntPtr transaction,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        uint flags,
        out uint database);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_get(
        IntPtr transaction,
        uint database,
        ref Value key,
        out Value data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_put(
        IntPtr transaction,
        uint database,
        ref Value key,
        ref Value data,
        uint flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_del(
        IntPtr transaction,
        uint database,
        ref Value key,
        IntPtr data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_cursor_open(
        IntPtr transaction,
        uint database,
        out IntPtr cursor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int mdb_cursor_get(
        IntPtr cursor,
        ref Value key,
        ref Value data,
        int operation);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void mdb_cursor_close(IntPtr cursor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr mdb_strerror(int error);

    internal static void Check(int result, string operation)
    {
        if (result == Success)
        {
            return;
        }

        var nativeMessage = Marshal.PtrToStringAnsi(mdb_strerror(result));
        throw new LmdbException(result, operation, nativeMessage);
    }
}

internal sealed class LmdbEnvironmentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal LmdbEnvironmentHandle(IntPtr environment)
        : base(ownsHandle: true)
    {
        SetHandle(environment);
    }

    protected override bool ReleaseHandle()
    {
        LmdbNative.mdb_env_close(handle);
        return true;
    }
}

internal sealed class LmdbException : IOException
{
    internal LmdbException(int errorCode, string operation, string? nativeMessage)
        : base($"LMDB {operation} failed ({errorCode}): {nativeMessage ?? "unknown error"}")
    {
        ErrorCode = errorCode;
    }

    internal int ErrorCode { get; }
}
