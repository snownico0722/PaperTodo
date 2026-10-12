using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using PaperTodo;

internal static class PluginPersistenceConcurrencyChecks
{
    private const string Provider = "sample.plugin";
    private const string OtherProvider = "other.plugin";
    private const string Paper = "paper-1";
    private const string Survivor = "paper-2";
    // These are deadlock guards, not performance thresholds. A fresh mutation must use the
    // debounce path; the deliberately much longer force interval cannot make that check pass.
    private const int GuardMilliseconds = 10_000;
    private const int LongForceMilliseconds = 60_000;

    internal static void BlockedWriteKeepsCachedStateAvailable()
    {
        using var directory = new TempDirectory();
        Seed(directory.Path, Provider, OtherProvider);
        using var writer = new ControlledWriter();
        using var store = NewStore(directory.Path, writer);
        var setting = new PaperBodyPluginSettingManifest { Id = "level", Type = "number" };
        var descriptor = Descriptor(Provider, setting);
        var otherDescriptor = Descriptor(OtherProvider, setting);
        AssertValue(store.ReadPaperState(Provider, Paper), 0, "warm first provider");
        AssertValue(store.ReadPaperState(OtherProvider, Paper), 0, "warm other provider");

        try
        {
            // One public mutation marks both providers dirty under the cache gate. Thus the
            // first batch contains both snapshots before either reaches the durable writer.
            RunWhileBlocked(writer, () => store.RemovePaperStateEverywhere(Survivor),
                "starting a provider batch waited for disk I/O");
            writer.WaitUntilBlocked();
            RunWhileBlocked(writer, () =>
            {
                AssertValue(store.ReadPaperState(Provider, Paper), 0, "read while its provider writes");
                AssertValue(store.ReadPaperState(OtherProvider, Paper), 0, "read while another provider writes");
                Assert(store.GetSettingValue(descriptor, setting).GetDouble() == 0 &&
                       store.GetSettingValue(otherDescriptor, setting).GetDouble() == 0,
                    "settings could not be read while a snapshot writes");
                store.SavePaperState(Provider, Paper, 1, Value(2));
                store.SaveRuntimeState(Provider, 2, Value(3));
                store.SavePaperState(OtherProvider, Paper, 1, Value(4));
                store.SaveRuntimeState(OtherProvider, 2, Value(7));
                store.SetSettingValue(descriptor, setting, JsonSerializer.SerializeToElement(5));
                store.SetSettingValue(otherDescriptor, setting, JsonSerializer.SerializeToElement(6));
                AssertValue(store.ReadPaperState(Provider, Paper), 2, "same-provider mutation is immediate");
                AssertValue(store.ReadRuntimeState(Provider), 3, "runtime mutation is immediate");
                AssertValue(store.ReadPaperState(OtherProvider, Paper), 4, "other-provider mutation is immediate");
                Assert(store.GetSettingValue(descriptor, setting).GetDouble() == 5 &&
                       store.GetSettingValue(otherDescriptor, setting).GetDouble() == 6,
                    "settings mutations were not immediately visible");
            }, "cached plugin reads and mutations waited for disk I/O");
            Assert(!writer.IsReleased, "cached calls were only checked after releasing the writer");
            writer.Release();

            // Two old snapshots, then two new ones, without Dispose or the force interval
            // helping. The second old snapshot is only serialized after the mutations above.
            writer.WaitForCompletedWrites(4);
            foreach (var attempt in new[] { 1, 2 })
            {
                // Both providers start with these same values; their HashSet order is irrelevant.
                using var queuedSnapshot = writer.ReadAttempt(attempt);
                Assert(queuedSnapshot.RootElement.GetProperty("papers").GetProperty(Paper)
                           .GetProperty("data").GetProperty("value").GetInt32() == 0 &&
                       queuedSnapshot.RootElement.GetProperty("runtime").GetProperty("data")
                           .GetProperty("value").GetInt32() == 0 &&
                       !queuedSnapshot.RootElement.GetProperty("settings").TryGetProperty(setting.Id, out _),
                    "a queued snapshot shared mutable paper, runtime or settings state with the cache");
            }
            AssertSavedPaper(directory.Path, Provider, Paper, 2);
            AssertSavedPaper(directory.Path, OtherProvider, Paper, 4);
            using var saved = ReadSaved(directory.Path, Provider);
            Assert(saved.RootElement.GetProperty("runtime").GetProperty("stateVersion").GetInt32() == 2 &&
                   saved.RootElement.GetProperty("runtime").GetProperty("data").GetProperty("value").GetInt32() == 3,
                "an older completed snapshot lost the newer runtime state");
            using var otherSaved = ReadSaved(directory.Path, OtherProvider);
            Assert(saved.RootElement.GetProperty("settings").GetProperty(setting.Id).GetDouble() == 5 &&
                   otherSaved.RootElement.GetProperty("settings").GetProperty(setting.Id).GetDouble() == 6 &&
                   otherSaved.RootElement.GetProperty("runtime").GetProperty("data").GetProperty("value").GetInt32() == 7,
                "new settings or runtime state were not persisted after the older batch");
        }
        finally
        {
            writer.Release();
        }
    }

    internal static void OldSnapshotCannotRestoreDeletedPaper()
    {
        using var directory = new TempDirectory();
        Seed(directory.Path, Provider, OtherProvider);
        using var writer = new ControlledWriter();
        using var store = NewStore(directory.Path, writer);
        _ = store.ReadPaperState(Provider, Paper);
        _ = store.ReadPaperState(OtherProvider, Paper);

        try
        {
            store.SaveRuntimeState(Provider, 2, Value(1));
            writer.WaitUntilBlocked();
            RunWhileBlocked(writer, () =>
            {
                store.RemovePaperStateEverywhere(Paper);
                foreach (var provider in new[] { Provider, OtherProvider })
                {
                    Assert(!store.TryReadPaperState(provider, Paper, out _),
                        "paper deletion was not immediately visible in the cache");
                    AssertValue(store.ReadPaperState(provider, Survivor), 9,
                        "deletion removed an unrelated paper");
                }
            }, "plugin cleanup waited for an in-progress snapshot write");
            writer.Release();
            writer.WaitForCompletedWrites(3);

            foreach (var provider in new[] { Provider, OtherProvider })
            {
                using var saved = ReadSaved(directory.Path, provider);
                Assert(!saved.RootElement.GetProperty("papers").TryGetProperty(Paper, out _),
                    "an older snapshot restored the deleted paper on disk");
                AssertSavedPaper(directory.Path, provider, Survivor, 9);
            }
        }
        finally
        {
            writer.Release();
        }
    }

    internal static void OldFailureDoesNotSpendNewMutationRetry()
    {
        using var directory = new TempDirectory();
        Seed(directory.Path, Provider);
        using var writer = new ControlledWriter(failedAttempts: 2);
        using var store = NewStore(directory.Path, writer, forceMilliseconds: 100);

        try
        {
            store.SavePaperState(Provider, Paper, 1, Value(1));
            writer.WaitUntilBlocked();
            RunWhileBlocked(writer,
                () => store.SavePaperState(Provider, Paper, 1, Value(2)),
                "a new mutation waited for the failing old write");
            writer.Release();

            // The old generation fails, then the new generation's initial attempt fails.
            // The new generation still owns its one delayed retry, which commits successfully.
            writer.WaitForCompletedWrites(3);
            Assert(writer.AttemptCount == 3, "plugin retry budget produced unexpected extra writes");
            Assert(writer.AttemptPaperValue(1, Paper) == 1 &&
                   writer.AttemptPaperValue(2, Paper) == 2 &&
                   writer.AttemptPaperValue(3, Paper) == 2,
                "retry wrote a stale snapshot instead of the latest mutation");
            AssertSavedPaper(directory.Path, Provider, Paper, 2);
        }
        finally
        {
            writer.Release();
        }
    }

    internal static void DisposeWaitsAndFlushesLatestState()
    {
        using var directory = new TempDirectory();
        Seed(directory.Path, Provider);
        using var writer = new ControlledWriter();
        using var store = NewStore(directory.Path, writer);
        PendingCall? disposal = null;

        try
        {
            store.SavePaperState(Provider, Paper, 1, Value(1));
            writer.WaitUntilBlocked();
            RunWhileBlocked(writer,
                () => store.SavePaperState(Provider, Paper, 1, Value(2)),
                "mutation before normal disposal waited for disk I/O");
            disposal = new PendingCall(() =>
            {
                store.Dispose();
                Assert(writer.IsReleased, "Dispose returned while a plugin write was still blocked");
                AssertSavedPaper(directory.Path, Provider, Paper, 2);
            });
            disposal.WaitUntilBlocked("normal disposal did not wait for the active writer");
        }
        finally
        {
            writer.Release();
            disposal?.Complete("normal disposal did not finish after releasing the writer");
        }

        writer.AssertSerializedWrites();
        DisposeRetriesExhaustedState();
    }

    private static void DisposeRetriesExhaustedState()
    {
        using var directory = new TempDirectory();
        Seed(directory.Path, Provider);
        using var writer = new ControlledWriter(failedAttempts: 2);
        writer.Release();
        using var store = NewStore(directory.Path, writer, forceMilliseconds: 100);
        store.SavePaperState(Provider, Paper, 1, Value(1));
        writer.WaitForCompletedWrites(2);

        // No mutation resets this generation's exhausted background retry budget. Normal
        // disposal must still make its final attempt and persist the already-dirty state.
        store.Dispose();
        Assert(writer.AttemptCount == 3, "normal disposal skipped a provider whose background retries were exhausted");
        AssertSavedPaper(directory.Path, Provider, Paper, 1);
    }

    internal static void SuppressionStopsWritesAfterReturning()
    {
        using var directory = new TempDirectory();
        Seed(directory.Path, Provider);
        using var writer = new ControlledWriter();
        using var store = NewStore(directory.Path, writer);
        PendingCall? suppression = null;
        var writesAtSuppression = 0;

        try
        {
            store.SavePaperState(Provider, Paper, 1, Value(1));
            writer.WaitUntilBlocked();
            RunWhileBlocked(writer,
                () => store.SavePaperState(Provider, Paper, 1, Value(2)),
                "mutation before shutdown suppression waited for disk I/O");
            suppression = new PendingCall(() =>
            {
                store.SuppressFinalFlushOnDispose();
                Assert(writer.IsReleased, "suppression returned before the active write finished");
                writesAtSuppression = writer.AttemptCount;
            });
            suppression.WaitUntilBlocked("shutdown suppression did not wait for the active writer");
        }
        finally
        {
            writer.Release();
            suppression?.Complete("shutdown suppression did not finish after releasing the writer");
        }

        // A queued callback can win the writer gate before suppression does. The contract starts
        // when suppression returns; no later mutation or normal Dispose may start another write.
        store.SavePaperState(Provider, Paper, 1, Value(3));
        AssertValue(store.ReadPaperState(Provider, Paper), 3, "suppression unexpectedly disabled memory state");
        store.Dispose();
        Assert(writer.AttemptCount == writesAtSuppression,
            "a plugin write started after shutdown suppression returned");
        writer.AssertSerializedWrites();
    }

    private static PaperBodyPluginDataStore NewStore(
        string directory,
        IDurableAtomicFileWriter writer,
        int forceMilliseconds = LongForceMilliseconds) =>
        new(directory, writer, saveDebounceMilliseconds: 0, forceSaveMilliseconds: forceMilliseconds);

    private static void Seed(string directory, params string[] providers)
    {
        using var seed = new PaperBodyPluginDataStore(
            directory, DurableAtomicFileWriter.Shared, LongForceMilliseconds, LongForceMilliseconds);
        foreach (var provider in providers)
        {
            seed.SavePaperState(provider, Paper, 1, Value(0));
            seed.SavePaperState(provider, Survivor, 1, Value(9));
            seed.SaveRuntimeState(provider, 1, Value(0));
        }
    }

    private static string Value(int value) => "{\"value\":" + value + "}";

    private static PaperBodyPluginDescriptor Descriptor(string provider, PaperBodyPluginSettingManifest setting) =>
        new(provider, "Test", "", new Version(1, 0), "2.2", 1, PaperBodyPluginKind.Web,
            default, new HashSet<string>(), "", "", "", Manifest: new() { Settings = [setting] });

    private static void AssertValue(PaperBodyStoredState state, int expected, string message)
    {
        using var parsed = JsonDocument.Parse(state.Json);
        Assert(parsed.RootElement.GetProperty("value").GetInt32() == expected, message);
    }

    private static JsonDocument ReadSaved(string directory, string provider) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "data", provider + ".json")));

    private static void AssertSavedPaper(string directory, string provider, string paper, int expected)
    {
        using var saved = ReadSaved(directory, provider);
        Assert(saved.RootElement.GetProperty("papers").GetProperty(paper)
                   .GetProperty("data").GetProperty("value").GetInt32() == expected,
            $"latest state for {provider}/{paper} was not persisted");
    }

    private static void RunWhileBlocked(ControlledWriter writer, Action action, string message)
    {
        var call = new PendingCall(action);
        try
        {
            call.Complete(message);
        }
        finally
        {
            // A regressed cache lock must fail the check without stranding its worker or the
            // datastore's using/Dispose cleanup behind the deliberately blocked disk operation.
            if (!call.IsComplete)
            {
                writer.Release();
                call.Complete("cached-state call did not unwind after releasing the writer");
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PendingCall
    {
        private readonly Thread _thread;
        private ExceptionDispatchInfo? _failure;

        internal PendingCall(Action action)
        {
            _thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { _failure = ExceptionDispatchInfo.Capture(ex); }
            }) { IsBackground = true };
            _thread.Start();
        }

        internal bool IsComplete => !_thread.IsAlive;

        internal void Complete(string message)
        {
            Assert(_thread.Join(GuardMilliseconds), message);
            _failure?.Throw();
        }

        internal void WaitUntilBlocked(string message)
        {
            // Observe an actual wait by this dedicated lifecycle caller. Merely signaling just
            // before Dispose/Suppress, or sleeping briefly, would not prove it reached the call.
            Assert(SpinWait.SpinUntil(() => IsComplete ||
                (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0, GuardMilliseconds), message);
            if (IsComplete)
            {
                Complete(message);
                throw new InvalidOperationException(message);
            }
        }
    }

    private sealed class ControlledWriter(int failedAttempts = 0) : IDurableAtomicFileWriter, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _released = new();
        private readonly object _recordsGate = new();
        private readonly Dictionary<int, byte[]> _attempts = new();
        private int _attemptCount;
        private int _completedWrites;
        private int _activeWrites;
        private int _overlappingWrites;

        internal int AttemptCount => Volatile.Read(ref _attemptCount);
        internal bool IsReleased => _released.IsSet;
        internal void Release() => _released.Set();

        internal void WaitUntilBlocked() =>
            Assert(_entered.Wait(GuardMilliseconds), "background plugin save never reached the durable writer");

        internal void WaitForCompletedWrites(int expected)
        {
            Assert(SpinWait.SpinUntil(() => Volatile.Read(ref _completedWrites) >= expected, GuardMilliseconds),
                $"plugin saves did not finish through the scheduled path: expected {expected}, saw {AttemptCount}");
            AssertSerializedWrites();
        }

        internal void AssertSerializedWrites() =>
            Assert(Volatile.Read(ref _overlappingWrites) == 0, "plugin snapshots entered the durable writer concurrently");

        internal int AttemptPaperValue(int attempt, string paper)
        {
            using var parsed = ReadAttempt(attempt);
            return parsed.RootElement.GetProperty("papers").GetProperty(paper)
                .GetProperty("data").GetProperty("value").GetInt32();
        }

        internal JsonDocument ReadAttempt(int attempt)
        {
            byte[] bytes;
            lock (_recordsGate) bytes = _attempts[attempt];
            return JsonDocument.Parse(bytes);
        }

        public void Write(string targetPath, byte[] bytes, Func<string, bool>? validateTemp = null)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);
            if (Interlocked.Increment(ref _activeWrites) != 1)
                Interlocked.Exchange(ref _overlappingWrites, 1);
            lock (_recordsGate) _attempts.Add(attempt, bytes);
            try
            {
                // Exercise actual temp-file creation and serialization bytes. Pause exactly at
                // the real writer's disk-flush boundary, before replacement of the normal file.
                var durable = new DurableAtomicFileWriter((stage, _) =>
                {
                    if (stage != DurableAtomicWriteStage.AfterTempWrite) return;
                    if (attempt == 1)
                    {
                        _entered.Set();
                        _released.Wait();
                    }
                    if (attempt <= failedAttempts)
                        throw new IOException("Injected failure of a plugin snapshot write.");
                });
                durable.Write(targetPath, bytes, validateTemp);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
                Interlocked.Increment(ref _completedWrites);
            }
        }

        public void Dispose()
        {
            _released.Set();
            _entered.Dispose();
            _released.Dispose();
        }
    }
}
