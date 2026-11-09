using System.Globalization;
using System.IO;
using System.Threading;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginDataStore
{
    private bool _workspaceDetached;

    internal PaperBodyPluginDataStore(string pluginRoot, int workspaceNumber) : this(pluginRoot)
    {
        if (workspaceNumber is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(workspaceNumber));
        DataRoot = Path.Combine(pluginRoot, "data" +
            (workspaceNumber == 1 ? "" : workspaceNumber.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Called after old Body/Runtime leases have ended. Wait for the writer, stop further
    /// requests to this store, and surface a failed final flush instead of discarding dirty data.
    /// A failed switch can reactivate this same store and its still-intact cache.
    /// </summary>
    internal void DetachForWorkspaceSwitch()
    {
        lock (_writeGate)
        {
            SaveSnapshot[] snapshots;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _workspaceDetached = true;
                _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _forceSaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                snapshots = CaptureDirtySnapshots(includeFailed: true);
            }
            foreach (var snapshot in snapshots) WriteSnapshot(snapshot);
            lock (_gate)
            {
                if (_dirtyProviderIds.Count == 0) return;
                _workspaceDetached = false;
                UpdateSaveTimersAfterFlush();
                throw new IOException("Plugin data could not be saved; the current workspace was retained.");
            }
        }
    }

    internal void ResumeAfterWorkspaceSwitch()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _workspaceDetached = false;
            UpdateSaveTimersAfterFlush();
        }
    }

    internal void CopyPaperStateTo(PaperBodyPluginDataStore destination, string paperId, string destinationPaperId)
    {
        var states = new Dictionary<string, PaperDataState>(StringComparer.Ordinal);
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var providerId in EnumerateStoredProviderIds())
                if (Load(providerId).Papers.TryGetValue(paperId, out var state))
                    states[providerId] = state;
        }

        // The target workspace is inactive. Persist only this paper's entries;
        // settings, Runtime state and every other paper stay owned by the target.
        lock (destination._writeGate)
        lock (destination._gate)
        {
            destination.ThrowIfDisposed();
            foreach (var (providerId, state) in states)
            {
                var current = destination.Load(providerId);
                var document = new PluginDataDocument
                {
                    StorageVersion = current.StorageVersion,
                    Settings = new(current.Settings, StringComparer.Ordinal),
                    Runtime = current.Runtime,
                    Papers = new(current.Papers, StringComparer.Ordinal)
                    { [destinationPaperId] = state }
                };
                destination.SaveNow(providerId, document);
                destination._cache[providerId] = document;
            }
        }
    }
}
