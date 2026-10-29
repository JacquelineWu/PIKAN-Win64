using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PIKAN;

/// <summary>
/// 真·全屏的两件事，缺一件就是「假全屏」：
///
///   ① 铺满整块显示器（rcMonitor），不是工作区（rcWork）——工作区是扣掉任务栏的那一块。
///      边框全无 + 最大化在 WPF 里通常已经拿到整块屏，但换屏 / DPI 变化 /
///      任务栏在别的边时未必，所以这里再用原生 API 精确铺一次，把尺寸钉死。
///
///   ② 把自己顶到任务栏之上。任务栏（Shell_TrayWnd）是 WS_EX_TOPMOST，
///      普通窗口就算几何上盖住它，也会被它重新盖回去——这就是「全屏了但任务栏还在」的原因。
///      同一条 topmost 带里，后调用 SetWindowPos(HWND_TOPMOST) 的压在先调用的上面，
///      所以我们这一下能压过系统启动时就建好的任务栏。
///
/// 不做「把任务栏改成自动隐藏」那套系统级改动：那会动到用户全局设置，
/// 万一进程被强杀任务栏就再也不回来了。盖住它效果一样，且退出全屏即恢复。
/// </summary>
public static class Fullscreen
{
    private const uint MonitorDefaultToNearest = 2;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfo mi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    /// <summary>
    /// 窗口所在那块显示器的顶边纵坐标（本机物理像素坐标系）。
    /// 主屏恒为 0，但副屏排在主屏下方/上方时并不为 0 —— 判「鼠标是否贴到屏幕顶边」
    /// 必须减掉这个值，否则在副屏里永远够不到 10 px 的那条线。拿不到就当 0。
    /// </summary>
    public static int MonitorTop(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return 0;
            }

            IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            MonitorInfo mi = new() { Size = Marshal.SizeOf<MonitorInfo>() };
            return GetMonitorInfo(mon, ref mi) ? mi.Monitor.Top : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 把窗口精确铺到它所在的那块显示器上，并顶到最前。
    /// 失败就静默——全屏铺不满最多是留条边，不该抛异常打断看图。
    /// </summary>
    public static void Snap(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            MonitorInfo mi = new() { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(mon, ref mi))
            {
                return;
            }

            NativeRect rc = mi.Monitor;
            SetWindowPos(
                hwnd, HwndTopmost,
                rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top,
                SwpNoActivate | SwpNoOwnerZOrder);
        }
        catch
        {
            // 全屏铺不满最多是留条边，不值得弹窗
        }
    }
}
