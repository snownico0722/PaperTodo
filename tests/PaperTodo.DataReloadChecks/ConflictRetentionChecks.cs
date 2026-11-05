using System.Globalization;
using System.IO;
using PaperTodo;

internal static partial class Program
{
    private static void ConflictArchiveRetentionChecks()
    {
        static (string Original, string Diff) Group(
            string directory, DateTime utcNow, int daysAgo, int number,
            string stem = "data")
        {
            var stamp = utcNow.AddDays(-daysAgo)
                .ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            var prefix = Path.Combine(directory,
                $"{stem}conflict-{stamp}-{number:x8}");
            var original = prefix + ".json";
            var diff = prefix + ".diff.json";
            File.WriteAllText(original, "{\"papers\":[]}");
            File.WriteAllText(diff, "{\"conflicts\":[]}");
            return (original, diff);
        }

        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        using (var scope = new Scope())
        {
            var old = Enumerable.Range(0, 5)
                .Select(i => Group(scope.DirectoryPath, now, 10 - i, i + 1)).ToArray();
            scope.Store.TryPruneConflictArchives();
            Require(old.All(p => File.Exists(p.Original) && File.Exists(p.Diff)),
                "five old groups must survive regardless of their age");

            var young = Enumerable.Range(0, 3)
                .Select(i => Group(scope.DirectoryPath, now, 1, i + 11)).ToArray();
            var unrelated = Group(scope.DirectoryPath, now, 100, 1, "data2");
            scope.Store.TryPruneConflictArchives();
            Require(old.Take(3).All(p => !File.Exists(p.Original) && !File.Exists(p.Diff)) &&
                    old.Skip(3).All(p => File.Exists(p.Original) && File.Exists(p.Diff)) &&
                    young.All(p => File.Exists(p.Original) && File.Exists(p.Diff)) &&
                    File.Exists(unrelated.Original) && File.Exists(unrelated.Diff),
                "retention failed to prune the oldest eligible pairs or crossed workspaces");
            Require(Directory.GetFiles(scope.DirectoryPath, "dataconflict-*").Length == 10,
                "pair retention did not stop at five complete groups");
        }

        using (var scope = new Scope())
        {
            var young = Enumerable.Range(0, 12)
                .Select(i => Group(scope.DirectoryPath, now.AddMinutes(i), 0, i + 1)).ToArray();
            scope.Store.TryPruneConflictArchives();
            Require(young.Take(7).All(pair => !File.Exists(pair.Original) && !File.Exists(pair.Diff)) &&
                    young.Skip(7).All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                "same-day conflict groups were not reduced to the latest five complete pairs");
            Require(Directory.GetFiles(scope.DirectoryPath, "dataconflict-*").Length == 10,
                "young archive retention did not stop at five complete groups");

            // An older archive added later must not displace one of the newest five.
            var older = Group(scope.DirectoryPath, now, 3, 21);
            scope.Store.TryPruneConflictArchives();
            Require(!File.Exists(older.Original) && !File.Exists(older.Diff) &&
                    young.Skip(7).All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                "retention used file arrival order instead of the archive timestamp");
        }

        // A crashed write may leave just one half of a conflict pair or
        // a temporary file. Neither counts toward the five COMPLETE records.
        using (var scope = new Scope())
        {
            var old = Enumerable.Range(0, 5)
                .Select(i => Group(scope.DirectoryPath, now, 15 - i, i + 1)).ToArray();
            var partial = Group(scope.DirectoryPath, now, 20, 19);
            File.Delete(partial.Diff);
            var interrupted = Path.Combine(scope.DirectoryPath,
                $"dataconflict-{now.AddDays(-22):yyyyMMddHHmmssfff}-00000020.diff.json.tmp");
            File.WriteAllText(interrupted, "{unfinished");
            scope.Store.TryPruneConflictArchives();
            Require(old.All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)) &&
                    File.Exists(partial.Original) && File.Exists(interrupted),
                "incomplete records consumed full-group retention slots or were treated as completed data");
        }

        // Even with excess complete pairs, an additional incomplete record must
        // not change the age-ordered number of whole pairs that survive.
        using (var scope = new Scope())
        {
            var old = Enumerable.Range(0, 6)
                .Select(i => Group(scope.DirectoryPath, now, 15 - i, i + 1)).ToArray();
            var orphan = Group(scope.DirectoryPath, now, 22, 19);
            File.Delete(orphan.Diff);
            scope.Store.TryPruneConflictArchives();
            Require(!File.Exists(old[0].Original) && !File.Exists(old[0].Diff) &&
                    old.Skip(1).All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)) &&
                    File.Exists(orphan.Original),
                "an incomplete group caused too many complete recovery records to be deleted");
        }

        // Two files do not automatically make a usable *complete* archive.
        // An unreadable original or malformed diff cannot evict good pairs.
        using (var scope = new Scope())
        {
            var old = Enumerable.Range(0, 5)
                .Select(i => Group(scope.DirectoryPath, now, 15 - i, i + 1)).ToArray();
            var invalid = Group(scope.DirectoryPath, now, 20, 19);
            File.WriteAllText(invalid.Diff, "{incomplete diff");
            scope.Store.TryPruneConflictArchives();
            Require(old.All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)) &&
                    File.Exists(invalid.Original) && File.Exists(invalid.Diff),
                "an invalid conflict pair incorrectly displaced one of five usable whole records");
        }

        // A locked archive cannot be verified as a complete recovery pair.
        // Do not delete other healthy records just to satisfy the count.
        using (var scope = new Scope())
        {
            var old = Enumerable.Range(0, 6)
                .Select(i => Group(scope.DirectoryPath, now, 20 - i, i + 1)).ToArray();
            using (var locked = new FileStream(old[0].Original,
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                scope.Store.TryPruneConflictArchives();
                Require(old.All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                    "an inaccessible archive caused deletion of another complete record");
            }
            // When access returns, exactly one eligible complete pair is retired.
            scope.Store.TryPruneConflictArchives();
            Require(!File.Exists(old[0].Original) && !File.Exists(old[0].Diff) &&
                    old.Skip(1).All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                "retention failed to prune precisely one group after the lock was released");
        }

        // A readable original can still reject deletion. A failed retirement must
        // leave other complete recovery points intact, regardless of deletion order.
        using (var scope = new Scope())
        {
            var pairs = Enumerable.Range(0, 7)
                .Select(i => Group(scope.DirectoryPath, now.AddMinutes(i), 0, i + 1)).ToArray();
            using (var locked = new FileStream(pairs[0].Original,
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                scope.Store.TryPruneConflictArchives();
                Require(File.Exists(pairs[0].Original) &&
                        pairs.Skip(1).All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                    "a failed archive deletion evicted another complete recovery point");
            }
            scope.Store.TryPruneConflictArchives();
            Require(pairs.Skip(2).All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)) &&
                    pairs.Count(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)) == 5,
                "retention retry failed to preserve exactly the newest five complete groups");
        }

        Console.WriteLine("PASS conflict retention keeps the latest five complete groups; failures and workspaces stay isolated");
    }
}
