using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PIKAN;

/// <summary>
/// 「选项」对话框：五项看图策略开关。
/// 直接绑在 <see cref="AppOptions"/> 实例上，勾中即改；点取消会退回打开时的取值。
/// </summary>
public partial class OptionsWindow : Window
{
    private readonly AppOptions _opts;
    private readonly Dictionary<string, bool> _snapshot = new();

    private static readonly (string Prop, string Title, string Desc)[] Rows =
    {
        (nameof(AppOptions.ColorManage),
         "启用色彩管理 / ICC 配置文件的支持",
         "如果您的图片包含的色彩空间信息，启用色彩管理可以提高您的显示器的色彩表现。" +
         "支持 ICCv2、ICCv4 和 WCS 配置文件——由 Windows 图像处理组件（WIC）原生处理。"),

        (nameof(AppOptions.HighQualityDownscale),
         "启用高品质等比例图片缩小",
         "启用高品质等比例图片缩小功能将在屏幕上平滑地呈现图片，尤其是当您的图片大小超过窗口大小时。" +
         "关闭则由 WPF 自带的插值承担，速度更快但细节偏软。"),

        (nameof(AppOptions.ExifRotate),
         "启用图片自动旋转（根据 EXIF 方向信息）",
         "启用图片自动旋转功能将指示看图器按照图片的 EXIF 方向标志来呈现图片。" +
         "相机竖着拍的那些照片会自己正过来。"),

        (nameof(AppOptions.UpscaleSmallToFit),
         "放大小图片以适合窗口大小",
         "勾上时尺寸小于窗口的图片会被放大到铺满窗口；取消则小图按 100% 原大小显示，不做拉伸。"),

        (nameof(AppOptions.InstantNavigation),
         "在图片列表中往前或往后不等待图片完全载入",
         "勾上时先用已缓存的清晰度立刻出图，高清版本随后到；取消则每一步都等当前这张完全载入后才推进。")
    };

    public OptionsWindow(AppOptions opts)
    {
        InitializeComponent();
        _opts = opts;

        foreach ((string prop, string title, string desc) in Rows)
        {
            _snapshot[prop] = ReadValue(prop);
            List.Items.Add(BuildRow(prop, title, desc));
        }
    }

    private Border BuildRow(string prop, string title, string desc)
    {
        System.Windows.Controls.CheckBox box = new()
        {
            IsChecked = ReadValue(prop),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = System.Windows.Media.Brushes.White
        };
        box.Checked += (_, _) => WriteValue(prop, true);
        box.Unchecked += (_, _) => WriteValue(prop, false);

        TextBlock titleText = new()
        {
            Text = title,
            Style = (Style)FindResource("OptTitle")
        };

        TextBlock descText = new()
        {
            Text = desc,
            Style = (Style)FindResource("OptDesc")
        };

        StackPanel texts = new()
        {
            Margin = new Thickness(8, 0, 0, 0)
        };
        texts.Children.Add(titleText);
        texts.Children.Add(descText);

        // 整行都可点：命中正好落在复选框上也无所谓，它会自己处理
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(box);
        Grid.SetColumn(box, 0);
        grid.Children.Add(texts);
        Grid.SetColumn(texts, 1);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(2, 8, 2, 8),
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    private bool ReadValue(string prop) =>
        (bool?)typeof(AppOptions).GetProperty(prop)?.GetValue(_opts) ?? false;

    private void WriteValue(string prop, bool value) =>
        typeof(AppOptions).GetProperty(prop)?.SetValue(_opts, value);

    private void ActOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void ActAll(object sender, RoutedEventArgs e)
    {
        _opts.ColorManage = true;
        _opts.HighQualityDownscale = true;
        _opts.ExifRotate = true;
        _opts.UpscaleSmallToFit = true;
        _opts.InstantNavigation = true;

        // 同步所有复选框的显示
        List.Items.Clear();
        foreach ((string prop, string title, string desc) in Rows)
        {
            List.Items.Add(BuildRow(prop, title, desc));
        }
    }

    /// <summary>取消 / Esc：把取值退回打开对话框那一刻。</summary>
    private void Restore()
    {
        foreach ((string prop, bool value) in _snapshot)
        {
            WriteValue(prop, value);
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        Restore();
        DialogResult = false;
        e.Handled = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 用标题栏 × 关掉等同于取消
        if (DialogResult != true)
        {
            Restore();
        }

        base.OnClosing(e);
    }
}
