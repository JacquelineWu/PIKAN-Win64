using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace PIKAN;

/// <summary>
/// 自然排序：用 Windows 资源管理器同一个比较函数，
/// 让 "IMG (2).jpg" 排在 "IMG (11).jpg" 前面（逐字符排会把 11 排到 2 之前）。
/// </summary>
internal static class NaturalSort
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    public static int Compare(string a, string b) => StrCmpLogicalW(a, b);
}

/// <summary>
/// 左侧「档案列表」里的一行。
///
/// 这一栏现在是文件浏览器：一行可以是**上级目录**、**子文件夹**，或**图片文件**，
/// 所以不能直接复用 <see cref="ImageItem"/>（那是「可翻页的图片序列」里的一项）。
///
/// 关键分工：
///   · ImageItem 集合 = 当前这批**可左右翻阅**的图片（翻页、预载、位置记忆都基于它）
///   · FsEntry    集合 = 左侧**看到的那几行**（可能比前者多出 .. 与若干子文件夹）
/// 两者靠 <see cref="ItemIndex"/> 单向挂钩：FsEntry 记住自己对应前者第几项，
/// 非图片行是 -1。
/// </summary>
public sealed class FsEntry : INotifyPropertyChanged
{
    public enum EntryKind
    {
        /// <summary>「..」回到上一级目录，只在浏览模式下出现且永远在第一行。</summary>
        Up,

        /// <summary>当前目录下的子文件夹，点进去就换到那个目录继续浏览。</summary>
        Folder,

        /// <summary>
        /// 当前目录下**不支持预览**的文件（txt / mov / psd …）。
        /// 只占一个名字让人知道「它在那儿」，点它最多给一句提示，不进翻页序列。
        /// </summary>
        File,

        /// <summary>当前目录下的图片，点一下右边立刻显示。</summary>
        Image
    }

    private long _size = -1;
    private DateTime? _modified;

    public FsEntry(string path, EntryKind kind, string? name = null)
    {
        Path = path;
        Kind = kind;
        Name = name ?? System.IO.Path.GetFileName(path);
        if (Name.Length == 0)
        {
            Name = path;
        }

        DisplayName = FileNameRepair.Fix(Name);
    }

    public string Path { get; }

    /// <summary>
    /// 磁盘上**真实**的文件名。用于拼路径、读文件、位置记忆快照。
    /// 即使它显示为乱码也必须用这个名字访问文件——换成修复后的名字会打不开。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 显示用文件名：乱码（GBK 字节被别的编码误解码）会在这里被还原成中文。
    /// 列表、信息栏、悬停标题都用它；<b>不要</b>拿它去 IO。
    /// </summary>
    public string DisplayName { get; private set; }

    /// <summary>开关变化后重算显示名（原名 <see cref="Name"/> 不受影响）。</summary>
    public void RefreshDisplayName()
    {
        string want = FileNameRepair.Fix(Name);
        if (want == DisplayName)
        {
            return;
        }

        DisplayName = want;
        OnPropertyChanged(nameof(DisplayName));
    }

    public EntryKind Kind { get; }

    public bool IsUp => Kind == EntryKind.Up;

    public bool IsFolder => Kind == EntryKind.Folder;

    public bool IsImage => Kind == EntryKind.Image;

    /// <summary>不支持预览的普通文件（灰色那些行）。</summary>
    public bool IsFile => Kind == EntryKind.File;

    /// <summary>对应 <c>_items</c> 中的下标； .. 与子文件夹为 -1。</summary>
    public int ItemIndex { get; set; } = -1;

    public long Size
    {
        get => _size;
        set
        {
            if (_size == value)
            {
                return;
            }

            _size = value;
            OnPropertyChanged(nameof(Size));
            OnPropertyChanged(nameof(SizeText));
        }
    }

    /// <summary>文件修改时间，未知为 null（与体积一样由后台分批补测）。</summary>
    public DateTime? Modified
    {
        get => _modified;
        set
        {
            if (_modified == value)
            {
                return;
            }

            _modified = value;
            OnPropertyChanged(nameof(DateText));
        }
    }

    /// <summary>
    /// 「大小」一列。图片按字节数带千分位（要的就是原始 byte）；
    /// 文件夹显总容量（后台递归求和，算完之前先是 …）；「..」留空。
    /// </summary>
    public string SizeText
    {
        get
        {
            if (IsUp)
            {
                return "";
            }

            if (IsFolder)
            {
                return Size < 0 ? "…" : ImageItem.FormatBytes(Size);
            }

            return Size > 0 ? Size.ToString("N0", CultureInfo.InvariantCulture) : "--";
        }
    }

    /// <summary>「日期」一列：年-月-日 时:分，未测到留空。</summary>
    public string DateText => _modified is { } m ? m.ToString("yyyy-MM-dd HH:mm") : "";

    /// <summary>
    /// 文件名颜色，一眼分清三种行：
    /// 可预览的图 → 粉色；不支持预览的文件 → 灰色（退到背景里，但仍看得见）；
    /// 目录（含「..」）→ 粉蓝。
    /// </summary>
    public string NameColor => Kind switch
    {
        EntryKind.Image => "#f9a8c8", // 粉
        EntryKind.File => "#6b7280",  // 灰
        _ => "#8fd2f2"                // 粉蓝
    };

    /// <summary>是否加粗（ .. 与子文件夹加粗，文件常规）。</summary>
    public FontWeight Weight => IsFolder || IsUp ? FontWeights.SemiBold : FontWeights.Normal;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
