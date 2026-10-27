using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace PIKAN;

/// <summary>
/// 切图链路的分段计时。
///
/// 设计上刻意保持"轻量"以免测不准：
///   · 只做字符串拼接 + 内存缓冲，落盘放到后台线程批量写；
///   · 另挂 CompositionTarget.Rendering 统计帧间隔，UI 线程被冻结多久一眼可见
///     （这才是"卡顿"的直接体感来源，比单看某段代码耗时更准）。
///
/// 日志：%LOCALAPPDATA%\PIKAN\perf.log
/// </summary>
public static class Perf
{
    private const int FlushThreshold = 40;

    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static readonly List<string> Buf = new();
    private static readonly object Gate = new();

    private static long _last;
    private static long _frameLast;
    private static bool _flushing;

    /// <summary>
    /// 默认关：日常使用不留日志、不打点。
    /// 需要排查时二选一：命令行 --bench，或设环境变量 PIKAN_PERF=1。
    /// </summary>
    public static bool Enabled =
        Environment.GetEnvironmentVariable("PIKAN_PERF") == "1";

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PIKAN", "perf.log");

    /// <summary>开一段新的计时，tag 作为分隔标题。</summary>
    public static void Start(string tag)
    {
        long now = Sw.ElapsedMilliseconds;
        lock (Gate)
        {
            _last = now;
        }

        Note("---- " + tag);
    }

    /// <summary>记录「距上一个打点」的耗时。</summary>
    public static void Tick(string label)
    {
        long now = Sw.ElapsedMilliseconds;
        long d;
        lock (Gate)
        {
            d = now - _last;
            _last = now;
        }

        Note($"{label}  {d} ms");
    }

    /// <summary>记录一段已知耗时（起点在别处，例如后台解码）。</summary>
    public static void Span(string label, long ms) => Note($"{label}  {ms} ms");

    public static void Note(string line)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            Buf.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        }

        if (Buf.Count >= FlushThreshold)
        {
            Flush();
        }
    }

    /// <summary>同步落盘。退出前必须用它——异步那版赶不上进程结束。</summary>
    public static void FlushSync()
    {
        List<string>? batch;
        lock (Gate)
        {
            if (Buf.Count == 0)
            {
                return;
            }

            batch = new List<string>(Buf);
            Buf.Clear();
        }

        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(LogPath, string.Concat(batch.Select(l => l + "\n")), Encoding.UTF8);
        }
        catch
        {
            // 计时日志写不了就算了
        }
    }

    public static void Flush()
    {
        List<string>? batch = null;
        lock (Gate)
        {
            if (_flushing || Buf.Count == 0)
            {
                return;
            }

            _flushing = true;
            batch = new List<string>(Buf);
            Buf.Clear();
        }

        _ = Task.Run(() =>
        {
            try
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(LogPath, string.Concat(batch.Select(l => l + "\n")), Encoding.UTF8);
            }
            catch
            {
                // 计时日志写不了就算了
            }
            finally
            {
                lock (Gate)
                {
                    _flushing = false;
                }
            }
        });
    }

    /// <summary>
    /// 挂上帧间隔监控。
    /// 阈值取 50ms：60Hz 正常是 16.7ms，而 WPF 空闲时刻意不触发 Rendering，
    /// 间隔天然会拉大到 30ms 出头——那是节流不是卡顿，取 30ms 会把日志刷满。
    /// </summary>
    public static void AttachFrameMonitor()
    {
        _frameLast = Sw.ElapsedMilliseconds;
        CompositionTarget.Rendering += (_, _) =>
        {
            if (!Enabled)
            {
                return;
            }

            long now = Sw.ElapsedMilliseconds;
            long d = now - _frameLast;
            _frameLast = now;
            if (d >= 50)
            {
                Note($"!! UI 冻结 {d} ms");
            }
        };
    }
}
