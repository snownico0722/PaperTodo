# 扩展 API 示例（Native）

完整示例同时使用 `PaperActions`、`NoteAssets` 和 `Surfaces`。没有复制设置页的下拉框实现；`Inspector.OnThemeChanged` 调用已有 `ApplySelectStyle`。

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 -ProjectPath .\plugin-samples\PaperTodo.Plugin.ExtensionApi\PaperTodo.Plugin.ExtensionApi.csproj
```

重启 PaperTodo，将一张笔记的正文切换到“扩展 API 示例”。保持它存在，其他 Markdown 纸片会出现顶栏浮层按钮和纸片菜单操作。可以隐藏示例纸片；删除最后一张示例纸片会结束 Runtime，并关闭该插件的辅助界面。

输入笔记中 `i:` 后面的图片 ID 读取 MIME 和编码大小。窗口以笔记 ID 区分，同一笔记重复打开只激活已有窗口；浮层一次只有一个。没有锚点的菜单来源会退回独立窗口，而不是猜测全局鼠标位置。

完整合同与错误码见上级 `README.md`。
