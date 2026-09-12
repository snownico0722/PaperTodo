using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.CodexCliBridge;

public sealed class CodexCliBridgePlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new Session(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new Runtime(context);

    private sealed class Session : IPaperBodySession
    {
        private readonly TextBlock _title;
        private readonly TextBlock _description;

        public Session(PaperBodyContext context)
        {
            _title = new TextBlock
            {
                Text = "Codex CLI Bridge",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _description = new TextBlock
            {
                Text = "保持这张纸存在即可启用全局桥接。\n\n" +
                       "• 待办项 >_：静默发送给 codex exec。\n" +
                       "• 纸片顶栏 >_：把当前纸片全文发送给 Codex，并打开前台窗口。\n" +
                       "• 待办绑定本地图片时，会自动通过 --image 作为附件发送；其他绑定文件/目录会作为本地工作上下文交给 Codex。",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22
            };

            View = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    _title,
                    _description
                }
            };

            context.Paper.SetHeaderText("Codex CLI");
            context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = "Codex CLI",
                ToolTip = "Codex CLI Bridge",
                Components =
                [
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = "Codex CLI",
                        Fill = true
                    }
                ]
            });
            ApplyTheme(context.Body.Theme);
        }

        public FrameworkElement View { get; }

        public void OnThemeChanged(PaperBodyTheme theme) => ApplyTheme(theme);

        public void OnTypographyChanged(PaperBodyTheme theme) => ApplyTheme(theme);

        public void Dispose()
        {
        }

        private void ApplyTheme(PaperBodyTheme theme)
        {
            _title.Foreground = BrushFrom(theme.TextColor, Brushes.Black);
            _description.Foreground = BrushFrom(theme.WeakTextColor, _title.Foreground);
        }

        private static Brush BrushFrom(string value, Brush fallback)
        {
            try
            {
                return new BrushConverter().ConvertFromString(value) as Brush ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }

    private sealed class Runtime : IPaperPluginRuntime
    {
        private const string TodoActionId = "send-todo-to-codex";
        private const string TopBarActionId = "send-paper-to-codex";

        private readonly PaperPluginRuntimeContext _context;
        private readonly IDisposable _workspaceSubscription;
        private bool _disposed;

        private static readonly PaperTodoAction[] TodoAction =
        [
            new PaperTodoAction
            {
                Id = TodoActionId,
                Icon = PaperTopBarIcon.Character(">_"),
                Text = "发送到 Codex CLI",
                ToolTip = "静默发送到 Codex CLI",
                Priority = 80,
                Placement = PaperTodoActionPlacement.Inline |
                            PaperTodoActionPlacement.ContextMenu
            }
        ];

        public Runtime(PaperPluginRuntimeContext context)
        {
            _context = context;

            context.TodoActions.SetActionHandler(OnTodoAction);
            context.GlobalTopBar.SetActionHandler(OnTopBarAction);
            context.GlobalTopBar.SetActions(
            [
                new PaperTopBarAction
                {
                    Id = TopBarActionId,
                    Icon = PaperTopBarIcon.Character(">_"),
                    ToolTip = "把当前纸片全文发送到 Codex CLI",
                    Priority = 80
                }
            ]);

            PublishTodoActions();
            _workspaceSubscription = context.Workspace.Subscribe(
                new PaperTodoEventFilter
                {
                    Kinds = new HashSet<PaperTodoEventKind>
                    {
                        PaperTodoEventKind.TodoCreated,
                        PaperTodoEventKind.TodoChanged,
                        PaperTodoEventKind.TodoDeleted
                    },
                    ExcludeOwnOperations = false
                },
                OnWorkspaceEvent);
        }

        private void PublishTodoActions()
        {
            foreach (var todo in _context.Workspace.ListTodos(includeBlank: false))
            {
                PublishTodoAction(todo);
            }
        }

        private void PublishTodoAction(TodoSnapshot todo)
        {
            if (_disposed)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(todo.Text))
            {
                _context.TodoActions.Clear(todo.PaperId, todo.Id);
                return;
            }
            _context.TodoActions.SetActions(todo.PaperId, todo.Id, TodoAction);
        }

        private void OnWorkspaceEvent(PaperTodoEvent value)
        {
            if (_disposed)
            {
                return;
            }

            switch (value)
            {
                case TodoCreatedEvent created:
                    PublishTodoAction(created.Todo);
                    break;
                case TodoChangedEvent changed:
                    PublishTodoAction(changed.After);
                    break;
                case TodoDeletedEvent deleted:
                    _context.TodoActions.Clear(deleted.Todo.PaperId, deleted.Todo.Id);
                    break;
            }
        }

        private void OnTodoAction(PaperTodoActionInvocation invocation)
        {
            if (_disposed || !string.Equals(invocation.ActionId, TodoActionId, StringComparison.Ordinal))
            {
                return;
            }

            var todo = invocation.Todo;
            _ = Task.Run(async () =>
            {
                try
                {
                    var settings = CodexBridgeSettings.Read(_context.Settings.Json);
                    var prompt = BuildTodoPrompt(todo);
                    var linkedPath = CodexCliLauncher.ResolveExistingPath(todo.LinkedPath);
                    var imageAttachment = CodexCliLauncher.IsSupportedImage(linkedPath)
                        ? linkedPath
                        : null;
                    var workingDirectory = CodexCliLauncher.ResolveWorkingDirectory(
                        settings.WorkingDirectory,
                        linkedPath,
                        todo.LinkedPathIsDirectory);
                    await CodexCliLauncher.RunSilentAsync(
                        settings.CodexPath,
                        workingDirectory,
                        prompt,
                        imageAttachment);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[CodexCliBridge] Silent send failed: {ex}");
                }
            });
        }

        private void OnTopBarAction(PaperTopBarActionInvocation invocation)
        {
            if (_disposed ||
                !string.Equals(invocation.ActionId, TopBarActionId, StringComparison.Ordinal))
            {
                return;
            }

            var paperId = invocation.TargetPaperId;
            _ = Task.Run(() =>
            {
                try
                {
                    var settings = CodexBridgeSettings.Read(_context.Settings.Json);
                    var prompt = BuildPaperPrompt(paperId);
                    var workingDirectory = CodexCliLauncher.ResolveWorkingDirectory(
                        settings.WorkingDirectory,
                        linkedPath: null,
                        linkedPathIsDirectory: null);
                    CodexCliLauncher.RunForeground(
                        settings.CodexPath,
                        workingDirectory,
                        prompt,
                        imageAttachment: null);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[CodexCliBridge] Foreground send failed: {ex}");
                }
            });
        }

        private string BuildTodoPrompt(TodoSnapshot todo)
        {
            var builder = new StringBuilder();
            builder.AppendLine(todo.Text.Trim());
            builder.AppendLine();
            builder.AppendLine("[PaperTodo context]");
            builder.Append("来源纸片：").AppendLine(todo.PaperTitle);

            if (!string.IsNullOrWhiteSpace(todo.LinkedPath))
            {
                builder.Append("绑定路径：").AppendLine(todo.LinkedPath);
            }

            if (!string.IsNullOrWhiteSpace(todo.LinkedPaperId))
            {
                AppendLinkedPaper(builder, todo.LinkedPaperId);
            }

            return builder.ToString().TrimEnd();
        }

        private void AppendLinkedPaper(StringBuilder builder, string linkedPaperId)
        {
            var paper = _context.Workspace.GetPaper(linkedPaperId);
            if (paper == null)
            {
                return;
            }

            builder.AppendLine();
            builder.AppendLine("[绑定 PaperTodo 纸片]");
            builder.Append("标题：").AppendLine(paper.Title);

            if (string.Equals(paper.Type, "todo", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in _context.Workspace.ListTodos(paper.Id, includeBlank: false)
                             .OrderBy(value => value.Order))
                {
                    builder.Append(item.Done ? "- [x] " : "- [ ] ")
                        .AppendLine(item.Text);
                }
                return;
            }

            if (!string.Equals(paper.Type, "note", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var note = _context.Workspace.GetNote(paper.Id);
            if (note?.ContentAvailable == true)
            {
                builder.AppendLine(note.Content);
            }
            else
            {
                builder.AppendLine("（该纸片正文不通过 Workspace 暴露。）");
            }
        }

        private string BuildPaperPrompt(string paperId)
        {
            var paper = _context.Workspace.GetPaper(paperId)
                        ?? throw new InvalidOperationException("目标纸片已经不存在。");
            var builder = new StringBuilder();
            builder.AppendLine("[PaperTodo 纸片全文]");
            builder.Append("标题：").AppendLine(paper.Title);
            builder.AppendLine();

            if (string.Equals(paper.Type, "todo", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var todo in _context.Workspace.ListTodos(paper.Id, includeBlank: false)
                             .OrderBy(value => value.Order))
                {
                    builder.Append(todo.Done ? "- [x] " : "- [ ] ")
                        .Append(todo.Text);
                    if (!string.IsNullOrWhiteSpace(todo.LinkedPath))
                    {
                        builder.Append("  [绑定路径: ").Append(todo.LinkedPath).Append(']');
                    }
                    builder.AppendLine();
                }
                return builder.ToString().TrimEnd();
            }

            if (string.Equals(paper.Type, "note", StringComparison.OrdinalIgnoreCase))
            {
                var note = _context.Workspace.GetNote(paper.Id);
                if (note?.ContentAvailable == true)
                {
                    builder.AppendLine(note.Content);
                }
                else
                {
                    builder.AppendLine("（该 Note 的正文不通过 Workspace 暴露。）");
                }
                return builder.ToString().TrimEnd();
            }

            builder.AppendLine("（当前纸片类型没有可导出的正文，只发送标题。）");
            return builder.ToString().TrimEnd();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _workspaceSubscription.Dispose();
            _context.TodoActions.SetActionHandler(null);
            _context.TodoActions.Clear();
            _context.GlobalTopBar.SetActionHandler(null);
            _context.GlobalTopBar.Clear();
        }
    }

    private sealed record CodexBridgeSettings(string CodexPath, string WorkingDirectory)
    {
        internal static CodexBridgeSettings Read(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = document.RootElement;
                return new CodexBridgeSettings(
                    Text(root, "codexPath", "codex"),
                    Text(root, "workingDirectory", string.Empty));
            }
            catch
            {
                return new CodexBridgeSettings("codex", string.Empty);
            }
        }

        private static string Text(JsonElement root, string name, string fallback)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }
            var text = (value.GetString() ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
    }

    private static class CodexCliLauncher
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".webp"
        };

        private const string PowerShellScript = """
$ErrorActionPreference = 'Stop'
try { $Host.UI.RawUI.WindowTitle = 'Codex CLI - PaperTodo' } catch {}
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$argsList = @('exec', '--skip-git-repo-check')
if ($env:PAPERTODO_CODEX_CWD) {
  $argsList += @('-C', $env:PAPERTODO_CODEX_CWD)
}
if ($env:PAPERTODO_CODEX_IMAGE) {
  $argsList += @('--image', $env:PAPERTODO_CODEX_IMAGE)
}
$argsList += '-'
$code = 1
try {
  Get-Content -LiteralPath $env:PAPERTODO_CODEX_PROMPT -Raw -Encoding UTF8 |
    & $env:PAPERTODO_CODEX_PATH @argsList
  $code = $LASTEXITCODE
}
catch {
  Write-Error $_
  $code = 1
}
finally {
  Remove-Item -LiteralPath $env:PAPERTODO_CODEX_PROMPT -Force -ErrorAction SilentlyContinue
}
if ($env:PAPERTODO_CODEX_FOREGROUND -eq '1') {
  Write-Host ''
  Write-Host "[PaperTodo] Codex exited with code $code."
}
else {
  exit $code
}
""";

        internal static async Task RunSilentAsync(
            string codexPath,
            string workingDirectory,
            string prompt,
            string? imageAttachment)
        {
            var promptPath = WritePromptFile(prompt);
            try
            {
                using var process = StartPowerShell(
                    codexPath,
                    workingDirectory,
                    promptPath,
                    imageAttachment,
                    foreground: false);
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    Trace.WriteLine(
                        $"[CodexCliBridge] codex exec exited {process.ExitCode}: {stderr}\n{stdout}");
                }
            }
            catch
            {
                TryDelete(promptPath);
                throw;
            }
        }

        internal static void RunForeground(
            string codexPath,
            string workingDirectory,
            string prompt,
            string? imageAttachment)
        {
            var promptPath = WritePromptFile(prompt);
            try
            {
                _ = StartPowerShell(
                    codexPath,
                    workingDirectory,
                    promptPath,
                    imageAttachment,
                    foreground: true);
            }
            catch
            {
                TryDelete(promptPath);
                throw;
            }
        }

        private static Process StartPowerShell(
            string codexPath,
            string workingDirectory,
            string promptPath,
            string? imageAttachment,
            bool foreground)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = !foreground,
                WindowStyle = foreground ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                RedirectStandardOutput = !foreground,
                RedirectStandardError = !foreground,
                WorkingDirectory = workingDirectory
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            if (!foreground)
            {
                startInfo.ArgumentList.Add("-NonInteractive");
            }
            else
            {
                startInfo.ArgumentList.Add("-NoExit");
            }
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(PowerShellScript);
            startInfo.Environment["PAPERTODO_CODEX_PATH"] =
                string.IsNullOrWhiteSpace(codexPath) ? "codex" : codexPath.Trim();
            startInfo.Environment["PAPERTODO_CODEX_CWD"] = workingDirectory;
            startInfo.Environment["PAPERTODO_CODEX_PROMPT"] = promptPath;
            startInfo.Environment["PAPERTODO_CODEX_IMAGE"] = imageAttachment ?? string.Empty;
            startInfo.Environment["PAPERTODO_CODEX_FOREGROUND"] = foreground ? "1" : "0";

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 PowerShell/Codex CLI。请检查插件设置中的 Codex CLI 命令。");
            }
            return process;
        }

        private static string WritePromptFile(string prompt)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PaperTodo-CodexCliBridge");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"prompt-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, prompt ?? string.Empty, new UTF8Encoding(false));
            return path;
        }

        internal static string? ResolveExistingPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                var expanded = ExpandPath(value);
                var fullPath = Path.GetFullPath(expanded);
                return File.Exists(fullPath) || Directory.Exists(fullPath) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsSupportedImage(string? path) =>
            path != null && File.Exists(path) && ImageExtensions.Contains(Path.GetExtension(path));

        internal static string ResolveWorkingDirectory(
            string configuredWorkingDirectory,
            string? linkedPath,
            bool? linkedPathIsDirectory)
        {
            var resolvedLinkedPath = ResolveExistingPath(linkedPath);
            if (resolvedLinkedPath != null)
            {
                var candidate = linkedPathIsDirectory == true || Directory.Exists(resolvedLinkedPath)
                    ? resolvedLinkedPath
                    : Path.GetDirectoryName(resolvedLinkedPath);
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                {
                    return FindGitRoot(candidate) ?? candidate;
                }
            }

            var configured = ResolveDirectory(configuredWorkingDirectory);
            if (configured != null)
            {
                return configured;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
            {
                return home;
            }
            return Environment.CurrentDirectory;
        }

        private static string? ResolveDirectory(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            try
            {
                var fullPath = Path.GetFullPath(ExpandPath(value));
                return Directory.Exists(fullPath) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ExpandPath(string value)
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            if (expanded == "~")
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            if (expanded.StartsWith("~\\", StringComparison.Ordinal) ||
                expanded.StartsWith("~/", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, expanded[2..]);
            }
            return expanded;
        }

        private static string? FindGitRoot(string startDirectory)
        {
            try
            {
                var cursor = new DirectoryInfo(startDirectory);
                while (cursor != null)
                {
                    var marker = Path.Combine(cursor.FullName, ".git");
                    if (Directory.Exists(marker) || File.Exists(marker))
                    {
                        return cursor.FullName;
                    }
                    cursor = cursor.Parent;
                }
            }
            catch
            {
            }
            return null;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
