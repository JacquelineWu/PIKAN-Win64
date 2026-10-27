using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

// Application 在 System.Windows（WPF）与 System.Windows.Forms 里都有，本项目要的是 WPF 的
using Application = System.Windows.Application;

namespace PIKAN;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 未处理异常一律不再「静默闪退」：写 crash.log + 弹窗，至少让用户看到原因。
        // 单文件版若卡在运行时自解压阶段，进程会在托管代码之前就退出，那种情况看不到弹窗，
        // 但凡是进了 App 的异常都逃不掉这里。
        DispatcherUnhandledException += (_, a) => { Report(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Report(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Report(a.Exception); a.SetObserved(); };

        // --nogpu：强制走 CPU 渲染，便于对比画质 / 排查 GPU 相关问题
        // --bench：连续翻页采集分段耗时（写 perf.log），测完自动退出
        bool bench = false;
        foreach (string a in e.Args)
        {
            if (string.Equals(a, "--nogpu", StringComparison.OrdinalIgnoreCase))
            {
                GpuResampler.Enabled = false;
            }
            else if (string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase))
            {
                bench = true;
                Perf.Enabled = true;
            }
            else if (string.Equals(a, "--novram", StringComparison.OrdinalIgnoreCase))
            {
                // A/B 对照用：关掉显存常驻 + 结果缓存，回到「每次都上传重算」的旧路径
                GpuImageCache.Enabled = false;
                GpuResampler.ResultCacheEnabled = false;
            }
            else if (string.Equals(a, "--nowrap", StringComparison.OrdinalIgnoreCase))
            {
                // A/B 对照用：预载窗口按线性截断，跨首尾时重新解码一批图
                PreviewCache.NoWrap = true;
            }
        }

        base.OnStartup(e);

        MainWindow window = new();
        window.Show();

        // 防止窗口被别的窗口盖住 / 起在后台，看起来像"双击没反应"
        window.Activate();

        // 支持命令行 / 资源管理器「打开方式」：PIKAN.exe <图片文件 或 文件夹>
        // （-- 开头的开关不算路径）
        string? openPath = null;
        foreach (string a in e.Args)
        {
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                openPath = a;
                break;
            }
        }

        if (openPath is not null)
        {
            window.OpenPath(openPath);
        }

        if (bench)
        {
            _ = window.BenchAsync(20, 350);
        }
    }

    private static void Report(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n\n";

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PIKAN");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), text);
        }
        catch
        {
            // 日志写不了也不能二次崩
        }

        try
        {
            System.Windows.MessageBox.Show(
                "PIKAN 遇到问题：\n\n" + ex.Message +
                "\n\n详细堆栈已写入 %LOCALAPPDATA%\\PIKAN\\crash.log",
                "PIKAN",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
            // 弹窗失败就到此为止
        }
    }
}
