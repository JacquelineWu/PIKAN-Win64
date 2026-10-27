using System.Windows;

// Application 在 System.Windows（WPF）与 System.Windows.Forms 里都有，本项目要的是 WPF 的
using Application = System.Windows.Application;

namespace PIKAN;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MainWindow window = new();
        window.Show();

        // 支持命令行 / 资源管理器「打开方式」：PIKAN.exe <图片文件 或 文件夹>
        if (e.Args.Length > 0)
        {
            window.OpenPath(e.Args[0]);
        }
    }
}
