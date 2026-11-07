using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PaperTodo;

// Capsules and the shared shortcut use one parser, engine resolver, process registry
// and shutdown path. This service does not create papers or own any UI.
internal static class ScriptExecutionService
{
    private static readonly object PersistentScriptProcessLock = new();
    private static readonly Dictionary<string, Process> PersistentScriptProcesses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ActiveScriptProcessLock = new();
    private static readonly Dictionary<Guid, Process> ActiveScriptProcesses = new();

    internal readonly record struct ScriptSpec(string Engine, string Script, bool UsePersistentProcess);

    internal static bool TryParse(string? text, bool allowDefaultEngine, out ScriptSpec spec)
    {
        text ??= string.Empty;
        var firstLineEnd = text.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd < 0 ? text : text[..firstLineEnd];
        if (TryParseMarker(firstLine, out var engine, out var persistent))
        {
            var scriptStart = firstLineEnd < 0 ? text.Length : firstLineEnd;
            if (scriptStart < text.Length && text[scriptStart] == '\r') scriptStart++;
            if (scriptStart < text.Length && text[scriptStart] == '\n') scriptStart++;
            spec = new ScriptSpec(engine, NormalizeScriptCapsuleIndent(text[scriptStart..]), persistent);
            return true;
        }
        // A shortcut without a marker uses the user's default engine; never rewrite
        // the saved input to insert a marker. Unknown/full-width markers are script text.
        spec = new ScriptSpec("auto", NormalizeScriptCapsuleIndent(text), false);
        return allowDefaultEngine;
    }

    internal static bool HasMarker(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var end = text.IndexOfAny(['\r', '\n']);
        return IsMarker(end < 0 ? text : text[..end]);
    }

    internal static bool IsMarker(string firstLine) => TryParseMarker(firstLine, out _, out _);

    private static bool TryParseMarker(string firstLine, out string engine, out bool persistent)
    {
        var marker = firstLine.Trim().ToLowerInvariant();
        persistent = marker is "!pf" or "!powerf";
        engine = marker switch
        {
            "!pf" or "!powerf" or "!p" or "!power" => "auto",
            "!pwsh" or "!ps7" => "pwsh",
            "!ps5" or "!winps" => "powershell",
            _ => string.Empty
        };
        return engine.Length > 0;
    }

    internal static async Task RunAsync(ScriptSpec spec, AppState state, Action<string> showFailure)
    {
        if (string.IsNullOrWhiteSpace(spec.Script))
        {
            showFailure(Strings.Get("ScriptCapsuleEmptyMessage"));
            return;
        }

        if (spec.UsePersistentProcess && state.UsePersistentPowerShellProcess)
        {
            RunPersistentScriptCapsule(spec, state, showFailure);
            return;
        }

        string? path = null;
        var executionId = Guid.NewGuid();
        var registeredProcess = false;
        // Shutdown withdraws this registration before stopping the process. Its
        // late exit must remain silent even after a failed workspace switch rolls back.
        bool WasStopped()
        {
            if (!registeredProcess) return false;
            lock (ActiveScriptProcessLock)
                return !ActiveScriptProcesses.ContainsKey(executionId);
        }
        try
        {
            path = WriteScriptCapsuleFile(spec.Script);
            var executable = ResolvePowerShellExecutable(state, spec.Engine);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = state.HideScriptRunWindow,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(EncodedPowerShellLaunchCommand(path));

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                showFailure(Strings.Get("ScriptCapsuleStartFailureMessage"));
                return;
            }

            lock (ActiveScriptProcessLock)
            {
                ActiveScriptProcesses[executionId] = process;
                registeredProcess = true;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0 && !WasStopped())
            {
                var detail = CompactScriptCapsuleOutput(output, error);
                showFailure(Strings.Format("ScriptCapsuleExitFailureMessage", process.ExitCode, detail));
            }
        }
        catch (Exception ex)
        {
            if (!WasStopped()) showFailure(ex.Message);
        }
        finally
        {
            if (registeredProcess)
            {
                lock (ActiveScriptProcessLock)
                {
                    ActiveScriptProcesses.Remove(executionId);
                }
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Temporary script cleanup must not affect the user's note.
                }
            }
        }
    }

    private static void RunPersistentScriptCapsule(ScriptSpec spec, AppState state, Action<string> showFailure)
    {
        string? path = null;
        var submitted = false;
        try
        {
            path = WriteScriptCapsuleFile(spec.Script);
            var executable = ResolvePowerShellExecutable(state, spec.Engine);
            var process = EnsurePersistentScriptProcess(executable, state.HideScriptRunWindow);
            var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
            process.StandardInput.WriteLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            process.StandardInput.WriteLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
            process.StandardInput.WriteLine($"try {{ & '{escapedPath}' }} finally {{ Remove-Item -LiteralPath '{escapedPath}' -ErrorAction SilentlyContinue }}");
            process.StandardInput.Flush();
            submitted = true;
        }
        catch (Exception ex)
        {
            showFailure(ex.Message);
        }
        finally
        {
            if (!submitted && !string.IsNullOrWhiteSpace(path))
            {
                DeleteScriptCapsuleFile(path);
            }
        }
    }

    private static Process EnsurePersistentScriptProcess(string executable, bool hideWindow)
    {
        var key = $"{executable}|{hideWindow}";
        lock (PersistentScriptProcessLock)
        {
            if (PersistentScriptProcesses.TryGetValue(key, out var existing) && !existing.HasExited)
            {
                return existing;
            }

            if (existing != null)
            {
                existing.Dispose();
                PersistentScriptProcesses.Remove(key);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = hideWindow,
                RedirectStandardInput = true,
                StandardInputEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-NoExit");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("-");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) =>
            {
                var ownsProcess = false;
                lock (PersistentScriptProcessLock)
                {
                    if (PersistentScriptProcesses.TryGetValue(key, out var current) && ReferenceEquals(current, process))
                    {
                        PersistentScriptProcesses.Remove(key);
                        ownsProcess = true;
                    }
                }

                if (ownsProcess)
                {
                    process.Dispose();
                }
            };
            process.Start();
            PersistentScriptProcesses[key] = process;
            return process;
        }
    }

    private static string NormalizeScriptCapsuleIndent(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        var normalized = script.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var commonIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] is ' ' or '\t')
            {
                indent++;
            }
            commonIndent = Math.Min(commonIndent, indent);
        }

        if (commonIndent is int.MaxValue or <= 0)
        {
            return script;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var remove = Math.Min(commonIndent, LeadingWhitespaceLength(lines[i]));
            lines[i] = lines[i][remove..];
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int LeadingWhitespaceLength(string text)
    {
        var length = 0;
        while (length < text.Length && text[length] is ' ' or '\t')
        {
            length++;
        }

        return length;
    }

    private static string WriteScriptCapsuleFile(string script)
    {
        var directory = ScriptCapsuleTempDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"script-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string ScriptCapsuleTempDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "PaperTodo", "Scripts");
    }

    private static void DeleteScriptCapsuleFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary script cleanup must not affect the user's note.
        }
    }

    internal static void CleanupOldScriptCapsuleTempFiles()
    {
        try
        {
            var directory = ScriptCapsuleTempDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var path in Directory.EnumerateFiles(directory, "script-*.ps1"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    internal static void EnsurePersistentScriptProcessForSettings(AppState state)
    {
        if (!state.UsePersistentPowerShellProcess)
        {
            return;
        }

        try
        {
            var executable = ResolvePowerShellExecutable(state, "auto");
            EnsurePersistentScriptProcess(executable, state.HideScriptRunWindow);
        }
        catch
        {
            // Prewarming is best-effort; explicit script execution will report failures.
        }
    }

    private static string ResolvePowerShellExecutable(AppState state, string engine)
    {
        if (engine == "pwsh")
        {
            return FindPowerShellExecutable("pwsh.exe")
                ?? throw new InvalidOperationException(Strings.Get("ScriptCapsulePowerShell7NotFound"));
        }

        if (engine == "powershell")
        {
            return "powershell.exe";
        }

        if (state.PreferPowerShell7)
        {
            var pwsh = FindPowerShellExecutable("pwsh.exe");
            if (!string.IsNullOrWhiteSpace(pwsh))
            {
                return pwsh;
            }
        }

        return "powershell.exe";
    }

    private static string? FindPowerShellExecutable(string fileName)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PATH")))
        {
            candidates.AddRange(
                (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => Path.Combine(path.Trim(), fileName)));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "PowerShell", "7", fileName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string EncodedPowerShellLaunchCommand(string path)
    {
        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var command = string.Join(
            "; ",
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
            "$OutputEncoding = [System.Text.Encoding]::UTF8",
            $"& '{escapedPath}'");
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static string CompactScriptCapsuleOutput(string output, string error)
    {
        var text = string.Join(
            Environment.NewLine,
            new[] { error.Trim(), output.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(text))
        {
            return Strings.Get("ScriptCapsuleNoOutput");
        }

        const int maxLength = 1800;
        return text.Length <= maxLength ? text : text[^maxLength..];
    }

    internal static void StopPersistentScriptProcesses() =>
        StopPersistentScriptProcessesAsync().GetAwaiter().GetResult();

    private static async Task<bool> StopPersistentScriptProcessesAsync()
    {
        Process[] processes;
        lock (PersistentScriptProcessLock)
        {
            processes = PersistentScriptProcesses.Values.Distinct().ToArray();
            PersistentScriptProcesses.Clear();
        }
        var stopped = await Task.WhenAll(processes.Select(process => StopScriptProcessAsync(process, persistent: true)))
            .ConfigureAwait(false);
        return stopped.Any(value => value);
    }

    internal static async Task<(bool ScriptsStopped, bool SessionsClosed)> StopAllScriptProcessesAsync()
    {
        var persistent = StopPersistentScriptProcessesAsync();
        Process[] active;
        lock (ActiveScriptProcessLock)
        {
            active = ActiveScriptProcesses.Values.Distinct().ToArray();
            ActiveScriptProcesses.Clear();
        }
        var stopped = await Task.WhenAll(active.Select(process => StopScriptProcessAsync(process, persistent: false)))
            .ConfigureAwait(false);
        return (stopped.Any(value => value), await persistent.ConfigureAwait(false));
    }

    private static Task<bool> StopScriptProcessAsync(Process process, bool persistent) => Task.Run(async () =>
    {
        try
        {
            if (process.HasExited) return false;
            if (persistent)
            {
                try { if (process.StartInfo.RedirectStandardInput) process.StandardInput.Close(); }
                catch { /* A broken pipe/already-exiting process needs no graceful request. */ }
                using var grace = new System.Threading.CancellationTokenSource(250);
                try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); return true; }
                catch (OperationCanceledException) when (grace.IsCancellationRequested) { }
            }
            if (process.HasExited) return false;
            using var deadline = new System.Threading.CancellationTokenSource(1000);
            // Subscribe before killing: RunAsync owns active-process disposal and
            // may finish as soon as the same exit event reaches its awaiter.
            var exited = process.WaitForExitAsync(deadline.Token);
            process.Kill(entireProcessTree: true);
            await exited.ConfigureAwait(false);
            return true;
        }
        catch { /* Process exit/disposal can race the execution task. Other processes still stop. */ }
        finally
        {
            // Active execution owns its process disposal and temporary-file cleanup.
            if (persistent) process.Dispose();
        }
        return false;
    });
}
