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

    public static bool GpuActive => Enabled && GpuAccel.Available;

    /// <summary>状态栏用的一行说明。</summary>
    public static string StatusText =>
        !Enabled ? "GPU 已关闭（CPU 渲染）"
        : GpuAccel.Available ? $"CUDA · {GpuAccel.DeviceName}"
        : $"CPU 渲染（{GpuAccel.FailReason}）";

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

        return WicResize(src, ow, oh);
    }

    private static BitmapSource? WicResize(BitmapSource src, int ow, int oh)
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
    /// GPU 不可用时返回 null —— 调用方应保留原有的 BlurEffect 路径。
    /// </summary>
    public static BitmapSource? Blur(BitmapSource src, double radius)
    {
        if (!GpuActive || radius <= 0.01 || src.PixelWidth <= 0 || src.PixelHeight <= 0)
        {
            return null;
        }

        byte[]? inBuf = Pixels(src);
        if (inBuf is null)
        {
            return null;
        }

        byte[] tmp = new byte[inBuf.Length];
        byte[] outBuf = new byte[inBuf.Length];
        if (!GpuAccel.Blur(inBuf, tmp, outBuf, src.PixelWidth, src.PixelHeight, radius))
        {
            return null;
        }

        return FromBgra(outBuf, src.PixelWidth, src.PixelHeight);
    }
}
