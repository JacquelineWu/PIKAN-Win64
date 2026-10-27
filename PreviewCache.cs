using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// 后台预载缓存，与鸿蒙版 previews 数组对应。
///
/// 目的是「翻页不等解码」：后台把当前位置前后各 20 张**按可显示清晰度**解码并缓存，
/// 切图时命中就直接上主视图，不用再解码（这是载入快的关键）。
/// 缓存的清晰度档位 Edge 由主窗口按画布尺寸动态决定（见 MainWindow.PreloadTargetEdge）。
///
///   命中缓存   → 立即显示该图；档位不够清晰时再后台解一张更高清的覆盖（渐进式）
///   未命中     → 先解一张 768 低清占位，再后台解高清覆盖
///
/// 窗口跟着当前位置滑动（EvictOutside）：翻到哪缓存哪附近，远处立刻释放，
/// 于是翻一整个大图库内存也不随翻页累积。
///
/// 上限是「张数」与「总字节」双重保险：
///   容量必须大于窗口大小，否则每入一张就淘汰一张、又被预载链加回来，来回抖动。
/// </summary>
public sealed class PreviewCache
{
    public const int QuickEdge = 768;      // 未命中缓存时的低清占位档位（单张约 1.7MB）
    public const int MinEdge = 1024;       // 预载档位下限
    public const int MaxEdge = 1536;       // 预载档位上限：41 张 × 约 7MB ≈ 290MB，再大就吃不消了
    public const int Capacity = 48;        // 窗口 41 张，留 7 张余量
    public const long MaxBytes = 320L * 1024 * 1024;

    /// <summary>当前预载档位（长边像素）。画布尺寸变化时由主窗口更新。</summary>
    public int Edge { get; set; } = 1280;

    /// <summary>
    /// 关掉「窗口环绕」（仅用于 --nowrap 做 A/B 对比）。
    /// 打开后窗口按线性截断，跨首尾时会重新解码一批图。
    /// </summary>
    public static bool NoWrap;

    private readonly List<DecodedImage> _items = new();
    private long _bytes;

    /// <summary>查缓存；命中则移到队尾（LRU）。</summary>
    public DecodedImage? Get(string path)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DecodedImage e = _items[i];
            if (i != _items.Count - 1)
            {
                _items.RemoveAt(i);
                _items.Add(e);
            }

            return e;
        }

        return null;
    }

    /// <summary>入缓存，并按「张数 + 总字节」双重上限淘汰最旧的。</summary>
    public void Add(DecodedImage image, params ImageSource?[] protectedSources)
    {
        _items.Add(image);
        _bytes += image.Bytes;

        while ((_items.Count > Capacity || _bytes > MaxBytes) && _items.Count > 1)
        {
            DecodedImage oldest = _items[0];
            // 正在显示的不能淘汰：它是主视图 / 过渡层的图，丢掉会让画面变黑
            if (protectedSources.Any(s => s is not null && ReferenceEquals(s, oldest.Source)))
            {
                break;
            }

            _items.RemoveAt(0);
            _bytes -= oldest.Bytes;
        }
    }

    /// <summary>
    /// 释放「窗口外」的缓存 —— 让缓存真正跟着位置滑动。
    /// 只靠容量淘汰的话，跳转（回到上次 / 点列表跳很远）之后远处那批旧图还会赖着，
    /// 把新窗口的位置挤掉。
    /// </summary>
    public void EvictOutside(
        IReadOnlyList<ImageItem> items,
        int cur,
        int back,
        int fwd,
        params ImageSource?[] protectedSources)
    {
        if (cur < 0 || items.Count == 0)
        {
            return;
        }

        // 循环窗口：末尾的「前方」绕回开头，开头的「后方」绕回末尾。
        // Go() 本来就是模运算循环的，缓存窗口若还按线性截断，
        // 每次跨过首尾边界都要重新解码一批图。
        int n = items.Count;
        int b = NoWrap ? back : Math.Min(back, n - 1);
        int f = NoWrap ? fwd : Math.Min(fwd, n - 1);

        HashSet<string> window = new(StringComparer.OrdinalIgnoreCase);
        for (int k = -b; k <= f; k++)
        {
            int raw = cur + k;
            if (NoWrap && (raw < 0 || raw >= n))
            {
                continue;
            }

            window.Add(items[((raw % n) + n) % n].Path);
        }

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            DecodedImage e = _items[i];
            if (window.Contains(e.Path))
            {
                continue;
            }

            if (protectedSources.Any(s => s is not null && ReferenceEquals(s, e.Source)))
            {
                continue;
            }

            _items.RemoveAt(i);
            _bytes -= e.Bytes;
        }
    }

    /// <summary>清空缓存；正在显示的图可以保留（否则画面会变黑）。</summary>
    public void Clear(params ImageSource?[] protectedSources)
    {
        List<DecodedImage> keep = _items
            .Where(e => protectedSources.Any(s => s is not null && ReferenceEquals(s, e.Source)))
            .ToList();

        _items.Clear();
        _bytes = 0;

        foreach (DecodedImage k in keep)
        {
            _items.Add(k);
            _bytes += k.Bytes;
        }
    }

    public int Count => _items.Count;

    public long Bytes => _bytes;
}
