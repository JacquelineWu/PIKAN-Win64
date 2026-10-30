using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PIKAN;

/// <summary>
/// 文件名乱码探测与还原（GB2312 / GBK / GB18030 / UTF-8 / Big5 / Shift-JIS）。
///
/// <para>本地磁盘（NTFS）上的文件名本来就是 UTF-16，本身不存在"编码不对"。真正出问题的是：
/// 某个环节把一串 GBK 字节用**别的编码**解成了 Unicode，从此这个名字就以"乱码字符"的形态
/// 存在于磁盘上——老解压工具、跨系统拷贝、网盘下载、老版本同步软件都会造成这种结果。</para>
///
/// <para>还原分两步：
/// <list type="number">
///   <item><b>回推</b>：把当前码位按「字节值 = 码位」还原成原始字节（Latin-1 的定义）；</item>
///   <item><b>解码</b>：拿这串字节用候选的真实编码重新解成文字。</item>
/// </list>
/// 可逆性按字节损失分三级：
/// <list type="bullet">
///   <item>字节被当成 Latin-1 / cp1252 解码：1 字节 ↔ 1 码位，一一对应，**完全可逆**；</item>
///   <item>UTF-8 字节被当成 GBK 解码（"锟斤拷"）：只要那些字还能用 cp936 无损编回字节即可逆；</item>
///   <item>GBK 字节被当成 UTF-8 解码：非法序列会变成 U+FFFD，信息已丢，**不可逆**。</item>
/// </list></para>
///
/// <para>因为存在不可逆的情况，这里的策略是**保守探测**：先用很严的前置条件筛掉正常文件名，
/// 再把原始字节交给各候选编码试解，要求解出来"像人读得懂的文字"（有连续表意文字、无替换字符），
/// 打分取最高且优于原样的那个；拿不到可信结果就原样返回。</para>
///
/// <para><b>安全边界</b>：还原结果只用于**显示**。读文件、拼路径、位置记忆一律用原始
/// <c>Name</c> / <c>Path</c>——拿修复后的名字去 IO 会直接打不开文件。
/// 代码页解码走 kernel32 的 <c>MultiByteToWideChar</c>（用系统自带的代码页表），
/// 因此不必引 <c>System.Text.Encoding.CodePages</c> 这个 NuGet 包，工程保持零依赖。</para>
/// </summary>
internal static class AnsiCodec
{
    // ---- Windows 代码页 ----
    private const uint CpGb18030 = 54936; // GB18030（向下含 GBK / GB2312）
    private const uint Cp936 = 936;       // GBK
    private const uint CpBig5 = 950;      // 繁体
    private const uint CpShiftJis = 932;  // 日文
    private const uint Cp949 = 949;       // 韩文
    private const uint CpUtf8 = 65001;    // UTF-8

    // 用非托管缓冲而不是托管数组：实测 P/Invoke 对 byte[] / char[] 的 [Out] 封送
    // **并不回写内容**（函数返回了正确长度，数组却仍是初值），所以这里自己申请 /
    // 释放内存，结果完全可控。
    //
    // 同理没有用 MB_ERR_INVALID_CHARS：那个标志只对「常规」代码页有效，对
    // GB18030(54936) 与 UTF-8(65001) 会被忽略，而这两条正是我们的主力路线。
    // 合法性改为解码后自己查替换字符 U+FFFD 兜底。

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint CodePage, uint dwFlags,
        IntPtr lpMultiByteStr, int cbMultiByte,
        IntPtr lpWideCharStr, int cchWideChar);

    /// <summary>一条候选路线：把回推出的原始字节用这个编码解一次。</summary>
    private readonly record struct Route(uint Real, string Label);

    /// <summary>
    /// 候选的真实编码，按「最可能发生」排序。
    /// 中文世界里绝大多数乱码是：GBK 字节被当成 Latin-1 / cp1252 直解，
    /// 所以 GB18030 / GBK 排最前；UTF-8 那条用来兜「双重编码」（UTF-8 字节又被当 Latin-1 读一遍）。
    /// </summary>
    private static readonly Route[] Routes =
    [
        new(CpGb18030, "GB18030"),
        new(Cp936,     "GBK"),
        new(CpBig5,    "Big5"),
        new(CpShiftJis,"Shift-JIS"),
        new(Cp949,     "cp949"),
        new(CpUtf8,    "UTF-8"),
    ];

    /// <summary>
    /// 典型的乱码字。UTF-8 字节被当 GBK 解时高频出现的就是这几个，
    /// 它们本身是合法汉字，只能靠"黑名单"排除，否则会把乱码修成另一坨乱码。
    /// </summary>
    private const string MojibakeChars = "锟斤拷烫屯锘錕硯擴矯絢纊燙�";

    private static readonly Dictionary<string, string> Cache = new();

    /// <summary>缓存条数上限：超大目录里名字各不相同，别让字典无限长。</summary>
    private const int CacheMax = 4096;

    /// <summary>
    /// 尽量把乱码名字还原成可读文字。看不出问题 / 拿不到可信结果时原样返回。
    /// 返回值只用于显示，<b>不能</b>当路径用。
    /// </summary>
    public static string Repair(string name)
    {
        if (string.IsNullOrEmpty(name) || !MaybeBroken(name))
        {
            return name;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(name, out string? hit))
        {
                return hit;
            }
        }

        // 回推：MaybeBroken 已保证非 ASCII 码位都落在 U+0080–U+00FF，
        // 而 Latin-1 的定义就是「码位 = 字节值」，所以这一步严格 1:1、绝不会失败。
        byte[] raw = Encoding.Latin1.GetBytes(name);

        string best = name;
        double bestScore = Score(name); // 原样是兜底，候选必须更好才采纳

        foreach (Route r in Routes)
        {
            string? text = Forward(raw, r.Real);
            if (text is null || CjkCount(text) < 2)
            {
                continue;
            }

            double s = Score(text);
            if (s > bestScore)
            {
                bestScore = s;
                best = text;
            }
        }

        lock (Cache)
        {
            if (Cache.Count < CacheMax)
            {
                Cache[name] = best;
            }
        }

        return best;
    }

    /// <summary>
    /// 整条路径逐段修复：目录名同样可能乱码，所以按分隔符切开一段段还原再拼回去。
    /// 驱动器号 / 根路径 / UNC 前缀原样保留。
    /// </summary>
    public static string RepairPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        int root = RootLength(path);
        if (root >= path.Length)
        {
            return path;
        }

        string head = path[..root];
        string rest = path[root..];
        string[] parts = rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                parts[i] = Repair(parts[i]);
            }
        }

        return head + string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>开关 / 换目录时清缓存。</summary>
    public static void ClearCache()
    {
        lock (Cache)
        {
            Cache.Clear();
        }
    }

    /// <summary>数一下有多少个表意文字 / 假名 / 韩文音节（乱码黑名单里的不算）。</summary>
    private static int CjkCount(string s)
    {
        int n = 0;
        foreach (char c in s)
        {
            if (MojibakeChars.IndexOf(c) >= 0)
            {
                continue;
            }

            if (c is >= '一' and <= '鿿' ||
                c is >= '぀' and <= 'ヿ' ||
                c is >= '가' and <= '힯')
        {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// 把原始字节按真实编码解成文字。
    ///
    /// 带 MB_ERR_INVALID_CHARS 让 Windows 对非法序列直接失败；不过该标志对 54936 / 65001
    /// 是无效的（会被忽略），所以这里再自己查一遍替换字符兜底。
    /// 返回 null 表示这条路线解不出可信结果。
    /// </summary>
    private static string? Forward(byte[] raw, uint cp)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        IntPtr src = IntPtr.Zero;
        IntPtr dst = IntPtr.Zero;
        try
        {
            src = Marshal.AllocHGlobal(raw.Length);
            Marshal.Copy(raw, 0, src, raw.Length);

            int need = MultiByteToWideChar(cp, 0, src, raw.Length, IntPtr.Zero, 0);
            if (need <= 0)
            {
                return null;
            }

            dst = Marshal.AllocHGlobal((need + 1) * 2);
            int got = MultiByteToWideChar(cp, 0, src, raw.Length, dst, need);
            if (got <= 0)
            {
                return null;
            }

            string text = Marshal.PtrToStringUni(dst, got) ?? "";

            // 非法字节会被系统填成 U+FFFD（见上面的标志说明），这里自己查一遍
            return text.IndexOf('�') >= 0 ? null : text;
        }
        finally
        {
            if (src != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(src);
            }

            if (dst != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dst);
            }
        }
    }

    /// <summary>
    /// 这个名字有没有可能是乱码 —— 前置筛选，刻意做得很严。
    ///
    /// 只有「含非 ASCII，且所有非 ASCII 都挤在 U+0080–U+00FF 这一带（即当初是单字节流被直解成码位），
    /// 且这类字符不少于 3 个」才肯往下试。正常的中文（U+4E00+）、假名、韩文都不在这个区间，
    /// 带一两个重音字母的西文名（Grüße、café）又够不到数量门槛，因此正常文件名一律不动。
    /// </summary>
    private static bool MaybeBroken(string s)
    {
        int odd = 0;
        foreach (char c in s)
        {
            if (c <= 0x7F)
            {
                continue;
            }

            if (c > 0x00FF)
            {
                return false; // 已经有正常的表意文字 / 其它文字体系，不干预
            }

            odd++;
        }

        return odd >= 3;
    }

    /// <summary>
    /// 给候选结果打分，越高越像"人读得懂的文字"。
    ///
    /// 核心是奖励中日韩表意文字与假名 / 韩文，惩罚替换字符与控制字符。
    /// 之所以可靠：乱码名回推后的字节解出来多半是毫无结构的怪字母，
    /// 而正确解码后会出现连续的表意文字，这个对比非常稳定。
    /// </summary>
    private static double Score(string s)
    {
        if (s.Length == 0)
        {
            return 0;
        }

        int cjk = 0, latin = 0, digit = 0, punct = 0, ctrl = 0, repl = 0, bad = 0;

        foreach (char c in s)
        {
            if (c == '�')
            {
                repl++;
            }
            else if (c < 0x20 || c is >= (char)0x7F and <= (char)0x9F)
            {
                ctrl++;
            }
            else if (MojibakeChars.IndexOf(c) >= 0)
            {
                bad++; // 典型的乱码字
            }
            else if (c is >= '一' and <= '鿿' ||      // CJK 统一表意文字
                     c is >= '぀' and <= 'ヿ' ||      // 日文假名
                     c is >= '가' and <= '힯')      // 韩文音节
            {
                cjk++;
            }
            else if (c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z')
            {
                latin++;
            }
            else if (c is >= '0' and <= '9')
            {
                digit++;
            }
            else
            {
                punct++;
            }
        }

        double score = cjk * 1.0 + latin * 0.35 + digit * 0.35 + punct * 0.2;
        score -= repl * 3.0;
        score -= ctrl * 1.5;
        score -= bad * 2.0;

        // 出现表意文字 / 假名 / 韩文且整体干净 → 强烈信号，说明还原成了真正的文字
        if (cjk > 0 && repl == 0 && ctrl == 0 && bad == 0)
        {
            score += 2.0;
        }

        return score / s.Length * 4.0; // 归一化，与名字长度无关
    }

    /// <summary>取路径前缀长度：`C:\` → 3，`\\server\share\` → 其长度，`\foo` → 1。</summary>
    private static int RootLength(string p)
    {
        if (p.Length >= 2 && p[1] == ':')
        {
            return p.Length > 2 && (p[2] == Path.DirectorySeparatorChar || p[2] == Path.AltDirectorySeparatorChar) ? 3 : 2;
        }

        if (p.Length >= 2 && (p[0] == Path.DirectorySeparatorChar || p[0] == Path.AltDirectorySeparatorChar) &&
            (p[1] == Path.DirectorySeparatorChar || p[1] == Path.AltDirectorySeparatorChar))
        {
            // UNC：跳到第二个分隔符之后再吃到下一个分隔符或结尾
            int i = 2;
            int slashes = 0;
            while (i < p.Length && slashes < 2)
            {
                if (p[i] == Path.DirectorySeparatorChar || p[i] == Path.AltDirectorySeparatorChar)
            {
                    slashes++;
                }

                i++;
            }

            return i;
        }

        return p.Length > 0 && (p[0] == Path.DirectorySeparatorChar || p[0] == Path.AltDirectorySeparatorChar) ? 1 : 0;
    }
}

/// <summary>
/// 文件名乱码修复的对外入口：带总开关，关掉就完全不碰名字。
///
/// 做成静态开关而非层层传参，是为了和解码 / 渲染链路一致（<c>Decoder.ColorManage</c> 那套）：
/// <see cref="FsEntry"/> 在构造时就要算出显示名，那时拿不到主窗口实例。
/// 开关一变就清缓存，保证下一次取名字按新策略重算。
/// </summary>
internal static class FileNameRepair
{
    private static bool _enabled = true;

    /// <summary>是否修复乱码文件名（默认开）。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            AnsiCodec.ClearCache();
        }
    }

    /// <summary>显示用的文件名；<b>不能</b>拿去拼路径。</summary>
    public static string Fix(string name) => _enabled ? AnsiCodec.Repair(name) : name;

    /// <summary>显示用的完整路径；<b>不能</b>拿去 IO。</summary>
    public static string FixPath(string path) => _enabled ? AnsiCodec.RepairPath(path) : path;
}
