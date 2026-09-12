# Codex CLI Bridge

一个很薄的 Native Protocol 2.1 插件，把 PaperTodo 现有内容直接交给本机 `codex` CLI，不在宿主里再造一套 AI/Agent 层。

## 行为

- 每个非空待办项增加 `>_` 操作：点击后后台执行 `codex exec`，不弹窗口。
- Todo 如果绑定本地 `.png` / `.jpg` / `.jpeg` / `.webp`，通过 Codex CLI 当前的 `--image` 参数作为图片附件发送。
- Todo 绑定其他本地文件或目录时，会把路径写进 prompt，并优先把对应 Git 仓库根目录（找不到 Git 根时为文件父目录/目录本身）作为 Codex 工作目录。
- Todo 如果绑定另一张 PaperTodo 纸片，会把可读的 Todo / Markdown 内容一起加入上下文。
- 所有 PaperTodo 纸片顶栏增加 `>_`：点击后把当前 Todo/Markdown 纸片全文通过 stdin 发送给 `codex exec`，并显示一个前台 PowerShell 窗口查看结果。窗口在 Codex 结束后保持打开。

插件不使用 `--dangerously-bypass-approvals-and-sandbox`，Codex 仍沿用用户自己的配置、登录状态、sandbox 和 approval 策略。

## 为什么需要一张插件纸片

Protocol 2.1 的 Global Top Bar / TodoActions 属于 provider Runtime；当前 Runtime 只有 provider 至少存在一张真实插件 Paper 时才存活。因此 manifest 默认用 `startupPaper` 创建一张折叠的 `Codex CLI` 纸片。删除最后一张该插件纸片后，全局按钮会随 Runtime 一起撤下。

## 设置

- **启动后自动启用**：默认开启，创建/恢复折叠的 Codex CLI 插件纸片。
- **Codex CLI 命令**：默认 `codex`；也可以填写 `codex.cmd` 或绝对路径。
- **默认工作目录**：没有绑定本地路径时使用；留空为用户目录，支持 `%USERPROFILE%` 等环境变量。

## 构建并安装

退出 PaperTodo 后，在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.CodexCliBridge\PaperTodo.Plugin.CodexCliBridge.csproj
```

随后重新打开 PaperTodo。需要本机已安装并登录 Codex CLI。

## 边界

Codex CLI 当前公开的直接附件参数是 `--image`，因此插件只把受支持图片作为真正的 CLI 附件；其他绑定文件/目录作为本地路径上下文交给 Codex，而不是伪造通用附件协议。
