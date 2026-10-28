using System.IO;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>解码结果：位图 + 原图宽高 + 估算占用字节。</summary>
public sealed class DecodedImage
{
    public required string Path { get; init; }

    /// <summary>已 Freeze 的位图，可跨线程交给 UI 显示。</summary>
    public required BitmapSource Source { get; init; }

    /// <summary>
    /// 解码时长边上限（即这张图的清晰度档位）。
    /// 用来判断缓存里的图够不够直接上主视图——不够就再后台解一张更高清的覆盖。
    /// </summary>
    public int Edge { get; init; }

    /// <summary>原图宽（用于 1:1 与信息栏，不是位图宽）。</summary>
    public required int Width { get; init; }

    /// <summary>原图高。</summary>
    public required int Height { get; init; }

    public long Bytes { get; init; }

    /// <summary>
    /// EXIF 方向标志（1~8，取值见 ExifOrientation 的注释）。
    /// 1 表示「不用做任何事」；读不到元数据时也是 1。
    /// 注意：这里是**原图的方向**，操作 / 预览缓存都靠它保持方向一致。
    /// </summary>
    public int ExifOrientation { get; init; } = 1;
}

/// <summary>
/// EXIF 方向标志（IFD Orientation, tag 0x0112）到「旋转 + 水平镜像」的换算。
///
/// 八个值里 5~8 是转置类的操作，用「旋转 + 翻转」组合出来，
/// 于是渲染层不需要额外支持转置矩阵——只需多几次符号判断。
/// </summary>
public static class ExifOrientation
{
    /// <summary>返回 (顺时针旋转角度, 是否水平镜像)。</summary>
    public static (int RotDeg, bool FlipH) ToTransform(int code) => code switch
    {
        2 => (0, true),
        3 => (180, false),
        4 => (180, true),       // 垂直镜像 = 水平镜像 + 转 180°
        5 => (270, true),       // 转置
        6 => (90, false),
        7 => (90, true),        // 横向转置
        8 => (270, false),
        _ => (0, false)
    };

    /// <summary>5~8 会让宽高互换。</summary>
    public static bool SwapsAxes(int code)
    {
        int r = ToTransform(code).RotDeg;
        return r == 90 || r == 270;
    }
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
///
/// 两项可选行为（见 <see cref="ColorManage"/> / <see cref="ReadExifOrientation"/>）：
///   色彩管理 —— 默认是 IgnoreColorProfile（不改 Embedded 剖面以外的任何值，最快）；
///   打开后 WIC 才把内嵌 ICC 剖面换算到工作空间，再由 <see cref="ColorMgmt"/> 换到显示器剖面。
///   EXIF 方向 —— 只在 <c>ReadExifOrientation</c> 为真时才去读元数据（多一次元数据读取）。
/// </summary>
public static class Decoder
{
    public static readonly string[] Suffixes =
    {
        ".heic", ".heif", ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };

    private static readonly HashSet<string> SuffixSet =
        new(Suffixes, StringComparer.OrdinalIgnoreCase);

    /// <summary>由「选项」开关驱动：是否启用 ICC 色彩管理。</summary>
    public static bool ColorManage { get; set; } = true;

    /// <summary>由「选项」开关驱动：是否读取 EXIF 方向（读不到一律当作「正」）。</summary>
    public static bool ReadExifOrientation { get; set; } = true;

    /// <summary>只有这些格式才可能带 EXIF 方向，其余格式读元数据纯属浪费。</summary>
    private static readonly HashSet<string> ExifCapable =
        new(new[] { ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff" }, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>读 EXIF 方向标志（1~8）；读不到返回 1（= 不用旋转）。</summary>
    private static int ProbeOrientation(string path)
    {
        if (!ReadExifOrientation)
        {
            return 1;
        }

        if (!ExifCapable.Contains(System.IO.Path.GetExtension(path)))
        {
            return 1;
        }

        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            BitmapDecoder decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
            {
                return 1;
            }

            if (decoder.Frames[0].Metadata is not BitmapMetadata md)
            {
                return 1;
            }

            // System.Photo.Orientation 是整数（JPEG 的 tag 0x0112）
            object? raw = md.GetQuery("System.Photo.Orientation");
            int code = raw switch
            {
                ushort u => u,
                short s => s,
                byte b => b,
                uint ui => (int)ui,
                int i => i,
                string s when int.TryParse(s, out int parsed) => parsed,
                _ => 1
            };

            return code is >= 1 and <= 8 ? code : 1;
        }
        catch
        {
            return 1; // 元数据损坏 / 格式不支持 → 当正常方向处理
        }
    }

    /// <summary>
    /// 按给定长边上限解码一张图，失败返回 null。
    /// edge = 768  → 低清预览（预载 / 切图过渡层）
    /// edge = 128  → 列表缩略图
    /// edge = 画布长边×1.5 → 主视图清晰图
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

        int orient = ProbeOrientation(path);

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
            bi.CreateOptions = ColorManage
                ? BitmapCreateOptions.None               // 让 WIC 处理内嵌 ICC 剖面
                : BitmapCreateOptions.IgnoreColorProfile; // 原样取值，最快／复古做法
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

            BitmapSource src = ColorManage ? ColorMgmt.Apply(bi) : bi;

            return new DecodedImage
            {
                Path = path,
                Source = src,
                Width = w,
                Height = h,
                Edge = edge,
                Bytes = (long)src.PixelWidth * src.PixelHeight * 4,
                ExifOrientation = orient
            };
        }
        catch
        {
            return null;
        }
    }
}
