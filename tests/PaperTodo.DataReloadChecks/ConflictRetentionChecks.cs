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

        Console.WriteLine("PASS conflict retention requires both age and count; pairs and workspaces stay isolated");
    }
}
