using System.Globalization;
using System.IO;
using System.Text;

namespace PIKAN;

/// <summary>
/// 排序自检：`PIKAN.exe --sorttest <目录>`，把目录里的名字按四种方式各排一遍写进 gpu.log。
///
/// 存在的理由：单片是这个产品的核心观感问题之一。用户文件名里带 (1)(2)(11) 这种序号时，
/// 逐字符排会把 11 排到 2 前面，看着就是乱的。这里把 Windows 资源管理器的那个比较函数
/// （StrCmpLogicalW）的结果直接打印出来，能一眼确认到底排对了没有。
/// </summary>
internal static class SortSelfTest
{
    public static string Run(string? dir)
    {
        StringBuilder sb = new();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return "sorttest: 需要一个存在的目录参数";
        }

        List<string> names = new();
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                names.Add(Path.GetFileName(f));
            }
        }
        catch (IOException ex)
        {
            return "sorttest: 读目录失败 " + ex.Message;
        }

        names.Sort(StringComparer.Ordinal);

        sb.AppendLine($"sorttest 目录 = {dir}（{names.Count} 个文件）");

        WriteVariants(sb, "逐字符 正序", Sort(names, natural: false, desc: false));
        WriteVariants(sb, "逐字符 倒序", Sort(names, natural: false, desc: true));
        WriteVariants(sb, "自然序 正序", Sort(names, natural: true, desc: false));
        WriteVariants(sb, "自然序 倒序", Sort(names, natural: true, desc: true));

        return sb.ToString();
    }

    private static List<string> Sort(IEnumerable<string> src, bool natural, bool desc)
    {
        Comparison<string> cmp = natural ? NaturalSort.Compare : string.CompareOrdinal;
        List<string> list = src.ToList();
        list.Sort((a, b) => desc ? -cmp(a, b) : cmp(a, b));
        return list;
    }

    private static void WriteVariants(StringBuilder sb, string title, List<string> list)
    {
        sb.AppendLine($"--- {title} ---");
        int show = Math.Min(12, list.Count);
        for (int i = 0; i < show; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {i + 1,2}. {list[i]}");
        }

        if (list.Count > show)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  … 共 {list.Count} 项");
        }
    }
}
