# PaperTodo Mobile

PaperTodo v3.31 的 Android 移植版，保留纸片样式、四套明暗配色、待办、Markdown 与贴边胶囊。面向个人离线使用；最低 Android 8.0，目标机型 realme GT5 Pro。

原项目：[snownico0722/PaperTodo](https://github.com/snownico0722/PaperTodo)，对照源码版本 `v3.31`（`8de746d03052e1cda14c44b3debf32548476a489`）。这是独立移植项目。

界面预览：[待办纸](docs/screenshots/todo-completed-api36.png) · [Markdown 笔记](docs/screenshots/note-markdown-api36.png) · [悬浮纸片](docs/screenshots/overlay-expanded-api36.png) · [键盘避让](docs/screenshots/overlay-keyboard-api36.png) · [贴边胶囊](docs/screenshots/capsules-only-api36.png)。图片均为合成测试内容。

## 安装与使用

1. 将 `dist/PaperTodo-Mobile-v0.1.1.apk` 传到手机，点击安装；按系统提示允许本次来源安装应用。已安装 0.1.0 时可直接覆盖升级。
2. 打开后即可写待办、笔记。内容自动保存在手机本机。
3. 点击“悬浮”，在系统页面允许悬浮窗，并按需允许通知。纸片会显示在其他应用上方。
4. 点击边缘胶囊展开或收起纸片，一次展开一张；拖动纸片标题移动，拖右下角调整大小。
5. 长拖胶囊可换边、调整顺序；拖动顶部主胶囊调整整列高度，点击它收拢或展开整列。展开纸片时胶囊会缩成图标，给正文和标题按钮让出空间。
6. 通过通知的“停止”或设置中的“停止所有悬浮窗口”退出悬浮。

realme 若在后台关闭悬浮服务，可在应用信息的电池管理中允许后台活动，并在最近任务中锁定应用。系统强制停止或手机重启后，需要打开 App 再开启悬浮。系统保护页面可能隐藏悬浮窗口。

## 纸片操作

- 标题栏 `+✓` 新建待办纸，`+✎` 新建笔记纸，`−` 收起。点击标题重命名，左上图标切换纸片。
- 待办：输入后按键盘完成或回车提交；粘贴多行自动拆分，常见列表前缀自动清理。勾选完成、拖右侧手柄排序，操作菜单可关联其他纸片或删除。底部提供撤销、重做和清理已完成。
- 笔记：直接编辑 Markdown；底部提供加粗、斜体、链接、图片、分享与字号按钮。纯文本、基础、增强三档均保留原始正文，编辑与浏览共用一个文本控件。
- 配色、明暗主题、字号、加粗、自定义 TTF/OTF 字体及完成项行为在设置中调整。
- 支持本地图片和原版 `i:` 引用；网络图片、表格和完整块编辑器不在 v3.31 的轻量笔记范围内。电脑脚本和文件路径会保留，PowerShell 与 Windows 路径只能在电脑端执行或打开。

## 电脑与手机迁移

传输采用整库替换，不自动合并两端独立修改。导入前会显示纸片、待办和图片数量，确认后先备份手机旧数据。最近三份导入前备份保存在手机本机，可从设置导出；卸载前请导出完整 ZIP。

**纯文字**：直接将电脑的 `data.json` 传到手机，在设置中导入。手机也可导出同格式 JSON，保留 ID、关联、排序、桌面坐标及未知字段。JSON 本身不含图片。

**含图片，电脑 → 手机**：

1. 从 Windows 托盘完全退出 PaperTodo。
2. 双击 `dist/desktop-transfer/PaperTodoTransfer.exe`，选择 `1`。
3. 输入电脑数据目录和要保存的 ZIP 路径。
4. 将 ZIP 传到手机，在设置中选择“导入电脑 JSON / 完整传输包 ZIP”，检查数量并确认。

**手机 → 电脑**：

1. 在手机设置中导出完整 ZIP，并传到电脑。
2. 双击传输工具，选择 `2`，输入 ZIP 路径和一个尚不存在的新文件夹。
3. 从托盘退出电脑端，备份原数据，再将新文件夹的 `data.json`、`data.backup.json`、`note-assets.lmdb` 复制到电脑数据目录。

传输工具只操作数据副本或新建目录；完整用法见 [电脑工具说明](tools/desktop-transfer/README.zh.md)。缺失图片会明确提示，原引用保留。单图上限 8 MiB、最长边 4096 像素、图片总量 120 MiB，与原版兼容。

## 开发与验证

技术栈：Kotlin、Android 原生 Views、RecyclerView、EditText、CommonMark Java；悬浮窗口由前台服务统一管理，Activity 与服务复用纸片组件，原始 JSON 树由单一数据仓库保存。

需要 JDK 17+（本机使用 21）、Android SDK 36。设置 `JAVA_HOME` 与 `ANDROID_HOME` 后：

```powershell
.\tools\build-android.ps1 -Mode Debug
.\tools\build-android.ps1 -Mode Test
.\tools\build-android.ps1 -Mode Release
```

Release 首次构建自动生成本地签名，后续复用。**私下备份 `.local` 中的签名密钥与密码配置**，更新 APK 需要同一密钥；不要上传这些文件。Android 仪器测试运行在独立模拟器上：

```powershell
.\gradlew.bat :app:connectedDebugAndroidTest
```

测试使用合成数据，覆盖协议往返、图片校验、保存恢复、撤销、中文输入与真实悬浮窗口。实际验证结果及待完成的 realme 真机检查见 [验证记录](VERIFICATION.md)。

## 许可

保留原作者许可和 Required Notice，详见 [LICENSE.md](LICENSE.md)、[NOTICE.md](NOTICE.md) 和 [第三方声明](THIRD-PARTY-NOTICES.md)。

Required Notice: PaperTodo Copyright © 2026 snownico0722. PaperTodo is licensed under the PolyForm Noncommercial License 1.0.0 together with the PaperTodo Individual Professional Use Additional Permission 1.0, available in the original project LICENSE file at https://github.com/snownico0722/PaperTodo/blob/main/LICENSE.md. Original project: https://github.com/snownico0722/PaperTodo

上传 GitHub 时上传本工程源码；个人 `data.json`、图片、构建缓存和签名密钥不应放进仓库。
