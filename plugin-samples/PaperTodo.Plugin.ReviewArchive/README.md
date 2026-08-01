# 待办复盘记录池

这是 Issue #37 的插件化实现，也是协议 1.3 事件监听与只读数据能力的完整原生示例。

它会记录：

- 新建待办的创建时刻；
- 待办完成、取消完成和删除的时刻；
- 所属纸片、正文变化和来源是否已经删除；
- 今日、近 7 天和累计完成数量。

记录池保存在插件目录的 `.runtime/review-archive.json`，不属于任何一张纸片的 `StateJson`。因此删除原待办、删除整张待办纸，甚至删除承载记录池界面的笔记纸，都不会顺带删除历史；重新创建一张笔记并切回本插件即可继续读取。安装脚本升级插件时也会保留 `.runtime`。

插件只在至少有一张使用它的笔记仍可见时监听变化。折叠成胶囊仍会继续记录；彻底隐藏、删除最后一张插件纸片或退出 PaperTodo 后，期间发生的变化无法被纯后台补记。再次打开后可用“导入当前”补录现状，但补录时间会标记为“首次观察值”。

## CSV 导出

“导出 CSV”导出当前筛选和搜索结果。默认使用带 BOM 的 UTF-8，Excel 可直接打开中文内容，不额外引入 Office 或 OpenXML 依赖。

## 构建并安装

先完全退出 PaperTodo，再从仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.ReviewArchive\PaperTodo.Plugin.ReviewArchive.csproj
```

脚本会安装到：

```text
plugins\sample.review-archive.native\
```

启动 PaperTodo，新建一张笔记纸，在正文类型菜单中选择“待办复盘记录池”。
