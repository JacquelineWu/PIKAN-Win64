# PIKAN · Windows 版（ACDSee 风格看图软件）

> **开源许可：[MIT](./LICENSE)** · Copyright (c) 2026 JacquelineWu (flyercat)
>
> 本仓库是 [PIKAN-HarmonyOS](https://github.com/JacquelineWu/PIKAN-HarmonyOS)（ArkTS / ArkUI）的
> **Windows x64 移植版**，用 **C# + WPF（.NET 10）** 重写。
> 鸿蒙版已加为 git 远端 `harmony`，可随时对照两边实现。

界面布局与鸿蒙版一致（ACDSee 风格）：

- **顶栏**：标题 + 当前文件名 + 打开图库 / 选择图片 / 选择文件夹 / 回到上次 / 全屏 / 幻灯片
- **左侧**：图像档案列表（缩略图 + 文件名 + 格式 + 体积），虚拟化滚动、点击切换
- **右侧**：大图预览区（深色背景），左上角信息浮层 + 右下角缩放角标
- **底栏**：状态栏

## 功能

| 能力 | 说明 |
|---|---|
| 格式 | HEIC / HEIF / JPG / JPEG / PNG / WEBP / BMP / GIF，走 **WIC（Windows Imaging Component）** |
| 滚轮翻页 | 鼠标滚轮 **上一张 / 下一张**；**Ctrl + 滚轮** = 缩放（互不冲突） |
| 鼠标键缩放 | 预览区 **左键放大 50%**（×1.5）、**右键放大 100%**（×2.0），均以光标为锚点 |
| 缩放 | 滚轮(Ctrl) / `＋` `－` 按钮 / 键盘 `↑` `↓` |
| 平移 | 鼠标拖拽画布 |
| 旋转 | `左旋` `右旋` 各 90°；键盘 `R` |
| 镜像 | `镜像H` `镜像V`（可叠加） |
| 复位 | **双击画布** / `适应` 按钮 / 键盘 `0` |
| 1:1 | `1:1` 按钮 / 键盘 `F`（一个图片像素对应一个屏幕像素） |
| 幻灯片 | `幻灯片` 按钮 / 空格键，默认 3 秒切换 |
| 键盘翻页 | `←` `→` 上一张/下一张 |
| 关闭应用 | **`Esc`**（非全屏时；全屏下 `Esc` 先退全屏，再按一次才关闭） |
| 来源 | `打开图库`（递归扫描「图片」库）/ `选择图片`（多选文件）/ `选择文件夹` / **拖放文件或文件夹** / **命令行传参** |
| 性能 | 列表虚拟化 + 128px 缩略图 LRU 缓存（限并发 3）；主视图按「画布长边 × 2」降采样解码 |
| 后台预载 | 自动预解码当前位置**前后各 20 张**（768px 低清），窗口随位置滑动，翻页几乎无等待 |
| 切图过渡 | 新图先以「低清 + 大模糊」出现 → **分档对焦** → 清晰图就绪后整层淡出（约 0.1s） |
| 位置记忆 | 记住「上次停在第几张」，下次打开自动跳回（顶栏「回到上次」可随时手动跳） |
| 全屏 | 顶栏 `全屏` 按钮 / **F11** 进入，**Esc** / **F11** / 右上角「退出全屏」退出 |

### HEIC / HEIF 说明

解码依赖系统装的 **「HEIF Image Extensions」**（Microsoft Store，免费）。
本机已装（`Microsoft.HEIFImageExtension`），所以 **无需 libheif 之类的第三方库**，项目零 NuGet 依赖。
没装的机器上 HEIC 会解码失败并在状态栏提示，其余格式不受影响 —— 与鸿蒙版依赖系统 codec 的行为一致。
同理 WEBP 依赖「Webp Image Extensions」（本机也已装）。

## 构建与运行

```bat
dotnet build -c Release
bin\Release\net10.0-windows\PIKAN.exe
```

需要 **.NET 10 SDK**（`net10.0-windows`，含 WPF / WindowsDesktop 运行时）。
本仓库用 Visual Studio 2026 / `dotnet build` 均可，无第三方包，restore 秒过。

### 独立单文件 exe（可拷到任意 Win64 机器）

仓库根目录的 **`PIKAN.exe`（约 66 MB）** 就是自包含单文件版本：**目标机不用装 .NET，不用装 CUDA Toolkit，
不依赖任何外部文件**——双击即用，拷到 U 盘也行。

重新生成：

```bat
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:SatelliteResourceLanguages=en

copy bin\Release\net10.0-windows\win-x64\publish\PIKAN.exe PIKAN.exe
```

几个已知事项：

- `IncludeNativeLibrariesForSelfExtract` 是必需的：WPF 的 native 组件（`wpfgfx_cor3.dll`、
  `PresentationNative_cor3.dll`、`D3DCompiler_47_cor3.dll` 等）没法压进单文件 bundle，
  靠它在运行时自解压，所以发布目录里最终只有一个 exe。
- 首次启动会比框架依赖版慢一点（压缩 bundle 要解压），之后有缓存。
- PTX kernel 已**嵌入程序集**（`EmbeddedResource`），单文件下 `Assembly.Location` 为空也能读到；
  如果想换 kernel 调试，在 exe 旁边放 `Kernels\pikan.ptx` 会优先用外部的。
- 目标机上 GPU 加速要生效，需要 NVIDIA 驱动（Volta 及以上）；否则自动回落 CPU，功能不受影响。
- 看 HEIC 需要目标机装微软的 **HEIF Image Extension**（微软商店免费）。

## GPU 加速（CUDA 优先）

PIKAN 自带一层 CUDA 加速：**不装 CUDA Toolkit、不引任何 NuGet 包**，
直接 P/Invoke 驱动自带的 `nvcuda.dll`（CUDA Driver API），把手写的 PTX 交给驱动 JIT 编译执行。

```
GpuAccel.cs       CUDA 上下文 + PTX JIT + kernel 调度 + 设备缓冲复用（失败自动降级）
GpuResampler.cs   BitmapSource ↔ CUDA 的桥接，不可用时回落 WIC
Kernels/pikan.ptx kernel 源码（文本，随输出目录复制，驱动运行时 JIT）
```

### kernel

| kernel | 用途 | 说明 |
|---|---|---|
| `resize_mitchell` | 主视图精修 | Mitchell-Netravali，带 area sampling（缩放比越大核越宽，抗走样），每方向最多 16 taps，权重归一化 |
| `resize_bilinear` | 兜底重采样 | 结构最简单，Mitchell 不可用时顶上 |
| `gauss_h` / `gauss_v` | 切图过渡的模糊 | 分离高斯，权重由 host 预计算后上传，半径取 3σ |

### 介入点

1. **主视图精修**：清晰图解码完成后，GPU 把它重采样到「屏幕实际显示像素」尺寸
   （原图尺寸 × FitFactor × zoom × DPI）。输出恰好等于显示像素，WPF 不再二次采样，
   且降采样用的是 Mitchell 而不是 WPF 的插值 —— 缩放/旋转停止 140ms 后重做一次，连续操作只保留最后一次。
   放大不精修（那只会凭空造像素），交给 `RenderTransform`。
2. **切图对焦动画**：过渡层的模糊改用 CUDA 高斯（结果按 0.5 粒度量化缓存，一次动画只在首帧真调 GPU）。
   低清图会被放大到画布显示，所以 σ 按 `FrameW / 低清图宽` 折算，视觉强度才与 `BlurEffect` 对齐。

### 降级与开关

CUDA 不可用时（无 NVIDIA 卡 / 驱动太旧 / PTX 加载失败）全部安静回落：
精修走 WIC 的 `TransformedBitmap`，模糊继续用 WPF 的 `BlurEffect`（它本身也是 GPU shader）。

- 顶栏 `CUDA` / `CPU` 按钮点击切换，**Ctrl+G** 同效，状态栏右端显示当前后端与设备名
- 启动参数 `--nogpu` 强制 CPU 渲染，便于对比
- 诊断日志写在 `%LOCALAPPDATA%\PIKAN\gpu.log`（只记初始化与异常，正常路径几乎不写盘）

PTX 的 `.target` 会在加载失败时按 `sm_86 → sm_75 → sm_70` 依次改写重试，
覆盖 Volta 及之后的绝大多数 NVIDIA 显卡。

## 工程结构

```
PIKAN-Win64/
├── PIKAN.csproj              net10.0-windows + UseWPF + UseWindowsForms，x64
├── App.xaml / .cs            启动 + 命令行参数（PIKAN.exe <图片 或 文件夹> [--nogpu]）
├── MainWindow.xaml           ACDSee 三段式布局（顶栏 / 主体 / 状态栏）
├── MainWindow.xaml.cs        全部 UI 与交互逻辑
├── ImageItem.cs              列表项（路径 / 文件名 / 体积 / 缩略图，INotifyPropertyChanged）
├── Decoder.cs                WIC 解码：Probe 原始尺寸 + 按长边降采样解码 + Freeze
├── ThumbLoader.cs            缩略图 LRU（600 张）+ 限并发（3）+ 限队列（240）
├── PreviewCache.cs           低清预览 LRU（48 张 / 96MB）+ 跟随位置的窗口淘汰
├── StateStore.cs             位置记忆 + 列表快照 → %LOCALAPPDATA%\PIKAN\state.json
├── GpuAccel.cs               CUDA Driver API：上下文 / PTX JIT / kernel 调度 / 缓冲复用
├── GpuResampler.cs           BitmapSource ↔ GPU 桥接 + WIC 回落
├── Kernels/pikan.ptx         CUDA kernel 源码（驱动运行时 JIT）
└── LICENSE / README.md
```

## 与鸿蒙版的移植映射

| 鸿蒙版（ArkTS / ArkUI） | Windows 版（C# / WPF） |
|---|---|
| `@State` 字段 | 普通字段 + `INotifyPropertyChanged` |
| `Image(PixelMap)` | `Image.Source = BitmapSource`（解码后 `Freeze()`，跨线程安全） |
| 外 `Column.rotate/translate` | `Viewport.RenderTransform = MatrixTransform`（**先旋转后平移**，平移量不被旋转带偏） |
| 内 `Image.scale` | `ScaleTransform`（zoom × 镜像方向） |
| `objectFit: Contain` | `Stretch="Uniform"` |
| `LazyForEach` | `ListBox` + `VirtualizingStackPanel`，容器 `Loaded` 触发缩略图请求 |
| `preferences` | `%LOCALAPPDATA%\PIKAN\state.json`（`System.Text.Json`） |
| `PhotoViewPicker`（图库） | 递归扫描「图片」文件夹，按修改时间倒序（Windows 没有图库 ContentProvider） |
| `DocumentViewPicker` | `Microsoft.Win32.OpenFileDialog` |
| `BlurEffect` 由渲染服务合成 | `System.Windows.Media.Effects.BlurEffect` |
| hilog | 状态栏文案 |

### Windows 版新增（鸿蒙版没有的）

- **选择文件夹**：直接指定要浏览的目录（含子目录）
- **拖放**：把图片文件或文件夹拖进窗口即载入
- **命令行 / 打开方式**：`PIKAN.exe <图片 或 文件夹>`
- **双击复位**之外，右键菜单冲突已屏蔽（右键不弹系统菜单）
- **CUDA 加速**：Mitchell 精修 + GPU 高斯模糊，见上文「GPU 加速」章节

## 验证状态

- `dotnet build -c Release`：**0 Warning / 0 Error**
- 冒烟测试（8 张 PNG + BMP）：进程正常、窗口响应、文件夹扫描 → 解码 → 显示 → 位置记忆落盘全部走通
- HEIC 实机解码：**尚未用真实 HEIC 文件验证**（本机没有样本），依赖系统 HEIF 扩展

### CUDA kernel 验证

每个 kernel 都写了独立的 CPU 参考实现做逐字节比对（随机噪声图，容差 1/255）：

| kernel | 用例 | 结果 |
|---|---|---|
| `resize_mitchell` | 8×8→4×4、4×4→8×8、8×8→8×8、16×9→7×5、64×48→16×12 | 全部 **maxDiff = 0** |
| `gauss_h` / `gauss_v` | 12×12、σ=2、r=6 | **maxDiff = 0** |
| 常数图归一化自检 | 1×1→4×4、4×4→8×8、8×8→4×4、7×5→16×9 | 偏差 **0** |

实测（RTX 3080 Laptop / 48 SM / sm_86 / CUDA 13.4，含 H2D + D2H）：

- 重采样 1920×1080 → 960×540：**约 3.7 ms**
- 高斯模糊 1920×1080、r=42：**约 6.6 ms**

### 独立 exe 验证

把 `PIKAN.exe` 单独拷到一个**空目录**（模拟没装 .NET 的机器）后运行 `PIKAN.exe big.png`：

```
init ok   NVIDIA GeForce RTX 3080 Laptop GPU · 15GB · sm_86 · 48 SM · CUDA 13.4
blur ok   #1  768x512 sigma=18.0      ← 切图对焦动画
blur ok   #2  768x512 sigma=13.5
blur ok   #3  768x512 sigma=9.0
refine    目标 1232x821  位图 1971x1314  原图 3000x2000
resize ok #1  1971x1314 -> 1232x821   ← Mitchell 精修
```

即：单文件自解压正常、嵌入的 PTX 读得到、CUDA 初始化 → 模糊 → 精修全链路走通。
