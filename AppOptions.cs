using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PIKAN;

/// <summary>
/// 用户可开关的看图策略。全部默认 true（对应「选项」对话框里五项是勾上的）。
///
/// 为什么要做成开关而不是写死：这五件事各有代价——
///   色彩管理  每张图多一次色彩空间变换
///   高品质缩小 每次停手要在 GPU / AVX2 上做一遍 Mitchell 重采样
///   EXIF 旋转  每次解码要多读一次元数据
/// 想压榨极致速度时可以关掉；但默认全开，因为默认体验应该是「正确 + 好看」。
/// </summary>
public sealed class AppOptions : INotifyPropertyChanged
{
    private bool _colorManage = true;
    private bool _highQualityDownscale = true;
    private bool _exifRotate = true;
    private bool _upscaleSmallToFit = true;
    private bool _instantNavigation = true;
    private bool _spreadPortrait = true;

    /// <summary>启用色彩管理 / ICC 配置文件支持。</summary>
    public bool ColorManage
    {
        get => _colorManage;
        set => Set(ref _colorManage, value);
    }

    /// <summary>启用高品质等比例缩小（GPU / AVX2 Mitchell，而非 WPF 默认插值）。</summary>
    public bool HighQualityDownscale
    {
        get => _highQualityDownscale;
        set => Set(ref _highQualityDownscale, value);
    }

    /// <summary>按 EXIF 方向标志自动旋转照片。</summary>
    public bool ExifRotate
    {
        get => _exifRotate;
        set => Set(ref _exifRotate, value);
    }

    /// <summary>放大小图片以适应窗口（关掉则小图按 100% 原大小显示，不拉伸）。</summary>
    public bool UpscaleSmallToFit
    {
        get => _upscaleSmallToFit;
        set => Set(ref _upscaleSmallToFit, value);
    }

    /// <summary>在列表中前 / 后翻页时不等待图片完全载入（先用已缓存的清晰度顶上）。</summary>
    public bool InstantNavigation
    {
        get => _instantNavigation;
        set => Set(ref _instantNavigation, value);
    }

    /// <summary>
    /// 对开显示：当前是竖图、紧挨着的下一张也是竖图时，两张左右并排一起显示
    /// （漫画 / 扫描件的看法），翻页一次跨两页。
    /// </summary>
    public bool SpreadPortrait
    {
        get => _spreadPortrait;
        set => Set(ref _spreadPortrait, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>把当前取值推给解码 / 渲染链路（那些模块用的是静态设置，避免层层传参）。</summary>
    public void Push()
    {
        Decoder.ColorManage = _colorManage;
        Decoder.ReadExifOrientation = _exifRotate;
    }

    public override string ToString() =>
        $"ICC={_colorManage} HQ={_highQualityDownscale} EXIF={_exifRotate} 放大={_upscaleSmallToFit} 即时={_instantNavigation}";
}
