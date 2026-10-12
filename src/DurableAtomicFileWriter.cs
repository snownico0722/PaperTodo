using System.IO;
using System.Threading;

namespace PaperTodo;

internal enum DurableAtomicWriteStage
{
    BeforeTempOpen,
    AfterTempWrite,
    AfterFlush,
    BeforeReplace
}

internal interface IDurableAtomicFileWriter
{
    void Write(
        string targetPath,
        byte[] bytes,
        Func<string, bool>? validateTemp = null);
}

internal interface IDurableAtomicFileOperations
{
    void FlushToDisk(FileStream stream);
    void Replace(string tempPath, string targetPath);
    void Delay(TimeSpan delay);
    void Delete(string path);
}

internal sealed class DurableAtomicFileWriter : IDurableAtomicFileWriter
{
    private const int ReplaceAttemptCount = 5;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(100);

    private sealed class SystemFileOperations : IDurableAtomicFileOperations
    {
        public void FlushToDisk(FileStream stream) =>
            stream.Flush(flushToDisk: true);

        public void Replace(string tempPath, string targetPath) =>
            File.Move(tempPath, targetPath, overwrite: true);

        public void Delay(TimeSpan delay) =>
            Thread.Sleep(delay);

        public void Delete(string path) =>
            File.Delete(path);
    }

    private static readonly IDurableAtomicFileOperations SystemOperations =
        new SystemFileOperations();

    private readonly Action<DurableAtomicWriteStage, string>? _faultInjector;
    private readonly IDurableAtomicFileOperations _fileOperations;

    internal static IDurableAtomicFileWriter Shared { get; } =
        new DurableAtomicFileWriter();

    internal DurableAtomicFileWriter(
        Action<DurableAtomicWriteStage, string>? faultInjector = null,
        IDurableAtomicFileOperations? fileOperations = null)
    {
        _faultInjector = faultInjector;
        _fileOperations = fileOperations ?? SystemOperations;
    }

    public void Write(
        string targetPath,
        byte[] bytes,
        Func<string, bool>? validateTemp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(bytes);

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = targetPath + ".tmp";
        _faultInjector?.Invoke(DurableAtomicWriteStage.BeforeTempOpen, targetPath);

        using (var stream = new FileStream(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 16 * 1024,
                   FileOptions.SequentialScan))
        {
            stream.Write(bytes);
            _faultInjector?.Invoke(DurableAtomicWriteStage.AfterTempWrite, targetPath);
            _fileOperations.FlushToDisk(stream);
        }

        _faultInjector?.Invoke(DurableAtomicWriteStage.AfterFlush, targetPath);

        if (validateTemp != null)
        {
            try
            {
                if (!validateTemp(tempPath))
                {
                    throw new InvalidDataException(
                        $"Durable temp validation failed for '{Path.GetFileName(targetPath)}'.");
                }
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        _faultInjector?.Invoke(DurableAtomicWriteStage.BeforeReplace, targetPath);
        ReplaceWithRetry(tempPath, targetPath);
    }

    private void ReplaceWithRetry(string tempPath, string targetPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _fileOperations.Replace(tempPath, targetPath);
                return;
            }
            catch (Exception ex) when (
                attempt < ReplaceAttemptCount &&
                IsRetryableReplaceFailure(ex))
            {
                _fileOperations.Delay(ReplaceRetryDelay);
            }
        }
    }

    private static bool IsRetryableReplaceFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private void TryDelete(string path)
    {
        try
        {
            _fileOperations.Delete(path);
        }
        catch
        {
            // A failed validation must never replace the old target. Temp cleanup is best-effort.
        }
    }
}
