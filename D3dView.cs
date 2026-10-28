using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PIKAN;

/// <summary>
/// 主视图的 D3DImage 零拷贝通道。
///
/// 原来的「算完即上屏」链路是：kernel → 回读主机内存 → 包成 BitmapSource
/// → WPF 再当新纹理上传显存。本来就在显存里的像素白白绕了一趟主机内存。
///
/// 这条通道把中间步骤全砍掉：
///   CUDA kernel 算进设备缓冲 → 设备内拷进 D3D9 纹理 → D3DImage 拿这张纹理上屏。
/// 主机内存从头到尾不参与，也没有中间 byte[] 和大对象分配。
///
/// 依赖两个前提，任一不满足就安静回落到位图路径（调用方不需要特殊处理）：
///   ① CUDA 上下文必须建在 D3D9Ex 设备上（GpuAccel.D3dCtxMode），否则无法注册纹理
///   ② D3D9 枚举到的 NVIDIA 适配器要与 CUDA 设备同一块 GPU
///      —— 部分双显卡笔记本上 D3D9 只暴露集显，此时 GpuAccel 早就回退了
///
/// 本类的成员都必须在 UI 线程使用（D3DImage 是 DispatcherObject）。
/// </summary>
public sealed class D3dView
{
    private static D3dView? _inst;

    /// <summary>
    /// 通道开关。关掉后回落到「复用位图 / 新建位图」路径，用于 A/B 对比。
    /// 注意真正的总闸是 GpuAccel.D3dEnabled（--nod3d），关掉它连 D3D9 设备都不会创建。
    /// </summary>
    public static bool Enabled { get; set; } = true;

    private D3DImage? _img;
    private IntPtr _tex;      // IDirect3DTexture9
    private IntPtr _surf;     // 纹理的 0 级表面，交给 D3DImage 的就是它
    private IntPtr _res;      // CUDA 侧的资源句柄（CUgraphicsResource）
    private int _w;
    private int _h;
    private int _fails;       // 连续失败次数，超过阈值就别再试了

    private D3dView() { }

    public static D3dView Instance => _inst ??= new D3dView();

    /// <summary>当前纹理对应的 CUDA 资源句柄。纹理未就绪时为 Zero。</summary>
    public IntPtr Resource => _res;

    /// <summary>是否处于可用状态（已备好纹理且曾成功上屏）。状态栏诊断用。</summary>
    public bool Ready => _surf != IntPtr.Zero && _res != IntPtr.Zero && _img is not null;

    /// <summary>
    /// 准备好一张 w×h 的纹理，返回可以赋给 Image.Source 的 D3DImage。
    /// 尺寸不变时复用既有纹理，不重建。失败返回 null。
    /// </summary>
    public ImageSource? Prepare(int w, int h)
    {
        if (!Enabled || w <= 0 || h <= 0 || _fails >= 3)
        {
            return null;
        }

        // 上下文没建在 D3D9 设备上（无 NVIDIA、被 --nod3d 关掉、初始化失败）→ 直接回落
        if (!GpuAccel.D3dCtxMode)
        {
            return null;
        }

        IntPtr dev = GpuAccel.D3dDevice;
        if (dev == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return EnsureSurface(dev, w, h) ? _img : null;
        }
        catch (Exception ex)
        {
            GpuAccel.Log($"d3dview: 准备纹理异常 {ex.GetType().Name}: {ex.Message}");
            _fails++;
            return null;
        }
    }

    /// <summary>
    /// 纹理写完之后标记脏区，WPF 会在下一帧把它拷到自己的渲染目标。
    /// 调用方应先把 Prepare 的返回值交给 Image.Source，再调这个，
    /// 否则脏区标记打在没有宿主的对象上不生效。
    /// </summary>
    public bool Present(int w, int h)
    {
        if (_img is null || _surf == IntPtr.Zero || w <= 0 || h <= 0)
        {
            return false;
        }

        try
        {
            _img.Lock();
            try
            {
                _img.AddDirtyRect(new Int32Rect(0, 0, w, h));
            }
            finally
            {
                _img.Unlock();
            }

            _fails = 0;
            return true;
        }
        catch (Exception ex)
        {
            GpuAccel.Log($"d3dview: 标记脏区失败 {ex.GetType().Name}: {ex.Message}");
            _fails++;
            return false;
        }
    }

    /// <summary>换图库 / 关闭窗口时释放，别让显存里的纹理一直挂着。</summary>
    public void Reset() => ReleaseSurface();

    // ==================================================================
    // 内部
    // ==================================================================

    private bool EnsureSurface(IntPtr dev, int w, int h)
    {
        if (_surf != IntPtr.Zero && _res != IntPtr.Zero && _img is not null && _w == w && _h == h)
        {
            return true;
        }

        ReleaseSurface();

        if (!D3d9.CreateTexture(dev, w, h, out IntPtr tex))
        {
            Fail($"CreateTexture {w}x{h} 失败（{D3d9.TestCooperativeLevel(dev)}）");
            return false;
        }

        IntPtr surf = D3d9.GetSurfaceLevel(tex);
        if (surf == IntPtr.Zero)
        {
            D3d9.Release(ref tex);
            Fail("GetSurfaceLevel 失败");
            return false;
        }

        // 登记给 CUDA —— 之后 kernel 结果就能直接拷进这张纹理
        if (!GpuAccel.RegisterTexture(tex, out IntPtr res))
        {
            D3d9.Release(ref surf);
            D3d9.Release(ref tex);
            Fail("注册纹理失败");
            return false;
        }

        _tex = tex;
        _surf = surf;
        _res = res;
        _w = w;
        _h = h;

        return Attach();
    }

    private bool Attach()
    {
        if (_img is null)
        {
            _img = new D3DImage(96, 96);
            _img.IsFrontBufferAvailableChanged += OnFrontBufferChanged;
        }

        try
        {
            _img.Lock();
            _img.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surf);
            _img.Unlock();
            return true;
        }
        catch (Exception ex)
        {
            Fail($"SetBackBuffer 失败 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void ReleaseSurface()
    {
        Detach();

        if (_res != IntPtr.Zero)
        {
            GpuAccel.UnregisterTexture(_res);
            _res = IntPtr.Zero;
        }

        D3d9.Release(ref _surf);
        D3d9.Release(ref _tex);
        _w = 0;
        _h = 0;
    }

    /// <summary>
    /// 把 back buffer 摘掉。锁屏、切显示模式、远程桌面结束时必须这么做，
    /// 否则 D3DImage 会一直持有已经失效的表面。
    /// </summary>
    private void Detach()
    {
        if (_img is null)
        {
            return;
        }

        try
        {
            _img.Lock();
            _img.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            _img.Unlock();
        }
        catch
        {
            // 已经失效时 Lock 可能失败，忽略即可
        }
    }

    /// <summary>
    /// 前台缓冲可用性的变化：不可用时（锁屏 / 显示模式切换）丢掉资源，
    /// 恢复后再挂一次。纹理内容此时已失效，下一次精修会重算，不会显示错图。
    /// </summary>
    private void OnFrontBufferChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        GpuAccel.Log($"d3dview: IsFrontBufferAvailable → {_img?.IsFrontBufferAvailable}");
        ReleaseSurface();
    }

    private void Fail(string why)
    {
        _fails++;
        GpuAccel.Log($"d3dview: {why}（连续失败 {_fails} 次）");
    }
}
