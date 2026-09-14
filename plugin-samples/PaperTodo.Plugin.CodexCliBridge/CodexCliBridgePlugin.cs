using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.CodexCliBridge;

public sealed class CodexCliBridgePlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new Session(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new Runtime(context);

    private sealed class Session : IPaperBodySession
    {
        private readonly PaperBodyContext _context;
        private readonly TextBlock _title;
        private readonly TextBlock _description;
        private readonly TextBlock _status;
        private readonly TextBox _promptBox;
        private readonly DispatcherTimer _saveTimer;
        private bool _suppressPromptChanged;
        private bool _receivedRuntimePrompt;
        private bool _hasLocalEdit;
        private bool _disposed;

        public Session(PaperBodyContext context)
        {
            _context = context;

            _title = new TextBlock
            {
                Text = "Codex CLI Bridge",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _description = new TextBlock
            {
                Text = "这里的内容会作为默认提示词，在每次从待办项或纸片发送给 Codex 时自动放到最前面。",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _promptBox = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 180,
                Padding = new Thickness(10),
                BorderThickness = new Thickness(1),
                ToolTip = "默认传入提示词。留空则只发送待办/纸片本身。"
            };
            _status = new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                Text = "正在读取默认提示词…",
                TextWrapping = TextWrapping.Wrap
            };

            View = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    _title,
                    _description,
                    _promptBox,
                    _status
                }
            };

            _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(450)
            };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SendPromptToRuntime();
            };
            _promptBox.TextChanged += OnPromptChanged;

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
            RequestPromptFromRuntime();
        }

        public FrameworkElement View { get; }

        public bool OnRuntimeMessage(JsonElement message)
        {
            if (_disposed ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("type", out var typeValue))
            {
                return false;
            }

            var type = typeValue.GetString() ?? string.Empty;
            if (string.Equals(type, "defaultPrompt", StringComparison.Ordinal))
            {
                _receivedRuntimePrompt = true;
                if (!_hasLocalEdit)
                {
                    var prompt = message.TryGetProperty("prompt", out var promptValue) &&
                                 promptValue.ValueKind == JsonValueKind.String
                        ? promptValue.GetString() ?? string.Empty
                        : string.Empty;
                    _suppressPromptChanged = true;
                    _promptBox.Text = prompt;
                    _suppressPromptChanged = false;
                }
                _status.Text = "默认提示词已载入。修改后会自动保存。";
                return true;
            }

            if (string.Equals(type, "defaultPromptSaved", StringComparison.Ordinal))
            {
                _status.Text = "默认提示词已保存。";
                return true;
            }

            return false;
        }

        public void OnActivated()
        {
            if (!_receivedRuntimePrompt)
            {
                RequestPromptFromRuntime();
            }
        }

        public void Commit()
        {
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SendPromptToRuntime();
            }
        }

        public void OnThemeChanged(PaperBodyTheme theme) => ApplyTheme(theme);

        public void OnTypographyChanged(PaperBodyTheme theme) => ApplyTheme(theme);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _saveTimer.Stop();
            _promptBox.TextChanged -= OnPromptChanged;
        }

        private void OnPromptChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPromptChanged || _disposed)
            {
                return;
            }

            _hasLocalEdit = true;
            _status.Text = "等待保存…";
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void RequestPromptFromRuntime()
        {
            if (_disposed)
            {
                return;
            }

            var sent = _context.Runtime.Post(JsonSerializer.SerializeToElement(new
            {
                type = "getDefaultPrompt"
            }));
            if (!sent)
            {
                _status.Text = "Codex Runtime 暂不可用；重新打开这张纸后会再次读取。";
            }
        }

        private void SendPromptToRuntime()
        {
            if (_disposed)
            {
                return;
            }

            var sent = _context.Runtime.Post(JsonSerializer.SerializeToElement(new
            {
                type = "setDefaultPrompt",
                prompt = _promptBox.Text ?? string.Empty
            }));
            _status.Text = sent
                ? "正在保存…"
                : "保存失败：Codex Runtime 暂不可用。";
        }

        private void ApplyTheme(PaperBodyTheme theme)
        {
            var text = BrushFrom(theme.TextColor, Brushes.Black);
            var weak = BrushFrom(theme.WeakTextColor, Brushes.Gray);
            var border = BrushFrom(theme.BorderColor, Brushes.Gray);
            var paper = BrushFrom(theme.PaperColor, Brushes.Transparent);

            _title.Foreground = text;
            _description.Foreground = weak;
            _status.Foreground = weak;
            _promptBox.Foreground = text;
            _promptBox.Background = paper;
            _promptBox.BorderBrush = border;
            _promptBox.FontFamily = new FontFamily(theme.FontFamily);
            _promptBox.FontSize = 14 * Math.Clamp(theme.FontScale, 0.85, 1.3);
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
        private readonly IDisposable _runtimePaperSubscription;
        private CodexPromptState _state;
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
            _state = CodexPromptState.Read(context.State.Json);

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
            _runtimePaperSubscription = context.Papers.Subscribe(OnRuntimePaperEvent);
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

        private void OnRuntimePaperEvent(PaperPluginRuntimeEvent value)
        {
            if (_disposed ||
                value.Kind != PaperPluginRuntimeEventKind.Message ||
                value.Message is not JsonElement message ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("type", out var typeValue))
            {
                return;
            }

            var type = typeValue.GetString() ?? string.Empty;
            if (string.Equals(type, "getDefaultPrompt", StringComparison.Ordinal))
            {
                PostPromptToBody(value.PaperId, saved: false);
                return;
            }

            if (!string.Equals(type, "setDefaultPrompt", StringComparison.Ordinal))
            {
                return;
            }

            var prompt = message.TryGetProperty("prompt", out var promptValue) &&
                         promptValue.ValueKind == JsonValueKind.String
                ? promptValue.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(_state.DefaultPrompt, prompt, StringComparison.Ordinal))
            {
                var next = _state with { DefaultPrompt = prompt };
                _context.State.Save(JsonSerializer.Serialize(next));
                _state = next;
            }
            PostPromptToBody(value.PaperId, saved: true);
        }

        private void PostPromptToBody(string paperId, bool saved)
        {
            _context.Papers.PostToBody(
                paperId,
                JsonSerializer.SerializeToElement(new
                {
                    type = saved ? "defaultPromptSaved" : "defaultPrompt",
                    prompt = _state.EffectivePrompt
                }));
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
                    var prompt = AddDefaultPrompt(BuildTodoPrompt(todo));
                    var linkedPath = CodexCliLauncher.ResolveExistingPath(todo.LinkedPath);
                    var imageAttachment = CodexCliLauncher.IsSupportedImage(linkedPath)
                        ? linkedPath
                        : null;
                    var workingDirectory = CodexCliLauncher.ResolveWorkingDirectory(
                        settings.WorkingDirectory,
                        linkedPath,
                        todo.LinkedPathIsDirectory);
                    await CodexCliLauncher.RunSilentAsync(
                        settings,
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
                    var prompt = AddDefaultPrompt(BuildPaperPrompt(paperId));
                    var workingDirectory = CodexCliLauncher.ResolveWorkingDirectory(
                        settings.WorkingDirectory,
                        linkedPath: null,
                        linkedPathIsDirectory: null);
                    CodexCliLauncher.RunForeground(
                        settings,
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

        private string AddDefaultPrompt(string content) =>
            _state.PrependTo(content, Path.GetDirectoryName(typeof(CodexCliBridgePlugin).Assembly.Location)!);

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
            _runtimePaperSubscription.Dispose();
            _workspaceSubscription.Dispose();
            _context.TodoActions.SetActionHandler(null);
            _context.TodoActions.Clear();
            _context.GlobalTopBar.SetActionHandler(null);
            _context.GlobalTopBar.Clear();
        }
    }

    private sealed record CodexBridgeSettings(
        string CodexPath,
        string WorkingDirectory,
        string Model,
        string ReasoningEffort)
    {
        internal static CodexBridgeSettings Read(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = document.RootElement;
                return new CodexBridgeSettings(
                    Text(root, "codexPath", "codex", allowEmpty: false),
                    Text(root, "workingDirectory", string.Empty, allowEmpty: true),
                    Text(root, "model", "gpt-5.6-sol", allowEmpty: true),
                    Text(root, "reasoningEffort", "xhigh", allowEmpty: true));
            }
            catch
            {
                return new CodexBridgeSettings("codex", string.Empty, "gpt-5.6-sol", "xhigh");
            }
        }

        private static string Text(JsonElement root, string name, string fallback, bool allowEmpty)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }
            var text = (value.GetString() ?? string.Empty).Trim();
            return allowEmpty || !string.IsNullOrWhiteSpace(text) ? text : fallback;
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
if ($env:PAPERTODO_CODEX_MODEL) {
  $argsList += @('-m', $env:PAPERTODO_CODEX_MODEL)
}
if ($env:PAPERTODO_CODEX_REASONING) {
  $argsList += @('-c', ('model_reasoning_effort=' + $env:PAPERTODO_CODEX_REASONING))
}
if ($env:PAPERTODO_CODEX_DEVELOPER_INSTRUCTIONS) {
  $argsList += @('-c', ('developer_instructions=' + $env:PAPERTODO_CODEX_DEVELOPER_INSTRUCTIONS))
}
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
            CodexBridgeSettings settings,
            string workingDirectory,
            string prompt,
            string? imageAttachment)
        {
            var promptPath = WritePromptFile(prompt);
            try
            {
                using var process = StartPowerShell(
                    settings,
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
            CodexBridgeSettings settings,
            string workingDirectory,
            string prompt,
            string? imageAttachment)
        {
            var promptPath = WritePromptFile(prompt);
            try
            {
                _ = StartPowerShell(
                    settings,
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
            CodexBridgeSettings settings,
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
                string.IsNullOrWhiteSpace(settings.CodexPath) ? "codex" : settings.CodexPath.Trim();
            startInfo.Environment["PAPERTODO_CODEX_CWD"] = workingDirectory;
            startInfo.Environment["PAPERTODO_CODEX_PROMPT"] = promptPath;
            startInfo.Environment["PAPERTODO_CODEX_IMAGE"] = imageAttachment ?? string.Empty;
            startInfo.Environment["PAPERTODO_CODEX_MODEL"] = settings.Model;
            startInfo.Environment["PAPERTODO_CODEX_REASONING"] = settings.ReasoningEffort;
            startInfo.Environment["PAPERTODO_CODEX_DEVELOPER_INSTRUCTIONS"] =
                ResolvePluginAgentInstructions() ?? string.Empty;
            startInfo.Environment["PAPERTODO_CODEX_FOREGROUND"] = foreground ? "1" : "0";

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 PowerShell/Codex CLI。请检查插件设置中的 Codex CLI 命令。");
            }
            return process;
        }

        private static string? ResolvePluginAgentInstructions()
        {
            try
            {
                var assemblyPath = typeof(CodexCliBridgePlugin).Assembly.Location;
                var pluginDirectory = Path.GetDirectoryName(assemblyPath);
                if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory))
                {
                    return null;
                }

                foreach (var fileName in new[] { "AGENTS.md", "AGENT.md", "agent.md" })
                {
                    var path = Path.Combine(pluginDirectory, fileName);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var content = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        return null;
                    }

                    if (content.Length <= 12_000)
                    {
                        return content;
                    }

                    return $"Before doing the task, read and follow the full instructions in this file first: {path}. " +
                           "Treat those plugin-local instructions as higher priority than ordinary project guidance when they conflict.";
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CodexCliBridge] Failed to read plugin AGENTS.md: {ex}");
            }

            return null;
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
