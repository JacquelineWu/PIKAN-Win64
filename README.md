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

**分发成单文件 exe**（目标机不用装 .NET，约 70MB，需联网拉运行时包）：

```bat
dotnet publish -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true
```

## 工程结构

```
PIKAN-Win64/
├── PIKAN.csproj              net10.0-windows + UseWPF + UseWindowsForms，x64
├── App.xaml / .cs            启动 + 命令行参数（PIKAN.exe <图片 或 文件夹>）
├── MainWindow.xaml           ACDSee 三段式布局（顶栏 / 主体 / 状态栏）
├── MainWindow.xaml.cs        全部 UI 与交互逻辑
├── ImageItem.cs              列表项（路径 / 文件名 / 体积 / 缩略图，INotifyPropertyChanged）
├── Decoder.cs                WIC 解码：Probe 原始尺寸 + 按长边降采样解码 + Freeze
├── ThumbLoader.cs            缩略图 LRU（600 张）+ 限并发（3）+ 限队列（240）
├── PreviewCache.cs           低清预览 LRU（48 张 / 96MB）+ 跟随位置的窗口淘汰
├── StateStore.cs             位置记忆 + 列表快照 → %LOCALAPPDATA%\PIKAN\state.json
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

## 验证状态

- `dotnet build -c Release`：**0 Warning / 0 Error**
- 冒烟测试（8 张 PNG + BMP）：进程正常、窗口响应、文件夹扫描 → 解码 → 显示 → 位置记忆落盘全部走通
- HEIC 实机解码：**尚未用真实 HEIC 文件验证**（本机没有样本），依赖系统 HEIF 扩展
