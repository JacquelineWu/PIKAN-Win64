using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
    private const double MaxScale = 16.0;

    // ---- 鼠标键缩放：左键 ×1.5，右键 ×2.0 ----
    private const double ZoomLeft = 1.5;
    private const double ZoomRight = 2.0;

    /// <summary>按下到抬起若移动超过该距离（px）就判定为拖拽平移，不触发点击缩放。</summary>
    private const double ClickMoveTol = 4;

    // ---- 后台预载窗口 ----
    private const int PreloadFwd = 20;
    private const int PreloadBack = 20;

    /// <summary>主视图解码的长边下限 / 上限（按「画布长边 × 2」取，放大到 200% 仍清晰）。</summary>
    private const int ViewEdgeMin = 1600;
    private const int ViewEdgeMax = 4096;

    // ---- 切图过渡：低清 + 大模糊 → 分档对焦 → 清晰图就绪后整层淡出 ----
    private const double BlurMax = 14;
    private const double BlurZoom = 0.02;
    private const int BlurSteps = 4;
    private const int BlurStepMs = 22;
    private const int BlurFadeSteps = 4;
    private const int BlurFadeStepMs = 22;

    // ---- 滚轮去抖 / 合并 ----
    private const long WheelNotchMs = 60;
    private const long WheelStepMs = 120;

    // ---- 其它 ----
    private const int SlideIntervalMs = 3000;
    private const long PosSaveMs = 500;
    private const int ScanCap = 20000;
    private const int SizeChunk = 60;
    private const int SizeYieldMs = 12;

    private readonly ObservableCollection<ImageItem> _items = new();
    private readonly PreviewCache _previews = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    private readonly ScaleTransform _mainScale = new(1, 1);
    private readonly ScaleTransform _lowScale = new(1, 1);
    private readonly BlurEffect _lowBlur = new() { Radius = 0, KernelType = KernelType.Gaussian };

    private readonly DispatcherTimer _slideTimer = new();
    private readonly DispatcherTimer _posTimer = new() { Interval = TimeSpan.FromMilliseconds(PosSaveMs) };
    private readonly DispatcherTimer _wheelTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // ---- 档案 ----
    private string _sourceLabel = "尚未打开";
    private int _cur = -1;

    // ---- 视图变换状态 ----
    private double _zoom = 1;
    private double _tx;
    private double _ty;
    private int _rot;
    private bool _flipH;
    private bool _flipV;

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

    // ---- 滚轮合并 ----
    private long _lastWheelMs;
    private int _lastWheelSign;
    private int _pendingDelta;
    private long _lastSwitchMs;
    private bool _wheelCtrl;

    // ---- 主视图 / 过渡层 ----
    private long _curToken;
    private long _focusSeq;
    private DecodedImage? _viewOwned;
    private DecodedImage? _warm;
    private double _blurRadius;
    private double _lowOpacity;

    /// <summary>过渡层的「未模糊」原图：GPU 模糊要始终从它取样，否则会二次模糊。</summary>
    private BitmapSource? _lowSource;

    // ---- GPU 加速 ----
    private long _refineSeq;
    private readonly Dictionary<(BitmapSource Src, int Key), BitmapSource> _blurCache = new();

    // ---- 预载链 ----
    private long _preloadSeq;
    private bool _preloadBusy;

    // ---- 体积补测 ----
    private long _sizeSeq;

    // ---- 位置记忆 ----
    private AppState _state = AppState.Load();
    private bool _snapDirty;
    private long _session;

    private bool _suppressSel;
    private bool _pendingReload;

    private WindowState _prevWindowState = WindowState.Normal;
    private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _prevResizeMode = ResizeMode.CanResize;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = this;

        // 先探一次 GPU：CUDA 上下文创建 + PTX JIT 有几十毫秒开销，放在启动时做，
        // 避免第一次切图才初始化导致卡顿。失败也只是安静降级。
        GpuAccel.Touch();
        RefreshGpu();

        MainImage.RenderTransform = _mainScale;
        LowImage.RenderTransform = _lowScale;
        LowImage.Effect = _lowBlur;

        _slideTimer.Interval = TimeSpan.FromMilliseconds(SlideIntervalMs);
        _slideTimer.Tick += (_, _) => Go(1);
        _posTimer.Tick += (_, _) => { _posTimer.Stop(); FlushPos(); };
        _wheelTimer.Tick += (_, _) => { _wheelTimer.Stop(); FlushWheel(); };

        Loaded += (_, _) => UpdateTransform();
        ReadLastPos();
    }

    // ==================================================================
    // 绑定属性
    // ==================================================================

    public ObservableCollection<ImageItem> Items => _items;

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

    public string ZoomText => Math.Round(_zoom * 100) + " %";

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

    public string GpuLabel => GpuResampler.GpuActive ? "CUDA" : "CPU";

    public string GpuBackground => GpuResampler.GpuActive ? "#3ddc84" : "#2a2f3a";

    public string GpuForeground => GpuResampler.GpuActive ? "#101216" : "#e8ecf4";

    public string GpuLine => "x64 · Windows · " + GpuResampler.StatusText;

    public string GpuTip => GpuResampler.GpuActive
        ? $"GPU 加速已开启\n{GpuAccel.DeviceName}\n点击切到 CPU 渲染（Ctrl+G）"
        : $"当前为 CPU 渲染{(GpuAccel.Available ? "（已手动关闭 GPU）" : "\n" + GpuAccel.FailReason)}\n点击开启 CUDA 加速（Ctrl+G）";

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

        // 预热「上次成功显示的那张」：后台只解 1 张低清图，用户打开图库时首屏直接命中缓存
        if (p.LastOkUri.Length > 0 && File.Exists(p.LastOkUri))
        {
            string uri = p.LastOkUri;
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                DecodedImage? img = Decoder.Decode(uri, PreviewCache.PreloadEdge);
                if (img is null)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() => { _warm = img; }, DispatcherPriority.Background);
            });
        }

        Status = $"上次看到「{p.LastName}」第 {p.LastIdx + 1} 张 · 点「打开图库」或「回到上次」自动跳回";
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

        _posTimer.Stop();
        _posTimer.Start(); // 防抖：滚轮连翻时没必要每张都落盘
    }

    private void FlushPos()
    {
        try
        {
            if (_snapDirty)
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
            var list = _state.Snapshot.Where(File.Exists).Select(p => new ImageItem(p)).ToList();
            if (list.Count > 0)
            {
                ApplyItems(list, _state.LastSrc.Length > 0 ? _state.LastSrc : "上次会话");
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
        _items.Clear();
        foreach (ImageItem it in list)
        {
            _items.Add(it);
        }

        SourceLabel = src;
        OnPropertyChanged(nameof(CountLabel));

        // 先算出落点再解码：否则会先按第 1 张解一遍、恢复时又解一遍，白白多一次大图解码
        int start = RestoreIndex(list);
        _cur = list.Count > 0 ? (start >= 0 ? start : 0) : -1;
        ResetView();

        if (_cur >= 0)
        {
            LoadMeta(_cur);
        }

        _preloadSeq++;
        ClearWheel();
        ClearBlur();
        _previews.Clear();
        _failed.Clear();

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

                        foreach ((ImageItem item, long size) in updates)
                        {
                            item.Size = size;
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
    private double FrameW => _rot % 180 != 0 ? CanvasArea.ActualHeight : CanvasArea.ActualWidth;

    private double FrameH => _rot % 180 != 0 ? CanvasArea.ActualWidth : CanvasArea.ActualHeight;

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

        _mainScale.ScaleX = _zoom * (_flipH ? -1 : 1);
        _mainScale.ScaleY = _zoom * (_flipV ? -1 : 1);

        double fz = FocusZoom();
        _lowScale.ScaleX = _mainScale.ScaleX * fz;
        _lowScale.ScaleY = _mainScale.ScaleY * fz;

        Matrix m = new();
        m.Translate(_tx, _ty);
        m.RotateAt(_rot, cw / 2, ch / 2);
        Viewport.RenderTransform = new MatrixTransform(m);
    }

    /// <summary>过渡层的附加缩放：模糊越大越「推近」（最多 +2%），对焦完成恰好回到 1.0。</summary>
    private double FocusZoom() => 1 + BlurZoom * (_blurRadius / BlurMax);

    private void ResetView()
    {
        _zoom = 1;
        _tx = 0;
        _ty = 0;
        _rot = 0;
        _flipH = false;
        _flipV = false;
        UpdateTransform();
        RefreshImageInfo();
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    private void ZoomBy(double factor)
    {
        _zoom = Clamp(_zoom * factor, MinScale, MaxScale);
        UpdateTransform();
        RefreshImageInfo();
        ScheduleRefine();
        Status = $"缩放 {Math.Round(_zoom * 100)}%";
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
            Status = $"已达缩放极限 {Math.Round(old * 100)}%";
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
        UpdateTransform();
        RefreshImageInfo();
        ScheduleRefine();
        Status = $"缩放 {Math.Round(_zoom * 100)}%";
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

        _zoom = Clamp(1 / f, MinScale, MaxScale);
        UpdateTransform();
        RefreshImageInfo();
        ScheduleRefine();
        Status = $"1:1 原始像素（{_imgW} × {_imgH}）";
    }

    private void RotateBy(int delta)
    {
        _rot = (_rot + delta + 360) % 360;
        UpdateTransform();
        ScheduleRefine();
        Status = $"旋转 {_rot}°";
    }

    private void ToggleFlipH()
    {
        _flipH = !_flipH;
        UpdateTransform();
        Status = _flipH ? "已水平镜像" : "已取消水平镜像";
    }

    private void ToggleFlipV()
    {
        _flipV = !_flipV;
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

        int n = _items.Count;
        _cur = ((_cur + delta) % n + n) % n;
        ResetView();
        LoadMeta(_cur);
        ShowPreviewOf(_cur);
        UpdateSelection();
        Status = $"第 {_cur + 1} / {n} 张";
        SchedulePreload();
        MarkPos();
    }

    private void UpdateSelection()
    {
        _suppressSel = true;
        if (_cur >= 0 && _cur < _items.Count)
        {
            FileList.SelectedIndex = _cur;
            FileList.ScrollIntoView(FileList.Items[_cur]);
        }
        else
        {
            FileList.SelectedIndex = -1;
        }

        _suppressSel = false;
    }

    // ==================================================================
    // 主视图与过渡层
    // ==================================================================

    /// <summary>主视图解码的长边：画布长边 × 2，并夹在 [1600, 4096] 内控制内存。</summary>
    private int ViewEdge()
    {
        double baseEdge = Math.Max(CanvasArea.ActualWidth, CanvasArea.ActualHeight);
        return (int)Math.Max(ViewEdgeMin, Math.Min(ViewEdgeMax, Math.Round(baseEdge * 2)));
    }

    /// <summary>
    /// 切到第 idx 张：
    ///   ① 代号 +1，作废一切在途的异步回调（含过渡动画）；
    ///   ② 命中预载缓存就把那张低清图盖到过渡层做「大模糊」，再分档收敛（对焦感）；
    ///   ③ 同时异步解码「画布长边 ×2」的清晰图，校验代号后放回底层，并让过渡层淡出。
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

        if (idx < 0 || idx >= _items.Count)
        {
            ClearBlur();
            return;
        }

        string path = _items[idx].Path;
        DecodedImage? hit = _previews.Get(path);
        if (hit is not null)
        {
            StartFocus(hit);
        }
        else
        {
            ClearBlur();
            _ = LoadQuickAsync(path, token);
        }

        _ = LoadViewAsync(path, token);
    }

    /// <summary>低清占位：与主视图解码并行，谁先回来谁先顶上。</summary>
    private async Task LoadQuickAsync(string path, long token)
    {
        DecodedImage? q = await Task.Run(() => Decoder.Decode(path, PreviewCache.PreloadEdge));
        if (token != _curToken)
        {
            return; // 解码期间又切了图 → 丢弃
        }

        if (q is null)
        {
            return;
        }

        if (_viewOwned is null)
        {
            StartFocus(q);
        }

        _previews.Add(q, MainImage.Source, LowImage.Source);
    }

    private async Task LoadViewAsync(string path, long token)
    {
        int edge = ViewEdge();
        DecodedImage? ret = await Task.Run(() => Decoder.Decode(path, edge));

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

        NoteShown(path);
        _viewOwned = ret;
        MainImage.Source = ret.Source;
        _imgW = ret.Width;
        _imgH = ret.Height;
        UpdateTransform();
        RefreshImageInfo();
        FinishFocus();

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
    /// 放大不做精修（那只会凭空造像素），交给 WPF 的 RenderTransform。
    /// </summary>
    private async Task RefineViewAsync(DecodedImage src, long token)
    {
        long seq = ++_refineSeq;
        if (!GpuResampler.GpuActive)
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

        BitmapSource? refined;
        try
        {
            refined = await Task.Run(() => GpuResampler.Resize(src.Source, w, h));
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

        MainImage.Source = refined;
    }

    /// <summary>当前 zoom / 旋转 / DPI 下，这张图实际占多少屏幕像素。</summary>
    private (int W, int H) TargetPixelSize(DecodedImage img)
    {
        double f = FitFactor();
        if (f <= 0 || img.Width <= 0 || img.Height <= 0)
        {
            return (0, 0);
        }

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(img.Width * f * _zoom * dpi);
        int h = (int)Math.Round(img.Height * f * _zoom * dpi);
        return (Math.Max(1, w), Math.Max(1, h));
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

    /// <summary>缩放/旋转/换图后请求一次 GPU 精修；连续操作只保留最后一次。</summary>
    private void ScheduleRefine()
    {
        if (_viewOwned is null)
        {
            return;
        }

        _ = RefineViewAsync(_viewOwned, _curToken);
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

    // ---- 过渡动画 ----

    private void StartFocus(DecodedImage img)
    {
        _focusSeq++;
        long seq = _focusSeq;
        NoteShown(img.Path);

        _lowSource = img.Source;
        LowImage.Source = img.Source;
        _blurRadius = BlurMax;
        _lowOpacity = 1;
        if (_imgW <= 0)
        {
            _imgW = img.Width; // 先让用户看到尺寸，清晰图到了再校正
            _imgH = img.Height;
            RefreshImageInfo();
        }

        UpdateLowLayer();
        _ = RunFocusAsync(seq);
    }

    private async Task RunFocusAsync(long seq)
    {
        for (int step = BlurSteps - 1; step >= 1; step--)
        {
            await Task.Delay(BlurStepMs);
            if (seq != _focusSeq)
            {
                return; // 又切了图：这次对焦作废
            }

            _blurRadius = BlurMax * step / BlurSteps;
            UpdateLowLayer();
        }
    }

    private void FinishFocus()
    {
        if (LowImage.Source is null)
        {
            return;
        }

        _focusSeq++; // 停掉还在跑的分档对焦，改由淡出链收尾
        _ = RunFadeAsync(_focusSeq, _blurRadius);
    }

    private async Task RunFadeAsync(long seq, double startBlur)
    {
        for (int i = BlurFadeSteps - 1; i >= 1; i--)
        {
            await Task.Delay(BlurFadeStepMs);
            if (seq != _focusSeq)
            {
                return;
            }

            double k = (double)i / BlurFadeSteps;
            _lowOpacity = k;
            _blurRadius = startBlur * k;
            UpdateLowLayer();
        }

        ClearBlur();
    }

    private void UpdateLowLayer()
    {
        LowImage.Opacity = _lowOpacity;

        // GPU 可用时改用 CUDA 高斯模糊；否则沿用 WPF 的 BlurEffect（它本身也是 GPU shader）
        if (GpuResampler.GpuActive && _lowSource is not null && _blurRadius > 0.01)
        {
            // 低清图会被放大到画布尺寸显示，模糊半径要按同一个比例折算，
            // 否则「低清图上 14px 的模糊」在屏幕上会显得比 BlurEffect 的 14 更大。
            double scale = _lowSource.PixelWidth > 0 && FrameW > 0
                ? FrameW / _lowSource.PixelWidth
                : 1;
            double sigma = _blurRadius * Math.Clamp(scale, 0.25, 4.0);

            BitmapSource? blurred = CachedBlur(_lowSource, sigma);
            if (blurred is not null)
            {
                LowImage.Effect = null;
                LowImage.Source = blurred;
            }
            else
            {
                _lowBlur.Radius = _blurRadius;
                LowImage.Effect = _lowBlur;
            }
        }
        else
        {
            _lowBlur.Radius = _blurRadius;
            LowImage.Effect = _lowBlur;
        }

        UpdateTransform(); // focusZoom 变了，缩放要跟着同步
    }

    /// <summary>
    /// 模糊结果缓存：一次对焦动画只有 4 档半径，缓存后只在首帧真正调 GPU。
    /// 半径按 0.5 粒度量化，避免每帧都算新权重。
    /// </summary>
    private BitmapSource? CachedBlur(BitmapSource src, double sigma)
    {
        int key = Math.Clamp((int)Math.Round(sigma * 2), 1, 512);
        if (_blurCache.TryGetValue((src, key), out BitmapSource? hit))
        {
            return hit;
        }

        BitmapSource? made = GpuResampler.Blur(src, key / 2.0);
        if (made is null)
        {
            return null;
        }

        if (_blurCache.Count > 48)
        {
            _blurCache.Clear();
        }

        _blurCache[(src, key)] = made;
        return made;
    }

    private void ClearBlur()
    {
        _focusSeq++;
        _lowSource = null;
        LowImage.Source = null;
        LowImage.Opacity = 0;
        _lowBlur.Radius = 0;
        _blurRadius = 0;
        _lowOpacity = 0;
        _blurCache.Clear();
    }

    // ==================================================================
    // 后台预载
    // ==================================================================

    private void SchedulePreload()
    {
        _previews.EvictOutside(_items, _cur, PreloadBack, PreloadFwd, MainImage.Source, LowImage.Source);
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

                // 先补「前方」（主要浏览方向）
                for (int k = 1; k <= PreloadFwd; k++)
                {
                    int i = _cur + k;
                    if (i >= _items.Count)
                    {
                        break;
                    }

                    string u = _items[i].Path;
                    if (_previews.Get(u) is null && !_failed.Contains(u))
                    {
                        target = u;
                        break;
                    }
                }

                // 再补「后方」：否则快速往回滚时一张都命中不了缓存
                if (target is null)
                {
                    for (int k = 1; k <= PreloadBack; k++)
                    {
                        int i = _cur - k;
                        if (i < 0)
                        {
                            break;
                        }

                        string u = _items[i].Path;
                        if (_previews.Get(u) is null && !_failed.Contains(u))
                        {
                            target = u;
                            break;
                        }
                    }
                }

                if (target is null)
                {
                    return; // 窗口内都预载好了
                }

                string t = target;
                DecodedImage? entry = await Task.Run(() => Decoder.Decode(t, PreviewCache.PreloadEdge));
                if (mySeq != _preloadSeq)
                {
                    continue; // 期间又切了图 → 重新挑目标，别把链断掉
                }

                if (entry is not null)
                {
                    _previews.Add(entry, MainImage.Source, LowImage.Source);
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
            _ = LoadFolderAsync(path);
            return;
        }

        if (File.Exists(path))
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                _ = LoadFolderAsync(dir, path);
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

        await LoadFolderAsync(root);
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

        ApplyItems(dlg.FileNames.Select(p => new ImageItem(p)).ToList(), "选择的文件");
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

        _ = LoadFolderAsync(dlg.SelectedPath);
    }

    private async Task LoadFolderAsync(string root, string? focusPath = null)
    {
        Status = "正在扫描文件夹…";
        List<ImageItem>? found = await Task.Run(() => ScanFolder(root));

        if (found is null || found.Count == 0)
        {
            Status = $"「{root}」里没有找到受支持的图片";
            return;
        }

        string capped = found.Count >= ScanCap ? $"（已按 {ScanCap} 张截断）" : "";
        ApplyItems(found, Path.GetFileName(root).Length > 0 ? Path.GetFileName(root) : root, capped);

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

    /// <summary>递归扫描文件夹里的受支持图片，按修改时间倒序（最新在前）。</summary>
    private static List<ImageItem> ScanFolder(string root)
    {
        List<(string Path, long Size, DateTime Modified)> found = new();
        try
        {
            EnumerationOptions opt = new()
            {
                RecurseSubdirectories = true,
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

        // 最新在前（对应鸿蒙版 orderByDesc('date_added')）
        found.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return found.Select(f => new ImageItem(f.Path) { Size = f.Size }).ToList();
    }

    // ==================================================================
    // 交互：滚轮 / 鼠标 / 键盘
    // ==================================================================

    /// <summary>
    /// 滚轮翻页：去抖 + 合并。
    /// 不能「来一个事件就翻一张」—— 一个物理刻度会连发多个事件，
    /// 且快速滚动时事件频率远高于解码速度，逐张切换会让解码队列堆积，
    /// 表现就是图片错位。这里把窗口内攒下的步数合并成一次跳转，只渲染最终落点那一张。
    /// </summary>
    private void CanvasArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        _wheelCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // 约定：向上滚（Delta > 0）= 上一张
        int dir = e.Delta > 0 ? -1 : 1;

        // 翻页用的滚轮一动就把画布拉回正中；Ctrl + 滚轮是缩放，平移量要留着当锚点
        if (!_wheelCtrl)
        {
            _tx = 0;
            _ty = 0;
        }

        long now = Environment.TickCount64;
        if (dir == _lastWheelSign && now - _lastWheelMs < WheelNotchMs)
        {
            return; // 同一刻度的连发事件合并成一步
        }

        _lastWheelSign = dir;
        _lastWheelMs = now;
        _pendingDelta += dir;
        FlushWheel();
    }

    private void FlushWheel()
    {
        if (_pendingDelta == 0 || _wheelTimer.IsEnabled)
        {
            return;
        }

        long wait = WheelStepMs - (Environment.TickCount64 - _lastSwitchMs);
        if (wait > 0)
        {
            _wheelTimer.Interval = TimeSpan.FromMilliseconds(wait);
            _wheelTimer.Start();
            return;
        }

        int d = _pendingDelta;
        _pendingDelta = 0;
        _lastSwitchMs = Environment.TickCount64;

        if (_wheelCtrl)
        {
            ZoomBy(d > 0 ? 0.9 : 1.1);
        }
        else
        {
            Go(d);
        }
    }

    private void ClearWheel()
    {
        _wheelTimer.Stop();
        _pendingDelta = 0;
        _lastWheelSign = 0;
        _lastSwitchMs = 0;
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
    }

    // ==================================================================
    // 列表 / 拖放 / 按钮
    // ==================================================================

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSel)
        {
            return;
        }

        if (FileList.SelectedIndex >= 0 && FileList.SelectedIndex != _cur)
        {
            Select(FileList.SelectedIndex);
        }
    }

    /// <summary>
    /// 列表行被虚拟化建出来时触发缩略图请求 —— 等价于鸿蒙版 ArchiveRow.aboutToAppear。
    /// 只有屏幕上的这几行会走到这里，几百张图也不会一次性排队解码。
    /// </summary>
    private void FileItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem li || li.DataContext is not ImageItem item)
        {
            return;
        }

        BitmapSource? hit = ThumbLoader.Get().Peek(item.Path);
        if (hit is not null)
        {
            item.Thumb = hit;
            return;
        }

        long session = _session;
        ThumbLoader.Get().Request(item.Path, () => session == _session, bmp =>
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (session == _session && bmp is not null)
                {
                    item.Thumb = bmp;
                }
            }), DispatcherPriority.Background);
        });
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
            _ = LoadFolderAsync(first);
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

        ApplyItems(list, "拖放的文件");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopSlide();
        _posTimer.Stop();
        FlushPos();
    }

    private void ActFit(object sender, RoutedEventArgs e)
    {
        ResetView();
        Status = "已适应窗口";
    }

    private void ActGpu(object sender, RoutedEventArgs e) => ToggleGpu();

    /// <summary>切换 GPU / CPU 渲染路径，便于直接对比画质与流畅度。</summary>
    private void ToggleGpu()
    {
        GpuResampler.Enabled = !GpuResampler.Enabled;
        RefreshGpu();

        Status = GpuResampler.GpuActive
            ? $"GPU 加速已开启 · {GpuAccel.DeviceName}"
            : GpuAccel.Available
                ? "已切到 CPU 渲染（GPU 仍可用，Ctrl+G 切回）"
                : $"CPU 渲染 · {GpuAccel.FailReason}";

        _blurCache.Clear();
        ScheduleRefine();
    }

    private void RefreshGpu()
    {
        OnPropertyChanged(nameof(GpuLabel));
        OnPropertyChanged(nameof(GpuBackground));
        OnPropertyChanged(nameof(GpuForeground));
        OnPropertyChanged(nameof(GpuLine));
        OnPropertyChanged(nameof(GpuTip));
    }

    private void ActOne(object sender, RoutedEventArgs e) => ZoomPixelPerfect();
    private void ActZoomIn(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void ActZoomOut(object sender, RoutedEventArgs e) => ZoomBy(0.8);
    private void ActRotL(object sender, RoutedEventArgs e) => RotateBy(-90);
    private void ActRotR(object sender, RoutedEventArgs e) => RotateBy(90);
    private void ActFlipH(object sender, RoutedEventArgs e) => ToggleFlipH();
    private void ActFlipV(object sender, RoutedEventArgs e) => ToggleFlipV();
    private void ActSlide(object sender, RoutedEventArgs e) => ToggleSlide();
    private void ActFull(object sender, RoutedEventArgs e) => SetFull(!_fullscreen);
    private void ActResume(object sender, RoutedEventArgs e) => ResumeLast();
}
