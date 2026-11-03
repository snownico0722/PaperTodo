# PaperTodo 电脑与手机传输工具

面向 Windows x64，读取电脑端 v3.31 的 `data.json` 和 `note-assets.lmdb`，与安卓端交换完整 ZIP 传输包。没有图片时也可直接在安卓端导入、导出 `data.json`。

## 电脑 → 手机

1. 从托盘完全退出电脑端 PaperTodo。
2. 双击 `PaperTodoTransfer.exe`，选择 `1`，输入电脑端数据文件夹及要保存的 ZIP 文件路径。
3. 把 ZIP 传到手机，在手机版菜单中导入；预览数量、备份并确认替换。

也可在终端运行：

```powershell
.\PaperTodoTransfer.exe export 'D:\Apps\PaperTodo' 'D:\Transfer\paper.zip'
```

导出仅打开临时副本，不会改动电脑端原始文件。若检测到此数据文件夹中的电脑端仍运行、图片库损坏或输出文件已存在，操作会停止并说明原因；其他目录中正在运行的 PaperTodo 不影响独立备份数据的转换。

## 手机 → 电脑

1. 在手机版导出完整 ZIP 传输包，并传到电脑。
2. 运行工具选择 `2`，指定 ZIP 和一个尚不存在的新文件夹。
3. 工具校验全部图片及数据后，在新目录生成 `data.json`、`data.backup.json`、`note-assets.lmdb`。
4. 备份电脑端旧数据并完全退出 PaperTodo，再手动将新目录中的三个文件复制到电脑端数据目录。

```powershell
.\PaperTodoTransfer.exe restore 'D:\Transfer\phone.zip' 'D:\Transfer\RestoredPaperTodo'
```

本工具执行整库传输，不自动合并两端分别修改的数据；往返时以最后一次导入的数据为准。缺失图片引用保留并提示，无法凭 JSON 还原未携带的图片。

## 传输包协议 v1

ZIP 中固定包含：

- `manifest.json`：`{"format":"PaperTodoTransfer","version":1}`。
- `data.json`：原始 UTF-8 JSON；桌面坐标、未知字段、纸片与待办 ID 原样保留。
- `images/meta.json`：v3.31 图片元数据对象数组，允许空数组。
- `images/<id>`：无扩展名的原始图片字节；图片 ID 是 3–8 位十进制数字。

元数据对象使用 `id`、`noteId`、`mime`、`width`、`height`、`sha256`、`byteLength`、`createdAt`、可选 `originalName` 字段。支持 PNG、JPEG、GIF、BMP、TIFF、WebP 原始编码。图片 ID（含合法历史 ID `000`）和正文 `i:` 引用保持不变，恢复后的 nextImageNumber 为最大数字 ID + 1。单图最多 8 MiB、最长边 4096 像素、图片总量最多 120 MiB、图片数量最多 20000，data.json 最多 16 MiB。

## 构建与自测试

需要 .NET 8 SDK（Windows x64）。普通不带运行库版本需要 .NET 8 Desktop Runtime；自包含版本无需另外安装运行库。

```powershell
dotnet build .\PaperTodoTransfer.csproj -c Release -p:MSBuildEnableWorkloadResolver=false
dotnet run --project .\PaperTodoTransfer.csproj -c Release --no-build -- self-test
dotnet publish .\PaperTodoTransfer.csproj -c Release --self-contained true -o ..\..\dist\desktop-transfer -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:MSBuildEnableWorkloadResolver=false
```

`self-test` 只使用临时生成的合成纸片和 PNG，实际读写 LMDB，覆盖往返、未知字段、缺图、SHA-256、尺寸、重复条目、路径越界、异常包及禁止覆盖。

本工具遵循原项目许可；请随程序保留 LICENSE.md、NOTICE.md 及 native 下的 LMDB 许可文件。WebP 的尺寸验证直接读取标准容器头，不要求额外安装 Windows WebP 编解码器。
