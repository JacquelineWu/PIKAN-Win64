using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// 显示器端色彩管理（ICC 配置文件输出变换）。
///
/// 两级作用，**缺一不可**：
///   ① 解码时不带 IgnoreColorProfile —— WIC 会把图片内嵌的 ICC 剖面
///      （ICCv2 / ICCv4 / WCS 都由 WIC 原生支持）转换到标准 sRGB 工作空间。
///      这一步在 <see cref="Decoder"/> 里按开关决定。
///   ② 再把 sRGB 工作空间转换到显示器的 ICC 剖面 —— 就是本类做的事。
///      少了这步，广色域屏上看到的还是「sRGB 版的正确颜色」，不是屏幕能呈现的颜色。
///
/// WPF 的公开 API 只有两条够用：颜色上下文可以从剖面文件构造
/// (<c>new ColorContext(Uri)</c>)，再做 <see cref="ColorConvertedBitmap"/>；
/// 想直接取出某张图的内嵌剖面没有公开入口（GetColorContexts 是 internal），
/// 所以① 借 WIC 自动完成、② 才需要自己上手。
///
/// 取主显示器而不是窗口所在显示器：解码发生在后台线程，而拿窗口句柄必须回 UI 线程，
/// 多一次线程切换不值得——多屏同型号时剖面本来就一样，真有差异用户会手动指定。
/// </summary>
public static class ColorMgmt
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    // 注意两个易错点：
    //   ① 第一个参数是 HDC（设备上下文），不是 HMONITOR——传显示器句柄必然返回 Win32 87
    //   ② 参数顺序是 (hdc, ref 长度, 缓冲区)，缓冲区在最后，不是中间
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetICMProfileW(IntPtr hdc, ref uint size, [Out] StringBuilder filename);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string driver, string device, string? port, IntPtr devmode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, ref NativeRect rc, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfo mi);

    /// <summary>缓存：同一个剖面只构造一次的颜色上下文。</summary>
    private static readonly Dictionary<string, ColorContext?> Cached = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    private static string _profilePath = "";
    private static bool _probed;

    /// <summary>当前显示器 ICC 剖面路径（拿不到就是空串）。</summary>
    public static string ProfilePath => _profilePath;

    /// <summary>上一次失败的原因，写状态栏 / 日志用。</summary>
    public static string LastNote { get; private set; } = "";

    /// <summary>
    /// 主动探测一次显示器剖面并返回路径。正常看图时由 <see cref="Apply"/> 惰性触发，
    /// 自检 / 状态栏想「先问一句」就需要这个显式入口。
    /// </summary>
    public static string Probe()
    {
        lock (Gate)
        {
            if (!_probed)
            {
                _probed = true;
                _profilePath = ProbeDisplayProfile();
                GpuAccel.WriteLog($"colormgmt 显示器剖面 = [{_profilePath}] {LastNote}");
            }
        }

        return _profilePath;
    }

    /// <summary>主显示器的 ICC 配置文件路径；失败返回 ""。</summary>
    private static string ProbeDisplayProfile()
    {
        // ① 主屏 DC（GetDC(NULL)）——绝大多数情况一次命中
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc != IntPtr.Zero)
        {
            string hit = ProbeOnDc(hdc, out string why);
            ReleaseDC(IntPtr.Zero, hdc);
            if (hit.Length > 0)
            {
                return hit;
            }

            LastNote = why;
        }

        // ② 逐台显示器：多屏时主屏可能没配剖面而从屏配了，逐个 CreateDC 试过去，
        //    取第一个真有剖面的（顺序 = EnumDisplayMonitors 的枚举序）
        string firstError = LastNote;
        foreach (IntPtr mon in EnumerateMonitors())
        {
            MonitorInfo mi = new() { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(mon, ref mi))
            {
                continue;
            }

            IntPtr mdc = CreateDC("DISPLAY", mi.DeviceName, null, IntPtr.Zero);
            if (mdc == IntPtr.Zero)
            {
                continue;
            }

            string hit = ProbeOnDc(mdc, out _);
            DeleteDC(mdc);
            if (hit.Length > 0)
            {
                LastNote = "";
                return hit;
            }
        }

        LastNote = firstError;
        return "";
    }

    /// <summary>在一个显示 DC 上问 GDI 要 ICC 剖面路径。</summary>
    private static string ProbeOnDc(IntPtr hdc, out string why)
    {
        // 文档里 lpcbName 是「字符数」，出来是实际长度；MAX_PATH * 2 足够放设备路径
        uint size = 520;
        StringBuilder sb = new((int)size);
        if (!GetICMProfileW(hdc, ref size, sb))
        {
            int err = Marshal.GetLastWin32Error();
            // 没给这台显示器指派剖面时是 0x57（ERROR_INVALID_PARAMETER）——是常态，不是 bug
            why = err == 87
                ? "这台显示器没有指派 ICC 剖面（Win32 87）"
                : $"GetICMProfile 失败（Win32 {err}）";
            return "";
        }

        string p = sb.ToString().Trim();
        if (p.Length == 0 || !File.Exists(p))
        {
            why = "ICC 剖面路径无效";
            return "";
        }

        why = "";
        return p;
    }

    private static List<IntPtr> EnumerateMonitors()
    {
        List<IntPtr> list = new();
        EnumDisplayMonitors(
            IntPtr.Zero, IntPtr.Zero,
            (IntPtr mon, IntPtr _, ref NativeRect _, IntPtr _) =>
            {
                list.Add(mon);
                return true;
            },
            IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// 把已在 sRGB 工作空间的位图转换到显示器剖面。
    /// 拿不到显示器剖面 / 构造失败时原样返回来源（此时第 ① 级仍然生效）。
    /// </summary>
    public static BitmapSource Apply(BitmapSource src)
    {
        ColorContext? dest;
        lock (Gate)
        {
            Probe();
            dest = _profilePath.Length == 0 ? null : DisplayContext(_profilePath);
        }

        if (dest is null)
        {
            return src;
        }

        try
        {
            ColorContext srgb = new(src.Format);
            ColorConvertedBitmap cc = new(src, srgb, dest, PixelFormats.Bgra32);
            cc.Freeze();
            return cc;
        }
        catch (Exception ex)
        {
            // 变换失败就退回原图：别让一张图的剖面问题连累整个看图体验
            LastNote = $"色彩变换失败：{ex.GetType().Name}: {ex.Message}";
            GpuAccel.WriteLog("colormgmt " + LastNote);
            return src;
        }
    }

    private static ColorContext? DisplayContext(string path)
    {
        if (Cached.TryGetValue(path, out ColorContext? hit))
        {
            return hit;
        }

        ColorContext? ctx;
        try
        {
            ctx = new ColorContext(new Uri(path, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            LastNote = $"剖面加载失败：{System.IO.Path.GetFileName(path)}（{ex.GetType().Name}）";
            GpuAccel.WriteLog("colormgmt " + LastNote);
            ctx = null;
        }

        Cached[path] = ctx;
        return ctx;
    }

    /// <summary>剖面 / 显示器换了（拖到另一块屏）就作废缓存，下次重新探测。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _probed = false;
            _profilePath = "";
        }
    }
}
