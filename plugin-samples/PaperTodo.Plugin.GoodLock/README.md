# Good Lock

PaperTodo 的官方全局调参插件。主程序独立读取 `user.json`；Good Lock 用专用纸片展示修改，提供应用、重置、有限微调、另存为与快速重启。

## 安装

需要支持插件 API 2.4 的 PaperTodo。

退出 PaperTodo，将已编译的 `plugins/official.good-lock.native/` 整个目录放到应用的 `plugins/` 下，再启动。在插件页启用 Good Lock，创建或打开它的纸片。

从源码构建和安装：

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.GoodLock\PaperTodo.Plugin.GoodLock.csproj
```

插件不随主程序发布包捆绑。最终目录只包含 DLL、依赖清单、manifest 等必要运行文件。

## 使用

- **打开编辑文件**：在系统关联的编辑器中打开主程序使用的 `user.json`；首次打开时创建空配置。保存并返回纸片，就能看到修改。
- **应用**：采用外部文件的新内容，并提交本页待应用的重置或微调。支持即时生效的项目立即采用；创建尺寸等从下一次使用生效；需要重启的项目留待重启。
- **全体重置**：形成清空外部覆盖的草稿，点击应用后生效。不会重置普通设置或纸片数据。
- **快速重启**：先应用本轮修改，再走 PaperTodo 正常保存退出与重启，返回这张纸片。
- **另存为**：将当前文件结合待应用修改保存为另一份 JSON，不切换主程序当前使用的路径，也不会把未修改的默认值写入方案。
- **单项重置图标**：删除该键的覆盖，待应用后恢复原取值方式。
- **魔棒图标**：尝试给本项形成微调草稿。仅有限规则提供建议，可以参考相关参数；没有合适建议时保持原值。

页面只显示已有覆盖、运行中尚未清除的覆盖和本页草稿。“默认值”可以是当前原值、简短计算说明或 `—`；“修改后”显示拟采用的值，清除覆盖时显示 `—`。

灰点表示待应用，绿点表示运行中已经采用，红点表示已保存但需要重启。修改后的数值标红只表示超出建议范围，不阻止应用。未知键会列出但不会显示为已采用。

本页的重置和微调草稿只保存按键操作。若文件在外部修改，应用会刷新文件并保留本页草稿，提示检查后再次应用，不会用旧整份副本覆盖文件。页面在打开、激活和展开时读取文件，没有后台监听或周期轮询。

## 开发边界

插件只引用 `PaperTodo.Plugin.Abstractions`，通过 `context.InjectionApi` 使用主程序的配置读写与生效逻辑。它不写普通工作区设置，不拥有第二份生效配置，也不增加 provider Runtime。前端草稿使用现有 per-paper state，在正文重建后仍可继续应用。

所有工作区共用一份 `user.json`。显式写入与默认相同的数也保留为覆盖；删除键才恢复默认来源。内部注入键可能随模块变化而退役，建议始终通过 API 目录核对当前支持项。

---

## English

Good Lock is the official Native companion for PaperTodo's global `user.json`. It requires plugin API 2.4. Install the complete `plugins/official.good-lock.native/` directory separately, enable the plugin, and open its Paper.

Use **Open file** for ordinary value edits. The Paper lists overrides with their default/reference value, proposed value, and application status. Reset and adjustment actions create a pending patch; **Apply** saves that patch and adopts the latest configuration. **Restart** applies first, then uses the host's normal saved shutdown/restart. **Save as** exports the current proposed overrides without changing the active file.

Gray means pending, green means adopted, and red means saved but awaiting restart. A red value is only a recommendation warning. Suggestions are limited rules, may consider related parameters, and may return no change. Unknown keys remain visible and are not marked applied. Returning to the Paper refreshes the file; the plugin has no background watcher or provider Runtime.
