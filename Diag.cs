using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PIKAN;

/// <summary>
/// 运行轨迹（面包屑）+ 启动环境快照，写进 %LOCALAPPDATA%\PIKAN\trace.log。
///
/// 为什么要有这东西：单文件版拿去别人的机器上跑，最坏的情况是**进程在 native 层直接没了**
/// ——WPF 的 native 组件、解码器、GPU 互操作任何一个崩，托管异常一个都抓不到，
/// 既没有弹窗也没有 crash.log，用户看到的只有「闪退」。
/// 那种时候唯一能留下的线索就是「死之前走到哪一步」，所以关键节点都在这里打一行点：
/// 崩溃后看 trace.log 的最后一行，就知道是死在选文件夹、扫目录、还是解某张图上。
///
/// 顺带把环境也记下来（exe 放在哪种盘、有没有被「解除锁定」、单文件的自解压目录在不在、
/// 受控文件夹访问开没开）——这几项正是单文件 exe 换机器后最常见的翻车点。
/// </summary>
public static class Diag
{
    private const int MaxBytes = 512 * 1024;  // 超过就砍掉前半段，别让它无限长

    private static readonly object Gate = new();
    private static string? _dir;

    private static string Dir
    {
        get
        {
            _dir ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PIKAN");
            return _dir;
        }
    }

    public static string TracePath => Path.Combine(Dir, "trace.log");

    /// <summary>进程一进来就调一次：写一段环境快照，并把上一段尾巴留着。</summary>
    public static void Start(string[] args)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Trim();
            StringBuilder sb = new();
            sb.AppendLine("==================================================================");
            sb.AppendLine($"启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(EnvText(args));
            Append(sb.ToString());
        }
        catch
        {
            // 诊断自己绝不能把程序带崩
        }
    }

    /// <summary>关键节点打一行点。路径太长时截尾巴，别让一行撑爆日志。</summary>
    public static void Mark(string what)
    {
        try
        {
            Append($"{DateTime.Now:HH:mm:ss.fff}  {Clip(what)}\n");
        }
        catch
        {
            // 同上
        }
    }

    /// <summary>取最后几行，塞进崩溃报告里（crash.log 里直接能看到死前走到哪）。</summary>
    public static string Tail(int lines = 25)
    {
        try
        {
            if (!File.Exists(TracePath))
            {
                return "";
            }

            string[] all = File.ReadAllLines(TracePath);
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch
        {
            return "";
        }
    }

    // ===============================================================
    // 内部
    // ===============================================================

    private static void Append(string text)
    {
        lock (Gate)
        {
            File.AppendAllText(TracePath, text);
        }
    }

    /// <summary>太长就只留后半段（从换行处切，切出半个字也无所谓，这是给人看的）。</summary>
    private static void Trim()
    {
        string path = TracePath;
        if (!File.Exists(path))
        {
            return;
        }

        FileInfo fi = new(path);
        if (fi.Length <= MaxBytes)
        {
            return;
        }

        byte[] all = File.ReadAllBytes(path);
        byte[] tail = all.Skip(all.Length - (MaxBytes / 2)).ToArray();
        File.WriteAllBytes(path, tail);
    }

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static string EnvText(string[] args)
    {
        StringBuilder sb = new();

        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? asm.GetName().Version?.ToString()
                         ?? "?";
            sb.AppendLine($"  PIKAN {ver} · {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")}");
            sb.AppendLine($"  OS    {Environment.OSVersion} · {OsBuild()}");
            sb.AppendLine($"  参数  {(args.Length == 0 ? "（无）" : string.Join(' ', args))}");
            sb.AppendLine($"  区域  {System.Globalization.CultureInfo.CurrentCulture.Name} / UI {System.Globalization.CultureInfo.CurrentUICulture.Name}");

            // 单文件 exe：Assembly.Location 是空的（程序集直接从 bundle 里读）
            string loc = asm.Location;
            sb.AppendLine($"  形态  {(loc.Length == 0 ? "单文件（自解压）" : "多文件目录")}");

            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                sb.AppendLine($"  exe   {exe}");
                sb.AppendLine($"  磁盘  {DriveKind(exe)}");
                sb.AppendLine($"  锁定  {(HasZoneIdentifier(exe) ? "★ 有 Zone.Identifier（网络下载标记，需「解除锁定」）" : "无")}");
            }

            sb.AppendLine($"  TEMP  {Path.GetTempPath()} · 可写 {(TempWritable() ? "是" : "★ 否")}");

            // 单文件版会把 wpfgfx / PresentationNative / D3DCompiler 这些 native 库
            // 解压到 %TEMP%\.net\PIKAN\<hash>\ 再加载。被安全软件删掉/拦掉就是加载失败 → 闪退。
            sb.AppendLine($"  解压  {ExtractDirs()}");

            sb.AppendLine($"  受控  {ControlledFolderAccess()}");
            sb.AppendLine($"  内存  {Environment.WorkingSet / 1024 / 1024} MB 已用 · {Environment.ProcessorCount} 核");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  （环境采集中断：{ex.Message}）");
        }

        return sb.ToString().TrimEnd();
    }

    private static string OsBuild()
    {
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            object? v = k?.GetValue("CurrentBuildNumber") ?? k?.GetValue("CurrentBuild");
            object? ubr = k?.GetValue("UBR");
            return v is null ? "?" : $"build {v}{(ubr is null ? "" : "." + ubr)}";
        }
        catch
        {
            return "?";
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);

    /// <summary>exe 落在哪种介质上：网络盘 / U 盘上运行是单文件版闪退的经典原因之一。</summary>
    private static string DriveKind(string exe)
    {
        try
        {
            string root = Path.GetPathRoot(exe) ?? "";
            if (root.Length == 0)
            {
                return "?";
            }

            // 0 未知 / 1 无根目录 / 2 可移动 / 3 固定 / 4 网络 / 5 光驱 / 6 RAM 盘
            return GetDriveTypeW(root) switch
            {
                2 => $"{root} 可移动磁盘（U 盘 / 存储卡）★",
                3 => $"{root} 本地固定磁盘",
                4 => $"{root} 网络磁盘 ★",
                5 => $"{root} 光驱 ★",
                6 => $"{root} RAM 盘 ★",
                _ => $"{root} 类型 {GetDriveTypeW(root)}"
            };
        }
        catch
        {
            return "?";
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);

    private const uint InvalidFileAttributes = 0xFFFFFFFF;

    /// <summary>
    /// 有没有「来自互联网」的标记（NTFS 附属数据流 Zone.Identifier）。
    /// 带着它的 exe 会被 Windows 当成可疑文件：要么被挡住，要么行为受限。
    /// </summary>
    private static bool HasZoneIdentifier(string exe)
    {
        try
        {
            return GetFileAttributesW(exe + ":Zone.Identifier") != InvalidFileAttributes;
        }
        catch
        {
            return false;
        }
    }

    private static bool TempWritable()
    {
        try
        {
            string probe = Path.Combine(Path.GetTempPath(), "pikan-probe.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractDirs()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), ".net", "PIKAN");
            if (!Directory.Exists(root))
            {
                return "（%TEMP%\\.net\\PIKAN 不存在：尚未自解压，或被清理/拦截）";
            }

            string[] dirs = Directory.GetDirectories(root);
            if (dirs.Length == 0)
            {
                return "（解压目录为空）";
            }

            List<string> parts = new();
            foreach (string d in dirs)
            {
                string[] files = Directory.GetFiles(d);
                parts.Add($"{Path.GetFileName(d)}：{files.Length} 个 dll（{string.Join(", ", files.Select(Path.GetFileName))}）");
            }

            return string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            return "（读不到：" + ex.Message + "）";
        }
    }

    /// <summary>「受控文件夹访问」开着的话，程序往受保护目录写东西会被无声拦掉。</summary>
    private static string ControlledFolderAccess()
    {
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access");
            object? v = k?.GetValue("EnableControlledFolderAccess");
            return v is null ? "读不到状态" : (Convert.ToInt32(v) == 0 ? "关" : "★ 开（可能拦住程序写盘）");
        }
        catch
        {
            return "读不到状态";
        }
    }
}
