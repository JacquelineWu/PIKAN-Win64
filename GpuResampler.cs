using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// GPU 加速的上层入口：在 BitmapSource 与 CUDA 之间做桥接，并在 GPU 不可用时回落 WIC。
///
/// 两条调用路径：
///   Resize(BitmapSource, w, h) → GPU Mitchell 重采样；失败则用 WIC 的 TransformedBitmap。
///   Blur(BitmapSource, radius) → GPU 分离高斯；失败返回 null，调用方继续用 WPF 的 BlurEffect。
///
/// 所有返回值都已 Freeze，可跨线程交给 UI 线程绑定。
/// </summary>
public static class GpuResampler
{
    /// <summary>总开关：默认开。关掉后全部走 CPU/WIC 路径，便于对比效果。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>GPU 是否可用（会触发 CUDA 初始化，只在工作线程调用）。</summary>
    public static bool GpuActive => Enabled && GpuAccel.Available;

    /// <summary>
    /// 同 GpuActive，但**不触发初始化**：CUDA 初始化要 1~2 秒，UI 线程读取会整段卡住。
    /// 界面展示一律用这个，初始化交给后台，完成后再刷新。
    /// </summary>
    public static bool GpuReady => Enabled && GpuAccel.IsReady;

    /// <summary>状态栏用的一行说明（不触发初始化；初始化中显示「正在检测」）。</summary>
    public static string StatusTextReady =>
        !Enabled ? $"CPU 渲染（{CpuResampler.ModeName}）"
        : GpuAccel.IsReady ? $"CUDA · {GpuAccel.DeviceNameOrEmpty}"
        : "CPU 渲染（正在检测 GPU…）";

    // ==================================================================
    // 像素进出
    // ==================================================================

    private static int _diag;

    private static void Diag(string line)
    {
        if (_diag >= 6)
        {
            return;
        }

        _diag++;
        GpuAccel.WriteLog("resampler: " + line);
    }

    /// <summary>统一转成 BGRA32 再取像素，保证与 PTX kernel 的内存布局一致。</summary>
    public static BitmapSource? ToBgra(BitmapSource src)
    {
        if (src.Format == PixelFormats.Bgra32)
        {
            return src;
        }

        try
        {
            FormatConvertedBitmap c = new(src, PixelFormats.Bgra32, null, 0);
            c.Freeze();
            return c;
        }
        catch (Exception ex)
        {
            Diag($"ToBgra 失败 {src.Format} → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>拷出 BGRA32 像素；格式转换失败返回 null。</summary>
    public static byte[]? Pixels(BitmapSource src)
    {
        BitmapSource? bgra = ToBgra(src);
        if (bgra is null)
        {
            return null;
        }

        int stride = bgra.PixelWidth * 4;
        byte[] buf = new byte[stride * bgra.PixelHeight];
        try
        {
            bgra.CopyPixels(buf, stride, 0);
        }
        catch (Exception ex)
        {
            Diag($"CopyPixels 失败 {src.Format} → {bgra.Format} {bgra.PixelWidth}x{bgra.PixelHeight} " +
                 $"stride={stride} buf={buf.Length} · {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        return buf;
    }

    /// <summary>把 BGRA32 像素包成可跨线程使用的 BitmapSource。</summary>
    public static BitmapSource FromBgra(byte[] buf, int w, int h)
    {
        BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // ==================================================================
    // 重采样
    // ==================================================================

    /// <summary>
    /// 高质量重采样到 ow×oh。GPU 走 Mitchell-Netravali（area sampling，抗走样），
    /// 回落走 WIC 的 TransformedBitmap（双线性/ Fant，质量略逊但零成本）。
    /// </summary>
    public static BitmapSource? Resize(BitmapSource src, int ow, int oh)
    {
        if (ow <= 0 || oh <= 0)
        {
            return null;
        }

        if (src.PixelWidth == ow && src.PixelHeight == oh)
        {
            return src;   // 尺寸已一致，无需重采样
        }

        if (GpuActive)
        {
            byte[]? inBuf = Pixels(src);
            if (inBuf is not null)
            {
                byte[] outBuf = new byte[ow * oh * 4];
                if (GpuAccel.Resize(inBuf, src.PixelWidth, src.PixelHeight, outBuf, ow, oh, true))
                {
                    return FromBgra(outBuf, ow, oh);
                }
            }
        }

        // GPU 不可用：走 CPU 的 AVX2 Mitchell（画质与 GPU 版一致），再不行才回 WIC 双线性
        return CpuResize(src, ow, oh) ?? WicResize(src, ow, oh);
    }

    /// <summary>CPU 侧高质量重采样：把 BitmapSource 转成 BGRA 后交给 CpuResampler。</summary>
    private static BitmapSource? CpuResize(BitmapSource src, int ow, int oh)
    {
        if (!CpuResampler.Enabled)
        {
            return null;
        }

        byte[]? inBuf = Pixels(src);
        if (inBuf is null)
        {
            return null;
        }

        byte[] outBuf = new byte[ow * oh * 4];
        return CpuResampler.Resize(inBuf, src.PixelWidth, src.PixelHeight, outBuf, ow, oh)
            ? FromBgra(outBuf, ow, oh)
            : null;
    }

    // ==================================================================
    // 显存常驻 + 结果缓存
    //
    // 切图时「尽量不要再计算」的两层保障：
    //   ① 源图常驻显存（GpuImageCache）：省掉每次的 CopyPixels + H2D 上传
    //   ② 结果缓存：同一张图 + 同一目标尺寸，直接把上次算好的位图拿出来，
    //      kernel 都不跑——来回翻页 / 缩放回原位时是真正的零计算
    // ==================================================================

    private const int ResultCapacity = 12;   // 12 张 × 约 4MB ≈ 48MB 主机内存

    /// <summary>结果缓存开关（--novram 时一起关掉，便于 A/B 对比）。</summary>
    public static bool ResultCacheEnabled { get; set; } = true;

    /// <summary>上一次重采样走的哪条路（"结果缓存" / "显存命中" / "上传后算" / "CPU/WIC"），诊断用。</summary>
    public static string LastFrom { get; private set; } = "";

    private static readonly object ResultGate = new();

    private static readonly LinkedList<KeyValuePair<string, BitmapSource>> ResultLru = new();

    private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> ResultMap = new();

    /// <summary>
    /// 走缓存的重采样：结果命中 → 显存命中 → 上传后命中 → 常规路径。
    /// 尺寸恰好一致时直接返回原位图。
    /// </summary>
    public static BitmapSource? ResizeCached(string path, BitmapSource src, int ow, int oh, int edge)
    {
        if (ow <= 0 || oh <= 0)
        {
            return null;
        }

        if (src.PixelWidth == ow && src.PixelHeight == oh)
        {
            return src;
        }

        if (ResultCacheEnabled)
        {
            BitmapSource? cached = TryResult(path, src, ow, oh);
            if (cached is not null)
            {
                return cached;
            }
        }

        LastFrom = "";

        BitmapSource? made = null;

        if (GpuActive)
        {
            if (GpuImageCache.Instance.TryGet(path, edge, out IntPtr dev, out int iw, out int ih))
            {
                GpuAccel.NoteHit();
                made = FromDevice(dev, iw, ih, ow, oh);   // ① 源已在显存
                LastFrom = made is not null ? "显存命中" : "显存命中但失败";
            }
            else if (GpuImageCache.Instance.Put(path, src, edge) &&
                     GpuImageCache.Instance.TryGet(path, edge, out IntPtr d2, out int iw2, out int ih2))
            {
                made = FromDevice(d2, iw2, ih2, ow, oh);  // 首次：传进显存后照常算
                LastFrom = made is not null ? "上传后算" : "上传后失败";
            }
            else
            {
                byte[]? inBuf = Pixels(src);
                if (inBuf is not null)
                {
                    byte[] outBuf = new byte[ow * oh * 4];
                    if (GpuAccel.Resize(inBuf, src.PixelWidth, src.PixelHeight, outBuf, ow, oh, true))
                    {
                        made = FromBgra(outBuf, ow, oh);
                    }
                }

                LastFrom = made is not null ? "常规上传" : "上传失败";
            }
        }

        if (made is null)
        {
            made = CpuResize(src, ow, oh);          // GPU 不可用 → AVX2 Mitchell
            if (made is not null)
            {
                LastFrom = "CPU/" + CpuResampler.ModeName;
            }
        }

        made ??= WicResize(src, ow, oh);            // 最后才是 WIC 双线性

        if (LastFrom.Length == 0)
        {
            LastFrom = made is not null ? "CPU/WIC" : "失败";
        }

        if (made is not null && ResultCacheEnabled)
        {
            string key = $"{path}|{src.PixelWidth}x{src.PixelHeight}|{ow}x{oh}";
            lock (ResultGate)
            {
                LinkedListNode<KeyValuePair<string, BitmapSource>> node =
                    ResultLru.AddLast(new KeyValuePair<string, BitmapSource>(key, made));
                ResultMap[key] = node;

                while (ResultLru.Count > ResultCapacity)
                {
                    LinkedListNode<KeyValuePair<string, BitmapSource>> oldest = ResultLru.First!;
                    ResultLru.RemoveFirst();
                    ResultMap.Remove(oldest.Value.Key);
                }
            }
        }

        return made;
    }

    /// <summary>
    /// 查结果缓存：同一张图 + 同一目标尺寸，直接把上次算好的位图拿出来，连 kernel 都不跑。
    /// 命中返回位图，未命中返回 null。
    /// </summary>
    public static BitmapSource? TryResult(string path, BitmapSource src, int ow, int oh)
    {
        if (!ResultCacheEnabled)
        {
            return null;
        }

        string key = $"{path}|{src.PixelWidth}x{src.PixelHeight}|{ow}x{oh}";
        lock (ResultGate)
        {
            if (ResultMap.TryGetValue(key, out LinkedListNode<KeyValuePair<string, BitmapSource>>? node))
            {
                ResultLru.Remove(node);
                ResultLru.AddLast(node);
                LastFrom = "结果缓存";   // 零计算
                return node.Value.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// 只算不回读：结果留在显存里，随后由 UI 线程写进复用的 WriteableBitmap。
    /// 省掉「中间 byte[] + BitmapSource.Create 再拷一遍」这两道。
    /// 尺寸恰好一致时不必重采样，返回 false。
    /// </summary>
    public static bool ResizeToDevice(string path, BitmapSource src, int ow, int oh, int edge)
    {
        if (ow <= 0 || oh <= 0 || !GpuActive)
        {
            return false;
        }

        if (src.PixelWidth == ow && src.PixelHeight == oh)
        {
            return false;   // 已经是 1:1，无需重采样
        }

        // ① 源已在显存 —— 连上传都省了
        if (GpuImageCache.Instance.TryGet(path, edge, out IntPtr dev, out int iw, out int ih))
        {
            GpuAccel.NoteHit();
            LastFrom = "显存命中";
            return GpuAccel.ResizeToDevice(null, dev, iw, ih, ow, oh);
        }

        // ② 首次：传进显存后照常算（传进去的这份会常驻，下次就走 ①）
        if (GpuImageCache.Instance.Put(path, src, edge) &&
            GpuImageCache.Instance.TryGet(path, edge, out IntPtr d2, out int iw2, out int ih2))
        {
            LastFrom = "上传后算";
            return GpuAccel.ResizeToDevice(null, d2, iw2, ih2, ow, oh);
        }

        // ③ 常规：从主机上传
        byte[]? inBuf = Pixels(src);
        if (inBuf is null)
        {
            return false;
        }

        LastFrom = "常规上传";
        return GpuAccel.ResizeToDevice(inBuf, IntPtr.Zero, src.PixelWidth, src.PixelHeight, ow, oh);
    }

    /// <summary>
    /// 零拷贝版：结果直接写进 D3D9 纹理，全程不回主机内存。
    /// res 由 D3dView 提供（已登记给 CUDA 的那张纹理）。
    /// 成功返回 true，调用方届时只需给 D3DImage 标一次脏区。
    /// </summary>
    public static bool ResizeToTexture(string path, BitmapSource src, IntPtr res, int ow, int oh, int edge)
    {
        if (ow <= 0 || oh <= 0 || res == IntPtr.Zero || !GpuActive)
        {
            return false;
        }

        if (src.PixelWidth == ow && src.PixelHeight == oh)
        {
            return false;   // 已经是 1:1，无需重采样
        }

        // ① 源已在显存 —— 连上传都省了
        if (GpuImageCache.Instance.TryGet(path, edge, out IntPtr dev, out int iw, out int ih))
        {
            GpuAccel.NoteHit();
            LastFrom = "显存命中";
            return GpuAccel.ResizeToTexture(null, dev, iw, ih, res, ow, oh);
        }

        // ② 首次：传进显存后照常算（传进去的这份会常驻，下次就走 ①）
        if (GpuImageCache.Instance.Put(path, src, edge) &&
            GpuImageCache.Instance.TryGet(path, edge, out IntPtr d2, out int iw2, out int ih2))
        {
            LastFrom = "上传后算";
            return GpuAccel.ResizeToTexture(null, d2, iw2, ih2, res, ow, oh);
        }

        // ③ 常规：从主机上传
        byte[]? inBuf = Pixels(src);
        if (inBuf is null)
        {
            return false;
        }

        LastFrom = "常规上传";
        return GpuAccel.ResizeToTexture(inBuf, IntPtr.Zero, src.PixelWidth, src.PixelHeight, res, ow, oh);
    }

    private static BitmapSource? FromDevice(IntPtr dev, int iw, int ih, int ow, int oh)
    {
        byte[] outBuf = new byte[ow * oh * 4];
        return GpuAccel.ResizeDevice(dev, iw, ih, outBuf, ow, oh, true)
            ? FromBgra(outBuf, ow, oh)
            : null;
    }

    /// <summary>换图库 / 清空缓存时把结果缓存一起清掉，避免旧图白占内存。</summary>
    public static void ClearResults()
    {
        lock (ResultGate)
        {
            ResultLru.Clear();
            ResultMap.Clear();
        }
    }

    internal static BitmapSource? WicResize(BitmapSource src, int ow, int oh)
    {
        try
        {
            TransformedBitmap tb = new(src, new ScaleTransform(
                ow / (double)src.PixelWidth, oh / (double)src.PixelHeight));
            tb.Freeze();
            return tb;
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================
    // 模糊
    // ==================================================================

    /// <summary>
    /// 高斯模糊。radius 语义与 WPF BlurEffect 的 Radius 对齐（≈ 标准差 σ）。
    /// GPU 走分离高斯，不可用时有 AVX2 版本顶上；两条路都失败返回 null，
    /// 调用方回落到 WPF 的 BlurEffect。
    /// </summary>
    public static BitmapSource? Blur(BitmapSource src, double radius)
    {
        if (radius <= 0.01 || src.PixelWidth <= 0 || src.PixelHeight <= 0)
        {
            return null;
        }

        if (GpuActive)
        {
            byte[]? inBuf = Pixels(src);
            if (inBuf is not null)
            {
                byte[] tmp = new byte[inBuf.Length];
                byte[] outBuf = new byte[inBuf.Length];
                if (GpuAccel.Blur(inBuf, tmp, outBuf, src.PixelWidth, src.PixelHeight, radius))
                {
                    return FromBgra(outBuf, src.PixelWidth, src.PixelHeight);
                }
            }
        }

        // CPU 版：同样是分离高斯，AVX2 实现
        byte[]? cpu = Pixels(src);
        if (cpu is null)
        {
            return null;
        }

        byte[] dst = new byte[cpu.Length];
        return CpuResampler.Blur(cpu, null, dst, src.PixelWidth, src.PixelHeight, radius)
            ? FromBgra(dst, src.PixelWidth, src.PixelHeight)
            : null;
    }
}
