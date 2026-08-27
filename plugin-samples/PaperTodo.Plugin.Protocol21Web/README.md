# Protocol 2.1 Web 示例

这个示例只验证 Protocol 2.1 新增的宿主原生 contribution surface，不实现具体产品业务。

它声明一个 provider Runtime，并演示：

- `papertodo.todoActions.set(paperId, todoId, actions)`：给普通 Todo 行右侧发布 `SVG Path + 文字` 按钮；同一 descriptor 默认也进入 Todo 右键菜单。
- `todoActionInvoked`：点击时返回当前 `PaperId`、`TodoId` 和最新 `TodoSnapshot`；示例会把 `linkedPaperId`、`linkedPath`、`linkedPathIsDirectory` 打到控制台。
- `papertodo.topBarLabels.set(paperId, labels)`：给任意现有 Paper 发布不可点击的宿主原生顶栏标签。
- 顶栏“刷新 2.1”动作：手动重新扫描当前 Workspace 并重发 contribution；示例不通过轮询维持状态。

这些 contribution 都是 Runtime 生命周期内的易失 presentation。插件自己的业务状态应保存在 Runtime state；Runtime 结束、Web document 被替换或失败恢复时，宿主撤销旧 contribution，由新 Runtime/document 根据业务状态重新发布。

按钮和标签都由 PaperTodo 渲染。插件不返回 WPF 控件、不拥有 Todo 行或顶栏 visual tree，也不能借此修改宿主布局/输入生命周期。
