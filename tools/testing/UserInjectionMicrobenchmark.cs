using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PaperTodo;

// Optional --benchmark mode only. No timing threshold participates in correctness checks.
internal static class UserInjectionMicrobenchmark
{
    private static double _sink;

    internal static void Run()
    {
        const int iterations = 10_000_000;
        const int repeats = 7;
        var random = new Random(62951);
        var inputs = Enumerable.Range(0, 256).Select(_ => random.NextDouble() * 100).ToArray();
        var cases = new (string Name, Func<double[], int, double> Run)[]
        {
            ("literal read", LiteralRead),
            ("typed slot, no override", SlotRead),
            ("original formula", Formula),
            ("Resolve, no override", ResolvedFormula)
        };
        foreach (var entry in cases) Volatile.Write(ref _sink, entry.Run(inputs, 500_000));
        var samples = cases.Select(_ => new List<double>()).ToArray();
        var allocations = new long[cases.Length];
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            // Rotate order so one implementation is not always measured first.
            for (var offset = 0; offset < cases.Length; offset++)
            {
                var index = (repeat + offset) % cases.Length;
                var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var result = cases[index].Run(inputs, iterations);
                var elapsed = Stopwatch.GetTimestamp() - start;
                allocations[index] += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                Volatile.Write(ref _sink, result);
                samples[index].Add(elapsed * 1_000_000_000d / Stopwatch.Frequency / iterations);
            }
        }
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; {Environment.ProcessorCount} logical CPUs");
        Console.WriteLine($"TieredCompilation env: {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}; {repeats} samples x {iterations:N0} iterations");
        Console.WriteLine("Hot-path microbenchmark only: no JSON, file I/O, catalog startup, WPF work or timer CPU cost is included.");
        Console.WriteLine("The JIT may hoist invariant reads. Results describe optimized steady loops, not whole-application latency.");
        for (var i = 0; i < cases.Length; i++)
        {
            samples[i].Sort();
            Console.WriteLine($"{cases[i].Name,-28} median {samples[i][repeats / 2],8:F3} ns/op; range {samples[i][0]:F3}..{samples[i][^1]:F3}; {allocations[i]} allocated bytes");
        }
        Console.WriteLine($"Observable checksum: {Volatile.Read(ref _sink):R}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double LiteralRead(double[] inputs, int count)
    {
        var sum = 0d;
        for (var i = 0; i < count; i++) sum += inputs[i & 255] + 140d;
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double SlotRead(double[] inputs, int count)
    {
        var sum = 0d;
        for (var i = 0; i < count; i++) sum += inputs[i & 255] + InjectionParameters.MarkdownRevealFadeMs.Value;
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double Formula(double[] inputs, int count)
    {
        var sum = 0d;
        for (var i = 0; i < count; i++) sum += Math.Min(inputs[i & 255], 64d) * 0.75 + 8d;
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double ResolvedFormula(double[] inputs, int count)
    {
        var sum = 0d;
        for (var i = 0; i < count; i++)
            sum += InjectionParameters.TodoSelectionEdge.Resolve(Math.Min(inputs[i & 255], 64d) * 0.75 + 8d);
        return sum;
    }
}
