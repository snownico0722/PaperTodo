# PR227 拖动优化复核与原生显示回归

日期：2026-09-22。**拖动路径优化已落地；当前完整 CI 仍有原生窗口首帧像素失败，不是全绿，不建议直接合入 main。** 本文不以早期成功的构建代替当前状态。

## 版本与已经保留的优化

- 拖动基线：`5b9114dd2ff4a8ed3d170f7242868f7cb37346a0`，上一轮材质重构完成后的版本。
- 拖动产品代码：`380ebcd9b0a9cdcf8f039605d754732523e0d118`。
- 当前代码及测试：`359bc834990d09287b6450c364887711e3dd9902`。它只把 Aero 视觉测试从具体 TranslateTransform 类型改为检查 Transform.Value 和关闭动画后的 Identity；没有新增产品改动。
- 本文提交只有文档。所有产品改动仍在 #227 原分支，保持 Draft，未合入 main。

实际产品变化仍只有四个文件，+47/-7 行：按 WINDOWPOS flags 区分纯位置变化和尺寸/显隐/窗口边框变化；纯移动只重投影已有背景和更新 Aero 反光，不重新检查背景生命周期、查询局部捕获区域或重画整个材质外壳；Aero 横纵反光坐标通过一次矩阵变更发布。

没有改写拖动输入、窗口句柄、正文、撤销记录或命中区域。没有在拖动时关特效、降模糊强度、降应用帧率或降低采样节拍。显隐、透明度、尺寸、DPI、系统环境变化仍走完整处理。WINDOWPOS 消息不被消费，保留 DefWindowProc/WPF 的 WM_MOVE、WM_SIZE 行为。

## 真实拖动结果已重新从原始 JSON 核对

[同机 ABBA 运行 35685292415](https://github.com/snownico0722/PaperTodo/actions/runs/35685292415) 成功。使用生产 PaperWindow 标题栏和折叠胶囊的实际鼠标按下、移动和释放，不是循环改 Left/Top。11 个场景，旧/新各四个手势/场景，共 88 个正式手势，另有 44 个预热手势。

原始数据确认全部 88 个正式手势都各进入/退出一次原生移动循环，尺寸变化计数为零，窗口和内容身份保留；每个手势记录 65–88 次实际窗口移动。运行环境为 Hyper-V Video、1024×768、DPI 1、.NET 10.0.12、4 个逻辑处理器；测试显式开启“拖动时显示窗口内容”，结束后恢复原设置。

以下为每场景每版四个正式手势的中位数，一次手势约 1.6 秒。托管分配是期间新增字节，不是应用常驻内存。

| 场景 | 外壳 OnRender 旧→新 | UI 线程分配旧→新 | 分配变化 | 进程 CPU 时间旧→新 |
| --- | ---: | ---: | ---: | ---: |
| Aero 展开纸片，动画开启 | 85→0 | 2,594,060→2,247,120 B | -13.37% | 851.5625→828.125 ms |
| 实时亚克力胶囊 | 71→0 | 5,796,300→2,815,020 B | -51.43% | 1171.875→1296.875 ms |
| Aero 胶囊 | 85.5→0 | 5,929,836→5,190,820 B | -12.46% | 625→593.75 ms |

实时亚克力胶囊的局部捕获区域查询从 71 降到 0，LayoutUpdated 通知从 73 降到 2；采样次数保持 16→16，背景投影仍执行。默认纸片和原生 Mica/Acrylic/Clear Acrylic/描图纸展开窗口原本就没有这类外壳重绘，没有为这些正常路径增加新机制。

**可以确认的是少做了 UI 侧重复工作，不能确认总 CPU 一致下降。** 实时亚克力胶囊的进程 CPU 中位数反而约增加 10.7%；其移动消息 P95 中位数虽为 40.2891→32.32975ms，样本很少且分布重叠，不能宣称卡顿消失。Aero 展开纸片对应 P95 为 16.47525→16.4168ms，基本相同。窗口移动消息不是显示帧，以上不能替代 240Hz/HDR 真机表现。

另外取回了 [线程诊断 35685040412](https://github.com/snownico0722/PaperTodo/actions/runs/35685040412) 的原始 trace 和线程计时。该诊断针对中间版本 `0d10dc94`，带采样器开销，不用于替代最终 ABBA 数据。它显示进程 CPU 很大部分不在 UI 线程；不能把所有非 UI 线程笼统归因于 DWM，也不能据此承诺继续删 UI 工作就会等比例减少整个进程或 GPU 开销。

## 当前完整构建确实有失败

[359bc834 完整构建 35689889387](https://github.com/snownico0722/PaperTodo/actions/runs/35689889387) 的 Release 编译、窗口层级、Markdown、待办、边缘浏览、线程、生命周期、关闭激活和重启检查均通过。材质行为检查继续通过，之后可选桌面像素检查失败：

```text
acrylic-light: actual WPF pin never appeared in the desktop composite
```

[插件/公开设置检查 35689892685](https://github.com/snownico0722/PaperTodo/actions/runs/35689892685) 和 [持久化检查 35689892674](https://github.com/snownico0722/PaperTodo/actions/runs/35689892674) 成功。专项拖动任务未启用桌面像素的 SKIP 不算像素验收成功。

### 不是只在拖动新版本出现

取回并核对了 [原版/新版同机视觉控制 35690637855](https://github.com/snownico0722/PaperTodo/actions/runs/35690637855)。其基线是拖动优化前的 `5b9114dd`，候选为 `359bc834`，使用同样的截图检查、只增加诊断日志，顺序为旧—新—新—旧：

| 进程 | 结果 |
| --- | --- |
| 1，拖动前基线 | tracingPaper-light 的正文 pin 在 12 次桌面检查中都未出现，失败 |
| 2，拖动优化版 | 同一 tracingPaper-light 检查失败 |
| 3，拖动优化版 | 通过 |
| 4，拖动前基线 | 通过 |

所以该显示问题在拖动前基线也能复现，不能把一次绿色重跑当成已经修好，也不能仅凭这四个进程声称发生频率完全没变。

### 诊断确认了什么

[原生呈现探针 35691357523](https://github.com/snownico0722/PaperTodo/actions/runs/35691357523) 在 acrylic-dark 失败时确认：窗口可见、Opacity=1、原生材质有效、截图 affinity=0，pin 位置最上层 HWND 就是目标窗口。WPF 离屏图像里存在完整控件，但桌面合成 pin 像素为 0。现有 InvalidateContent（RedrawWindow INVALIDATE|ALLCHILDREN）调用后，桌面 pin 像素恢复为 42。

这把调查缩小到原生重定向图像/呈现同步附近，但没有证明具体的底层根因。离屏图像存在不能替代实际桌面正确，也没有改测试去把离屏图算成成功。

## 本轮试验并否决了一种补绘方案

在临时工作分支试验：保留原有 Clear Acrylic 首帧延迟启用；其他采用 redirection alpha 的窗口，在已有的一次性 ContentRendered 回调里额外请求一次全窗口重绘。不加定时器，不改拖动热路径，不放宽像素断言。

[同机 ABBA 首帧压力对照 35692739417](https://github.com/snownico0722/PaperTodo/actions/runs/35692739417) 已完成，结果失败。此处 baseline 是当前 `359bc834`，不是上节拖动前的基线。每个进程先运行 VisualChecks，再运行 12 轮 NativeSurfaceChecks：

| 进程 | 方案 | 失败轮数/12轮 |
| --- | --- | ---: |
| 1 | 现有代码 | 4 |
| 2 | 首次 ContentRendered 补绘 | 4 |
| 3 | 首次 ContentRendered 补绘 | 3 |
| 4 | 现有代码 | 2 |

现有代码合计 6/24 轮失败，试验方案 7/24 轮失败。每轮在首个失败断言后结束，不是逐窗口故障率，未做统计显著性判断。**这个方案没有证明有效，也没有消除失败，因此未写入 #227 的产品代码。** 不继续以多次定时补绘或无限重试来掩盖问题。

WPF 的 [Window.PostContentRendered 源码](https://source.dot.net/PresentationFramework/System/Windows/Window.cs.html) 是 Dispatcher Input 优先级通知，不能当成 GPU/DWM 物理提交完成的屏障；[dotnet/wpf #5652](https://github.com/dotnet/wpf/issues/5652) 也有关于等待 Rendering/ContentRendered 仍无法保证截图同步的原始报告。这里是结合源码和本次实测的限制说明，不把第三方 issue 当成当前故障已经确诊。

此前 Clear Acrylic 先等首轮内容再启用 accent 的修复仍保留。它改变的是首次 recipe 启用顺序，与本次被否决的新增补绘不是同一个处理，不能因补绘试验失败就删除旧修复。

## 复现证据与后续边界

- 真实拖动：artifact `10677270159`；ZIP SHA-256 `a6a171b19f8ed669477179fc259514266c4349b7f443938a3b7aec9f1fe1ba8f`。
- 拖动前/后视觉控制：artifact `10678825338`；ZIP SHA-256 `84d17ef7dda5371a6fb2efd03ee946cd0b6303fc3d06158f82feb9033ef8c331`。
- 原生呈现探针：artifact `10679075928`；ZIP SHA-256 `d5196b4dba4b2ad1a08c052ca66b605805118c5f79d85665a44b34b0b2339d76`。
- 本轮无效补绘试验：artifact `10679423728`；ZIP SHA-256 `39b4f426ebcd840c709148a7eef9a190a6770391304d842031e91dc173e2ea56`。

原始日志、JSON 和截图另存于本轮对话，Actions 原始保留期只有一天。没有将临时工作流、补丁、源码导出 bundle 或试验失败的补绘加入产品分支。

结论：保留已经有对照依据的拖动减负，不宣称总 CPU、显示 FPS 或主观手感已全面改善。当前未解决项是实际原生窗口内容偶发没有进入桌面合成。应在独立的小复现中继续核对 HwndTarget/窗口样式/原生图像失效的时序，并做 Windows 11 真机对照；在此之前保持 Draft，不用早期材质 CI 成功覆盖当前失败。