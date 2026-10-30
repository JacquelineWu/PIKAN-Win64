using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// 鼠标悬停左侧档案名时那张小缩略图的来源。
///
/// 三级取图，优先白拿、实在没有才解码：
///   ① 主预载缓存 PreviewCache：当前位置前后各 50 张已经按可显示清晰度解好了，
///      直接把那张位图交给缩略图框（WPF 负责缩小），同步命中，鼠标一到图就出来
///   ② 悬停专用小缓存：只有 384 边长的小图，24 张 LRU，约 10 MB
///   ③ 都没有 → 后台解一张 384 的小图，解完进 ②
///
/// 为什么不直接把缩略图塞进主缓存：主缓存是按「当前档位 Edge」记的（1280~1536），
/// 混进 384 的小图会污染容量与 LRU，还会让主视图误判「这张已经缓存了」而先显示一张糊的。
/// 所以缩略图自己有一个小缓存，且解码很小，划过上百行也不会拖慢翻页。
/// </summary>
public sealed class HoverThumbCache
{
    /// <summary>缩略图解码档位（长边像素）。220 DIP × 1.5 DPI 也够用。</summary>
    public const int ThumbEdge = 384;

    private const int Capacity = 24;

    private readonly Dictionary<string, BitmapSource> _map = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    public static HoverThumbCache Instance { get; } = new();

    private HoverThumbCache() { }

    /// <summary>同步查一次（主缓存 / 小缓存），命中就返回，不解码。</summary>
    public BitmapSource? Peek(string path, PreviewCache previews, bool exifRotate)
    {
        DecodedImage? hit = previews.Get(path);
        if (hit is not null)
        {
            return Orient(hit.Source, hit.ExifOrientation, exifRotate);
        }

        if (_map.TryGetValue(path, out BitmapSource? cached))
        {
            Touch(path);
            return cached;
        }

        return null;
    }

    /// <summary>
    /// 取一张缩略图；缓存没命中就后台解一张小的。
    /// 调用方负责丢弃过期结果（鼠标可能早就划到别的行了）。
    /// </summary>
    public async Task<BitmapSource?> GetAsync(string path, PreviewCache previews, bool exifRotate)
    {
        BitmapSource? fast = Peek(path, previews, exifRotate);
        if (fast is not null)
        {
            return fast;
        }

        DecodedImage? d = await Task.Run(() => Decoder.Decode(path, ThumbEdge));
        if (d is null)
        {
            return null;
        }

        // await 之后已回到 UI 线程：旋转 / 冻结 / 入缓存都在这里做，位图不必跨线程
        BitmapSource src = Orient(d.Source, d.ExifOrientation, exifRotate);
        if (src.CanFreeze)
        {
            src.Freeze();
        }

        Add(path, src);
        return src;
    }

    /// <summary>
    /// 按 EXIF 方向摆正。主视图是把旋转交给 RenderTransform 做的，
    /// 缩略图这里直接烘进位图，免得为了它再挂一套变换（也免得布局算错宽高）。
    /// </summary>
    private static BitmapSource Orient(BitmapSource src, int orientation, bool exifRotate)
    {
        if (!exifRotate)
        {
            return src;
        }

        (int rot, bool flipH) = ExifOrientation.ToTransform(orientation);
        if (rot == 0 && !flipH)
        {
            return src;
        }

        bool swap = rot is 90 or 270;
        int w = swap ? src.PixelHeight : src.PixelWidth;
        int h = swap ? src.PixelWidth : src.PixelHeight;
        double cx = src.PixelWidth / 2.0;
        double cy = src.PixelHeight / 2.0;

        // 变换顺序：源图中心搬到原点 → 镜像 → 旋转 → 搬到输出中心
        TransformGroup g = new();
        g.Children.Add(new TranslateTransform(-cx, -cy));
        if (flipH)
        {
            g.Children.Add(new ScaleTransform(-1, 1));
        }

        if (rot != 0)
        {
            g.Children.Add(new RotateTransform(rot));
        }

        g.Children.Add(new TranslateTransform(w / 2.0, h / 2.0));
        g.Freeze();

        DrawingVisual dv = new();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.PushTransform(g);
            dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
        }

        RenderTargetBitmap rtb = new(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private void Touch(string path)
    {
        LinkedListNode<string>? node = _lru.Find(path);
        if (node is null)
        {
            return;
        }

        _lru.Remove(node);
        _lru.AddLast(path);
    }

    private void Add(string path, BitmapSource src)
    {
        _map[path] = src;
        _lru.AddLast(path);
        Touch(path);

        while (_lru.Count > Capacity)
        {
            string oldest = _lru.First!.Value;
            _lru.RemoveFirst();
            _map.Remove(oldest);
        }
    }

    /// <summary>换目录 / 清预载时一起清掉，避免旧图占内存。</summary>
    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
    }
}
