using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// 列表缩略图加载器（进程级单例：跨数据源切换保留缓存，靠 LRU 自然淘汰）。
///
/// 与鸿蒙版 ThumbLoader 一一对应：
///   · 统一先解出「小图」再显示 —— 列表格只有 56×42，为显示它而把 4000×3000 的原图
///     解码进内存（≈48MB）是「图片一多就崩」的主因；128px 长边单张约 60KB。
///   · 限并发（HEIC 解码较重，同时开太多会瞬间吃满内存）
///   · 限队列（快速滑动会堆积大量「已划走」的请求，超了丢最旧的）
///   · 刻意不做整体清空：被释放的 BitmapSource 若正被某行显示会导致花屏，
///     而 LRU 淘汰的总是「最久没人用」的，那时对应行早已离屏。
/// </summary>
public sealed class ThumbLoader
{
    public const int ThumbEdge = 128;      // 缩略图长边（列表显示区 56×42，128 在高分屏仍清晰）
    private const int MaxCount = 600;       // 128px 时单张约 60KB → 600 张 ≈ 36MB
    private const int Concurrency = 3;      // 解码并发上限
    private const int QueueMax = 240;       // 待处理队列上限

    private static readonly ThumbLoader Instance = new();

    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();   // 队头最旧，队尾最新
    private readonly LinkedList<ThumbTask> _pending = new();
    private int _active;

    private ThumbLoader()
    {
    }

    public static ThumbLoader Get() => Instance;

    private sealed record ThumbTask(string Path, Func<bool> Alive, Action<BitmapSource?> Callback);

    /// <summary>同步查缓存；命中则刷新最近使用位置。</summary>
    public BitmapSource? Peek(string path)
    {
        lock (Instance)
        {
            if (!_cache.TryGetValue(path, out BitmapSource? hit))
            {
                return null;
            }

            Touch(path);
            return hit;
        }
    }

    /// <summary>请求一张缩略图：命中立刻回调，否则排进限流队列。</summary>
    public void Request(string path, Func<bool> alive, Action<BitmapSource?> callback)
    {
        BitmapSource? hit = Peek(path);
        if (hit is not null)
        {
            callback(hit);
            return;
        }

        lock (Instance)
        {
            _pending.AddLast(new ThumbTask(path, alive, callback));
            while (_pending.Count > QueueMax)
            {
                _pending.RemoveFirst();
            }
        }

        Pump();
    }

    private void Pump()
    {
        while (true)
        {
            ThumbTask? task;
            lock (Instance)
            {
                if (_active >= Concurrency || _pending.Count == 0)
                {
                    return;
                }

                task = _pending.First!.Value;
                _pending.RemoveFirst();
                _active++;
            }

            if (!task.Alive())
            {
                lock (Instance)
                {
                    _active--;
                }

                continue; // 行已经划出屏幕，这个请求作废
            }

            string p = task.Path;
            _ = Task.Run(() =>
            {
                DecodedImage? decoded = Decoder.Decode(p, ThumbEdge);
                lock (Instance)
                {
                    _active--;
                    if (decoded is not null)
                    {
                        Put(p, decoded.Source);
                    }
                }

                if (task.Alive())
                {
                    task.Callback(decoded?.Source);
                }

                Pump();
            });
        }
    }

    private void Put(string path, BitmapSource bmp)
    {
        _cache[path] = bmp;
        Touch(path);
        while (_order.Count > MaxCount)
        {
            string oldest = _order.First!.Value;
            _order.RemoveFirst();
            _cache.Remove(oldest);
            // BitmapSource 已 Freeze 且不可变，交给 GC 即可，不需要显式 release
        }
    }

    /// <summary>把 path 提到「最近使用」队尾。</summary>
    private void Touch(string path)
    {
        LinkedListNode<string>? node = _order.Find(path);
        if (node is not null)
        {
            _order.Remove(node);
        }

        _order.AddLast(path);
    }
}
