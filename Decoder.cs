using System.IO;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>解码结果：位图 + 原图宽高 + 估算占用字节。</summary>
public sealed class DecodedImage
{
    public required string Path { get; init; }

    /// <summary>已 Freeze 的位图，可跨线程交给 UI 显示。</summary>
    public required BitmapSource Source { get; init; }

    /// <summary>原图宽（用于 1:1 与信息栏，不是位图宽）。</summary>
    public required int Width { get; init; }

    /// <summary>原图高。</summary>
    public required int Height { get; init; }

    public long Bytes { get; init; }
}

/// <summary>
/// 图片解码（走 Windows Imaging Component，与鸿蒙版「系统 codec」对位）。
///
/// HEIC / HEIF：依赖系统装的 “HEIF Image Extensions”（Microsoft Store）。
///   本机已装（Microsoft.HEIFImageExtension），故无需 libheif 之类的第三方库；
///   没装的机器上 HEIC 会解码失败，其余格式不受影响 —— 与鸿蒙版 onError 的降级行为一致。
/// WEBP：同理依赖 “Webp Image Extensions”。
///
/// 关键：DecodePixelWidth / DecodePixelHeight 让 WIC 在解码阶段就降采样，
/// 不会先解出整张原图再缩放 —— 这才是「图片一多不 OOM」的前提。
/// </summary>
public static class Decoder
{
    public static readonly string[] Suffixes =
    {
        ".heic", ".heif", ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };

    private static readonly HashSet<string> SuffixSet =
        new(Suffixes, StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path) =>
        SuffixSet.Contains(System.IO.Path.GetExtension(path));

    /// <summary>OpenFileDialog 的 Filter 串。</summary>
    public static string DialogFilter
    {
        get
        {
            string img = string.Join(";", Suffixes.Select(s => "*" + s));
            return $"所有文件 (*.*)|*.*|图片 ({img})|{img}";
        }
    }

    /// <summary>只读文件头拿原始像素尺寸，不做完整解码。</summary>
    public static (int W, int H) Probe(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        BitmapDecoder decoder = BitmapDecoder.Create(
            fs,
            BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);

        if (decoder.Frames.Count == 0)
        {
            return (0, 0);
        }

        BitmapFrame frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>
    /// 按给定长边上限解码一张图，失败返回 null。
    /// edge = 768  → 低清预览（预载 / 切图过渡层）
    /// edge = 128  → 列表缩略图
    /// edge = 画布长边×2 → 主视图清晰图
    /// </summary>
    public static DecodedImage? Decode(string path, int edge)
    {
        int w;
        int h;
        try
        {
            (w, h) = Probe(path);
        }
        catch
        {
            return null;
        }

        if (w <= 0 || h <= 0)
        {
            return null;
        }

        int dw = w;
        int dh = h;
        if (w > edge || h > edge)
        {
            double k = Math.Max(w, h) / (double)edge;
            dw = Math.Max(1, (int)(w / k));
            dh = Math.Max(1, (int)(h / k));
        }

        try
        {
            BitmapImage bi = new();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;   // 解完就关文件句柄
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            // 只设长边那一个维度，短边由 WPF 按原始比例换算（两个都设会改变比例语义）
            if (w >= h)
            {
                bi.DecodePixelWidth = dw;
            }
            else
            {
                bi.DecodePixelHeight = dh;
            }

            bi.EndInit();
            bi.Freeze(); // 冻结后跨线程可用，UI 线程直接绑定

            return new DecodedImage
            {
                Path = path,
                Source = bi,
                Width = w,
                Height = h,
                Bytes = (long)bi.PixelWidth * bi.PixelHeight * 4
            };
        }
        catch
        {
            return null;
        }
    }
}
