using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// 左侧档案列表中的一项。
///
/// 与鸿蒙版 ArchiveRow 的对应关系：
///   uri  → Path（Windows 下就是普通文件路径，不再有 content:// 之类 URI）
///   size → Size（-1 表示未知，由后台 fillSizes 分批补测）
///   thumb→ Thumb（缩略图解码完成后回填，走 INotifyPropertyChanged 只刷这一行）
/// </summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    private long _size = -1;
    private BitmapSource? _thumb;

    public ImageItem(string path, string? name = null)
    {
        Path = path;
        Name = string.IsNullOrEmpty(name) ? System.IO.Path.GetFileName(path) : name!;
        if (Name.Length == 0)
        {
            Name = path;
        }
    }

    /// <summary>文件绝对路径（鸿蒙版的 uri）。</summary>
    public string Path { get; }

    /// <summary>显示用文件名。</summary>
    public string Name { get; }

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

    /// <summary>列表缩略图；未就绪为 null（此时显示深灰占位块，绝不按原图分辨率解码）。</summary>
    public BitmapSource? Thumb
    {
        get => _thumb;
        set
        {
            if (ReferenceEquals(_thumb, value))
            {
                return;
            }

            _thumb = value;
            OnPropertyChanged(nameof(Thumb));
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

    public string SizeText => FormatBytes(_size);

    public static string FormatBytes(long n)
    {
        if (n <= 0)
        {
            return "--";
        }

        if (n < 1024)
        {
            return n + " B";
        }

        if (n < 1024 * 1024)
        {
            return (n / 1024.0).ToString("0.0") + " KB";
        }

        return (n / 1024.0 / 1024.0).ToString("0.00") + " MB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}
