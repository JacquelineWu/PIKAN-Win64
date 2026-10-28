using System.ComponentModel;
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

        /// <summary>当前目录下的图片，点一下右边立刻显示。</summary>
        Image
    }

    private long _size = -1;

    public FsEntry(string path, EntryKind kind, string? name = null)
    {
        Path = path;
        Kind = kind;
        Name = name ?? System.IO.Path.GetFileName(path);
        if (Name.Length == 0)
        {
            Name = path;
        }
    }

    public string Path { get; }

    public string Name { get; }

    public EntryKind Kind { get; }

    public bool IsUp => Kind == EntryKind.Up;

    public bool IsFolder => Kind == EntryKind.Folder;

    public bool IsImage => Kind == EntryKind.Image;

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
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>行尾小字：文件是「JPG · 2.4 MB」，其余是类型说明。</summary>
    public string Detail
    {
        get
        {
            if (IsUp)
            {
                return "上级目录";
            }

            if (IsFolder)
            {
                return "文件夹";
            }

            string e = System.IO.Path.GetExtension(Name);
            string ext = e.Length <= 1 ? "RAW" : e.Substring(1).ToUpperInvariant();
            string size = _size > 0 ? ImageItem.FormatBytes(_size) : "--";
            return $"{ext} · {size}";
        }
    }

    /// <summary>文件名颜色：目录/上级用青蓝，图片用常规灰，一眼就分得开。</summary>
    public string NameColor => IsImage ? "#9fb0c8" : "#6fd3cb";

    /// <summary>是否加粗（ .. 与子文件夹加粗，图片常规）。</summary>
    public FontWeight Weight => IsImage ? FontWeights.Normal : FontWeights.SemiBold;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
