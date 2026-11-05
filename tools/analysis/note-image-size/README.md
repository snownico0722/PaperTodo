# 笔记长图体积预测的校准

当前导出职责与数据流以 [ARCHITECTURE.md](../../../../doc/ARCHITECTURE.md) 为准。本目录只保存预测模型的可复现校准方式，不参与应用运行，不增加应用依赖。

## 方法与边界

`NoteImageSizeEstimate` 扫描每个输出像素：PNG 侧统计 Sub 差分的熵、零值比例及相邻 RGB 差分的重复程度；JPG 侧统计每个 8×8 块的亮度、色度变化、多尺度均值差和棋盘卷积。固定线性回归输出质量 90 JPG、RGB/Sub PNG 两个预测值。没有抽样、DCT、量化、LZ 匹配、熵编码或文件写入；仍有完整分条光栅化和像素扫描成本，复杂度与像素总数成正比。

统计特征参考 [Predicting and Optimizing Image Compression](https://www.pure.ed.ac.uk/ws/files/28267853/p665_murashko.pdf) 的内容复杂度思路，代码和系数自行实现。[ACACIA](https://github.com/johndthomson/acacia) 的模型针对照片，并明确不支持高反差矢量图形，因此未引入其代码、模型或依赖。PNG 统计针对本项目固定使用的 Sub 过滤器，[PNG 规范](https://www.w3.org/TR/png-3/#10Compression) 的通用压缩说明不能直接提供本项目的体积预测系数。

这是经验预测。常见笔记以约 1 MiB 的显示误差为目标，大图允许较大的绝对误差；没有通用最大误差保证。小于 1 MiB 显示“小于1m”，其余四舍五入显示整数，验收包含显示取整的误差。

## 离线校准

开发工具使用 Python、Pillow、NumPy、SciPy、scikit-learn；初次校准使用 Pillow 13.0.0。Linux 需要 DejaVu Sans 字体，其他路径可用 `PAPERTODO_CALIBRATION_FONT` 指定。照片取自 scikit-learn 的内置示例图片，本仓库不复制图片。

```sh
python tools/analysis/note-image-size/calibrate.py
```

脚本生成 140 个 1200×1200 样例：文字、代码、图文、照片、纯色、噪声、纹理、渐变、形状、棋盘。按种子固定划分 100 个拟合、40 个保留验证样例，输出 `dataset.json` 和 `coefficients.json`。保留种子的照片来源仍可能与训练组重复，不能把这一组当作独立照片泛化验证。离线脚本为了获得真实标签会编码图片；这不属于应用预览路径。

Python 标签使用 Pillow JPEG 质量 90 / 4:2:0，以及 RGB/Sub 扫描行的 zlib level 6。Windows 行为测试 `NoteImageSizeChecks` 使用真正的 WPF 排版、WIC JPEG 和 `NoteImagePngWriter`，测试中文长文、不同汉字、代码、混合格式及图片，并打印预测、实际大小和包含光栅化的耗时。PNG/JPEG 编码器、质量、过滤方式改变时需要重新校准。

首次独立 Windows 验证（优化前）中，9 组样例的预测原值最大绝对误差约为 JPG 0.71 MiB、PNG 0.60 MiB。1200×55644 中文长文 JPG 实际 12,964,272 字节、预测 12,223,632；PNG 实际 9,108,601、预测 9,162,349。一个独立照片长图的 Python 验证曾出现 PNG 约 1.05 MiB 误差，保留这一限制，不把固定样例通过表述成普遍误差保证。

预览先出现，估算在后台运行；一次扫描计算两个格式，切换时不重新扫描。全像素统计不一定比一次原生 JPG 编码快，尤其超长纯文字；它满足不预压缩的选择，不能宣传为零成本计算。
