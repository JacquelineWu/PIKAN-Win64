using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// 显存里的源图常驻缓存。
///
/// 没有它时，每次精修都要做两件重复的事：
///   ① 从 BitmapSource 把几 MB 像素 CopyPixels 到 CPU 缓冲；
///   ② 再把这几 MB 通过 PCIe 上传（H2D）到显存。
/// 同一张图来回翻，这两步就白做一遍又一遍。
///
/// 有了它：预载解码完顺便把 BGRA 传进显存并留在那儿，之后切图时
/// kernel 直接从这块显存取源，只剩「算 + 回传」——上传和 CPU 拷贝全省掉。
/// 再叠一层结果缓存（见 GpuResampler），同一张图同一尺寸连 kernel 都不用跑。
///
/// 淘汰策略与 PreviewCache 对齐：跟随当前位置的窗口滑动，
/// 且按显存配额做 LRU，翻一整个大图库显存也不会累积。
/// </summary>
public sealed class GpuImageCache
{
    private sealed class Entry
    {
        public required string Path { get; init; }
        public IntPtr Ptr;
        public int W;
        public int H;

        /// <summary>入缓存时的位图宽高（= 实际像素行列，kernel 要的就是这个）。</summary>
        public int Edge;
        public long Bytes;
    }

    /// <summary>总开关：关掉后不再往显存放图（--novram，用于 A/B 对比收益）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// 显存缓存最多能吃掉总显存的比例，默认 50%（--vram=N 覆盖，N 是百分比）。
    /// 翻过的图继续留在显存里，往回翻就是零成本；一直往前翻则由 LRU 挤掉最旧的。
    /// </summary>
    public static double Ratio { get; set; } = 0.5;

    /// <summary>
    /// 紧跟随窗口（--tightvram）：翻页时立刻释放窗口外的图。
    /// 默认关——那样缓存大小恒等于窗口大小（101 张 / 640MB），配额给到 50% 也用不上。
    /// 只作 A/B 对照用。
    /// </summary>
    public static bool TightWindow { get; set; }

    private readonly object _gate = new();

    // 头 = 最旧，尾 = 最近用过
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map =
        new(System.StringComparer.OrdinalIgnoreCase);

    private long _bytes;
    private long _maxBytes;
    private long _maxStamp = long.MinValue;

    public static readonly GpuImageCache Instance = new();

    private GpuImageCache() { }

    private long MaxBytes
    {
        get
        {
            // GPU 还没就绪时绝不查显存：TotalMemBytes 会走 Ensure() 触发 CUDA 初始化，
            // 在 UI 线程上就是 1~2 秒的冻结。用上次算过的值顶着，没有就先给个小的。
            if (!GpuResampler.GpuReady)
            {
                return _maxBytes > 0 ? _maxBytes : 192L * 1024 * 1024;
            }

            // 配额不一次性定死：浏览器、游戏随时在吃显存，
            // 隔一小段重算一次，才能既用满又不跟别人抢。
            long now = Environment.TickCount64;
            if (_maxBytes > 0 && now - _maxStamp < 500)
            {
                return _maxBytes;
            }

            _maxStamp = now;

            ulong total = GpuAccel.TotalMemBytes;
            if (total == 0)
            {
                _maxBytes = 256L * 1024 * 1024;
                return _maxBytes;
            }

            long want = (long)(total * Math.Clamp(Ratio, 0.05, 0.8));
            long ceiling = (long)(total * 0.8);

            // 给系统和其他程序留 1GB 安全垫：空闲不够就往回收。
            // 否则把显存吃光会让 CUDA 分配失败，整条加速路径反而回落。
            ulong free = GpuAccel.FreeMemBytes;
            if (free > 0)
            {
                long others = (long)total - (long)free - _bytes;   // 别人占的
                want = Math.Min(want, ceiling - others);
            }

            // 下限 640MB ≈ 一个满档预载窗口（101 张 / 610MB）。低于这个数窗口里的图
            // 就会被 LRU 挤掉，翻回去还得重新上传，反而更卡 —— 显存再紧也先保住窗口。
            // 下限不能超过配额上限（小显存机器），所以先跟 ceiling 取小。
            long floor = Math.Min(640L * 1024 * 1024, ceiling);
            _maxBytes = Math.Clamp(want, floor, ceiling);
            return _maxBytes;
        }
    }

    public int Count
    {
        get { lock (_gate) { return _map.Count; } }
    }

    public long Bytes
    {
        get { lock (_gate) { return _bytes; } }
    }

    /// <summary>当前显存配额（MB）。会随空闲显存变化，仅供状态栏 / bench 展示。</summary>
    public long QuotaMb
    {
        get { return MaxBytes / (1024 * 1024); }
    }

    /// <summary>
    /// 查显存里的源图。edge 不一致视为未命中——档位变了说明旧图的清晰度
    /// 已经不是当前要用的那张，留着只会占着显存。
    /// </summary>
    public bool TryGet(string path, int edge, out IntPtr ptr, out int w, out int h)
    {
        ptr = IntPtr.Zero;
        w = 0;
        h = 0;

        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_map.TryGetValue(path, out LinkedListNode<Entry>? node))
            {
                return false;
            }

            if (node.Value.Edge != edge)
            {
                return false;
            }

            _lru.Remove(node);
            _lru.AddLast(node);   // 命中即刷新 LRU 位置

            ptr = node.Value.Ptr;
            w = node.Value.W;
            h = node.Value.H;
            return true;
        }
    }

    /// <summary>
    /// 把一张已解码的位图传进显存并常驻。已在里面则只刷新 LRU。
    /// 失败（显存不足 / GPU 不可用）一律安静返回 false，调用方继续走常规路径。
    /// </summary>
    public bool Put(string path, BitmapSource src, int edge)
    {
        // 用 GpuReady（不触发初始化）：预载线程不该把 1~2 秒的 CUDA init 抢到自己身上，
        // 更不该让同时读 GpuActive 的 UI 线程被堵住
        if (!Enabled || !GpuResampler.GpuReady || src.PixelWidth <= 0 || src.PixelHeight <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_map.TryGetValue(path, out LinkedListNode<Entry>? node) && node.Value.Edge == edge)
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                return true;
            }
        }

        byte[]? pixels = GpuResampler.Pixels(src);
        if (pixels is null)
        {
            return false;
        }

        long bytes = (long)src.PixelWidth * src.PixelHeight * 4;
        if (!GpuAccel.DevAlloc((ulong)bytes, out IntPtr dev) || dev == IntPtr.Zero)
        {
            return false;
        }

        if (!GpuAccel.DevUpload(dev, pixels, (int)bytes))
        {
            GpuAccel.DevFree(dev);
            return false;
        }

        Entry e = new()
        {
            Path = path,
            Ptr = dev,
            W = src.PixelWidth,
            H = src.PixelHeight,
            Edge = edge,
            Bytes = bytes
        };

        lock (_gate)
        {
            // 并发下可能已被别的线程放入同一张图，那就释放刚上传的这块，别泄漏
            if (_map.TryGetValue(path, out LinkedListNode<Entry>? exist))
            {
                if (exist.Value.Edge == edge)
                {
                    GpuAccel.DevFree(dev);
                    _lru.Remove(exist);
                    _lru.AddLast(exist);
                    return true;
                }

                Drop(exist);
            }

            LinkedListNode<Entry> node = _lru.AddLast(e);
            _map[path] = node;
            _bytes += bytes;

            while (_bytes > MaxBytes && _lru.Count > 1)
            {
                Drop(_lru.First!);
            }
        }

        GpuAccel.NoteUpload(e.W, e.H, bytes);
        return true;
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(path, out LinkedListNode<Entry>? node))
            {
                Drop(node);
            }
        }
    }

    /// <summary>释放不在窗口内的图（跟随位置滑动，与 PreviewCache.EvictOutside 同步调用）。</summary>
    public void EvictOutside(IReadOnlyCollection<string> keep)
    {
        HashSet<string> set = new(keep, System.StringComparer.OrdinalIgnoreCase);
        List<string> victims = new();

        lock (_gate)
        {
            foreach (Entry e in _lru)
            {
                if (!set.Contains(e.Path))
                {
                    victims.Add(e.Path);
                }
            }

            foreach (string p in victims)
            {
                if (_map.TryGetValue(p, out LinkedListNode<Entry>? node))
                {
                    Drop(node);
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            while (_lru.First is not null)
            {
                Drop(_lru.First);
            }
        }
    }

    /// <summary>释放一块显存并从索引里摘掉；只在持有 _gate 时调用。</summary>
    private void Drop(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);
        _map.Remove(node.Value.Path);
        _bytes -= node.Value.Bytes;
        GpuAccel.DevFree(node.Value.Ptr);
        node.Value.Ptr = IntPtr.Zero;
    }
}
