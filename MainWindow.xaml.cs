using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

// WinForms（只为 FolderBrowserDialog 引入）与 WPF 有一批同名类型，这里显式指向 WPF 的那一个
using Point = System.Windows.Point;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace PIKAN;

/// <summary>
/// PIKAN Windows 版 —— 鸿蒙版 PIKAN-HarmonyOS 的 WPF 移植（ACDSee 风格）。
///
/// 布局：顶栏 / 左档案列表 + 右大图预览 / 状态栏，配色与鸿蒙版一致。
/// 交互：滚轮翻页、Ctrl + 滚轮缩放、左右键锚点缩放、拖拽平移、旋转镜像、
///       1:1、幻灯片、键盘、全屏、位置记忆、后台预载 + 切图模糊对焦过渡。
///
/// 移植映射（ArkTS → WPF）：
///   @State 字段            → 普通字段 + INotifyPropertyChanged
///   Image(PixelMap)         → Image.Source = BitmapSource（解码后 Freeze，跨线程安全）
///   外 Column.rotate/translate → Viewport.RenderTransform = MatrixTransform（先旋转后平移）
///   内 Image.scale          → ScaleTransform（zoom × 镜像方向）
///   LazyForEach + 组件重建  → ListBox 虚拟化（VirtualizingStackPanel）+ 容器 Loaded 触发缩略图
///   preferences             → %LOCALAPPDATA%\PIKAN\state.json
///   PhotoViewPicker         → 「图片」文件夹递归扫描（Windows 上没有图库 ContentProvider）
///   DocumentViewPicker      → OpenFileDialog
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    // ---- 视图变换 ----
    private const double MinScale = 0.05;

    // 上限按「相对适应窗口的倍数」算。竖幅高像素图（如 4672×7008）按长边适应时
    // 缩放比很小，1:1 就要 7.84 倍；16 倍只能放到原图的 ~200%，看细节不够。
    // 放到 32 倍可覆盖到 ~400%（此刻 target 已远超位图，精修会跳过，不会多分配内存）。
    private const double MaxScale = 32.0;

    // ---- 鼠标键缩放：左键 ×1.5，右键 ×2.0 ----
    private const double ZoomLeft = 1.5;
    private const double ZoomRight = 2.0;

    /// <summary>按下到抬起若移动超过该距离（px）就判定为拖拽平移，不触发点击缩放。</summary>
    private const double ClickMoveTol = 4;

    // ---- 后台预载窗口：前后各 20 张，解码成「可显示清晰度」缓存起来 ----
    private const int PreloadFwd = 20;
    private const int PreloadBack = 20;

    /// <summary>
    /// 预载命中后，停手多久再补一张全清（画布长边 ×2）。
    /// 连续翻页时这一步会被下一切图作废，所以延迟执行能省掉大量无用解码。
    /// </summary>
    private const int UpgradeDelayMs = 250;

    /// <summary>
    /// 主视图解码的长边下限 / 上限。
    /// 系数取 1.5 而不是 2：画布 985 时全清要解 1971px，一张大图能解到 1.5 秒，
    /// 而屏幕实际只需要约 1196px——多出来的像素既不显示又拖慢切图。
    /// </summary>
    private const double ViewEdgeFactor = 1.5;
    private const int ViewEdgeMin = 1200;
    private const int ViewEdgeMax = 4096;

    // ---- 放大时重新取样的档位 ----
    // ViewEdge 只按「适应窗口」算，放大后就撑不住了：
    // 那张 1536px 的位图被拉到 2700px 屏幕上 = 拉伸 1.8 倍，糊是必然的。
    // 放大到一定程度必须回去按屏幕所需重新解一张（真正的「再次 sample」），
    // 所以这里允许比 ViewEdgeMax 高得多的档位。
    private const int ZoomEdgeMax = 8192;

    // 单张放大用位图的像素预算（BGRA 4 字节）：64MP ≈ 256 MB。
    // 再高就是白白占内存——1:1 看一张 4600×6900 的图要 127 MB，已经在预算内。
    private const long ZoomPixelBudget = 64_000_000;

    // ---- 切图过渡：低清图立刻顶上 → 清晰图就绪后直接替换。
    // 早期版本这里有「大模糊 → 分档对焦 → 淡出」的动画，已按需求移除：
    // 现在切图是瞬时替换，不做任何模糊/缩放/淡入淡出。

    // ---- 滚轮 ----
    // 一个物理刻度 = Delta 120。攒够一格就立刻翻，不做任何定时节流。
    private const int WheelNotch = 120;
    // 停手这么久就清零累积：避免半格的零头被带到下一次滚动里，
    // 让「滚一点、停一下、再滚一点」每次都从干净状态开始。
    private const long WheelAccumResetMs = 400;

    // ---- 其它 ----
    private const int SlideIntervalMs = 3000;
    private const long PosSaveMs = 500;
    private const int ScanCap = 20000;
    private const int SizeChunk = 60;
    private const int SizeYieldMs = 12;

    private readonly ObservableCollection<ImageItem> _items = new();

    /// <summary>左侧实际显示的那些行（ .. / 子文件夹 / 图片 ），见 <see cref="FsEntry"/>。</summary>
    private readonly ObservableCollection<FsEntry> _entries = new();

    private readonly PreviewCache _previews = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    private readonly ScaleTransform _mainScale = new(1, 1);
    private readonly ScaleTransform _lowScale = new(1, 1);

    private readonly DispatcherTimer _slideTimer = new();
    private readonly DispatcherTimer _posTimer = new() { Interval = TimeSpan.FromMilliseconds(PosSaveMs) };

    // ---- 档案 ----
    private string _sourceLabel = "尚未打开";
    private int _cur = -1;

    /// <summary>
    /// 正在浏览的**单级**目录。为 null 表示当前不是浏览模式
    /// （例如「打开图库」递归出来的扁平列表，或手工挑选的一批文件），
    /// 此时左侧没有 .. 也没有子文件夹行。
    /// </summary>
    private string? _browseDir;

    // ---- 列表排序 ----
    private bool _sortDesc;
    private bool _naturalSort = true;

    // ---- 视图变换状态 ----
    private double _zoom = 1;
    private double _tx;
    private double _ty;
    private int _rot;
    private bool _flipH;
    private bool _flipV;

    /// <summary>EXIF 方向带来的基础旋转 / 镜像，叠加在用户手动操作之上。</summary>
    private int _baseRot;
    private bool _baseFlipH;

    /// <summary>
    /// 用户对「当前这一张」做过缩放 / 旋转 / 镜像 / 平移就置 true。
    /// 「放大小图片适应窗口」这类自动策略只在没动过的情况下施加，
    /// 否则向后翻一页就把用户刚摆好的视图位置顶掉了。
    /// </summary>
    private bool _viewTouched;

    // ---- 看图选项（五项开关，见 AppOptions）----
    private AppOptions _opts = new();

    /// <summary>
    /// 「不等图片载入」开关用：当前这张的高清解码何时完成的信号量。
    /// 打开时不 await 它（立即显示已缓存的清晰度），关闭时才等它落定再下一步。
    /// </summary>
    private TaskCompletionSource<bool>? _fullTcs;

    // ---- 图片元信息 ----
    private int _imgW;
    private int _imgH;
    private long _bytes;

    // ---- 交互 ----
    private bool _fullscreen;
    private bool _playing;
    private string _status = "点顶栏「打开图库」扫描图片库，或「选择图片」挑文件";
    private string _resumeHint = "";

    private MouseButton? _pressBtn;
    private double _pressX;
    private double _pressY;
    private bool _pressMoved;
    private double _baseTx;
    private double _baseTy;

    // ---- 滚轮 ----
    private int _wheelAccum;
    private long _wheelAccumMs;
    private bool _wheelCtrl;

    /// <summary>
    /// 解码并发闸门。
    /// 以前靠「滚轮每 120ms 最多翻一张」来防止快速滚动时解码任务堆积，
    /// 但那正是滚轮比键盘慢的原因（实测每次切换要排队 40~125ms）。
    /// 现在翻页不再节流，改由这里限制同时进行的解码数量：排队的任务拿到闸门时
    /// 若发现自己已经过期（token 变了），直接放弃，不做无用功。
    /// </summary>
    private static readonly SemaphoreSlim DecodeGate = new(2);

    // ---- 主视图 / 过渡层 ----
    private long _curToken;
    private DecodedImage? _viewOwned;
    private DecodedImage? _warm;

    // ---- GPU 加速 ----
    private long _refineSeq;
    private long _upgradeSeq;

    // ---- 预载链 ----
    private long _preloadSeq;
    private bool _preloadBusy;
    private long _preloadLoggedAt;

    // ---- 体积补测 ----
    private long _sizeSeq;

    // ---- 位置记忆 ----
    private AppState _state = AppState.Load();
    private bool _snapDirty;
    private long _session;

    // 本次会话的图片来源：退出时要连同"停在第几张"一起落盘，
    // 下次进来才知道该扫哪个目录（见 AutoRestoreAsync）。
    private string _sessionRoot = "";
    private string _sessionKind = "";

    /// <summary>用户自己已经开过图库/文件夹/文件；启动时的自动恢复要让路。</summary>
    private bool _userLoaded;

    private bool _suppressSel;
    private bool _pendingReload;

    private WindowState _prevWindowState = WindowState.Maximized;
    private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _prevResizeMode = ResizeMode.CanResize;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = this;

        // 先探一次 GPU：CUDA 上下文创建 + PTX JIT 有几十毫秒开销，放在启动时做，
        // 避免第一次切图才初始化导致卡顿。失败也只是安静降级。
        //
        // 必须放在后台线程：个别机器上驱动初始化会明显变慢甚至卡住，同步跑在 UI 线程
        // 会把窗口的首次显示一起拖住，表现出来就是「双击后毫无反应」。
        InitGpuAsync();

        MainImage.RenderTransform = _mainScale;
        LowImage.RenderTransform = _lowScale;

        _slideTimer.Interval = TimeSpan.FromMilliseconds(SlideIntervalMs);
        _slideTimer.Tick += (_, _) => Go(1);
        _posTimer.Tick += (_, _) => { _posTimer.Stop(); FlushPos(); };

        Loaded += (_, _) => UpdateTransform();
        Closed += (_, _) => Perf.FlushSync();

        // 帧间隔监控：UI 线程被冻结多久会直接体现成"顿一下"
        Perf.AttachFrameMonitor();

        ReadLastPos();

        // 列表排序沿用上次的偏好
        _sortDesc = _state.SortDesc;
        _naturalSort = _state.NaturalSort;
        OnPropertyChanged(nameof(SortDirLabel));
        OnPropertyChanged(nameof(NaturalBackground));
        OnPropertyChanged(nameof(NaturalForeground));

        // 「选项」里的五项开关同样沿用上次设置（缺字段的旧 state.json 会拿到默认全开）
        _opts = _state.Options ?? new AppOptions();
        _opts.Push();
        ApplyScalingMode();

        // 启动即回到上次退出的目录 + 照片（后台扫目录，不挡窗口显示）
        _ = AutoRestoreAsync();
    }

    // ==================================================================
    // 绑定属性
    // ==================================================================

    public ObservableCollection<ImageItem> Items => _items;

    /// <summary>左侧列表的数据源（ .. / 子文件夹 / 图片混在一起）。</summary>
    public ObservableCollection<FsEntry> Entries => _entries;

    /// <summary>当前目录的完整路径；非浏览模式显示来源说明。</summary>
    public string DirLabel => _browseDir ?? "（非目录浏览）";

    /// <summary>排序方向按钮上的文案。</summary>
    public string SortDirLabel => _sortDesc ? "名称 ↓" : "名称 ↑";

    public string NaturalBackground => _naturalSort ? "#4fd1c5" : "#2a2f3a";

    public string NaturalForeground => _naturalSort ? "#101216" : "#e8ecf4";

    /// <summary>「↑ 上级」按钮：只有在浏览模式且不在盘根时才可用。</summary>
    public bool CanGoUp
    {
        get
        {
            if (_browseDir is null)
            {
                return false;
            }

            DirectoryInfo? parent = Directory.GetParent(_browseDir.TrimEnd(Path.DirectorySeparatorChar));
            return parent is not null;
        }
    }

    public string SourceLabel
    {
        get => _sourceLabel;
        set { _sourceLabel = value; OnPropertyChanged(nameof(SourceLabel)); }
    }

    public string CountLabel => _items.Count > 0 ? _items.Count + " 张" : "";

    public string CurName => _cur >= 0 && _cur < _items.Count ? _items[_cur].Name : "未打开";

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public string ResumeHint
    {
        get => _resumeHint;
        set { _resumeHint = value; OnPropertyChanged(nameof(ResumeHint)); OnPropertyChanged(nameof(ResumeHintVisibility)); }
    }

    public string ZoomText
    {
        get
        {
            double pct = PixelZoom() * 100;

            // 小到 <10% 时给一位小数，否则整百分数会长时间卡在同一个数上
            return (pct < 10 ? pct.ToString("F1", CultureInfo.InvariantCulture)
                             : Math.Round(pct).ToString(CultureInfo.InvariantCulture)) + " %";
        }
    }

    public string InfoLine
    {
        get
        {
            string ext = _cur >= 0 && _cur < _items.Count ? _items[_cur].Ext : "";
            string size = ImageItem.FormatBytes(_bytes);
            string dim = _imgW > 0 ? $" · {_imgW}×{_imgH}" : "";
            return ext + " · " + size + dim;
        }
    }

    // ==================================================================
    // GPU 加速
    // ==================================================================

    public string GpuLine => "x64 · Windows · " + GpuResampler.StatusTextReady;

    /// <summary>状态栏：已预载并缓存的图片张数与占用（含显存常驻部分）。</summary>
    public string CacheLabel =>
        _previews.Count > 0
            ? $"预载 {_previews.Count} 张 · {ImageItem.FormatBytes(_previews.Bytes)}{VramPart()}"
            : "预载中";

    private static string VramPart()
    {
        long bytes = GpuImageCache.Instance.Bytes;
        int n = GpuImageCache.Instance.Count;
        if (n <= 0)
        {
            return "";
        }

        // 配额随空闲显存浮动，一并显示出来，便于判断缓存到底用起来没有
        long quota = GpuImageCache.Instance.QuotaMb;
        string cap = quota > 0 ? $" / {quota / 1024.0:F1} GB" : "";
        return $" · 显存 {n} 张 {ImageItem.FormatBytes(bytes)}{cap}";
    }

    /// <summary>
    /// 预载张数变化后刷新状态栏（预载链在后台线程，故走 Dispatcher）。
    /// 只在文本真的变了才通知：状态栏文本变化会触发布局，布局又可能带动画布尺寸变化，
    /// 不做这层去抖会和 SizeChanged 形成互相刺激的死循环。
    /// </summary>
    private void NotifyCache()
    {
        string text = CacheLabel;
        if (text == _cacheLabelShown)
        {
            return;
        }

        _cacheLabelShown = text;
        OnPropertyChanged(nameof(CacheLabel));
    }

    private string _cacheLabelShown = "";

    // GPU 的开关从顶栏挪走后，只剩状态栏这行小字还挂着 —— Ctrl+G 仍可切换（排障用）
    public string GpuTip => GpuResampler.GpuReady
        ? $"GPU 加速已开启\n{GpuAccel.DeviceNameOrEmpty}\nCtrl+G 切到 CPU 渲染"
        : $"当前为 CPU 渲染{(GpuAccel.IsReady ? "（已手动关掉 GPU）" : "")}\nCtrl+G 开启 CUDA 加速";

    public string SlideLabel => _playing ? "暂停" : "幻灯片";
    public string SlideBackground => _playing ? "#f6c85f" : "#2a2f3a";
    public string SlideForeground => _playing ? "#101216" : "#e8ecf4";

    public Visibility ChromeVisibility => _fullscreen ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FullscreenVisibility => _fullscreen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => _cur >= 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasImageView => _cur >= 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeHintVisibility => _resumeHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>图片与缩放相关的显示项统一刷新。</summary>
    private void RefreshImageInfo()
    {
        OnPropertyChanged(nameof(CurName));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(InfoLine));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(HasImageView));
    }

    // ==================================================================
    // 上次浏览位置
    // ==================================================================

    private void ReadLastPos()
    {
        AppState p = _state;
        if (p.LastIdx < 0 && p.LastUri.Length == 0)
        {
            return; // 首次启动，没有记忆
        }

        ResumeHint = $"上次看到「{p.LastName}」（第 {p.LastIdx + 1} 张 · {p.LastSrc}）";

        // 预热「上次成功显示的那张」：按预载档位后台解 1 张，用户打开图库时首屏直接命中缓存
        if (p.LastOkUri.Length > 0 && File.Exists(p.LastOkUri))
        {
            string uri = p.LastOkUri;
            int warmEdge = _previews.Edge;
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                DecodedImage? img = Decoder.Decode(uri, warmEdge);
                if (img is null)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() => { _warm = img; }, DispatcherPriority.Background);
            });
        }

        Status = $"正在恢复上次的位置：{p.LastName}（第 {p.LastIdx + 1} 张 · {p.LastSrc}）";
    }

    // ==================================================================
    // 启动自动恢复：目录 + 照片一起回到上次退出时的样子
    // ==================================================================

    /// <summary>
    /// 记下本次会话的图片来源。<paramref name="kind"/> 见 <see cref="AppState.LastKind"/>。
    /// </summary>
    private void MarkSource(string root, string kind)
    {
        _sessionRoot = root;
        _sessionKind = kind;
        MarkPos(); // 防抖由 MarkPos 统一负责，退出时 Window_Closing 还会再兜一次
    }

    private static string FolderLabel(string root)
    {
        string name = Path.GetFileName(root);
        return name.Length > 0 ? name : root;
    }

    /// <summary>
    /// 重新扫一次上次退出的目录，并停在上次那张照片上。
    ///
    /// 为什么要重扫而不是直接用列表快照：
    ///   · 目录里新增 / 删除的照片会被自然反映出来（快照是死的）；
    ///   · 快照最多存 <see cref="AppState.SnapshotMax"/> 条，大图库必然不全；
    ///   · 按 <see cref="AppState.LastUri"/> 路径命中优先，行数变了也不指错图。
    /// 快照退化为兜底：目录被挪走 / 删掉，或者上次来源是手工挑的散文件。
    /// </summary>
    private async Task AutoRestoreAsync()
    {
        try
        {
            AppState p = _state;
            if (p.LastIdx < 0 && p.LastUri.Length == 0)
            {
                return; // 首次启动，没有记忆，保持空态
            }

            await Task.Delay(250); // 先让窗口把空壳画出来，扫描不抢首帧

            bool restored = false;
            string root = p.LastRoot;

            // 「选择的文件」是用户挑的一批散图，目录里有别的图，不能按目录重扫
            if (p.LastKind != "files" && root.Length > 0 && Directory.Exists(root))
            {
                Status = $"正在恢复上次的目录「{FolderLabel(root)}」…";

                // 上次是「打开图库」（递归摊平）就按递归恢复，是浏览模式就恢复单级 + 子文件夹
                bool recursive = p.LastKind == "gallery";
                _browseDir = recursive ? null : root;
                List<ImageItem>? found = await Task.Run(() => ScanFolder(root, recursive));

                // 扫描这几百毫秒里用户可能已经自己开了别的图库，那就别覆盖
                if (_userLoaded)
                {
                    return;
                }

                if (found is { Count: > 0 })
                {
                    MarkSource(root, p.LastKind);
                    int hit = RestoreIndex(found);
                    ApplyItems(found, FolderLabel(root));

                    Status = hit >= 0
                        ? $"已回到上次的位置：{found[hit].Name}（第 {hit + 1} / {found.Count} 张 · {FolderLabel(root)}）"
                        : $"已恢复上次的目录（{found.Count} 张 · {FolderLabel(root)}）；上次那张照片已不在，从第 1 张开始";
                    restored = true;
                }
                else
                {
                    // 目录还在但图没了：仍然把 .. 与子文件夹列出来，用户可以自己往上走
                    BuildEntries();
                    OnPropertyChanged(nameof(DirLabel));
                    OnPropertyChanged(nameof(CanGoUp));
                    restored = true;
                }
            }

            if (!restored && !_userLoaded)
            {
                RestoreFromSnapshot();
            }
        }
        catch
        {
            // 恢复失败不影响正常使用：空态里照样能点「打开图库」
        }
    }

    /// <summary>
    /// 用上次会话的列表快照恢复。目录不在了、或上次来源是「选择的文件」时走这条路。
    /// 返回是否真的恢复了列表。
    /// </summary>
    private bool RestoreFromSnapshot()
    {
        List<ImageItem> list = _state.Snapshot.Where(File.Exists).Select(p => new ImageItem(p)).ToList();
        if (list.Count == 0)
        {
            Status = "上次那批图片找不到了（目录或文件已移动），请打开图库或选择文件夹";
            return false;
        }

        _browseDir = null; // 这批是快照里的文件，不是「正在浏览某个目录」
        ApplyItems(list, _state.LastSrc.Length > 0 ? _state.LastSrc : "上次会话");
        return true;
    }

    /// <summary>算出「应该从第几张开始」。返回 -1 表示没有可恢复的位置。</summary>
    private int RestoreIndex(IReadOnlyList<ImageItem> list)
    {
        AppState p = _state;
        if (list.Count == 0 || (p.LastIdx < 0 && p.LastUri.Length == 0))
        {
            return -1;
        }

        // ① 最可靠：路径命中就是同一张图
        if (p.LastUri.Length > 0)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Path, p.LastUri, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        // ② 退一步：总数一致说明多半还是同一批（顺序未变），按序号回退
        if (p.LastCount == list.Count && p.LastIdx >= 0 && p.LastIdx < list.Count)
        {
            return p.LastIdx;
        }

        return -1;
    }

    private void MarkPos()
    {
        if (_cur < 0 || _cur >= _items.Count)
        {
            return;
        }

        ImageItem it = _items[_cur];
        _state.LastIdx = _cur;
        _state.LastUri = it.Path;
        _state.LastName = it.Name;
        _state.LastCount = _items.Count;
        _state.LastSrc = _sourceLabel;

        // 目录也一起记：只记"第几张"在图片增删后会指错图，
        // 记住目录就可以下次重新扫一遍再按路径对位。
        if (_sessionRoot.Length > 0)
        {
            _state.LastRoot = _sessionRoot;
            _state.LastKind = _sessionKind;
        }

        _posTimer.Stop();
        _posTimer.Start(); // 防抖：滚轮连翻时没必要每张都落盘
    }

    private void FlushPos()
    {
        try
        {
            // 列表为空时绝不覆盖快照：否则一次「没加载出图」的会话会把上次的图库抹掉，
            // 下次打开就永远是空列表（之前踩过：强杀进程后重启，图全没了）
            if (_snapDirty && _items.Count > 0)
            {
                _snapDirty = false;
                _state.Snapshot = _items.Take(AppState.SnapshotMax).Select(i => i.Path).ToList();
            }

            _state.Save();
        }
        catch
        {
            // 写盘失败无所谓
        }
    }

    private void ResumeLast()
    {
        if (_state.LastIdx < 0 && _state.LastUri.Length == 0)
        {
            Status = "还没有记录过浏览位置";
            return;
        }

        if (_items.Count > 0)
        {
            int i = RestoreIndex(_items);
            if (i >= 0)
            {
                Select(i);
                Status = $"已回到上次位置：第 {i + 1} / {_items.Count} 张 · {_items[i].Name}";
                return;
            }

            Status = $"当前这批图片里没有上次的「{_state.LastName}」（上次来自{_state.LastSrc}）";
            return;
        }

        if (_state.Snapshot.Count > 0)
        {
            Status = "正在用上次会话的列表恢复…";
            if (RestoreFromSnapshot())
            {
                return;
            }
        }

        Status = "上次那批图片找不到了，请重新选择";
    }

    // ==================================================================
    // 数据
    // ==================================================================

    private void LoadMeta(int idx)
    {
        _imgW = 0;
        _imgH = 0;
        if (idx < 0 || idx >= _items.Count)
        {
            _bytes = 0;
            return;
        }

        ImageItem it = _items[idx];
        _bytes = it.Size > 0 ? it.Size : TrySize(it.Path);
    }

    private static long TrySize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>统一入口：提交档案列表、刷新计数、重建预览与预载。</summary>
    private void ApplyItems(List<ImageItem> list, string src, string capped = "")
    {
        _session++;
        SortImages(list); // 先排好顺序，_items 与左侧列表的行序才一致
        _items.Clear();
        foreach (ImageItem it in list)
        {
            _items.Add(it);
        }

        SourceLabel = src;
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(DirLabel));
        OnPropertyChanged(nameof(CanGoUp));

        // 先算出落点再解码：否则会先按第 1 张解一遍、恢复时又解一遍，白白多一次大图解码
        int start = RestoreIndex(list);
        _cur = list.Count > 0 ? (start >= 0 ? start : 0) : -1;
        ResetView();

        BuildEntries();

        if (_cur >= 0)
        {
            LoadMeta(_cur);
        }

        _preloadSeq++;
        ClearWheel();
        ClearBlur();
        _previews.Clear();
        _failed.Clear();
        GpuImageCache.Instance.Clear();   // 换图库：显存里那批一起释放
        GpuResampler.ClearResults();

        // 接上启动预热的那张：此刻列表已有、cur 已定，交给缓存后 showPreviewOf 会直接命中
        if (_warm is not null)
        {
            _previews.Add(_warm, MainImage.Source, LowImage.Source);
            _warm = null;
        }

        UpdateSelection();
        ShowPreviewOf(_cur);
        SchedulePreload();
        FillSizes();

        _snapDirty = true;
        MarkPos();

        string where = start >= 0 ? $" · 已回到上次位置（第 {start + 1} 张）" : "";
        if (start >= 0)
        {
            ResumeHint = "";
        }

        Status = $"已载入 {list.Count} 张（{src}）{capped}{where}";
    }

    /// <summary>
    /// 后台分批补测体积。
    /// 不能在主线程里对几百个文件逐个 stat —— 那是同步系统调用，会把 UI 卡死。
    /// 切成小批、批间让出主线程，用户会看到体积陆续从 '--' 变成实际值。
    /// </summary>
    private void FillSizes()
    {
        _sizeSeq++;
        long seq = _sizeSeq;
        List<ImageItem> snapshot = _items.ToList();

        _ = Task.Run(async () =>
        {
            int i = 0;
            while (seq == _sizeSeq && i < snapshot.Count)
            {
                int end = Math.Min(i + SizeChunk, snapshot.Count);
                List<(ImageItem Item, long Size)> updates = new();
                for (int k = i; k < end; k++)
                {
                    ImageItem it = snapshot[k];
                    if (it.Size < 0)
                    {
                        long s = TrySize(it.Path);
                        if (s > 0)
                        {
                            updates.Add((it, s));
                        }
                    }
                }

                i = end;
                if (updates.Count > 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (seq != _sizeSeq)
                        {
                            return;
                        }

                        // ImageItem 与左侧 FsEntry 是两份对象，补测到的体积要同步过去，
                        // 否则列表行会一直停在 '--'
                        foreach ((ImageItem item, long size) in updates)
                        {
                            item.Size = size;

                            int row = EntryIndexOfImage(snapshot.IndexOf(item));
                            if (row >= 0)
                            {
                                _entries[row].Size = size;
                            }
                        }
                    }, DispatcherPriority.Background);
                }

                await Task.Delay(SizeYieldMs);
            }
        });
    }

    // ==================================================================
    // 视图操作
    // ==================================================================

    /// <summary>可用框：旋转 90°/270° 时宽高互换，保证旋转后仍完整落在画布内。</summary>
    /// <summary>EXIF 方向 + 用户手动旋转的总和（EXIF 那部分每换一张图重新算）。</summary>
    private int TotalRot => (_baseRot + _rot) % 360;

    /// <summary>EXIF 镜像与用户手动镜像叠加（都是取反，异或即可）。</summary>
    private bool TotalFlipH => _baseFlipH != _flipH;

    /// <summary>转 90° / 270° 之后可用画布的宽高要互换。</summary>
    private double FrameW => TotalRot % 180 != 0 ? CanvasArea.ActualHeight : CanvasArea.ActualWidth;

    private double FrameH => TotalRot % 180 != 0 ? CanvasArea.ActualWidth : CanvasArea.ActualHeight;

    /// <summary>一个 WPF 逻辑像素（DIP）对应几个物理屏幕像素。</summary>
    private double DpiFactor => Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);

    /// <summary>Stretch=Uniform 时图片相对可用框的缩放比。</summary>
    private double FitFactor()
    {
        if (_imgW <= 0 || _imgH <= 0)
        {
            return 0;
        }

        return Math.Min(FrameW / _imgW, FrameH / _imgH);
    }

    /// <summary>
    /// **相对原图像素的**显示倍率：1 表示「一个图片像素 = 一个屏幕物理像素」（即 1:1 / 100%）。
    ///
    /// 注意 <c>_zoom</c> 是相对「适应窗口」的倍数（<see cref="ResetView"/> 把它设为 1），
    /// 只在内部用；界面上给用户看的百分比必须是这个——适应窗口通常是 20% 上下而非 100%，
    /// 这也才符合 ACDSee / Photoshop 的习惯。
    ///
    /// 推导：图片按 Uniform 适应后宽 = <c>_imgW × f</c> 个 DIP，再乘 _zoom，
    /// 再乘 DPI 得到物理像素数，除以原图宽即为倍率（与高矮无关，故取长边同解）。
    /// </summary>
    private double PixelZoom()
    {
        double f = FitFactor();
        return f <= 0 ? _zoom : f * _zoom * DpiFactor;
    }

    /// <summary>
    /// 把当前变换状态推给渲染层。
    /// 顺序：先绕画布中心旋转，再平移（平移量不被旋转带偏，拖动手感才对）。
    /// Matrix 的方法都是「右乘追加」，故先 Translate 再 RotateAt 得到 M = T·R，
    /// 即点先被旋转、再被平移。
    /// </summary>
    private void UpdateTransform()
    {
        double cw = CanvasArea.ActualWidth;
        double ch = CanvasArea.ActualHeight;

        Frame.Width = FrameW;
        Frame.Height = FrameH;

        _mainScale.ScaleX = _zoom * (TotalFlipH ? -1 : 1);
        _mainScale.ScaleY = _zoom * (_flipV ? -1 : 1);

        // 低清占位层与主层用同样的变换（早期版本这里有过对焦呼吸缩放，已移除）
        _lowScale.ScaleX = _mainScale.ScaleX;
        _lowScale.ScaleY = _mainScale.ScaleY;

        Matrix m = new();
        m.Translate(_tx, _ty);
        m.RotateAt(TotalRot, cw / 2, ch / 2);
        Viewport.RenderTransform = new MatrixTransform(m);
    }

    private void ResetView()
    {
        _zoom = 1;
        _tx = 0;
        _ty = 0;
        _rot = 0;
        _flipH = false;
        _flipV = false;
        _viewTouched = false; // 换图 = 视图归位，「适应 / 小图不放大」这类策略重新生效
        UpdateTransform();
        RefreshImageInfo();
    }

    /// <summary>
    /// 按当前规则（方向 + 是否自然序）给一堆名称排序。
    /// 目录与图片分开排、然后目录在前——这是资源管理器的惯例，
    /// 混在一起会让「往下走一层的入口」散落在几百张图片中间。
    /// </summary>
    private void SortEntries(List<FsEntry> list)
    {
        Comparison<string> cmp = _naturalSort
            ? NaturalSort.Compare
            : string.CompareOrdinal;

        int Compare(FsEntry a, FsEntry b)
        {
            // .. 永远钉在最上面，不参与排序
            int upRank = (a.IsUp ? 0 : 1).CompareTo(b.IsUp ? 0 : 1);
            if (upRank != 0)
            {
                return upRank;
            }

            // 其次：文件夹排在图片前
            int dirRank = (a.IsFolder ? 0 : 1).CompareTo(b.IsFolder ? 0 : 1);
            if (dirRank != 0)
            {
                return dirRank;
            }

            int r = cmp(a.Name, b.Name);
            return _sortDesc ? -r : r;
        }

        list.Sort(Compare);
    }

    /// <summary>给「可翻阅的图片序列」排序（就是左侧图片行的那部分，不含目录）。</summary>
    private void SortImages(List<ImageItem> list)
    {
        Comparison<string> cmp = _naturalSort
            ? NaturalSort.Compare
            : string.CompareOrdinal;

        list.Sort((a, b) =>
        {
            int r = cmp(a.Name, b.Name);
            return _sortDesc ? -r : r;
        });
    }

    /// <summary>
    /// 依据当前的 <c>_browseDir</c> 与 <c>_items</c> 重建左侧那几行。
    /// <c>_items</c> 与 <c>_entries</c> 里的图片部分一一对应，靠 <see cref="FsEntry.ItemIndex"/>
    /// 挂钩，这样翻页 / 预载 / 位置记忆这整套逻辑完全不用动。
    /// </summary>
    private void BuildEntries()
    {
        _entries.Clear();

        if (_browseDir is not null)
        {
            _entries.Add(new FsEntry(_browseDir, FsEntry.EntryKind.Up, ".."));

            foreach (FsEntry d in ListSubdirs(_browseDir))
            {
                _entries.Add(d);
            }
        }

        for (int i = 0; i < _items.Count; i++)
        {
            ImageItem it = _items[i];
            _entries.Add(new FsEntry(it.Path, FsEntry.EntryKind.Image)
            {
                ItemIndex = i,
                Size = it.Size
            });
        }

        List<FsEntry> rows = new(_entries);
        SortEntries(rows);
        _entries.Clear();
        foreach (FsEntry row in rows)
        {
            _entries.Add(row);
        }

        OnPropertyChanged(nameof(CountLabel));
    }

    /// <summary>列出目录下的直接子文件夹（只一层，不递归）。</summary>
    private static List<FsEntry> ListSubdirs(string dir)
    {
        List<FsEntry> list = new();
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                FileInfo fi = new(sub);
                // 跳过系统 / 隐藏目录（System Volume Information、$RECYCLE.BIN 之类），
                // 它们在资源管理器里也默认不显示，列出来只会干扰
                if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    continue;
                }

                list.Add(new FsEntry(sub, FsEntry.EntryKind.Folder));
            }
        }
        catch
        {
            // 没有权限就当作没有子目录
        }

        return list;
    }

    /// <summary>把 <c>_items</c> 重排一遍，保持当前这张仍处于选中。</summary>
    private void Resort()
    {
        if (_items.Count == 0)
        {
            return;
        }

        ImageItem? keep = _cur >= 0 && _cur < _items.Count ? _items[_cur] : null;

        List<ImageItem> list = _items.ToList();
        SortImages(list);

        _items.Clear();
        foreach (ImageItem it in list)
        {
            _items.Add(it);
        }

        BuildEntries();

        _cur = keep is not null ? Math.Max(0, list.IndexOf(keep)) : 0;
        UpdateSelection();
        MarkPos();
        SchedulePreload();
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    private void ZoomBy(double factor)
    {
        _viewTouched = true;
        _zoom = Clamp(_zoom * factor, MinScale, MaxScale);
        UpdateTransform();
        RefreshImageInfo();
        ScheduleRefine();
        Status = $"缩放 {ZoomText}";
    }

    /// <summary>
    /// 以某个点为锚缩放。
    /// 设 d 为光标相对画面中心的偏移、t 为平移量、z 为缩放，光标下的内容点满足 d = t + z·p。
    /// 要让它缩放后仍在原位：d = t' + z'·p = t' + (z'/z)(d − t) ⇒ t' = d − (z'/z)(d − t)。
    /// </summary>
    private void ZoomAt(double factor, double ax, double ay)
    {
        double old = _zoom;
        double next = Clamp(old * factor, MinScale, MaxScale);
        if (Math.Abs(next - old) < 1e-9)
        {
            Status = $"已达缩放极限 {ZoomText}";
            return;
        }

        double k = next / old;
        if (_rot == 0 && ax >= 0 && ay >= 0)
        {
            double dx = ax - CanvasArea.ActualWidth / 2;
            double dy = ay - CanvasArea.ActualHeight / 2;
            _tx = dx - k * (dx - _tx);
            _ty = dy - k * (dy - _ty);
        }
        else
        {
            // 旋转态下懒得做坐标旋转，直接以中心缩放并复位平移，行为最可预期
            _tx = 0;
            _ty = 0;
        }

        _zoom = next;
        _viewTouched = true;
        UpdateTransform();
        RefreshImageInfo();
        ScheduleRefine();
        Status = $"缩放 {ZoomText}";
    }

    /// <summary>1:1 —— 一个图片像素对应一个屏幕像素。</summary>
    private void ZoomPixelPerfect()
    {
        _tx = 0;
        _ty = 0;
        double f = FitFactor();
        if (f <= 0)
        {
            _zoom = 1;
            UpdateTransform();
            Status = "尺寸未就绪，先按适应窗口显示";
            return;
        }

        // 乘 DPI 是关键：_zoom 作用在 DIP 上，而 1:1 指「一个图片像素 = 一个物理像素」。
        // 漏掉这一项时，125% 缩放下按 F 得到的其实是 125%（虚胖），且屏幕需求算出
        // 比原图更多的像素，永远解码不到、只能靠 WPF 拉。
        _zoom = Clamp(1 / (f * DpiFactor), MinScale, MaxScale);
        _viewTouched = true;
        UpdateTransform();
        RefreshImageInfo();
        ScheduleRefine();
        Status = $"1:1 原始像素（{_imgW} × {_imgH}）· {ZoomText}";
    }

    private void RotateBy(int delta)
    {
        _rot = (_rot + delta + 360) % 360;
        _viewTouched = true;
        UpdateTransform();
        ScheduleRefine();
        Status = $"旋转 {_rot}°";
    }

    private void ToggleFlipH()
    {
        _flipH = !_flipH;
        _viewTouched = true;
        UpdateTransform();
        Status = _flipH ? "已水平镜像" : "已取消水平镜像";
    }

    private void ToggleFlipV()
    {
        _flipV = !_flipV;
        _viewTouched = true;
        UpdateTransform();
        Status = _flipV ? "已垂直镜像" : "已取消垂直镜像";
    }

    private void Select(int idx)
    {
        if (idx < 0 || idx >= _items.Count)
        {
            return;
        }

        _cur = idx;
        ResetView();
        LoadMeta(idx);
        ShowPreviewOf(idx);
        UpdateSelection();
        Status = $"第 {idx + 1} / {_items.Count} 张";
        SchedulePreload();
        MarkPos();
    }

    private void Go(int delta)
    {
        if (_items.Count == 0)
        {
            return;
        }

        // 「翻页不等载入」打开（默认）：立刻走。
        // 关掉：先等上一张的高清解码落地再迈步，连按时会明显感觉到「一档一顿」——
        // 这就是这一项要关掉的体验。
        if (_opts.InstantNavigation)
        {
            StepTo(delta);
            return;
        }

        _ = StepAfterLoadAsync(delta);
    }

    /// <summary>等当前这张完全载入之后再翻一格。</summary>
    private async Task StepAfterLoadAsync(int delta)
    {
        Task? wait = Volatile.Read(ref _fullTcs)?.Task;
        if (wait is not null)
        {
            await wait.ConfigureAwait(false);
        }

        await Dispatcher.InvokeAsync(() => StepTo(delta));
    }

    private void StepTo(int delta)
    {
        int n = _items.Count;

        Perf.Start($"Go {delta}  → 第 {((_cur + delta) % n + n) % n + 1} 张");
        _cur = ((_cur + delta) % n + n) % n;
        ResetView();
        Perf.Tick("ResetView");
        LoadMeta(_cur);
        Perf.Tick("LoadMeta");
        ShowPreviewOf(_cur);
        Perf.Tick("ShowPreviewOf");
        UpdateSelection();
        Perf.Tick("UpdateSelection（含 ScrollIntoView）");
        Status = $"第 {_cur + 1} / {n} 张";
        SchedulePreload();
        Perf.Tick("SchedulePreload");
        MarkPos();
        Perf.Tick("MarkPos");
    }

    private void UpdateSelection()
    {
        _suppressSel = true;
        if (_cur >= 0 && _cur < _items.Count)
        {
            // 列表现在是 _entries，_cur 是 _items 的下标，不能直接当 SelectedIndex 用
            int row = EntryIndexOfImage(_cur);
            if (row >= 0)
            {
                FileList.SelectedIndex = row;
                FileList.ScrollIntoView(_entries[row]);
            }
            else
            {
                FileList.SelectedIndex = -1;
            }
        }
        else
        {
            FileList.SelectedIndex = -1;
        }

        _suppressSel = false;
    }

    /// <summary>图片在 _items 里的下标 → 它在左侧 _entries 里的行号。</summary>
    private int EntryIndexOfImage(int itemIndex)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].ItemIndex == itemIndex)
            {
                return i;
            }
        }

        return -1;
    }

    // ==================================================================
    // 主视图与过渡层
    // ==================================================================

    /// <summary>主视图解码的长边：画布长边 × ViewEdgeFactor，并夹在 [ViewEdgeMin, 4096] 内控制内存。</summary>
    private int ViewEdge()
    {
        double baseEdge = Math.Max(CanvasArea.ActualWidth, CanvasArea.ActualHeight);
        double dpi = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
        return (int)Math.Max(ViewEdgeMin, Math.Min(ViewEdgeMax, Math.Round(baseEdge * dpi * ViewEdgeFactor)));
    }

    /// <summary>
    /// 屏幕此刻真正要让 ImageView 撑多少像素（长边），含 **当前 zoom** 与 DPI，留 5% 余量。
    ///
    /// 关键是与 <see cref="ViewEdge"/> 的区别：ViewEdge 只代表「适应窗口」时的需求，
    /// 而这是在 estimating **放大之后** 的需求——放大画面靠的就是这个数。
    /// Stretch=Uniform 下适应时长边刚好等于可用框长边，所以 Needs = 框长边 × zoom。
    /// </summary>
    private int NeededEdge()
    {
        double baseEdge = Math.Max(FrameW, FrameH);
        return (int)Math.Ceiling(baseEdge * DpiFactor * _zoom * 1.05);
    }

    /// <summary>
    /// 按需求 <paramref name="need"/> 算出该让解码器给到多少档位：
    /// 夹在 [ViewEdgeMin, ZoomEdgeMax]、不超过原图本身像素（解更大毫无意义），
    /// 并按像素预算收一收，避免超大图被拖进几百 MB。
    /// </summary>
    private static int DecodeEdgeFor(int need, int nativeW, int nativeH)
    {
        int native = Math.Max(nativeW, nativeH);
        if (native <= 0)
        {
            return need;
        }

        int edge = Math.Clamp(need, ViewEdgeMin, ZoomEdgeMax);
        edge = Math.Min(edge, native);

        double k = edge / (double)native;
        long px = (long)Math.Round(nativeW * k) * (long)Math.Round(nativeH * k);
        if (px > ZoomPixelBudget)
        {
            double cut = Math.Sqrt(ZoomPixelBudget / (double)px);
            edge = Math.Max(ViewEdgeMin, (int)Math.Floor(edge * cut));
        }

        return edge;
    }

    /// <summary>
    /// 切到第 idx 张：
    ///   ① 代号 +1，作废一切在途的异步回调；
    ///   ② 命中预载缓存 → 那张图已经是「可显示清晰度」，直接上主视图，不等解码；
    ///   ③ 未命中 → 先解一张 768 低清占位（避免空白），再解高清覆盖；
    ///   ④ 缓存档位不够清晰时，停手 UpgradeDelayMs 之后再补一张全清的（渐进式升级）。
    /// </summary>
    private void ShowPreviewOf(int idx)
    {
        _curToken++;
        long token = _curToken;

        _viewOwned = null;
        MainImage.Source = null;
        _imgW = 0;
        _imgH = 0;
        RefreshImageInfo();

        BeginFullWait(); // 「翻页不等载入」关闭时靠它串行化

        if (idx < 0 || idx >= _items.Count)
        {
            ClearBlur();
            EndFullWait();
            return;
        }

        string path = _items[idx].Path;
        DecodedImage? hit = _previews.Get(path);
        Perf.Tick("查缓存");

        if (hit is not null)
        {
            ShowDecoded(hit); // 预载命中：立刻出图，这是「翻页不等解码」的关键

            // ShowNeed 依赖 _imgW/_imgH（FitFactor 要用），所以必须等上面这行落定再算
            int need = ShowNeed(hit);
            Perf.Note($"  缓存命中 edge={hit.Edge}（显示需 {need}）");

            // 还要不要再解一张全清？只看「屏幕实际要显示多少像素」。
            // 早先拿缓存档位去和 ViewEdge 比，而 ViewEdge 带 1.5 倍缩放余量，
            // 于是本已够用的缓存图一律被判成"不够"，每张都要再解一次全清——
            // 大图那一次能解到 1.5 秒，切图卡顿就是从这儿来的。
            if (need <= 0 || hit.Edge < need)
            {
                _ = LoadViewAsync(path, token, UpgradeDelayMs); // 停下来再升到全清
            }
            else
            {
                ScheduleRefine();
            }
        }
        else
        {
            Perf.Note($"  缓存未命中 → 低清占位 + 全清解码并行（全清需 {ViewEdge()}）");
            ClearBlur();
            _ = LoadQuickAsync(path, token);   // 低清占位
            _ = LoadViewAsync(path, token, 0); // 尽快拿到全清
        }
    }

    /// <summary>
    /// 这张图按当前 zoom / DPI 实际要占多少像素（长边），再留 5% 余量。
    /// 用来判断"缓存里这张够不够看"，够就不必再解全清。
    /// </summary>
    /// <summary>开始等「当前这张的高清解码」。</summary>
    private void BeginFullWait()
    {
        Interlocked.Exchange(ref _fullTcs,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>高清解码到位（或已无望）时放开等待者。</summary>
    private void EndFullWait()
    {
        Interlocked.Exchange(ref _fullTcs, null)?.TrySetResult(true);
    }

    private int ShowNeed(DecodedImage img)
    {
        (int w, int h) = TargetPixelSize(img);
        int need = Math.Max(w, h);
        return need <= 0 ? 0 : (int)Math.Ceiling(need * 1.05);
    }

    /// <summary>把一张已解码的图放上主视图（预载命中 / 高清到达都走这里）。</summary>
    private void ShowDecoded(DecodedImage img)
    {
        NoteShown(img.Path);
        Perf.Tick("NoteShown");
        _viewOwned = img;
        MainImage.Source = img.Source;
        Perf.Tick("MainImage.Source 赋值（位图上传 + 布局失效）");

        ApplyExifOrientation(img);   // 必须先于 _imgW/_imgH 与 ApplyFitPolicy

        if (!_viewTouched)
        {
            ApplyFitPolicy();        // 这张还没被手动动过 → 自动策略说了算
        }

        UpdateTransform();
        Perf.Tick("UpdateTransform");
        RefreshImageInfo();
        Perf.Tick("RefreshImageInfo");
    }

    /// <summary>
    /// 按 EXIF 方向标正画面（关掉该选项或与正方向的图都等于什么也不做）。
    /// 注意喂出去的 _imgW/_imgH 是**转正之后**的宽高：FitFactor 与信息栏都按这个算，
    /// 否则竖拍的照片会被当成横向、适应窗口时溢出画布。
    /// </summary>
    private void ApplyExifOrientation(DecodedImage img)
    {
        if (!_opts.ExifRotate)
        {
            _baseRot = 0;
            _baseFlipH = false;
            _imgW = img.Width;
            _imgH = img.Height;
            return;
        }

        (int rot, bool flipH) = ExifOrientation.ToTransform(img.ExifOrientation);
        _baseRot = rot;
        _baseFlipH = flipH;

        if (ExifOrientation.SwapsAxes(img.ExifOrientation))
        {
            _imgW = img.Height;
            _imgH = img.Width;
        }
        else
        {
            _imgW = img.Width;
            _imgH = img.Height;
        }
    }

    /// <summary>
    /// 「放大小图片以适合窗口」没勾时的策略：图比窗口小就按 1:1 显示，不拉伸凑满。
    /// FitFactor 是「适应窗口时一个图片像素占几个 DIP」，乘上 DPI 才是物理像素；
    /// 要让一个图片像素正好占一个物理像素，就得来回除回去。
    /// </summary>
    private void ApplyFitPolicy()
    {
        if (_opts.UpscaleSmallToFit)
        {
            return;
        }

        double f = FitFactor();
        if (f <= 0)
        {
            return;
        }

        double physical = f * DpiFactor;
        if (physical > 1.0)
        {
            _zoom = 1.0 / physical;
        }
    }

    /// <summary>低清占位：仅在预载未命中时用，与主视图解码并行。</summary>
    private async Task LoadQuickAsync(string path, long token)
    {
        if (token != _curToken)
        {
            return;
        }

        await DecodeGate.WaitAsync();
        try
        {
            if (token != _curToken)
            {
                return; // 排队期间又切了图 → 不必再解
            }

            Stopwatch sw = Stopwatch.StartNew();
            DecodedImage? q = await Task.Run(() => Decoder.Decode(path, PreviewCache.QuickEdge));
            Perf.Span("  解码 低清 768", sw.ElapsedMilliseconds);
            if (token != _curToken || q is null)
            {
                return; // 解码期间又切了图 → 丢弃
            }

            if (MainImage.Source is null)
            {
                StartFocus(q);
            }

            _previews.Add(q, MainImage.Source, LowImage.Source);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    /// <summary>
    /// 解出「画布长边 ×2」的全清图并覆盖主视图。
    /// delayMs &gt; 0 时先等一会儿：连续翻页途中就不浪费 CPU 去解高清了，
    /// 停手之后才把当前这张升到全清。
    /// </summary>
    private async Task LoadViewAsync(string path, long token, int delayMs = 0)
    {
        try
        {
            await LoadViewCoreAsync(path, token, delayMs);
        }
        finally
        {
            // 无论解出来、解码失败还是中途作废，等待者都要放开，不能再卡着翻页
            if (token == _curToken)
            {
                EndFullWait();
            }
        }
    }

    private async Task LoadViewCoreAsync(string path, long token, int delayMs)
    {
        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
            if (token != _curToken)
            {
                return;
            }
        }

        // 常规按画布取档；但如果在放大状态下翻到这里（zoom > 适应），
        // 再按 ViewEdge 解一张照样不够——必须按放大后的真实需求取档，否则还是糊的。
        int edge = ViewEdge();
        int need = NeededEdge();
        if (need > edge)
        {
            edge = Math.Min(need, ZoomEdgeMax);
        }

        // 并发闸门：快速翻页时最多 2 个解码在跑，排队的那个一旦发现自己已经过期
        // （token 变了）就直接放弃，不再做无用功。这是原先「滚轮节流」的真正替代者。
        await DecodeGate.WaitAsync();
        DecodedImage? ret;
        try
        {
            if (token != _curToken)
            {
                return; // 排队期间又切了图 → 不必再解
            }

            Stopwatch sw = Stopwatch.StartNew();
            ret = await Task.Run(() => Decoder.Decode(path, edge));
            Perf.Span($"  解码 全清 edge={edge}", sw.ElapsedMilliseconds);
        }
        finally
        {
            DecodeGate.Release();
        }

        if (token != _curToken)
        {
            return; // 迟到的结果绝不能盖到新图上
        }

        if (ret is null)
        {
            Status = "图片解码失败（HEIC / WEBP 需系统装对应的“图像扩展”）";
            FinishFocus();
            return;
        }

        ShowDecoded(ret);
        ClearBlur();

        // 清晰图就位后，再让 GPU 按屏幕实际像素精修一次（缩放停止后也会重做）
        _ = RefineViewAsync(ret, token);
    }

    // ==================================================================
    // GPU 精修
    // ==================================================================

    private const int RefineDelayMs = 140;

    /// <summary>
    /// 把已解码的位图在 GPU 上重采样到「屏幕实际显示像素」尺寸。
    ///
    /// 显示链路原本是：位图(画布长边×2) → WPF Stretch 缩放 → 屏幕。
    /// 中间那步是 WPF 的插值，缩小比例大时偏软。GPU 精修把它换成 Mitchell
    /// （带 area sampling 的抗走样降采样），并且输出恰好等于显示像素，
    /// 于是 WPF 拿到的是 1:1 位图，不再二次采样。
    ///
    /// 精修只管「降采样」。放大时目标比源还大，这里不动（目标是要 new 像素，
    /// 交给 WPF 插值）；真正需要更高清晰度时由 <see cref="UpgradeViewAsync"/>
    /// 回解码器按屏幕所需重新取一张，那才是「放大后再次 sample」。
    /// </summary>
    private async Task RefineViewAsync(DecodedImage src, long token)
    {
        long seq = ++_refineSeq;

        // GPU 不可用时，只要有 CPU 的高质量重采样（AVX2 Mitchell）就照样精修：
        // 否则非 NVIDIA 机器只能靠 WPF 自己缩放（双线性，高频细节会糊）。
        // --noavx 关掉 CPU 回退后才彻底交给 WPF。
        bool cpuOnly = !GpuResampler.GpuActive;
        if (cpuOnly && !CpuResampler.Enabled)
        {
            return;
        }

        await Task.Delay(RefineDelayMs);
        if (seq != _refineSeq || token != _curToken)
        {
            return;
        }

        (int w, int h) = TargetPixelSize(src);
        GpuDiagnose($"refine 目标 {w}x{h}  位图 {src.Source.PixelWidth}x{src.Source.PixelHeight}  原图 {src.Width}x{src.Height}");

        if (w <= 0 || h <= 0)
        {
            return;
        }

        if (w >= src.Source.PixelWidth || h >= src.Source.PixelHeight)
        {
            return; // 已经是 1:1 或需要放大，保持原图
        }

        // ① CPU 分支：GPU 不可用，用 AVX2 Mitchell 算一张成品位图（结果缓存照样命中）
        if (cpuOnly)
        {
            BitmapSource? cpuMade;
            try
            {
                Stopwatch swc2 = Stopwatch.StartNew();
                cpuMade = await Task.Run(() => GpuResampler.ResizeCached(src.Path, src.Source, w, h, src.Edge));
                Perf.Span($"  CPU 重采样[{GpuResampler.LastFrom}] {src.Source.PixelWidth}x{src.Source.PixelHeight} → {w}x{h}",
                          swc2.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                GpuDiagnose($"cpu refine 异常 {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (cpuMade is null || seq != _refineSeq || token != _curToken)
            {
                return;
            }

            MainImage.Source = cpuMade;
            Perf.Tick("CPU 精修上屏");
            return;
        }

        // ② 结果缓存命中：连 kernel 都不用跑，直接把上次那张拿上来
        if (ViewSurface.ReuseEnabled)
        {
            BitmapSource? hit = GpuResampler.TryResult(src.Path, src.Source, w, h);
            if (hit is not null)
            {
                if (seq != _refineSeq || token != _curToken)
                {
                    return;
                }

                MainImage.Source = hit;
                Perf.Tick("精修上屏[结果缓存]");
                return;
            }

            // ③ 零拷贝：kernel 结果直接写进 D3D9 纹理，WPF 拿这张纹理上屏。
            //    相较于 ④，省掉的是「回读几 MB 到主机内存」这一趟；
            //    相较于 ②（结果缓存），省的是主机内存里那份成品位图。
            if (D3dView.Enabled)
            {
                ImageSource? d3d = D3dView.Instance.Prepare(w, h);
                if (d3d is not null)
                {
                    IntPtr res = D3dView.Instance.Resource;
                    bool wrote = false;
                    try
                    {
                        Stopwatch sw3 = Stopwatch.StartNew();
                        wrote = await Task.Run(() => GpuResampler.ResizeToTexture(
                            src.Path, src.Source, res, w, h, src.Edge));
                        Perf.Span($"  GPU 重采样[零拷贝][{GpuResampler.LastFrom}] " +
                                  $"{src.Source.PixelWidth}x{src.Source.PixelHeight} → {w}x{h}",
                                  sw3.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        GpuDiagnose($"d3d refine 异常 {ex.GetType().Name}: {ex.Message}");
                    }

                    if (wrote && seq == _refineSeq && token == _curToken)
                    {
                        // 先给 Source（让 D3DImage 挂进视觉树），再标脏区，
                        // 否则脏区打在没有宿主的对象上不触发任何拷贝。
                        MainImage.Source = d3d;

                        Stopwatch sw4 = Stopwatch.StartNew();
                        bool shown = D3dView.Instance.Present(w, h);
                        Perf.Span("  D3DImage 上屏（标记脏区）", sw4.ElapsedMilliseconds);

                        if (shown)
                        {
                            Perf.Tick("精修上屏[零拷贝]");
                            return;
                        }

                        GpuDiagnose("D3DImage 标记脏区失败，回落位图路径");
                    }
                }
            }

            // ④ 后台算完留在显存，UI 线程再直接写进复用位图：
            //    没有中间数组、没有新位图，尺寸不变时 WPF 也不必重建纹理。
            bool computed;
            try
            {
                Stopwatch sw2 = Stopwatch.StartNew();
                computed = await Task.Run(() => GpuResampler.ResizeToDevice(src.Path, src.Source, w, h, src.Edge));
                Perf.Span($"  GPU 重采样[{GpuResampler.LastFrom}] {src.Source.PixelWidth}x{src.Source.PixelHeight} → {w}x{h}",
                          sw2.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                GpuDiagnose($"refine 异常 {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (computed && seq == _refineSeq && token == _curToken)
            {
                Stopwatch swc = Stopwatch.StartNew();
                BitmapSource? view = ViewSurface.Instance.Commit(w, h);
                Perf.Span("  写复用位图（回读 + 标脏）", swc.ElapsedMilliseconds);

                if (view is not null)
                {
                    MainImage.Source = view;
                    Perf.Tick("精修上屏[复用位图]");
                    return;
                }

                GpuDiagnose("复用位图提交失败，回退新建位图");
            }
        }

        // ⑤ 兜底：原来的「新建位图」路径（--noreuse 或前面的零拷贝都没成功）
        BitmapSource? refined;
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            refined = await Task.Run(() => GpuResampler.ResizeCached(src.Path, src.Source, w, h, src.Edge));
            Perf.Span($"  GPU 重采样[{GpuResampler.LastFrom}] {src.Source.PixelWidth}x{src.Source.PixelHeight} → {w}x{h}",
                      sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            GpuDiagnose($"refine 异常 {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (refined is null)
        {
            GpuDiagnose("refine 返回 null（重采样失败）");
            return;
        }

        if (seq != _refineSeq || token != _curToken)
        {
            return;
        }

        Stopwatch swo = Stopwatch.StartNew();
        MainImage.Source = refined;
        Perf.Span("  新位图上屏（新建 + 标脏）", swo.ElapsedMilliseconds);
        Perf.Tick("精修结果上屏");
    }

    /// <summary>当前 zoom / 旋转 / DPI 下，这张图实际占多少屏幕像素。</summary>
    private (int W, int H) TargetPixelSize(DecodedImage img)
    {
        double f = FitFactor();
        if (f <= 0 || img.Width <= 0 || img.Height <= 0)
        {
            return (0, 0);
        }

        (int ew, int eh) = DisplaySize(img);
        double dpi = DpiFactor;
        int w = (int)Math.Round(ew * f * _zoom * dpi);
        int h = (int)Math.Round(eh * f * _zoom * dpi);
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>
    /// 画面上看到的宽高 —— EXIF 把横竖换过来的话这里也要跟着换，
    /// 否则竖拍照片会被按原始（横）尺寸去算重采样目标。
    /// </summary>
    private (int W, int H) DisplaySize(DecodedImage img)
    {
        bool swap = _opts.ExifRotate && ExifOrientation.SwapsAxes(img.ExifOrientation);
        return swap ? (img.Height, img.Width) : (img.Width, img.Height);
    }

    /// <summary>精修路径的一次性诊断（只写前 6 条），用于确认 GPU 重采样是否真的介入。</summary>
    private static int _refineDbg;

    private static void GpuDiagnose(string line)
    {
        if (_refineDbg >= 6)
        {
            return;
        }

        _refineDbg++;
        GpuAccel.WriteLog(line);
    }

    /// <summary>缩放/旋转/换图后请求一次重新取样；连续操作只保留最后一次。</summary>
    private void ScheduleRefine()
    {
        if (_viewOwned is null)
        {
            return;
        }

        // 「高品质等比例缩小」关掉：谁也别算，直接让 WPF 带着它的插值上。
        // 速度更快，代价是缩放比例大时偏软 / 有锯齿。
        if (!_opts.HighQualityDownscale)
        {
            return;
        }

        (int w, int h) = TargetPixelSize(_viewOwned);
        int need = Math.Max(w, h);
        int have = Math.Max(_viewOwned.Source.PixelWidth, _viewOwned.Source.PixelHeight);

        // 屏幕要的比手上这张多（放大了），且实际还能从原图取到更多像素
        // → 回去按新需求重新解码，这才是「再次 sample」。
        // have 已经等于原图像素时就到此为止：再解也解不出更多东西，
        // 剩下的（>100% 的放大）只能交给 WPF 插值。
        int native = Math.Max(_viewOwned.Width, _viewOwned.Height);
        if (need > have && have < native)
        {
            _ = UpgradeViewAsync(_viewOwned, need, _curToken);
            return;
        }

        _ = RefineViewAsync(_viewOwned, _curToken);
    }

    /// <summary>
    /// 放大之后按屏幕所需重新取一张更高档的位图。
    ///
    /// 为什么必须回原图而不是把现有位图放大：
    /// 现有这张是「适应窗口」那档（1536px 之类），放大到 2700px 时 WPF 只能插值，
    /// 补出来的全是猜测像素。回解码器按 2700px 重新采样，拿到的是原始信息。
    /// 解码由 WIC 完成（它本身就是高质量重采样），回来后再交给 GPU 精修对齐到目标像素。
    /// </summary>
    private async Task UpgradeViewAsync(DecodedImage cur, int need, long token)
    {
        long seq = ++_upgradeSeq;

        int edge = DecodeEdgeFor(need, cur.Width, cur.Height);
        if (edge <= Math.Max(cur.Source.PixelWidth, cur.Source.PixelHeight))
        {
            return; // 手上这张已经比请求档更高/相同，不用白解一遍
        }

        // 与精修一样等一小会儿：连点放大时只算最后一次，中间的作废
        await Task.Delay(RefineDelayMs);
        if (seq != _upgradeSeq || token != _curToken)
        {
            return;
        }

        await DecodeGate.WaitAsync();
        DecodedImage? bigger;
        try
        {
            if (seq != _upgradeSeq || token != _curToken)
            {
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            bigger = await Task.Run(() => Decoder.Decode(cur.Path, edge));
            Perf.Span($"  解码 放大档 edge={edge}", sw.ElapsedMilliseconds);
        }
        finally
        {
            DecodeGate.Release();
        }

        if (bigger is null || seq != _upgradeSeq || token != _curToken)
        {
            return;
        }

        // 换上去的那张按原图比例、取 Higher 档；_imgW/_imgH 不变，所以 FitFactor 与
        // 当前的 zoom / 平移完全不受影响，画面位置不会跳。
        ShowDecoded(bigger);
        ClearBlur();

        // 到位后再让 GPU 精修一次，把它对齐到屏幕像素（通常这一步已是 1:1 级降采样）
        _ = RefineViewAsync(bigger, token);
    }

    private void NoteShown(string path)
    {
        if (path.Length == 0 || string.Equals(path, _state.LastOkUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _state.LastOkUri = path;
        _posTimer.Stop();
        _posTimer.Start();
    }

    // ---- 低清占位层 ----
    // 切图时把已缓存的低清图立刻顶上（避免空白），清晰图解码完成后直接替换。
    // 不做模糊、不做缩放呼吸、不做淡入淡出——纯瞬时切换。

    private void StartFocus(DecodedImage img)
    {
        NoteShown(img.Path);

        LowImage.Source = img.Source;
        LowImage.Opacity = 1;
        LowImage.Effect = null;
        if (_imgW <= 0)
        {
            // 先让用户看到尺寸，清晰图到了再校正（低清图同样要按 EXIF 方向转正）
            (int ew, int eh) = DisplaySize(img);
            _imgW = ew;
            _imgH = eh;
            RefreshImageInfo();
        }

        UpdateTransform();
    }

    /// <summary>清晰图已就位（或解码失败）：撤掉低清占位层。</summary>
    private void FinishFocus() => ClearBlur();

    private void ClearBlur()
    {
        LowImage.Source = null;
        LowImage.Opacity = 0;
        LowImage.Effect = null;
    }

    // ==================================================================
    // 后台预载
    // ==================================================================

    /// <summary>当前预载窗口内的所有路径（给显存缓存做滑动淘汰用）。</summary>
    /// <summary>
    /// 图库是循环的（翻过最后一张就回到第一张），所以索引一律绕回。
    /// 否则翻到末尾时「前面那 20 张」会被算成窗口外而淘汰掉，
    /// 等真绕回第一张时缓存是空的，就会卡一下。
    /// </summary>
    private static int WrapIndex(int i, int n) => n <= 0 ? 0 : ((i % n) + n) % n;

    private HashSet<string> WindowPaths()
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (_cur < 0 || _items.Count == 0)
        {
            return set;
        }

        int n = _items.Count;
        int back = PreviewCache.NoWrap ? PreloadBack : Math.Min(PreloadBack, n - 1);
        int fwd = PreviewCache.NoWrap ? PreloadFwd : Math.Min(PreloadFwd, n - 1);

        for (int k = -back; k <= fwd; k++)
        {
            int raw = _cur + k;
            if (PreviewCache.NoWrap && (raw < 0 || raw >= n))
            {
                continue;
            }

            set.Add(_items[WrapIndex(raw, n)].Path);
        }

        return set;
    }

    private void SchedulePreload()
    {
        // 画布尺寸变了 → 预载清晰度档位跟着变，旧档位的缓存作废重解
        int want = PreloadTargetEdge();
        if (want != _previews.Edge)
        {
            _previews.Edge = want;
            _previews.Clear(MainImage.Source, LowImage.Source);
            // 档位变了 → 显存里那批是旧档位，一起作废，否则会一直命中错的清晰度
            GpuImageCache.Instance.Clear();
            GpuResampler.ClearResults();
        }

        _previews.EvictOutside(_items, _cur, PreloadBack, PreloadFwd, MainImage.Source, LowImage.Source);

        // 显存缓存不跟着窗口立刻释放：那样缓存大小恒等于窗口大小（41 张 / 258MB），
        // 配额给到 50% 也用不上。改由配额 + LRU 兜底（见 GpuImageCache.Put）：
        // 翻过的图继续留着，往回翻零成本；一直往前翻则挤掉最旧的，总量不会失控。
        // --tightvram 可回到旧的紧跟随行为，用于对比收益。
        if (GpuImageCache.TightWindow)
        {
            GpuImageCache.Instance.EvictOutside(WindowPaths());
        }

        NotifyCache();
        _preloadSeq++;
        if (_preloadBusy)
        {
            return; // 已在跑：只自增代号，让现有链自动跟到最新位置
        }

        _ = PreloadLoopAsync();
    }

    private async Task PreloadLoopAsync()
    {
        _preloadBusy = true;
        try
        {
            while (true)
            {
                long mySeq = _preloadSeq;
                string? target = null;

                // 先补「前方」（主要浏览方向）。绕回：末尾之后接着补开头那几张，
                // 这样翻过最后一张回到第一张时，第一张早已在缓存里。
                int n = _items.Count;
                int fwd = PreviewCache.NoWrap ? PreloadFwd : Math.Min(PreloadFwd, n - 1);
                int back = PreviewCache.NoWrap ? PreloadBack : Math.Min(PreloadBack, n - 1);

                for (int k = 1; k <= fwd; k++)
                {
                    int raw = _cur + k;
                    if (PreviewCache.NoWrap && raw >= n)
                    {
                        break;
                    }

                    string u = _items[WrapIndex(raw, n)].Path;
                    if (_previews.Get(u) is null && !_failed.Contains(u))
                    {
                        target = u;
                        break;
                    }
                }

                // 再补「后方」：否则快速往回滚时一张都命中不了缓存
                if (target is null)
                {
                    for (int k = 1; k <= back; k++)
                    {
                        int raw = _cur - k;
                        if (PreviewCache.NoWrap && raw < 0)
                        {
                            break;
                        }

                        string u = _items[WrapIndex(raw, n)].Path;
                        if (_previews.Get(u) is null && !_failed.Contains(u))
                        {
                            target = u;
                            break;
                        }
                    }
                }

                if (target is null)
                {
                    // 窗口内全部就绪：记一行便于确认预载真的铺满（限流，功能无关）
                    long now = Environment.TickCount64;
                    if (now - _preloadLoggedAt > 5000)
                    {
                        _preloadLoggedAt = now;
                        GpuAccel.WriteLog(
                            $"preload ready  {_previews.Count} 张 · {_previews.Bytes / (1024 * 1024)} MB · edge={_previews.Edge}" +
                            $" · 显存 {GpuImageCache.Instance.Count} 张 {GpuImageCache.Instance.Bytes / (1024 * 1024)} MB");
                    }

                    return; // 窗口内都预载好了
                }

                string t = target;
                DecodedImage? entry = await Task.Run(() => Decoder.Decode(t, _previews.Edge));
                if (mySeq != _preloadSeq)
                {
                    continue; // 期间又切了图 → 重新挑目标，别把链断掉
                }

                if (entry is not null)
                {
                    _previews.Add(entry, MainImage.Source, LowImage.Source);

                    // 顺手把这张图传进显存常驻：等真翻到它时，精修直接从显存取源，
                    // 不用再 CopyPixels + 上传。上传失败无所谓，走常规路径即可。
                    GpuImageCache.Instance.Put(entry.Path, entry.Source, _previews.Edge);

                    await Dispatcher.InvokeAsync(NotifyCache, DispatcherPriority.Background);
                }
                else
                {
                    _failed.Add(t); // 记黑名单：否则下一轮还会选中它，无限重试
                }

                await Task.Yield(); // 让出主线程
            }
        }
        finally
        {
            _preloadBusy = false;
        }
    }

    // ==================================================================
    // 打开图片来源
    // ==================================================================

    /// <summary>命令行 / 拖放入口：文件就取它所在的整个文件夹，文件夹就直接扫。</summary>
    public void OpenPath(string path)
    {
        if (Directory.Exists(path))
        {
            _ = LoadFolderAsync(path, "folder", recursive: false);
            return;
        }

        if (File.Exists(path))
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                _ = LoadFolderAsync(dir, "folder", recursive: false, focusPath: path);
            }
        }
    }

    private async void ActGallery(object sender, RoutedEventArgs e)
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!Directory.Exists(root))
        {
            Status = "找不到「图片」文件夹 → 已打开选择文件";
            ActFiles(sender, e);
            return;
        }

        await LoadFolderAsync(root, "gallery", recursive: true);
    }

    private void ActFiles(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dlg = new()
        {
            Title = "选择图片",
            Filter = Decoder.DialogFilter,
            FilterIndex = 1,
            Multiselect = true
        };

        if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0)
        {
            Status = "已取消选择";
            return;
        }

        List<ImageItem> picked = dlg.FileNames.Select(p => new ImageItem(p)).ToList();
        _userLoaded = true;

        // 「选择的文件」是挑出来的一批，目录不代表这批照片 → kind 记 files，
        // 下次启动用列表快照恢复（按目录重扫会把没挑过的图也带进来）。
        // 目录仍然记下来，作为快照失效时的兜底线索。
        string? dir = picked.Count > 0 ? Path.GetDirectoryName(picked[0].Path) : null;
        if (dir is not null)
        {
            MarkSource(dir, "files");
        }

        _browseDir = null; // 挑出来的一批散图，左侧不显示 .. 与子文件夹
        ApplyItems(picked, "选择的文件");
    }

    private void ActFolder(object sender, RoutedEventArgs e)
    {
        using Forms.FolderBrowserDialog dlg = new()
        {
            Description = "选择要浏览的图片文件夹（含子文件夹）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dlg.ShowDialog() != Forms.DialogResult.OK)
        {
            Status = "已取消选择";
            return;
        }

        _ = LoadFolderAsync(dlg.SelectedPath, "folder", recursive: false);
    }

    /// <summary>
    /// 打开一个目录。<paramref name="recursive"/> 为真时把整棵子树里的图片摊平成一张表
    /// （「打开图库」是这个模式），为假时只取这一层——子文件夹会作为可进入的行列在左侧，
    /// 也就是文件浏览器模式。
    /// </summary>
    private async Task LoadFolderAsync(string root, string kind, bool recursive, string? focusPath = null)
    {
        _userLoaded = true; // 用户主动开目录：启动时的自动恢复要让路
        _browseDir = recursive ? null : root; // null ⇒ 左侧不显示 .. 与子文件夹
        Status = "正在扫描文件夹…";
        List<ImageItem>? found = await Task.Run(() => ScanFolder(root, recursive));

        if (found is null || found.Count == 0)
        {
            Status = $"「{root}」里没有找到受支持的图片";
            _browseDir = recursive ? null : root;
            BuildEntries();
            OnPropertyChanged(nameof(DirLabel));
            OnPropertyChanged(nameof(CanGoUp));
            return;
        }

        // 记下来源：下次启动自动回到这里。成功才记，扫空了别把上次的目录冲掉。
        MarkSource(root, kind);

        string capped = found.Count >= ScanCap ? $"（已按 {ScanCap} 张截断）" : "";
        ApplyItems(found, FolderLabel(root), capped);

        if (focusPath is not null)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].Path, focusPath, StringComparison.OrdinalIgnoreCase))
                {
                    Select(i);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 扫出目录里的受支持图片。
    /// 递归模式下按「修改时间倒序」（鸿蒙版 orderByDesc('date_added') 的对应关系）；
    /// 单级浏览模式交给 <see cref="SortImages"/> 按名称排——既然要显示文件名，
    /// 列表顺序就该跟看到的一致，否则方向键翻页的走向和用户眼睛的顺序对不上。
    /// </summary>
    private static List<ImageItem> ScanFolder(string root, bool recursive)
    {
        List<(string Path, long Size, DateTime Modified)> found = new();
        try
        {
            EnumerationOptions opt = new()
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            };

            foreach (string path in Directory.EnumerateFiles(root, "*", opt))
            {
                if (!Decoder.IsImage(path))
                {
                    continue;
                }

                long size = -1;
                DateTime modified = DateTime.MinValue;
                try
                {
                    FileInfo fi = new(path);
                    size = fi.Length;
                    modified = fi.LastWriteTime;
                }
                catch
                {
                    // 取不到就留给后台补测
                }

                found.Add((path, size, modified));
                if (found.Count >= ScanCap)
                {
                    break;
                }
            }
        }
        catch
        {
            // 部分目录不可访问时，返回已经扫到的
        }

        // 递归摊平才按时间倒序；单级浏览的排序在 ApplyItems 里按名称做
        if (recursive)
        {
            found.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        }

        return found.Select(f => new ImageItem(f.Path) { Size = f.Size }).ToList();
    }

    // ==================================================================
    // 交互：滚轮 / 鼠标 / 键盘
    // ==================================================================

    /// <summary>
    /// 滚轮翻页：按累积的滚动量翻，攒够一个物理刻度（Delta 120）就立刻翻一张。
    ///
    /// 这里刻意不再做定时节流。早先的写法是「每 120ms 最多翻一张」，
    /// 后果就是滚轮比键盘明显慢——键盘是直接调 Go()，而滚轮每次都要排队
    /// 等定时器（实测快速连滚时每次切换被推迟 40~125ms，还会攒成一次跳 2~3 张）。
    /// 如今切图同步只要 0~2ms、命中预载缓存即刻上屏，「怕解码堆积」已不该由
    /// 翻页节流来兜底，改交给 DecodeGate 限制并发。
    ///
    /// 按 Delta 累积（而非「一个事件一张」）是必要的：
    /// 一个物理刻度会连发好几个事件，触控板更是密集的小 Delta，
    /// 逐事件翻页会一滚飞掉几十张。
    /// </summary>
    private void CanvasArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        _wheelCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // 翻页用的滚轮一动就把画布拉回正中；Ctrl + 滚轮是缩放，平移量要留着当锚点
        if (!_wheelCtrl)
        {
            _tx = 0;
            _ty = 0;
        }

        // Ctrl + 滚轮是缩放，本来就要求跟手，按事件直接走，不做刻度累积
        // 方向沿用原来的约定：向上滚（Delta > 0）= 放大
        if (_wheelCtrl)
        {
            ZoomBy(e.Delta > 0 ? 1.1 : 0.9);
            return;
        }

        long now = Environment.TickCount64;

        // 停手超过 400ms，或滚动方向掉头 → 清零累积，从头开始攒
        if (now - _wheelAccumMs > WheelAccumResetMs ||
            Math.Sign(e.Delta) != Math.Sign(_wheelAccum))
        {
            _wheelAccum = 0;
        }

        _wheelAccumMs = now;
        _wheelAccum += e.Delta;

        int notches = _wheelAccum / WheelNotch;
        if (notches == 0)
        {
            return; // 还不够一格，攒着
        }

        _wheelAccum -= notches * WheelNotch;

        // 约定：向上滚（Delta > 0）= 上一张
        Perf.Note($"wheel 事件 delta={e.Delta} → 立刻翻 {-notches} 张");
        Go(-notches);
    }

    private void ClearWheel()
    {
        _wheelAccum = 0;
        _wheelAccumMs = 0;
    }

    private void CanvasArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_cur < 0)
        {
            return;
        }

        _pressBtn = e.ChangedButton;
        Point p = e.GetPosition(CanvasArea);
        _pressX = p.X;
        _pressY = p.Y;
        _pressMoved = false;
        _baseTx = _tx;
        _baseTy = _ty;
        CanvasArea.CaptureMouse();

        if (e.ChangedButton == MouseButton.Right)
        {
            e.Handled = true; // 右键别让系统弹菜单
        }
    }

    private void CanvasArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressBtn is null)
        {
            return;
        }

        Point p = e.GetPosition(CanvasArea);
        if (!_pressMoved &&
            (Math.Abs(p.X - _pressX) > ClickMoveTol || Math.Abs(p.Y - _pressY) > ClickMoveTol))
        {
            _pressMoved = true;
        }

        if (_pressMoved)
        {
            _tx = _baseTx + (p.X - _pressX);
            _ty = _baseTy + (p.Y - _pressY);
            _viewTouched = true;
            UpdateTransform();
        }
    }

    private void CanvasArea_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressBtn is null)
        {
            return;
        }

        MouseButton btn = _pressBtn.Value;
        bool moved = _pressMoved;
        _pressBtn = null;
        CanvasArea.ReleaseMouseCapture();

        if (moved)
        {
            return; // 拖拽平移，不算点击
        }

        Point p = e.GetPosition(CanvasArea);
        if (btn == MouseButton.Left)
        {
            ZoomAt(ZoomLeft, p.X, p.Y);
        }
        else if (btn == MouseButton.Right)
        {
            ZoomAt(ZoomRight, p.X, p.Y);
        }

        if (e.ClickCount == 2)
        {
            ResetView();
            Status = "已适应窗口";
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+G：GPU 加速开关（其余快捷键不接修饰键，故单独在这里判）
        if (e.Key == Key.G && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ToggleGpu();
            e.Handled = true;
            return;
        }

        // Shift+字母：原来在左下角按钮上的三个功能（左旋 / 镜像），改走键盘
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            switch (e.Key)
            {
                case Key.R:
                    RotateBy(-90);
                    e.Handled = true;
                    return;
                case Key.H:
                    ToggleFlipH();
                    e.Handled = true;
                    return;
                case Key.V:
                    ToggleFlipV();
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Left:
                Go(-1);
                e.Handled = true;
                break;
            case Key.Right:
                Go(1);
                e.Handled = true;
                break;
            case Key.Up:
                ZoomBy(1.25);
                e.Handled = true;
                break;
            case Key.Down:
                ZoomBy(0.8);
                e.Handled = true;
                break;
            case Key.Back:
                GoUp();
                e.Handled = true;
                break;
            case Key.Space:
                ToggleSlide();
                e.Handled = true;
                break;
            case Key.R:
                RotateBy(90);
                e.Handled = true;
                break;
            case Key.F:
                ZoomPixelPerfect();
                e.Handled = true;
                break;
            case Key.D0:
                ResetView();
                Status = "已复位视图";
                e.Handled = true;
                break;
            case Key.F11:
                SetFull(!_fullscreen);
                e.Handled = true;
                break;
            case Key.Escape:
                // 全屏时 Esc 先退全屏（第一优先，避免误触直接退出）
                if (_fullscreen)
                {
                    SetFull(false);
                }
                else
                {
                    Close();
                }

                e.Handled = true;
                break;
        }
    }

    private void ToggleSlide()
    {
        if (_playing)
        {
            StopSlide();
            return;
        }

        if (_items.Count == 0)
        {
            Status = "先打开图片再播放幻灯片";
            return;
        }

        _playing = true;
        OnPropertyChanged(nameof(SlideLabel));
        OnPropertyChanged(nameof(SlideBackground));
        OnPropertyChanged(nameof(SlideForeground));
        Status = $"幻灯片播放中（每 {SlideIntervalMs / 1000} 秒切换）";
        _slideTimer.Start();
    }

    private void StopSlide()
    {
        _slideTimer.Stop();
        if (_playing)
        {
            _playing = false;
            OnPropertyChanged(nameof(SlideLabel));
            OnPropertyChanged(nameof(SlideBackground));
            OnPropertyChanged(nameof(SlideForeground));
            Status = "幻灯片已暂停";
        }
    }

    private void SetFull(bool on)
    {
        if (on == _fullscreen)
        {
            return;
        }

        _fullscreen = on;
        _pendingReload = true; // 画布尺寸变了 → 落地后按新尺寸重解，避免把旧分辨率拉大发虚
        OnPropertyChanged(nameof(ChromeVisibility));
        OnPropertyChanged(nameof(FullscreenVisibility));

        if (on)
        {
            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevResizeMode = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _prevWindowStyle;
            ResizeMode = _prevResizeMode;
            WindowState = _prevWindowState;
        }

        Status = on ? "全屏模式 · Esc 退出全屏 / F11 切换" : "已退出全屏 · Esc 关闭应用";
    }

    private void CanvasArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTransform();
        if (_pendingReload && _cur >= 0)
        {
            _pendingReload = false;
            ShowPreviewOf(_cur);
        }

        // 只有预载档位真的变了才重排（档位没变时不必动预载链，否则会和布局互相刺激）
        if (PreloadTargetEdge() != _previews.Edge)
        {
            SchedulePreload();
        }
    }

    /// <summary>
    /// 预载清晰度档位：取「画布长边 ×1.15」，夹在 1024–1600 之间，再量化到 256 的倍数。
    /// 量化是为了窗口每变几像素不至于整批重解码。
    /// 上限 1600 是内存约束：41 张 × 约 6.8MB ≈ 280MB，再大就吃不消了；
    /// 真正「正在看」的那张仍会单独解到全清（画布长边 ×2）。
    /// </summary>
    private int PreloadTargetEdge()
    {
        double edge = Math.Max(CanvasArea.ActualWidth, CanvasArea.ActualHeight);
        if (edge <= 0)
        {
            return PreviewCache.MinEdge;
        }

        // 必须向上取整：向下会把 1133 量化成 1024，反而小于屏幕实际要显示的 1196，
        // 于是每张命中缓存的图都还得再解一次全清才能看——那才是切图卡顿的真正来源。
        // 另外要乘 DPI：ActualWidth 是逻辑像素，高分屏上真实像素还要再放大一倍多。
        double dpi = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
        int v = (int)(Math.Ceiling(edge * dpi * 1.15 / 256.0) * 256);
        return Math.Clamp(v, PreviewCache.MinEdge, PreviewCache.MaxEdge);
    }

    // ==================================================================
    // 列表 / 拖放 / 按钮
    // ==================================================================

    /// <summary>
    /// 左侧点一下：图片立刻上右边显示；子文件夹直接进；「..」回上一级。
    /// 单击（而不是双击）是为了和「点文件名立刻看图」保持一致的手感。
    /// </summary>
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSel)
        {
            return;
        }

        int i = FileList.SelectedIndex;
        if (i < 0 || i >= _entries.Count)
        {
            return;
        }

        FsEntry row = _entries[i];

        if (row.IsUp)
        {
            GoUp();
            return;
        }

        if (row.IsFolder)
        {
            _ = LoadFolderAsync(row.Path, "folder", recursive: false);
            return;
        }

        if (row.ItemIndex >= 0 && row.ItemIndex != _cur)
        {
            Select(row.ItemIndex);
        }
    }

    /// <summary>回到上一级目录。</summary>
    private void GoUp()
    {
        if (_browseDir is null)
        {
            return;
        }

        DirectoryInfo? parent = Directory.GetParent(_browseDir.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null)
        {
            Status = "已经是盘符根目录";
            return;
        }

        _ = LoadFolderAsync(parent.FullName, "folder", recursive: false);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        string first = paths[0];
        if (Directory.Exists(first))
        {
            _ = LoadFolderAsync(first, "folder", recursive: false);
            return;
        }

        List<ImageItem> list = paths.Where(p => File.Exists(p) && Decoder.IsImage(p))
                                   .Select(p => new ImageItem(p))
                                   .ToList();
        if (list.Count == 0)
        {
            Status = "拖进来的不是受支持的图片";
            return;
        }

        _userLoaded = true;
        _sessionRoot = ""; // 散文件没有统一目录：别拿第一个文件的目录去覆盖记忆
        _browseDir = null;
        ApplyItems(list, "拖放的文件");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopSlide();
        _posTimer.Stop();
        FlushPos();
    }

    /// <summary>
    /// 拖到另一块不同缩放比的显示器时，同一个 _zoom 对应的物理像素数就变了。
    /// 这里保持显示的物理大小（_zoom 不动），只把百分比重新算一遍。
    /// </summary>
    private void Window_DpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        RefreshImageInfo();
        ScheduleRefine();
    }

    private void ActGpu(object sender, RoutedEventArgs e) => ToggleGpu();

    /// <summary>切换 GPU / CPU 渲染路径，便于直接对比画质与流畅度。</summary>
    private void ToggleGpu()
    {
        GpuResampler.Enabled = !GpuResampler.Enabled;
        RefreshGpu();

        // 这里是从用户操作进来的，可以等初始化（GpuActive 会触发 Ensure）
        Status = GpuResampler.GpuActive
            ? $"GPU 加速已开启 · {GpuAccel.DeviceName}"
            : GpuAccel.Available
                ? "已切到 CPU 渲染（GPU 仍可用，Ctrl+G 切回）"
                : $"CPU 渲染 · {GpuAccel.FailReason}";

        ScheduleRefine();
    }

    /// <summary>
    /// 性能自测（命令行 --bench）：连续翻页若干张，各段耗时写入 perf.log 后自动退出。
    /// 走的是和键盘翻页完全相同的 Go() 路径，所以测到的就是真实体感链路。
    /// </summary>
    public async Task BenchAsync(int steps, int intervalMs, int rounds = 2)
    {
        await Task.Delay(6000); // 等启动、预载铺开

        for (int r = 1; r <= rounds; r++)
        {
            Perf.Note($"==== bench 第 {r} 轮 去程：前进 {steps} 张 / 间隔 {intervalMs} ms ====");

            for (int i = 0; i < steps; i++)
            {
                await Dispatcher.InvokeAsync(() => Go(1));
                await Task.Delay(intervalMs);
            }

            await Task.Delay(1500);
            Perf.Note($"==== bench 第 {r} 轮 返程：原路后退 {steps} 张（同一批图第二/三次经过）====");

            for (int i = 0; i < steps; i++)
            {
                await Dispatcher.InvokeAsync(() => Go(-1));
                await Task.Delay(intervalMs);
            }

            await Task.Delay(1500);
        }

        // 滚轮轮：抛真实的 MouseWheel 路由事件，走与用户滚滚轮完全相同的代码路径。
        // 间隔刻意压到 40 ms（人手连滚的真实节奏），这样才会踩到滚轮的节流窗口；
        // 用 350 ms 的慢间隔测就等于没测——节流窗口根本不会被触发。
        await Task.Delay(1500);

        // 先跳到一段完全没有预载的区域：这里每一张都要现解，是最容易触发解码风暴的场景。
        // 把「未命中 + 快速连滚」放进同一个测试，才能验证 DecodeGate 真的兜住了。
        Perf.Note("==== bench 冷区轮：跳到未预载处后立即连续快滚 ====");
        await Dispatcher.InvokeAsync(() => Go(80));
        await Task.Delay(300);

        for (int i = 0; i < steps; i++)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                MouseWheelEventArgs e = new(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = CanvasArea
                };
                CanvasArea.RaiseEvent(e);
            });
            await Task.Delay(40);
        }

        await Task.Delay(1500);
        Perf.Note("==== bench 滚轮轮：连续快速滚轮（真实事件 · 间隔 40 ms）====");

        for (int i = 0; i < steps; i++)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                MouseWheelEventArgs e = new(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = CanvasArea
                };
                CanvasArea.RaiseEvent(e);
            });
            await Task.Delay(40);
        }

        // 边界轮：停在倒数第 3 张，等预载把「绕回开头那几张」也补上，
        // 再连翻 6 张跨过首尾边界。这 6 次若全是「缓存命中」，说明循环预载真的生效了。
        await Task.Delay(1500);
        Perf.Note("==== bench 边界轮：翻过最后一张绕回第一张 ====");

        int total = _items.Count;
        await Dispatcher.InvokeAsync(() => Go(total - 3 - _cur));
        await Task.Delay(4000); // 给预载留出绕回补张的时间

        for (int i = 0; i < 6; i++)
        {
            await Dispatcher.InvokeAsync(() => Go(1));
            await Task.Delay(350);
        }

        await Task.Delay(5000); // 等最后一次全清 / 精修落定

        Perf.Note($"==== 显存缓存：{GpuImageCache.Instance.Count} 张 · " +
                  $"{GpuImageCache.Instance.Bytes / (1024 * 1024)} MB / 配额 " +
                  $"{GpuImageCache.Instance.QuotaMb} MB（总显存 " +
                  $"{GpuAccel.TotalMemBytes / (1024 * 1024)} MB · 上限 " +
                  $"{GpuImageCache.Ratio * 100:0}%）· {GpuAccel.VramStats} ====");
        Perf.Note("==== bench 结束 ====");
        Perf.FlushSync();
        await Task.Delay(800);

        await Dispatcher.InvokeAsync(Close);
    }

    /// <summary>
    /// 后台探测 GPU，完成后刷新状态栏角标。
    /// 探测失败只按 CPU 渲染处理，绝不影响窗口显示。
    /// </summary>
    private void InitGpuAsync()
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                GpuAccel.Touch();
            }
            catch
            {
                // 驱动异常一律降级为 CPU 渲染
            }

            _ = Dispatcher.BeginInvoke(new Action(RefreshGpu));
        });
    }

    private void RefreshGpu()
    {
        // 按钮已经在顶栏去掉了，这里只需要刷新状态栏那行 GPU 说明。
        // 注意用 GpuReady 而非触初始化的版本：CUDA 起来要 1~2 秒，
        // UI 线程同步等它会直接冻结窗口（实测启动那一下会卡 2 秒）。
        OnPropertyChanged(nameof(GpuLine));
    }

    private void ActSortDir(object sender, RoutedEventArgs e)
    {
        _sortDesc = !_sortDesc;
        OnPropertyChanged(nameof(SortDirLabel));
        Resort();
        SaveSortPref();
        Status = "列表按名称" + (_sortDesc ? "倒序" : "正序") + (_naturalSort ? "（自然序）" : "");
    }

    private void ActNatural(object sender, RoutedEventArgs e)
    {
        _naturalSort = !_naturalSort;
        OnPropertyChanged(nameof(NaturalBackground));
        OnPropertyChanged(nameof(NaturalForeground));
        Resort();
        SaveSortPref();
        Status = _naturalSort
            ? "已按自然序排列：(1) (2) (10) (11) 这种数字会按大小排"
            : "已按逐字符排列：(1) (11) (2) 这种数字按字符排";
    }

    private void ActUpDir(object sender, RoutedEventArgs e) => GoUp();

    /// <summary>排序偏好写进状态文件，下次打开还在。</summary>
    private void SaveSortPref()
    {
        _state.SortDesc = _sortDesc;
        _state.NaturalSort = _naturalSort;
        _state.Save();
    }

    private void ActSlide(object sender, RoutedEventArgs e) => ToggleSlide();
    private void ActFull(object sender, RoutedEventArgs e) => SetFull(!_fullscreen);
    private void ActResume(object sender, RoutedEventArgs e) => ResumeLast();

    // ==================================================================
    // 选项（五项开关）
    // ==================================================================

    private void ActOptions(object sender, RoutedEventArgs e) => OpenOptions();

    private void OpenOptions()
    {
        OptionsWindow dlg = new(_opts)
        {
            Owner = this
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        ApplyOptions();
        Status = "已保存选项：" + _opts;
    }

    /// <summary>把新取值推给解码 / 渲染链路，并让改动立刻作用到正在看的那张图。</summary>
    private void ApplyOptions()
    {
        _opts.Push();
        _state.Options = _opts;
        _state.Save();
        ApplyScalingMode();

        // 这些开关改变了「已经解出来的位图」，缓存里的旧结果必须作废，
        // 否则看到的还是改之前的那版，会误以为选项没生效。
        _previews.Clear(MainImage.Source, LowImage.Source);
        GpuImageCache.Instance.Clear();

        if (_cur >= 0 && _cur < _items.Count)
        {
            _viewTouched = false; // 让「适应 / 小图不放大」重新算一遍
            ResetView();
            ShowPreviewOf(_cur);
        }
    }

    /// <summary>
    /// 「高品质缩小」关掉时连 WPF 自己的插值也降级：这个是最后一道 fallback，
    /// （GPU 精修被跳过之后剩下的缩放全由它承担），不跟着降就白关了。
    /// </summary>
    private void ApplyScalingMode()
    {
        RenderOptions.SetBitmapScalingMode(MainImage,
            _opts.HighQualityDownscale ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
        RenderOptions.SetBitmapScalingMode(LowImage,
            _opts.HighQualityDownscale ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
    }

    /// <summary>
    /// 自检：逐档量化「屏幕要多少像素 vs 手上位图有多少」，写 gpu.log 后退出。
    /// 比值 &gt; 1 就说明画面在拉伸 = 会糊；比值 ≈ 1 表示做到了「放大后再次取样」。
    ///
    /// 用法：PIKAN.exe --zoomtest <图片>
    /// </summary>
    public async Task ZoomSelfTestAsync(string? path)
    {
        if (path is null)
        {
            GpuAccel.WriteLog("zoomtest: 需要一张图片路径");
            Finish();
            return;
        }

        // 自检期间必须屏蔽输入：否则测到一半有人动一下鼠标/滚轮，zoom 就变了，
        // 读数会像「档位错乱」，实际是外部输入。
        CanvasArea.PreviewMouseDown += (_, e) => e.Handled = true;
        CanvasArea.PreviewMouseUp += (_, e) => e.Handled = true;
        CanvasArea.PreviewMouseWheel += (_, e) => e.Handled = true;
        CanvasArea.PreviewMouseMove += (_, e) => e.Handled = true;

        await Dispatcher.InvokeAsync(() => OpenPath(path));
        await Task.Delay(4000); // 等载入、精修到位

        // 档位按「相对原图像素的倍率」给（即界面上显示的百分比），不再是内部的 _zoom
        // （_zoom 是相对「适应窗口」的倍数，会随窗口大小浮动，不直观）
        double perZoom = Math.Max(1e-9, FitFactor() * DpiFactor); //  1 单位 _zoom 等于多少倍像素
        (string Name, Func<double> Zoom)[] steps =
        {
            ("适应", () => 1.0),
            ("25%", () => 0.25 / perZoom),
            ("100%", () => 1.0 / perZoom),
            ("200%", () => 2.0 / perZoom),
            ("400%", () => 4.0 / perZoom)
        };

        foreach ((string name, Func<double> zoom) in steps)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _tx = 0;
                _ty = 0;
                _zoom = Clamp(zoom(), MinScale, MaxScale);
                UpdateTransform();
                RefreshImageInfo();
                ScheduleRefine();
            });

            await Task.Delay(2500); // 重新解码 + GPU 精修要花点时间

            await Dispatcher.InvokeAsync(() =>
            {
                if (_viewOwned is null)
                {
                    GpuAccel.WriteLog($"zoomtest {name}: 没有主视图");
                    return;
                }

                // 显示链路有两套：普通 BitmapSource（含精修后的 WriteableBitmap）
                // 与零拷贝的 D3DImage，两者尺寸属性不通用，分开取。
                (int tw, int th) = TargetPixelSize(_viewOwned);
                int bw = 0;
                int bh = 0;
                switch (MainImage.Source)
                {
                    case BitmapSource bs:
                        bw = bs.PixelWidth;
                        bh = bs.PixelHeight;
                        break;
                    case D3DImage d3:
                        bw = d3.PixelWidth;
                        bh = d3.PixelHeight;
                        break;
                }

                double ratio = bw > 0 ? Math.Max(tw / (double)bw, th / (double)bh) : 0;

                // 顺序再往下就两回事了：需求还没到位图档（真・不够，糊）vs
                // 位图已是原图全像素（超过 100% 之后必然如此，没有更多像素可取，
                // 再往上的放大只能插值，不算缺陷）
                bool atNative = _viewOwned.Width > 0 && bw >= _viewOwned.Width;
                string verdict = ratio <= 1.02 ? "  ✓ 够用"
                               : atNative ? "  · 已到原图像素上限，再放大靠插值"
                               : "  ← 不够，会糊";

                GpuAccel.WriteLog($"zoomtest {name}: 显示 {ZoomText}  zoom={_zoom:F2}  " +
                                  $"屏幕需 {tw}x{th}  位图 {bw}x{bh}（档 {_viewOwned.Edge}）  " +
                                  $"原图 {_viewOwned.Width}x{_viewOwned.Height}  " +
                                  $"拉伸比 {ratio:F2}{verdict}");
            });
        }

        Finish();
    }

    private static void Finish()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                System.Windows.Application.Current.Shutdown());
        });
    }
}
