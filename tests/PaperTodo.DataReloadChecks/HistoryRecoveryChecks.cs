using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using PaperTodo;

internal static partial class Program
{
    private static string WriteHistoryFixture(string directory, DateTime stamp,
        params (string Name, string Json)[] documents)
    {
        var backups = Path.Combine(directory, "backups");
        Directory.CreateDirectory(backups);
        var path = Path.Combine(backups,
            "PaperTodo-core-" + stamp.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var document in documents)
        {
            using var output = new StreamWriter(zip.CreateEntry(document.Name).Open(), new UTF8Encoding(false));
            output.Write(document.Json);
        }
        return path;
    }

    private static void HistoryRecoveryChecks()
    {
        var stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var number in new[] { 1, 2 })
        {
            using var scope = new Scope();
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var name = Path.GetFileName(store.FilePath);
            var older = NewState();
            older.Papers[0].Content = "older history";
            WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(-1), (name, store.SerializeState(older)));
            var latest = NewState();
            latest.Papers[0].Content = "latest valid history";
            var selected = WriteHistoryFixture(scope.DirectoryPath, stamp, (name, store.SerializeState(latest)),
                (number == 1 ? "data2.json" : "data.json", "{unrelated broken workspace"));
            WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(1), (name, "{}"));
            WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(2), ("data5.json", store.SerializeState(older)));
            var brokenZip = WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(3), (name, store.SerializeState(older)));
            File.WriteAllText(brokenZip, "not a ZIP");
            var crcZip = WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(4), (name, store.SerializeState(older)));
            var damagedBytes = File.ReadAllBytes(crcZip);
            var central = -1;
            for (var offset = damagedBytes.Length - 20; offset >= 0; offset--)
                if (damagedBytes[offset] == 0x50 && damagedBytes[offset + 1] == 0x4B &&
                    damagedBytes[offset + 2] == 0x01 && damagedBytes[offset + 3] == 0x02)
                { central = offset; break; }
            Require(central >= 0, "history CRC fixture missing central directory");
            damagedBytes[central + 16] ^= 0x80;
            File.WriteAllBytes(crcZip, damagedBytes);
            var archiveBytes = File.ReadAllBytes(selected);
            File.WriteAllText(store.FilePath, "{unrecoverable primary");
            File.WriteAllText(store.BackupPath, "{unrecoverable backup");
            var orphan = store.FilePath + ".orphaned.20260101.tmp";
            File.WriteAllText(orphan, "{partial");
            var otherPath = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared,
                number == 1 ? 2 : 1).FilePath;
            File.WriteAllText(otherPath, "other workspace must stay untouched");

            Throws<StateRecoveryException>(() => store.Load());
            var prompts = 0;
            Throws<OperationCanceledException>(() => store.Load(out _, time =>
            {
                prompts++;
                Require(time == stamp, "offer did not choose the latest valid workspace entry");
                return false;
            }));
            Require(prompts == 1 && !store.NeedsStartupRecoverySave && File.Exists(orphan) &&
                    File.ReadAllText(store.FilePath) == "{unrecoverable primary" &&
                    File.ReadAllBytes(selected).SequenceEqual(archiveBytes),
                "declined history recovery changed old data or tried another backup");

            var reject = true;
            var writer = new DurableAtomicFileWriter((stage, target) =>
            {
                if (reject && target == store.FilePath && stage == DurableAtomicWriteStage.BeforeReplace)
                    throw new IOException("Injected historical recovery write failure.");
            });
            var recovering = new StateStore(scope.DirectoryPath, writer, number);
            var state = recovering.Load(out var empty, time => time == stamp);
            Require(!empty && recovering.NeedsStartupRecoverySave &&
                    state.Papers[0].Content == "latest valid history" &&
                    !recovering.TryCollectProtectedImageIds(state, out _) && File.Exists(orphan),
                "history was not staged as a protected full recovery");
            Throws<IOException>(() => recovering.SaveJsonSync(recovering.SerializeState(state), 2));
            Require(File.ReadAllText(store.FilePath) == "{unrecoverable primary" && File.Exists(orphan),
                "failed historical recovery changed primary or cleaned orphans");
            reject = false;
            recovering.SaveJsonSync(recovering.SerializeState(state), 3);
            Require(recovering.Load().Papers[0].Content == "latest valid history" &&
                    !recovering.NeedsStartupRecoverySave && !File.Exists(orphan) &&
                    File.ReadAllText(otherPath) == "other workspace must stay untouched" &&
                    File.ReadAllText(store.BackupPath) == "{unrecoverable backup" &&
                    File.ReadAllBytes(selected).SequenceEqual(archiveBytes),
                "history restore did not commit exactly one workspace and retain its ZIP");
        }

        foreach (var changed in new[] { "primary", "archive", "shutdown" })
        {
            using var scope = new Scope();
            var json = scope.Store.SerializeState(NewState());
            var archive = WriteHistoryFixture(scope.DirectoryPath, stamp, ("data.json", json));
            File.WriteAllText(scope.Store.FilePath, "{broken");
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            bool Confirm(DateTime _)
            {
                if (changed == "primary") File.WriteAllText(store.FilePath, json);
                if (changed == "archive") File.AppendAllText(archive, "changed during prompt");
                if (changed == "shutdown") store.StopWritesForTermination();
                return true;
            }
            if (changed == "shutdown")
            {
                Throws<OperationCanceledException>(() => store.Load(out _, Confirm));
                Require(!store.NeedsStartupRecoverySave, "shutdown accepted pending history recovery");
            }
            else
            {
                var state = store.Load(out _, Confirm);
                Throws<IOException>(() => store.SaveJsonSync(store.SerializeState(state), 2));
            }
            Require(File.ReadAllText(store.FilePath) == (changed == "primary" ? json : "{broken"),
                "confirmation allowed stale or terminating historical recovery to overwrite primary");
        }

        foreach (var source in new[] { "primary", "backup", "temp", "none", "busy", "fresh" })
        {
            using var scope = new Scope();
            var json = File.ReadAllText(scope.Store.FilePath);
            WriteHistoryFixture(scope.DirectoryPath, stamp, ("data.json", source == "none" ? "{}" : json));
            if (source != "primary" && source != "busy") File.WriteAllText(scope.Store.FilePath, "{broken");
            if (source == "backup") File.WriteAllText(scope.Store.BackupPath, json);
            if (source == "temp") File.WriteAllText(scope.Store.FilePath + ".tmp", json);
            if (source == "fresh") File.Delete(scope.Store.FilePath);
            using var held = source == "busy"
                ? new FileStream(scope.Store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            bool UnexpectedPrompt(DateTime _) => throw new InvalidOperationException("Unexpected history offer: " + source);
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            if (source == "none") Throws<StateRecoveryException>(() => store.Load(out _, UnexpectedPrompt));
            else if (source == "busy") Throws<IOException>(() => store.Load(out _, UnexpectedPrompt));
            else store.Load(out _, UnexpectedPrompt);
        }

        // The same callback is used for main startup and numbered workspace entry.
        using (var scope = new Scope())
        {
            var json = scope.Store.SerializeState(NewState());
            WriteHistoryFixture(scope.DirectoryPath, stamp, ("data.json", json), ("data2.json", json));
            File.WriteAllText(scope.Store.FilePath, "{broken main");
            File.WriteAllText(Path.Combine(scope.DirectoryPath, "data2.json"), "{broken secondary");
            var offered = new List<int>();
            var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared,
                (store, time) => { offered.Add(store.WorkspaceNumber); return time == stamp; });
            var main = catalog.OpenInitial();
            var second = catalog.Open(2, catalog.Options, main.State);
            Require(offered.SequenceEqual(new[] { 1, 2 }) && !main.Store.NeedsStartupRecoverySave &&
                    second.Store.NeedsStartupRecoverySave && second.State.Papers[0].Content == "original",
                "workspace entry did not route historical recovery through the shared store");
        }
        Console.WriteLine("PASS historical recovery: latest valid ZIP entry, confirmation, isolation, retry and source/termination fences");
    }

    private static int RunHistoryDialogFixture()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        try
        {
            // Startup has no owning window. Exercise the real themed dialog and
            // both buttons through its modal Dispatcher loop.
            foreach (var accept in new[] { false, true })
            {
                Exception? failure = null;
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = app.Windows.OfType<System.Windows.Window>().Single();
                    try
                    {
                        Require(dialog.Owner == null && dialog.ShowInTaskbar, "startup recovery dialog is not reachable");
                        var nodes = Descendants(dialog).ToArray();
                        var text = nodes.OfType<System.Windows.Controls.TextBlock>().Select(block => block.Text);
                        Require(text.Any(value => value.Contains("2026-01-01")), "recovery dialog omitted backup time");
                        var label = Strings.Get(accept ? "HistoryRecoveryRestore" : "CommonCancel");
                        var button = nodes.OfType<System.Windows.Controls.Button>().Single(item => Equals(item.Content, label));
                        button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                }));
                var result = PaperNoticeDialog.ShowChoice(null, Strings.Get("HistoryRecoveryTitle"),
                    Strings.Format("HistoryRecoveryMessage", 2, "2026-01-01 12:00"),
                    Strings.Get("CommonCancel"), Strings.Get("HistoryRecoveryRestore"));
                if (failure != null) throw failure;
                Require(result == accept, "recovery dialog did not honor the selected action");
            }
            Console.WriteLine("PASS ownerless historical recovery dialog: restore and cancel");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }

        static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
        {
            yield return root;
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
                foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
