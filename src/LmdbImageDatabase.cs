using System;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo;

internal sealed class LmdbImageDatabase : IDisposable
{
    private const int StoreVersion = 1;
    // Address-space ceiling only; LMDB grows the Windows data file incrementally.
    private const long MapSizeBytes = 256L * 1024 * 1024;
    private const int FileMode = 438; // 0666; ignored by LMDB on Windows.

    private static readonly byte[] StoreKey = Encoding.ASCII.GetBytes("!store");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly LmdbEnvironmentHandle _environment;
    private uint _metadataDatabase;
    private uint _blobDatabase;
    private bool _disposed;

    private LmdbImageDatabase(LmdbEnvironmentHandle environment)
    {
        _environment = environment;
    }

    internal static LmdbImageDatabase Open(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LmdbNative.Check(LmdbNative.mdb_env_create(out var rawEnvironment), "create environment");
        var environment = new LmdbEnvironmentHandle(rawEnvironment);
        try
        {
            LmdbNative.Check(
                LmdbNative.mdb_env_set_mapsize(rawEnvironment, checked((nuint)MapSizeBytes)),
                "set map size");
            LmdbNative.Check(
                LmdbNative.mdb_env_set_maxdbs(rawEnvironment, 2),
                "set database count");
            LmdbNative.Check(
                LmdbNative.mdb_env_open(
                    rawEnvironment,
                    path,
                    // PaperTodo is single-instance and NoteImageStore serializes every call.
                    // NOLOCK avoids LMDB's second, persistent lock file.
                    LmdbNative.NoSubdirectory | LmdbNative.NoLock,
                    FileMode),
                "open environment");

            var database = new LmdbImageDatabase(environment);
            database.Initialize();
            return database;
        }
        catch
        {
            environment.Dispose();
            throw;
        }
    }

    internal LmdbImageIndex ReadIndex()
    {
        EnsureNotDisposed();

        var transaction = BeginTransaction(write: false);
        IntPtr cursor = IntPtr.Zero;
        try
        {
            if (!TryGet(transaction, _metadataDatabase, StoreKey, out var storeBytes))
            {
                throw new InvalidDataException("The image database has no store metadata.");
            }

            var store = DeserializeStore(storeBytes);
            var assets = new List<NoteImageAsset>();
            // Keep damaged records untouched in LMDB. The caller quarantines their ids in memory
            // so healthy images remain available and the original bytes stay recoverable.
            var corruptedImageIds = new HashSet<string>(StringComparer.Ordinal);
            var maximumImageNumber = 0;

            LmdbNative.Check(
                LmdbNative.mdb_cursor_open(transaction, _metadataDatabase, out cursor),
                "open metadata cursor");

            var key = default(LmdbNative.Value);
            var value = default(LmdbNative.Value);
            var result = LmdbNative.mdb_cursor_get(cursor, ref key, ref value, LmdbNative.CursorFirst);
            while (result == LmdbNative.Success)
            {
                var keyBytes = CopyValue(key);
                if (!keyBytes.AsSpan().SequenceEqual(StoreKey))
                {
                    var imageId = Encoding.ASCII.GetString(keyBytes);
                    if (MarkdownImageReferences.IsValidImageId(imageId) &&
                        int.TryParse(imageId, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        maximumImageNumber = Math.Max(maximumImageNumber, number);
                    }

                    if (!TryDeserializeAsset(CopyValue(value), out var asset) ||
                        !string.Equals(asset.Id, imageId, StringComparison.Ordinal) ||
                        !TryGetLength(transaction, _blobDatabase, keyBytes, out var blobLength) ||
                        blobLength != asset.ByteLength)
                    {
                        corruptedImageIds.Add(imageId);
                    }
                    else
                    {
                        assets.Add(asset);
                    }
                }

                result = LmdbNative.mdb_cursor_get(cursor, ref key, ref value, LmdbNative.CursorNext);
            }

            if (result != LmdbNative.NotFound)
            {
                LmdbNative.Check(result, "enumerate metadata");
            }

            LmdbNative.mdb_cursor_close(cursor);
            cursor = IntPtr.Zero;
            LmdbNative.Check(
                LmdbNative.mdb_cursor_open(transaction, _blobDatabase, out cursor),
                "open blob cursor");

            key = default;
            value = default;
            result = LmdbNative.mdb_cursor_get(cursor, ref key, ref value, LmdbNative.CursorFirst);
            while (result == LmdbNative.Success)
            {
                var keyBytes = CopyValue(key);
                var imageId = Encoding.ASCII.GetString(keyBytes);
                if (MarkdownImageReferences.IsValidImageId(imageId) &&
                    int.TryParse(imageId, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    maximumImageNumber = Math.Max(maximumImageNumber, number);
                    if (!TryGetLength(transaction, _metadataDatabase, keyBytes, out _))
                    {
                        // Preserve blob-only keys and reserve their ids. They may be recoverable,
                        // and the allocator must never repeatedly collide with them.
                        corruptedImageIds.Add(imageId);
                    }
                }

                result = LmdbNative.mdb_cursor_get(cursor, ref key, ref value, LmdbNative.CursorNext);
            }

            if (result != LmdbNative.NotFound)
            {
                LmdbNative.Check(result, "enumerate blobs");
            }

            var nextImageNumber = Math.Max(store.NextImageNumber, maximumImageNumber + 1);
            return new LmdbImageIndex(assets, corruptedImageIds, nextImageNumber);
        }
        finally
        {
            if (cursor != IntPtr.Zero)
            {
                LmdbNative.mdb_cursor_close(cursor);
            }
            AbortTransaction(ref transaction);
        }
    }

    internal bool TryReadBlob(string imageId, out byte[] bytes)
    {
        EnsureNotDisposed();

        var transaction = BeginTransaction(write: false);
        try
        {
            return TryGet(transaction, _blobDatabase, ImageKey(imageId), out bytes);
        }
        finally
        {
            AbortTransaction(ref transaction);
        }
    }

    internal void AddImages(IReadOnlyList<LmdbImageWrite> images, int nextImageNumber)
    {
        EnsureNotDisposed();
        if (images.Count == 0)
        {
            return;
        }

        var transaction = BeginTransaction(write: true);
        try
        {
            foreach (var image in images)
            {
                if (image.Asset.ByteLength != image.Bytes.Length)
                {
                    throw new InvalidDataException($"Image '{image.Asset.Id}' has an invalid byte length.");
                }

                var key = ImageKey(image.Asset.Id);
                var metadata = JsonSerializer.SerializeToUtf8Bytes(image.Asset, JsonOptions);
                Put(transaction, _metadataDatabase, key, metadata, LmdbNative.NoOverwrite);
                Put(transaction, _blobDatabase, key, image.Bytes, LmdbNative.NoOverwrite);
            }

            PutStore(transaction, new ImageStoreMetadata
            {
                Version = StoreVersion,
                NextImageNumber = nextImageNumber
            });
            CommitTransaction(ref transaction);
        }
        finally
        {
            AbortTransaction(ref transaction);
        }
    }

    internal void DeleteImages(IReadOnlyCollection<string> imageIds)
    {
        EnsureNotDisposed();
        if (imageIds.Count == 0)
        {
            return;
        }

        var transaction = BeginTransaction(write: true);
        try
        {
            foreach (var imageId in imageIds)
            {
                var key = ImageKey(imageId);
                Delete(transaction, _metadataDatabase, key);
                Delete(transaction, _blobDatabase, key);
            }
            CommitTransaction(ref transaction);
        }
        finally
        {
            AbortTransaction(ref transaction);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _environment.Dispose();
    }

    private void Initialize()
    {
        var transaction = BeginTransaction(write: true);
        try
        {
            LmdbNative.Check(
                LmdbNative.mdb_dbi_open(
                    transaction,
                    "meta",
                    LmdbNative.CreateDatabase,
                    out _metadataDatabase),
                "open metadata database");
            LmdbNative.Check(
                LmdbNative.mdb_dbi_open(
                    transaction,
                    "blob",
                    LmdbNative.CreateDatabase,
                    out _blobDatabase),
                "open blob database");

            if (!TryGet(transaction, _metadataDatabase, StoreKey, out var storeBytes))
            {
                PutStore(transaction, new ImageStoreMetadata());
            }
            else
            {
                _ = DeserializeStore(storeBytes);
            }

            CommitTransaction(ref transaction);
        }
        finally
        {
            AbortTransaction(ref transaction);
        }
    }

    private ImageStoreMetadata DeserializeStore(byte[] bytes)
    {
        var store = JsonSerializer.Deserialize<ImageStoreMetadata>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The image database store metadata is empty.");
        if (store.Version != StoreVersion)
        {
            throw new InvalidDataException("The image database format is not supported.");
        }
        if (store.NextImageNumber < 1)
        {
            // The numeric allocator is derivable from the union of metadata/blob keys. Keep
            // healthy assets readable and let ReadIndex reconstruct a collision-free value.
            store.NextImageNumber = 1;
        }
        return store;
    }

    private static bool TryDeserializeAsset(byte[] bytes, out NoteImageAsset asset)
    {
        try
        {
            var deserialized = JsonSerializer.Deserialize<NoteImageAsset>(bytes, JsonOptions);
            if (deserialized == null)
            {
                asset = null!;
                return false;
            }

            asset = deserialized;
            return true;
        }
        catch (JsonException)
        {
            asset = null!;
            return false;
        }
        catch (NotSupportedException)
        {
            asset = null!;
            return false;
        }
    }

    private void PutStore(IntPtr transaction, ImageStoreMetadata store)
        => Put(
            transaction,
            _metadataDatabase,
            StoreKey,
            JsonSerializer.SerializeToUtf8Bytes(store, JsonOptions),
            flags: 0);

    private IntPtr BeginTransaction(bool write)
    {
        LmdbNative.Check(
            LmdbNative.mdb_txn_begin(
                _environment.DangerousGetHandle(),
                IntPtr.Zero,
                write ? 0 : LmdbNative.ReadOnly,
                out var transaction),
            write ? "begin write transaction" : "begin read transaction");
        return transaction;
    }

    private static void CommitTransaction(ref IntPtr transaction)
    {
        var current = transaction;
        transaction = IntPtr.Zero;
        LmdbNative.Check(LmdbNative.mdb_txn_commit(current), "commit transaction");
    }

    private static void AbortTransaction(ref IntPtr transaction)
    {
        if (transaction == IntPtr.Zero)
        {
            return;
        }

        LmdbNative.mdb_txn_abort(transaction);
        transaction = IntPtr.Zero;
    }

    private static unsafe bool TryGet(
        IntPtr transaction,
        uint database,
        byte[] keyBytes,
        out byte[] valueBytes)
    {
        fixed (byte* keyPointer = keyBytes)
        {
            var key = new LmdbNative.Value
            {
                Size = checked((nuint)keyBytes.Length),
                Data = (IntPtr)keyPointer
            };
            var result = LmdbNative.mdb_get(transaction, database, ref key, out var value);
            if (result == LmdbNative.NotFound)
            {
                valueBytes = Array.Empty<byte>();
                return false;
            }

            LmdbNative.Check(result, "read value");
            valueBytes = CopyValue(value);
            return true;
        }
    }

    private static unsafe bool TryGetLength(
        IntPtr transaction,
        uint database,
        byte[] keyBytes,
        out int length)
    {
        fixed (byte* keyPointer = keyBytes)
        {
            var key = new LmdbNative.Value
            {
                Size = checked((nuint)keyBytes.Length),
                Data = (IntPtr)keyPointer
            };
            var result = LmdbNative.mdb_get(transaction, database, ref key, out var value);
            if (result == LmdbNative.NotFound)
            {
                length = 0;
                return false;
            }

            LmdbNative.Check(result, "read value length");
            length = checked((int)value.Size);
            return true;
        }
    }

    private static unsafe void Put(
        IntPtr transaction,
        uint database,
        byte[] keyBytes,
        byte[] valueBytes,
        uint flags)
    {
        fixed (byte* keyPointer = keyBytes)
        fixed (byte* valuePointer = valueBytes)
        {
            var key = new LmdbNative.Value
            {
                Size = checked((nuint)keyBytes.Length),
                Data = (IntPtr)keyPointer
            };
            var value = new LmdbNative.Value
            {
                Size = checked((nuint)valueBytes.Length),
                Data = (IntPtr)valuePointer
            };
            LmdbNative.Check(
                LmdbNative.mdb_put(transaction, database, ref key, ref value, flags),
                "write value");
        }
    }

    private static unsafe void Delete(IntPtr transaction, uint database, byte[] keyBytes)
    {
        fixed (byte* keyPointer = keyBytes)
        {
            var key = new LmdbNative.Value
            {
                Size = checked((nuint)keyBytes.Length),
                Data = (IntPtr)keyPointer
            };
            var result = LmdbNative.mdb_del(transaction, database, ref key, IntPtr.Zero);
            if (result != LmdbNative.NotFound)
            {
                LmdbNative.Check(result, "delete value");
            }
        }
    }

    private static unsafe byte[] CopyValue(LmdbNative.Value value)
    {
        var length = checked((int)value.Size);
        if (length == 0)
        {
            return Array.Empty<byte>();
        }
        return new ReadOnlySpan<byte>((void*)value.Data, length).ToArray();
    }

    private static byte[] ImageKey(string imageId)
        => Encoding.ASCII.GetBytes(imageId);

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ImageStoreMetadata
    {
        public int Version { get; set; } = StoreVersion;
        public int NextImageNumber { get; set; } = 1;
    }
}

internal sealed record LmdbImageIndex(
    IReadOnlyList<NoteImageAsset> Assets,
    IReadOnlyCollection<string> CorruptedImageIds,
    int NextImageNumber);

internal readonly record struct LmdbImageWrite(NoteImageAsset Asset, byte[] Bytes);
