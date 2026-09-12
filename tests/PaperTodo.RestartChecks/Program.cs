using System.Diagnostics;
using System.Globalization;
using PaperTodo;

internal static class Program
{
    private static int _passed;
    private const int OtherPid = 12345;
    private const int CurrentPid = 54321;
    private const long StartTicks = 638900000000000000;

    private static int Main(string[] args)
    {
        if (args.Contains("--wait-probe", StringComparer.Ordinal))
        {
            Console.WriteLine("ready");
            Console.Out.Flush();
            Console.ReadLine();
            return 0;
        }

        try
        {
            Check("apphost and single-file restart do not require a DLL", CheckApphost);
            Check("dotnet restart puts the entry DLL before application arguments", CheckDotnet);
            Check("restart arguments validate a complete process identity", CheckArguments);
            Check("restart arguments are independent of the current culture", CheckCulture);
            Check("mismatched process identity never waits for an unrelated live process", CheckMismatchedProcess);
            Check("matched process identity waits for that process to exit", CheckMatchedProcess);
            Console.WriteLine($"Restart checks passed: {_passed} groups.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckApphost()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Paper Todo 测试");
        var executable = Path.Combine(directory, "PaperTodo.exe");
        var info = AppRestart.CreateStartInfo(executable, null, directory, OtherPid, StartTicks);
        Assert(info.FileName == executable && info.WorkingDirectory == directory && !info.UseShellExecute,
            "native executable and working directory are retained");
        Assert(info.ArgumentList.SequenceEqual(Identity(OtherPid, StartTicks)),
            "only restart metadata is sent; no DLL and no replay of one-shot commands");
        Assert(info.Arguments.Length == 0, "arguments are never concatenated or manually quoted");
    }

    private static void CheckDotnet()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Paper Todo 测试");
        var entry = Path.Combine(directory, "PaperTodo.dll");
        foreach (var name in new[] { "dotnet", "dotnet.exe", "DOTNET.EXE" })
        {
            var executable = Path.Combine(directory, name);
            var info = AppRestart.CreateStartInfo(executable, entry, directory, OtherPid, StartTicks);
            Assert(info.ArgumentList.Count == 3 && info.ArgumentList[0] == entry,
                "a managed entry point is the first argument, with spaces preserved");
            Assert(info.ArgumentList.Skip(1).SequenceEqual(Identity(OtherPid, StartTicks)),
                "application arguments follow the entry DLL");
            Throws<ArgumentException>(() =>
                AppRestart.CreateStartInfo(executable, null, directory, OtherPid, StartTicks));
        }
        var relative = AppRestart.CreateStartInfo(Path.Combine(directory, "dotnet"), "PaperTodo.dll",
            directory, OtherPid, StartTicks);
        Assert(relative.ArgumentList[0] == entry, "relative entry resolves against the application directory");
        Throws<ArgumentOutOfRangeException>(() =>
            AppRestart.CreateStartInfo("PaperTodo.exe", null, directory, 0, StartTicks));
        Throws<ArgumentOutOfRangeException>(() =>
            AppRestart.CreateStartInfo("PaperTodo.exe", null, directory, OtherPid, long.MaxValue));
    }

    private static void CheckArguments()
    {
        var valid = Identity(OtherPid, StartTicks);
        foreach (var args in new[]
        {
            valid,
            valid.Reverse().ToArray(),
            new[] { "PaperTodo.dll", "--unrelated", valid[0].ToUpperInvariant(), valid[1] }
        })
        {
            Assert(AppRestart.TryParseRestartTarget(args, CurrentPid, out var pid, out var ticks) &&
                pid == OtherPid && ticks == StartTicks, "complete valid identity accepted");
        }

        var malformed = new List<string[]>
        {
            Array.Empty<string>(), new[] { valid[0] }, new[] { valid[1] },
            new[] { valid[0], valid[0], valid[1] },
            new[] { valid[0], valid[1], valid[1] },
            Identity(CurrentPid, StartTicks), Identity(0, StartTicks), Identity(-1, StartTicks),
            Identity(OtherPid, 0), Identity(OtherPid, -1), Identity(OtherPid, long.MaxValue),
            new[] { AppRestart.WaitForProcessPrefix + "2147483648", valid[1] },
            new[] { valid[0], AppRestart.WaitForStartTimePrefix + "9223372036854775808" }
        };
        foreach (var bad in new[] { "", "x", "+1", " 1", "1 ", "1.0" })
        {
            malformed.Add(new[] { AppRestart.WaitForProcessPrefix + bad, valid[1] });
            malformed.Add(new[] { valid[0], AppRestart.WaitForStartTimePrefix + bad });
        }
        foreach (var args in malformed)
        {
            Assert(!AppRestart.TryParseRestartTarget(args, CurrentPid, out var pid, out var ticks) &&
                pid == 0 && ticks == 0, "incomplete, duplicate, self or malformed identity rejected");
        }
    }

    private static void CheckCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var info = AppRestart.CreateStartInfo("PaperTodo.exe", null, AppContext.BaseDirectory,
                OtherPid, StartTicks);
            Assert(AppRestart.TryParseRestartTarget(info.ArgumentList.ToArray(), CurrentPid,
                out var pid, out var ticks) && pid == OtherPid && ticks == StartTicks,
                "invariant formatting round trips under a different culture");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void CheckMismatchedProcess()
    {
        using var child = StartProbe();
        try
        {
            var identity = Identity(child.Id, child.StartTime.ToUniversalTime().Ticks + 1);
            var wait = Task.Run(() => AppRestart.WaitForPreviousInstance(identity));
            Assert(wait.Wait(TimeSpan.FromSeconds(3)), "wrong start time must not wait for this live PID");
            Assert(!child.HasExited, "identity mismatch never terminates the other process");
        }
        finally { StopProbe(child); }
    }

    private static void CheckMatchedProcess()
    {
        using var child = StartProbe();
        try
        {
            var identity = Identity(child.Id, child.StartTime.ToUniversalTime().Ticks);
            using var entered = new ManualResetEventSlim();
            var wait = Task.Run(() =>
            {
                entered.Set();
                AppRestart.WaitForPreviousInstance(identity);
            });
            Assert(entered.Wait(TimeSpan.FromSeconds(3)), "wait task was scheduled");
            Assert(!wait.Wait(TimeSpan.FromMilliseconds(150)), "a live matched parent is awaited");
            child.StandardInput.WriteLine();
            child.StandardInput.Flush();
            Assert(wait.Wait(TimeSpan.FromSeconds(10)), "wait completes when the matched parent exits");
            Assert(child.WaitForExit(1000) && child.ExitCode == 0, "probe exited normally");
            // Opening an already-exited process also returns without blocking startup.
            AppRestart.WaitForPreviousInstance(identity);
        }
        finally { StopProbe(child); }
    }

    private static Process StartProbe()
    {
        using var current = Process.GetCurrentProcess();
        var info = AppRestart.CreateStartInfo(Environment.ProcessPath!, typeof(Program).Assembly.Location,
            AppContext.BaseDirectory, Environment.ProcessId, current.StartTime.ToUniversalTime().Ticks);
        info.ArgumentList.Add("--wait-probe");
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Probe did not start.");
        try
        {
            var ready = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            Assert(ready == "ready", "probe is alive and ready");
            return process;
        }
        catch
        {
            StopProbe(process);
            process.Dispose();
            throw;
        }
    }

    private static void StopProbe(Process process)
    {
        if (process.HasExited) return;
        try { process.StandardInput.WriteLine(); process.StandardInput.Flush(); }
        catch (IOException) { }
        if (!process.WaitForExit(3000))
        {
            // Test-owned helper only; production AppRestart never kills a process.
            process.Kill();
            if (!process.WaitForExit(3000)) throw new InvalidOperationException("Probe cleanup failed.");
        }
    }

    private static string[] Identity(int pid, long ticks) => new[]
    {
        AppRestart.WaitForProcessPrefix + pid.ToString(CultureInfo.InvariantCulture),
        AppRestart.WaitForStartTimePrefix + ticks.ToString(CultureInfo.InvariantCulture)
    };

    private static void Check(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
