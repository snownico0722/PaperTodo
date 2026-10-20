# 行为检查

检查使用隔离临时目录，不读取日常 PaperTodo 数据。需要 Windows 和 .NET 10 SDK。

```powershell
dotnet run --project tests/PaperTodo.PersistenceChecks -c Release
dotnet run --project tests/PaperTodo.ClipboardImageChecks -c Release
```

图片检查通过实际编辑器入口导入 PNG 和 Bitmap，验证透明、半透明与不透明黑色像素、PNG 优先读取、旧截图的透明通道兼容、单步撤销／重做，以及关闭后重新读取 LMDB 中的图片。补充图片格式时还检查原有文本、HTML、RTF 和自定义数据保持不变。默认不访问系统剪贴板，也不显示窗口。

实际 Windows 剪贴板往返单独启用：

```powershell
dotnet run --project tests/PaperTodo.ClipboardImageChecks -c Release -- --clipboard
```

此模式会用合成图片替换当前系统剪贴板，须与其他剪贴板操作串行执行。它调用产品复制入口，检查方法返回后 PNG 和 Bitmap 仍可读取，并确认写入进程退出后 PNG 像素仍完整。
