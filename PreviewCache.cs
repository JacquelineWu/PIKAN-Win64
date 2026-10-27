using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// 后台预载缓存（低清预览），与鸿蒙版 previews 数组对应。
///
/// 思路（和 ACDSee 的「先出缩略预览再 sharpen」一致）：
///   · 后台把当前位置前后各 20 张解成低清预览并缓存；
///   · 切到某张时缓存命中就立刻把预览画上去做「大模糊 → 对焦」，清晰图到了再覆盖；
///   · 窗口跟着当前位置滑动（EvictOutside）：翻到哪缓存哪附近，远处立刻释放，
///     于是翻一整个大图库内存也不随翻页累积。
///
/// 上限是「张数」与「总字节」双重保险：
///   容量必须大于窗口大小，否则每入一张就淘汰一张、又被预载链加回来，来回抖动。
/// </summary>
public sealed class PreviewCache
{
    public const int PreloadEdge = 768;    // 预载长边：768px 时单张约 1.7MB，41 张 ≈ 70MB
    public const int Capacity = 48;        // 窗口 41 张，留 7 张余量
    public const long MaxBytes = 96L * 1024 * 1024;

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

        int lo = Math.Max(0, cur - back);
        int hi = Math.Min(items.Count - 1, cur + fwd);
        HashSet<string> window = new(StringComparer.OrdinalIgnoreCase);
        for (int i = lo; i <= hi; i++)
        {
            window.Add(items[i].Path);
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

    public void Clear()
    {
        _items.Clear();
        _bytes = 0;
    }

    public int Count => _items.Count;

    public long Bytes => _bytes;
}
