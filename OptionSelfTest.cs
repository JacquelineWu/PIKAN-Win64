using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// `--optselftest <图片或目录>`：把「选项」里最难肉眼判断的两项跑成数字——
///   ① ICC 色彩管理到底有没有生效（有没有找到显示器剖面、转换后像素差多少）
///   ② EXIF 方向读出来是几、会不会被自动转正
/// 结果写进 gpu.log。这两项就算不动代码也值得周期性跑一次：
/// 显示器剖面会被系统更新换掉，EXIF 方向则取决于相机固件写了什么。
/// </summary>
public static class OptionSelfTest
{
    public static string Run(string? path)
    {
        StringBuilder sb = new();
        sb.AppendLine("optselftest ===== 选项自检 =====");

        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            sb.AppendLine($"  路径无效：{path}");
            return sb.ToString();
        }

        string? file = File.Exists(path) ? path : null;
        if (file is null && Directory.Exists(path))
        {
            file = Directory.EnumerateFiles(path)
                .Where(Decoder.IsImage)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        if (file is null)
        {
            sb.AppendLine($"  目录里没有受支持的图片：{path}");
            return sb.ToString();
        }

        sb.AppendLine($"  样本图：{file}");

        // ---- 一、ICC / 显示器剖面 ----
        sb.AppendLine("  [ICC]");
        // 先显式探测，否则下面这两行会读到「还没问过」的空值
        sb.AppendLine($"    显示器剖面 = [{ColorMgmt.Probe()}] {ColorMgmt.LastNote}");

        string off = AveragePixel(file, colorManage: false);
        string on = AveragePixel(file, colorManage: true);
        sb.AppendLine($"    关色彩管理  → {off}");
        sb.AppendLine($"    开色彩管理  → {on}");
        sb.AppendLine($"    变换后备注：{ColorMgmt.LastNote}");

        // ---- 二、EXIF 方向 ----
        sb.AppendLine("  [EXIF]");
        IEnumerable<string> files = File.Exists(path)
            ? new[] { path }
            : Directory.EnumerateFiles(path).Where(Decoder.IsImage)
                  .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(8);

        foreach (string f in files)
        {
            Decoder.ReadExifOrientation = true;
            DecodedImage? img = Decoder.Decode(f, 64);
            if (img is null)
            {
                sb.AppendLine($"    {Path.GetFileName(f)} → 解码失败");
                continue;
            }

            (int rot, bool flip) = ExifOrientation.ToTransform(img.ExifOrientation);
            bool swap = ExifOrientation.SwapsAxes(img.ExifOrientation);
            sb.AppendLine($"    {Path.GetFileName(f)}  code={img.ExifOrientation}  " +
                          $"旋转={rot}° 镜像={(flip ? "是" : "否")} 宽高互换={(swap ? "是" : "否")}  " +
                          $"原图 {img.Width}×{img.Height} → 显示 {(swap ? img.Height : img.Width)}×{(swap ? img.Width : img.Height)}");
        }

        sb.AppendLine("optselftest ===== 结束 =====");
        return sb.ToString();
    }

    /// <summary>解码后取一排像素的平均值，用来判断两次解码有没有真的差出颜色。</summary>
    private static string AveragePixel(string path, bool colorManage)
    {
        try
        {
            Decoder.ColorManage = colorManage;
            DecodedImage? img = Decoder.Decode(path, 256);
            if (img is null)
            {
                return "解码失败";
            }

            BitmapSource src = img.Source;
            int w = src.PixelWidth;
            int h = src.PixelHeight;
            int stride = w * 4;
            byte[] buf = new byte[stride * h];
            src.CopyPixels(buf, stride, 0);

            long r = 0;
            long g = 0;
            long b = 0;
            int n = 0;
            for (int y = 0; y < h; y += Math.Max(1, h / 16))
            {
                for (int x = 0; x < w; x += Math.Max(1, w / 16))
                {
                    int i = (y * stride) + (x * 4);
                    b += buf[i];
                    g += buf[i + 1];
                    r += buf[i + 2];
                    n++;
                }
            }

            return n == 0
                ? "取样失败"
                : $"格式={src.Format} 平均 RGB=({r / n},{g / n},{b / n})";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
