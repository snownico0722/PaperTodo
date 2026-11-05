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
            scope.Store.TryPruneConflictArchives(now);
            Require(old.All(p => File.Exists(p.Original) && File.Exists(p.Diff)),
                "five old groups must survive regardless of their age");

            var young = Enumerable.Range(0, 3)
                .Select(i => Group(scope.DirectoryPath, now, 1, i + 11)).ToArray();
            var unrelated = Group(scope.DirectoryPath, now, 100, 1, "data2");
            scope.Store.TryPruneConflictArchives(now);
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
                .Select(i => Group(scope.DirectoryPath, now, 1, i + 1)).ToArray();
            scope.Store.TryPruneConflictArchives(now);
            Require(young.All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                "a large number of young conflict groups must be retained");
            // Crossing the exact 3-day boundary is still NOT 'older than 3 days'.
            var boundary = Group(scope.DirectoryPath, now, 3, 21);
            scope.Store.TryPruneConflictArchives(now);
            Require(File.Exists(boundary.Original) && File.Exists(boundary.Diff),
                "an exactly three-day-old pair was deleted");
        }

        using (var scope = new Scope())
        {
            var old = Enumerable.Range(0, 7)
                .Select(i => Group(scope.DirectoryPath, now, 20 - i, i + 1)).ToArray();
            scope.Store.TryPruneConflictArchives(now);
            Require(old.Take(2).All(pair => !File.Exists(pair.Original) && !File.Exists(pair.Diff)) &&
                    old.Skip(2).All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                "aged excess groups were not pruned as whole original/diff pairs");
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
            scope.Store.TryPruneConflictArchives(now);
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
            scope.Store.TryPruneConflictArchives(now);
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
            scope.Store.TryPruneConflictArchives(now);
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
                scope.Store.TryPruneConflictArchives(now);
                Require(old.All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                    "an inaccessible archive caused deletion of another complete record");
            }
            // When access returns, exactly one eligible complete pair is retired.
            scope.Store.TryPruneConflictArchives(now);
            Require(!File.Exists(old[0].Original) && !File.Exists(old[0].Diff) &&
                    old.Skip(1).All(pair => File.Exists(pair.Original) && File.Exists(pair.Diff)),
                "retention failed to prune precisely one group after the lock was released");
        }

        Console.WriteLine("PASS conflict retention requires both age and count; pairs and workspaces stay isolated");
    }
}
