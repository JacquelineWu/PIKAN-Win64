# PIKAN 发行说明模板

这是 `Release.ps1` 用的 Release Notes **模板**，每次发版改这里。

正文发到 GitHub Release 和 Gitee 发行版之前，脚本会把下面的占位符换成真实值：

| 占位符 | 替换成 |
|---|---|
| `{{TAG}}` | tag 名，例如 `v1.0.0` |
| `{{VERSION}}` | 不带 v 的版本号，例如 `1.0.0` |
| `{{DATE}}` | 发布日期 `YYYY-MM-DD` |
| `{{COMMIT}}` | tag 指向的 commit 短哈希 |
| `{{SIZE_MB}}` | exe 大小（MB，一位小数） |
| `{{SIZE_BYTES}}` | exe 大小（字节） |
| `{{SHA256}}` | exe 的 SHA256（小写） |
| `{{CERT_SHA256}}` | 自签公钥证书的 SHA256（没附带证书时留空） |

留着没填的占位符会在发布前被检查出来并警告，不会悄悄发上去。

下面 `---` 之后的部分才是真正发布出去的正文。

---

PIKAN 看图器 **{{TAG}}** —— **单个 exe，{{SIZE_MB}} MB，拷过去双击就用**。

> 发布于 {{DATE}}，对应 commit `{{COMMIT}}`。

## 这一版改了什么

- **完整路径搬到窗口标题栏**：标题栏现在显示 `PIKAN — 当前目录的完整路径`，
  左栏抬头下面那行路径因此撤掉了（那一行的位置留给排序 / 回上层 / 选择文件夹三个按钮）。
  换目录标题跟着变；**收起左栏时也照样看得到自己在哪**。
  非浏览模式（递归图库、手工挑的散图）没有"当前目录"，标题就只有 `PIKAN`。
  路径放不下时**自己**在中间打省略号，末尾那截目录名整段保留——
  交给系统截的话截掉的恰恰是「我现在在哪个文件夹」，最不能丢的部分。
- **收起左栏之后，档案名那一栏也按内容自动调节宽度**：以前收起就是一刀切压到 168，
  名字一长照样挂省略号。现在窄条也在 **168 ~ 360** 之间跟着最长名走——收起态没有大小 / 日期两列，
  装下同一个名字要的宽度小得多，所以那条窄条始终明显窄于展开态，不会「收起反而跟展开一样宽」。
  超过 360 才截断；两种形态各算各的宽度，来回切换互不干扰。
- **档案名列按内容自适应宽度**：左栏总宽自动跟着最长那一行档案名走。换目录时量一遍最长名
  （「..」、子文件夹、图片名都算，并且按**加粗**的字宽量，目录行也放得下），
  把左栏调到刚好装下它——名字长就变宽、名字短就收回去，不再让最长的那行挂省略号，
  也不白占画面。手动拖分隔条照样有效，只是换目录时以内容为准。
- 收窄带 24 像素的滞回：后台补测文件夹容量的那几批数据陆续到达时，左栏不会跟着来回抖。
- 展开态上限 900，并且**始终给预览区留至少 420**；真遇上超长的怪名字就照旧截断，不会把画面挤没。

> 另含 v1.0.1 的改动：收起 / 展开左栏的按钮挪到了左右分隔条的正中间（`Ctrl+B` 同效）。

## 下载

| 文件 | 说明 |
|---|---|
| `PIKAN.exe` | 单文件版，Windows 64 位。不需要装 .NET，不需要装 CUDA Toolkit |
| `PIKAN-CodeSigning.cer` | 自签公钥证书（可选）。导入后本机不再弹 SmartScreen |

拿 `PIKAN.exe` 一个文件就行，放到任意目录双击即可。

## 使用前请先看两条

**1. 签名是我自签的，第一次运行 SmartScreen 可能拦**

exe 已做 Authenticode 签名（SHA256 + DigiCert RFC3161 时间戳），但没有第三方 CA 背书，
SmartScreen 在陌生机器上仍可能提示「Windows 已保护你的电脑」。点「仍要运行」即可。
想在自己机器上彻底消除提示，先导入这张公钥证书：

```bat
certutil -addstore -user Root PIKAN-CodeSigning.cer
certutil -addstore -user TrustedPublisher PIKAN-CodeSigning.cer
```

**2. 看 HEIC / WEBP 需要装微软的解码扩展**（都是商店里的免费组件）

- HEIC → 「HEIF Image Extension」
- WEBP → 「Webp Image Extensions」

JPG / PNG / TIFF / BMP / ICO 等格式走内置 WIC，无需额外安装。

## 这一版能做什么

- **GPU 重采样**：CUDA 核直接在显存里做 Mitchell-Netravali 缩放，结果写进 D3D9 纹理交给 WPF 直显（零拷贝），大幅缩小尺寸时的画质明显好于 WPF 默认插值。没有 NVIDIA 显卡或驱动不匹配时自动回落到 CPU 的 AVX2 版本，功能不受影响。
- **显存缓存**：最近的图常驻显存（默认占总量 50%，`--vram=25` 可调），翻回去几乎瞬时。
- **竖图对开**：连续两张竖图自动左右并排显示，点列表里任意一张都能让它成为左页。翻页步长恒为一格，键盘方向键、鼠标滚轮、幻灯片三种入口统一。
- **ICC 色彩管理**：跟着显示器色彩剖面走，同时识别 EXIF 方向自动旋转。
- **乱码文件名修复**：历史上以 GBK/GB18030 编码、后来被 Latin-1 误解码的中文文件名会在列表里还原显示（磁盘原名不变，不影响打开文件）。可在选项里关掉。
- **即时浏览 / 全屏**：列表双击或双击图片都能进出全屏；按住左键拖拽平移、滚轮缩放。

命令行参数（`PIKAN.exe <图片或文件夹>` 直接打开）：

```
--nogpu       强制走 CPU 渲染，用于对比画质
--novram      关掉显存常驻，每次重算
--vram=50     显存缓存上限（占显存总量的百分比）
--noavx       关掉 CPU 的 AVX2 Mitchell，回落到 WIC 双线性
--bench       连续翻页采集分段耗时，写 perf.log，测完自动退出
```

## 打不开怎么办

绝大多数情况双击就行。如果毫无反应：

1. 把 `%LOCALAPPDATA%\Temp\.net\PIKAN\` 整个删掉，再双击一次
2. 还是不行，在 PowerShell / 终端里跑一次，能看到原本被藏起来的错误：
   ```powershell
   & .\PIKAN.exe 2>&1
   ```
   如果打印 `Failure processing application bundle.`，说明上述临时目录写不进去，查一下安全软件有没有拦
3. 崩溃日志在 `%LOCALAPPDATA%\PIKAN\crash.log`，GPU 相关在 `gpu.log`

> 技术背景：单文件版首次启动会把 5 个 WPF native 组件（7.9 MB）解压到
> `%LOCALAPPDATA%\Temp\.net\PIKAN\<随机串>\`，之后命中缓存。这是单文件形态唯一的失败路径，
> 上面的第 1、2 步就是针对它。仓库里也保留了多文件版的自包含目录形态（`Publish.ps1 -Portable`），
> 它不做任何运行时解压。

## 体积是怎么压到 {{SIZE_MB}} MB 的

- 砍掉 Windows Forms 依赖：项目原本只为「双击间隔 / 屏幕工作区 / 文件夹对话框」三个 API 引用它，代价是自包含发布里多出 ~23 MB 的程序集。现在这三条由 `NativeWin32.cs` 自己调用 Win32 / COM 实现。
- `EnableCompressionInSingleFile`：单文件内建压缩，133 MB → {{SIZE_MB}} MB。

> 没开 trimming，原因见代码注释：WPF 的 XAML 引擎在 DeferredLoad 时反射创建
> `TemplateContentLoader`，trimmer 看不见这次反射会把构造函数裁掉，窗口根本出不来。

## 校验

`PIKAN.exe` {{SIZE_BYTES}} 字节（{{SIZE_MB}} MB），SHA256：

```
{{SHA256}}
```

下载后核一下（`CertUtil` / PowerShell 都行）：

```bat
certutil -hashfile PIKAN.exe SHA256
```

```powershell
(Get-FileHash PIKAN.exe -Algorithm SHA256).Hash
```

## 源码构建

需要 .NET 10 SDK（`net10.0-windows`，含 WindowsDesktop / WPF 工作负载）。无第三方 NuGet 包。

```bat
dotnet build -c Release
pwsh .\Publish.ps1            :: 编译 → 发布成 release-portable\PIKAN.exe → 签名
pwsh .\Publish.ps1 -Portable  :: 出多文件目录形态（排障对照用）
:: 发版分两步：先只推代码与 tag，确认过再建发行版（发出去就撤不回来了）
pwsh .\Release.ps1 -Tag v1.0.3 -SkipRelease          :: ① 编译 → 发布 → 签名 → 推 tag，两站都不建发行版
pwsh .\Release.ps1 -Tag v1.0.3 -SkipPublish -SkipTag :: ② 确认要发之后，只建两站发行版 + 传附件 + 回下载校验
```
