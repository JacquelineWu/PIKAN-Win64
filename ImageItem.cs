using System.ComponentModel;
using System.IO;

namespace PIKAN;

/// <summary>
/// 左侧档案列表中的一项。
///
/// 与鸿蒙版 ArchiveRow 的对应关系：
///   uri  → Path（Windows 下就是普通文件路径，不再有 content:// 之类 URI）
///   size → Size（-1 表示未知，由后台 fillSizes 分批补测）
///   thumb→ 已移除：左侧列表现在只显示文件名 + 一行小字（类型 / 体积），不再显示缩略图
/// </summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    private long _size = -1;

    public ImageItem(string path, string? name = null)
    {
        Path = path;
        Name = string.IsNullOrEmpty(name) ? System.IO.Path.GetFileName(path) : name!;
        if (Name.Length == 0)
        {
            Name = path;
        }

        DisplayName = FileNameRepair.Fix(Name);
    }

    /// <summary>文件绝对路径（鸿蒙版的 uri）。</summary>
    public string Path { get; }

    /// <summary>磁盘上**真实**的文件名。用于拼路径、读文件、位置记忆。</summary>
    public string Name { get; }

    /// <summary>
    /// 显示用文件名（乱码会被还原成中文）。仅用于展示，<b>不要</b>拿去 IO。
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

    /// <summary>文件字节数，未知为 -1。</summary>
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

    /// <summary>大写扩展名，无扩展名为 RAW。</summary>
    public string Ext
    {
        get
        {
            string e = System.IO.Path.GetExtension(Name);
            return e.Length <= 1 ? "RAW" : e.Substring(1).ToUpperInvariant();
        }
    }

    /// <summary>文件修改时间，未知为 null。与体积同一次 stat 取到，见 TryStat。</summary>
    public DateTime? Modified { get; set; }

    /// <summary>未知（&lt; 0）显 --，0 字节显 0 B——文件夹总容量可能是 0，不能跟着显 --。</summary>
    public string SizeText => _size >= 0 ? FormatBytes(_size) : "--";

    public static string FormatBytes(long n)
    {
        if (n < 0)
        {
            return "--";
        }

        if (n < 1024)
        {
            return n + " B";
        }

        if (n < 1024L * 1024)
        {
            return (n / 1024.0).ToString("0.0") + " KB";
        }

        if (n < 1024L * 1024 * 1024)
        {
            return (n / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }

        return (n / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}
