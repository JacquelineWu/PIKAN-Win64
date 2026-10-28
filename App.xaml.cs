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
        bool cmp = false;
        bool zoomtest = false;
        bool sorttest = false;
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
            else if (a.StartsWith("--vram=", StringComparison.OrdinalIgnoreCase))
            {
                // 显存缓存最多吃掉总显存的百分比，默认 50。
                // 例：--vram=25 保守些，留给别的程序；--vram=80 激进占用。
                if (double.TryParse(a["--vram=".Length..],
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out double pct))
                {
                    GpuImageCache.Ratio = Math.Clamp(pct, 5, 80) / 100.0;
                }
            }
            else if (string.Equals(a, "--tightvram", StringComparison.OrdinalIgnoreCase))
            {
                // A/B 对照用：显存缓存紧跟随窗口，离开窗口立刻释放（旧行为）
                GpuImageCache.TightWindow = true;
            }
            else if (string.Equals(a, "--noavx", StringComparison.OrdinalIgnoreCase))
            {
                // A/B 对照用：关掉 CPU 的 AVX2 Mitchell，回落到 WIC 双线性
                CpuResampler.Enabled = false;
            }
            else if (string.Equals(a, "--cmp", StringComparison.OrdinalIgnoreCase))
            {
                // 自检：同一张图分别用 GPU 与 CPU 重采样，比对结果与耗时（写 gpu.log）
                cmp = true;
            }
            else if (string.Equals(a, "--zoomtest", StringComparison.OrdinalIgnoreCase))
            {
                // 自检：逐档检查放大后有没有按规定需求重新取样（写 gpu.log）
                zoomtest = true;
            }
            else if (string.Equals(a, "--sorttest", StringComparison.OrdinalIgnoreCase))
            {
                // 自检：把某个目录下的名称按四种排序方式排一遍写进 gpu.log，
                // 用于确认「自然序」真的把 (11) 排在 (2) 后面
                sorttest = true;
            }
            else if (string.Equals(a, "--nowrap", StringComparison.OrdinalIgnoreCase))
            {
                // A/B 对照用：预载窗口按线性截断，跨首尾时重新解码一批图
                PreviewCache.NoWrap = true;
            }
            else if (string.Equals(a, "--nod3d", StringComparison.OrdinalIgnoreCase))
            {
                // A/B 对照用：关掉 D3D9 互操作（它要求 CUDA 与 D3D9 在同一块 GPU 上，
                // 双显卡笔记本上 D3D9 只认集显，会自动回退）
                GpuAccel.D3dEnabled = false;
            }
            else if (string.Equals(a, "--nogpupref", StringComparison.OrdinalIgnoreCase))
            {
                // 不让程序自动在 Windows 图形首选项里申请独显（见 GpuAccel.RequestGpuPreference）
                GpuAccel.GpuPrefEnabled = false;
            }
            else if (string.Equals(a, "--noreuse", StringComparison.OrdinalIgnoreCase))
            {
                // A/B 对照用：关掉复用位图，回到「每次新建 BitmapSource」的旧路径
                ViewSurface.ReuseEnabled = false;
            }
        }

        base.OnStartup(e);

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

        if (cmp)
        {
            GpuAccel.WriteLog("cmp: " + CpuResampler.SelfTest(openPath));
            Shutdown();
            return;
        }

        if (sorttest)
        {
            GpuAccel.WriteLog(SortSelfTest.Run(openPath));
            Shutdown();
            return;
        }

        MainWindow window = new();
        window.Show();

        // 防止窗口被别的窗口盖住 / 起在后台，看起来像"双击没反应"
        window.Activate();

        if (openPath is not null)
        {
            window.OpenPath(openPath);
        }

        if (zoomtest)
        {
            // 注意这里不能传 openPath 之前未排除开关：它与 --cmp 一样把首个非 -- 参数当图片
            _ = window.ZoomSelfTestAsync(openPath);
        }
        else if (bench)
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
