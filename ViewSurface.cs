using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// 主视图的复用位图。
///
/// 原先每次 GPU 精修都要：新开一个几 MB 的 byte[] → CUDA 回读进去 → BitmapSource.Create
/// 再拷一遍 → WPF 把新位图当新纹理整个上传。三道拷贝外加一次大对象分配（8MB 直接进
/// 大对象堆，翻页多了 GC 压力明显）。
///
/// 改成：kernel 结果先留在显存，UI 线程再把它直接写进这张 WriteableBitmap 的后备内存。
/// 于是中间数组没了，位图对象本身也复用——尺寸不变时 WPF 只更新脏区，
/// 不必重新分配纹理。
///
/// 注意：这张位图**不进结果缓存**，因为它的内容会被下一次精修覆盖。
/// </summary>
public sealed class ViewSurface
{
    /// <summary>是否启用复用（--noreuse 关掉，回到每次新建位图的旧路径，便于 A/B 对比）。</summary>
    public static bool ReuseEnabled { get; set; } = true;

    private static ViewSurface? _inst;

    private WriteableBitmap? _bmp;

    public static ViewSurface Instance => _inst ??= new ViewSurface();

    /// <summary>
    /// 把「显存里算好的结果」提交到复用位图并返回它。必须在 UI 线程调用。
    /// 失败返回 null，调用方应回退到原来的新建位图路径。
    /// </summary>
    public BitmapSource? Commit(int ow, int oh)
    {
        if (ow <= 0 || oh <= 0)
        {
            return null;
        }

        if (_bmp is null || _bmp.PixelWidth != ow || _bmp.PixelHeight != oh)
        {
            _bmp = new WriteableBitmap(ow, oh, 96, 96, PixelFormats.Bgra32, null);
        }

        // 行距必须恰好是 宽×4 才能整块回读；个别情况 D3D 会加对齐，
        // 那时交回旧路径处理，不为此写逐行拷贝。
        int stride = _bmp.BackBufferStride;
        if (stride != ow * 4)
        {
            return null;
        }

        ulong bytes = (ulong)ow * (ulong)oh * 4;

        _bmp.Lock();
        try
        {
            if (!GpuAccel.CopyResultTo(_bmp.BackBuffer, bytes))
            {
                return null;
            }

            // 保险：万一回读没写进去（四个采样点全 0），就交回旧路径，
            // 顶多慢一点，总好过显示一片黑。
            if (AllZero(_bmp.BackBuffer, stride, ow, oh))
            {
                return null;
            }

            _bmp.AddDirtyRect(new Int32Rect(0, 0, ow, oh));
        }
        finally
        {
            _bmp.Unlock();
        }

        return _bmp;
    }

    private static bool AllZero(IntPtr p, int stride, int w, int h)
    {
        int[] xs = { w / 4, w / 2, w * 3 / 4 };
        int[] ys = { h / 4, h / 2, h * 3 / 4 };

        foreach (int y in ys)
        {
            foreach (int x in xs)
            {
                if (Marshal.ReadInt32(p, y * stride + x * 4) != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>换图库 / 关掉复用时丢掉复用位图，避免占着内存。</summary>
    public void Reset() => _bmp = null;
}
