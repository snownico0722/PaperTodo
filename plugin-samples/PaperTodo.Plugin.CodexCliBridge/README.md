# Codex CLI Bridge

一个很薄的 Native Protocol 2.1 插件，把 PaperTodo 现有内容直接交给本机 `codex` CLI，不在宿主里再造一套 AI/Agent 层。

## 行为

- 每个非空待办项增加 Codex 操作：行内按钮保持紧凑的 `Codex` / `>_`，右键菜单显示“发送到 Codex”；默认以前台 PowerShell 窗口执行 `codex exec`，仅在插件设置中启用“后台执行”后才隐藏窗口静默运行。
- Todo 如果绑定本地 `.png` / `.jpg` / `.jpeg` / `.webp`，通过 Codex CLI 当前的 `--image` 参数作为图片附件发送。
- Todo 绑定其他本地文件或目录时，会把路径写进 prompt，并优先把对应 Git 仓库根目录（找不到 Git 根时为文件父目录/目录本身）作为 Codex 工作目录。
- Todo 如果绑定另一张 PaperTodo 纸片，会把可读的 Todo / Markdown 内容一起加入上下文。
- 所有 PaperTodo 纸片顶栏增加 `>_`：点击后把当前 Todo/Markdown 纸片全文通过 stdin 发送给 `codex exec`；同样默认前台执行，勾选“后台执行”后改为静默运行。前台窗口在 Codex 结束后保持打开。
- 自动创建的 `Codex CLI` 插件纸片是默认提示词编辑器，启动时默认隐藏；需要修改时从插件“更多设置”点击“编辑提示词”即可直接展开并打开。未编辑时使用内置默认提示词，用户编辑后使用保存在 provider Runtime state 的内容，每次调用都会放到本次 Todo/Note 内容前面。
- 插件设置、插件纸片文案、待办/纸片发送上下文以及“从未编辑”的内置默认提示词会跟随 PaperTodo 当前 UI 语言：中文界面使用中文，其他语言回退英文。用户自己编辑过的默认提示词不会被切换语言覆盖。
- 默认提示词只保留普通任务要求；插件制作技能、PaperTodo 操作技能按各自开关在发送时注入，三个新开关均默认开启。
- 如果安装后的插件目录中存在 `AGENTS.md` / `AGENT.md` / `agent.md`，会优先把它作为 Codex 的 `developer_instructions`。不存在时不加覆盖，Codex 正常读取用户自己的全局配置与项目规则。

插件不使用 `--dangerously-bypass-approvals-and-sandbox`，Codex 仍沿用用户自己的登录状态、sandbox 和 approval 策略。

## 为什么需要一张插件纸片

Protocol 2.1 的 Global Top Bar / TodoActions 属于 provider Runtime；当前 Runtime 只有 provider 至少存在一张真实插件 Paper 时才存活。因此 manifest 默认用 `startupPaper` 创建一张**隐藏的** `Codex CLI` 纸片。隐藏不影响它作为 Runtime owner；删除最后一张该插件纸片后，全局按钮才会随 Runtime 一起撤下。

这张纸同时承载“默认传入提示词”的编辑入口；不建立第二套全局 AI 管理页。

## 内置 Skill 与默认提示词

Skill 安装在 `skills/papertodo-plugin-creator/SKILL.md`，包含 Web / Native 选择、最小 Web 示例、状态与 Runtime 边界、构建安装和验证方法。`references/plugin-development.md` 是构建时从仓库 `plugin-samples/README.md` 直接复制的完整开发手册，随插件离线携带；维护 API 说明仍只修改原手册。

“开启插件技能”只在发送时追加插件开发 Skill 的用途与绝对路径；“开启本软件操作技能”只追加一段简短的 MCP 操作指引。两者互相独立，也独立于用户可编辑的默认提示词，前台与后台共用同一逻辑；技能正文不会写回提示词编辑框。操作指引与软件设置中的“复制 AI Skill”共用 `src/PaperTodoOperationSkill.cs`，中文界面使用中文，其他语言回退英文。

“允许本插件开启 MCP”允许 Codex 在任务需要 PaperTodo MCP、而 MCP 或所需权限尚未开启时，运行本次上下文提供的 `PaperTodo.exe --enable-mcp-for-codex` 命令。该命令通过已有单实例通道交给正在运行的宿主；宿主重新检查插件仍有纸片、Runtime 正常、当前 `allowMcp` 设置和设置读取状态后，一次性开启 MCP 总开关以及“新增/空白写入”“完整写入”“直接删除”以及“敏感软件设置控制”全部 MCP 权限。没有主实例时不启动 GUI；取消勾选后不再允许后续自动开启，但不会自动撤销已经开启的 MCP 权限。

启用操作技能或允许按需开启 MCP 时，插件用本次 `codex exec -c` 参数注册 `papertodo_bridge` stdio 服务，指向正在运行的 PaperTodo 可执行文件；不改写用户全局 Codex 配置、登录信息或其他 MCP 服务。启动 bridge 本身不会打开 GUI 的 MCP 总开关。写入仍通过现有 MCP / `PaperCommandService` 完成。

内置默认提示词按 PaperTodo UI 语言动态选择；中文使用中文版本，其他语言使用英文版本。这个动态默认值只对“从未编辑”的状态生效，不会把语言切换写成一次用户编辑。默认提示词要求 Todo 保持简短，内容过长时可以拆到绑定 Note；如果“启用待办关联纸片”功能关闭，则不用绑定 Note。Note 同样避免过度冗长，但不要求过度精简。

| 状态 | 纸片显示与本次调用 |
| --- | --- |
| 从未编辑，无保存的提示词 | 使用与当前 PaperTodo UI 语言匹配的插件内置默认提示词；仅打开纸片不会把它保存为用户自定义内容 |
| 已编辑 | 使用用户保存的提示词，重启、切换界面语言及插件更新都不覆盖；即使改回与任一内置文本相同，也视为用户自定义 |
| 主动清空 | 默认提示词保持为空；仍按三个独立开关注入技能和按需开启说明。三个开关都关闭时只发送本次内容 |

旧版 `DefaultPrompt` / `defaultPrompt` 字符串原样保留，包括空字符串；缺失或 `null` 才表示未编辑。损坏的 JSON 会交给宿主的 Runtime 失败流程，不生成可覆盖旧内容的默认状态。

## 设置

基础设置：

- **启动后自动启用**：默认开启，创建/恢复默认隐藏的 Codex CLI 插件纸片，让 Runtime 与全局 Codex 操作保持可用。
- **后台执行**：默认关闭。关闭时待办行内按钮和纸片顶栏按钮都以前台 PowerShell 窗口运行；开启后两种入口都隐藏窗口静默执行。
- **模型**：默认 `gpt-6-astra`，通过 `-m` 传给 Codex；留空则不覆盖 Codex 自己的模型配置。
- **推理**：默认 `xhigh`（极高），通过 `-c model_reasoning_effort=xhigh` 传给 Codex；留空则不覆盖 Codex 自己的推理配置。

“更多设置”：

- **Codex CLI 命令**：默认 `codex`；也可以填写 `codex.cmd` 或绝对路径。
- **默认工作目录**：没有绑定本地路径时使用；留空为用户目录，支持 `%USERPROFILE%` 等环境变量。
- **编辑提示词**：打开并展开默认隐藏的 Codex CLI 提示词编辑纸片。

宿主绘制的 manifest/settings 文案通过 Protocol 2.1 `locales` 本地化；Native 插件自己绘制的纸片内容读取 `context.UiLanguage` 使用同一语言判断。

更多设置中的三个新开关（默认全部开启）：

- **开启插件技能**：运行时注入 `papertodo-plugin-creator` 的指引。
- **开启本软件操作技能**：运行时注入纸片、待办、笔记的 MCP 操作指引。
- **允许本插件开启 MCP**：任务需要 MCP 时，允许自动开启 MCP 总开关及新增/空白写入、完整写入、直接删除、敏感软件设置控制等全部 MCP 权限。

## AGENTS.md 优先级

插件每次调用 Codex 前都会检查自身安装目录。如果发现 `AGENTS.md`（兼容 `AGENT.md` / `agent.md`），会优先将其作为本次调用的 `developer_instructions`；如果没有，就完全不设置这一覆盖项，让 Codex 继续使用正常的全局/项目默认规则。

短文件直接作为 developer instructions 传入；过长文件则给 Codex 一条高优先级指令，要求它先读取该文件，避免把很长内容塞进命令行。

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
