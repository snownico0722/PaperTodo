using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperTodo;

internal static partial class Program
{
    private static StateMergeResolution ResolvePreview(StateReloadCommit preview,
        ConflictChoice choice = ConflictChoice.KeepMemory) => StateMergeResolution.Resolve(
            preview.State!, preview.Conflicts, preview.ExternalJson, DataMergeConflictPolicies.Ask, _ => choice);

    private static void MergePolicies()
    {
        Require(new AppState().DataMergeConflictPolicy == DataMergeConflictPolicies.Ask,
            "new and legacy documents must default to asking");
        foreach (var policy in new string?[] { "ask", "external", "internal", null, "unrecognized" })
        foreach (var manual in new[] { ConflictChoice.KeepMemory, ConflictChoice.KeepExternal })
        {
            using var s = new Scope();
            s.Memory.Papers[0].Content = "local conflict";
            s.Memory.Papers[0].Title = "LOCAL";
            var disk = s.Read();
            disk["papers"]![0]!["content"] = "external conflict";
            disk["papers"]![1]!["items"]![0]!["text"] = "external independent";
            s.Write(disk);
            var sourceBytes = File.ReadAllBytes(s.Store.FilePath);
            var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
            Require(preview.State != null && preview.Conflicts is { Count: 1 }, "expected one body conflict");
            var calls = 0;
            var resolution = StateMergeResolution.Resolve(preview.State!, preview.Conflicts, preview.ExternalJson,
                policy, _ => { calls++; return manual; });
            var interactive = DataMergeConflictPolicies.Normalize(policy) == DataMergeConflictPolicies.Ask;
            var external = interactive ? manual == ConflictChoice.KeepExternal : policy == "external";
            Require(calls == (interactive ? 1 : 0), "automatic policy prompted or fallback failed to ask");
            Require(resolution.State.Papers[0].Content == (external ? "external conflict" : "local conflict") &&
                resolution.State.Papers[0].Title == "LOCAL" &&
                resolution.State.Papers[1].Items[0].Text == "external independent",
                "policy changed a nonconflicting field or selected the wrong conflict value");
            var files = s.Store.CommitExternalMerge(resolution, preview.ObservedDiskJson!, 2);
            var archive = J(File.ReadAllText(Path.Combine(s.DirectoryPath, files.ConflictDiffFile!)));
            var entry = archive["conflicts"]!.AsArray().Single()!;
            Require(Str(entry["resolution"]) == (external ? "external" : "paperTodo") &&
                Str(entry["resolutionSource"]) == (interactive ? "user" : "policy") &&
                Str(archive["policy"]) == DataMergeConflictPolicies.Normalize(policy),
                "archive disagrees with the actual choice, choice source or selected policy");
            Require(Str(s.Read()["papers"]![0]!["content"]) == resolution.State.Papers[0].Content &&
                File.ReadAllBytes(Path.Combine(s.DirectoryPath, files.ConflictFile!)).SequenceEqual(sourceBytes),
                "committed data or preserved external bytes disagree with the accepted merge");
        }
    }

    private static void MergePoliciesMissingBaseline()
    {
        foreach (var policy in new[] { "ask", "external", "internal" })
        {
            using var s = new Scope();
            s.Memory.Papers[0].Content = "local";
            var disk = s.Read(); disk["papers"]![0]!["content"] = "external"; s.Write(disk);
            var preview = s.Store.PreviewExternalMerge(s.Memory, 2);
            var calls = 0;
            var result = StateMergeResolution.Resolve(preview.State!, preview.Conflicts, preview.ExternalJson, policy,
                bucket => { Require(bucket is UnknownBaselineBucket, "fabricated a baseline"); calls++; return ConflictChoice.KeepMemory; });
            Require(calls == 1 && result.State.Papers[0].Content == "local" &&
                result.Conflicts.Single().Source == "user", "missing baseline silently used an automatic preference");
        }
    }

    private static void MergeMixedPolicyChoices()
    {
        using var s = new Scope();
        s.Memory.Papers[0].Content = "local note";
        s.Memory.Papers[1].Items[0].Text = "local todo";
        var disk = s.Read();
        disk["papers"]![0]!["content"] = "external note";
        disk["papers"]![1]!["items"]![0]!["text"] = "external todo";
        s.Write(disk);
        var preview = s.Store.PreviewExternalMerge(s.Memory, 2, s.BaselineJson);
        var result = StateMergeResolution.Resolve(preview.State!, preview.Conflicts, preview.ExternalJson, "ask",
            bucket => bucket is PaperBucket { PaperId: "note-a" } ? ConflictChoice.KeepExternal : ConflictChoice.KeepMemory);
        var files = s.Store.CommitExternalMerge(result, preview.ObservedDiskJson!, 2);
        var records = J(File.ReadAllText(Path.Combine(s.DirectoryPath, files.ConflictDiffFile!)))["conflicts"]!.AsArray();
        Require(result.State.Papers[0].Content == "external note" && result.State.Papers[1].Items[0].Text == "local todo" &&
            records.Any(r => Str(r!["path"]).Contains("note-a", StringComparison.Ordinal) && Str(r["resolution"]) == "external") &&
            records.Any(r => Str(r!["path"]).Contains("todo", StringComparison.Ordinal) && Str(r["resolution"]) == "paperTodo"),
            "mixed user selections were collapsed to one archive preference");
    }

    private static void MergeNormalizeAfterChoices()
    {
        using var s = new Scope();
        var baseline = s.Read(); baseline["maxTitleLength"] = 10;
        var memory = baseline.Deserialize<AppState>(StateStore.JsonOptions)!;
        memory.MaxTitleLength = 6;
        var external = (JsonObject)baseline.DeepClone();
        external["maxTitleLength"] = 20;
        const string title = "external-long-title";
        external["papers"]![0]!["title"] = title;
        s.Write(external);
        var preview = s.Store.PreviewExternalMerge(memory, 2, baseline.ToJsonString());
        Require(preview.State != null && preview.Conflicts!.Any(c => c.Path == "maxTitleLength"),
            "expected a title-limit setting conflict");
        var resolution = StateMergeResolution.Resolve(preview.State!, preview.Conflicts, preview.ExternalJson,
            "external", _ => throw new InvalidOperationException("automatic preference unexpectedly prompted"));
        s.Store.CommitExternalMerge(resolution, preview.ObservedDiskJson!, 2);
        Require(resolution.State.MaxTitleLength == 20 && resolution.State.Papers[0].Title == title &&
            Str(s.Read()["papers"]![0]!["title"]) == title,
            "the provisional internal limit truncated independent external text before the final choice");
    }

    private static void MergeIdentityOrderChecks()
    {
        // Compare observable ordering against the previous algorithm, including empty inputs,
        // independent additions, and anchors at either end. Fixed seed, no timing assertion.
        static void Reference(List<string> result, IReadOnlyList<string> other)
        {
            for (var i = 0; i < other.Count; i++)
            {
                if (result.Contains(other[i], StringComparer.Ordinal)) continue;
                var previous = i > 0 ? result.IndexOf(other[i - 1]) : -1;
                var next = other.Skip(i + 1).Select(item => result.IndexOf(item)).FirstOrDefault(index => index >= 0, -1);
                result.Insert(previous >= 0 ? previous + 1 : next >= 0 ? next : result.Count, other[i]);
            }
        }
        var random = new Random(340351);
        var ids = Enumerable.Range(0, 40).Select(i => "id-" + i).ToArray();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var original = ids.OrderBy(_ => random.Next()).Take(random.Next(ids.Length + 1)).ToList();
            var incoming = ids.OrderBy(_ => random.Next()).Take(random.Next(ids.Length + 1)).ToArray();
            var expected = original.ToList(); Reference(expected, incoming);
            StateJsonMerge.AddMissingIdentities(original, incoming);
            Require(original.SequenceEqual(expected), "identity insertion moved an independent addition away from its anchor");
        }
        foreach (var count in new[] { 1000, 10000, 50000 })
        {
            var existing = Enumerable.Range(0, count).Select(i => "id-" + i).ToList();
            var incoming = existing.SelectMany(id => new[] { id, id + "-new" }).ToArray();
            var clock = Stopwatch.StartNew();
            StateJsonMerge.AddMissingIdentities(existing, incoming);
            Require(existing.SequenceEqual(incoming), "large identity insertion lost or reordered entities");
            Console.WriteLine($"MEASURE identity insertion: {count} existing + {count} new, {clock.Elapsed.TotalMilliseconds:F2} ms");
        }
    }
}
