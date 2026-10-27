using System.IO;
using System.Runtime.InteropServices;

namespace PIKAN;

/// <summary>
/// CUDA Driver API 的 P/Invoke 声明。
///
/// 直接链到驱动自带的 nvcuda.dll（System32 下，随 NVIDIA 驱动安装），
/// 因此不需要装 CUDA Toolkit / nvcc / nvrtc，也不引入任何 NuGet 包。
/// </summary>
internal static class CudaNative
{
    private const string Lib = "nvcuda";

    [DllImport(Lib)] internal static extern int cuInit(uint flags);
    [DllImport(Lib)] internal static extern int cuDriverGetVersion(out int version);
    [DllImport(Lib)] internal static extern int cuDeviceGetCount(out int count);
    [DllImport(Lib)] internal static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport(Lib)] internal static extern int cuDeviceGetName(IntPtr name, int len, int dev);
    [DllImport(Lib)] internal static extern int cuDeviceTotalMem_v2(out ulong bytes, int dev);
    [DllImport(Lib)] internal static extern int cuDeviceGetAttribute(out int value, int attrib, int dev);

    [DllImport(Lib, EntryPoint = "cuCtxCreate_v2")] internal static extern int cuCtxCreate(out IntPtr ctx, uint flags, int dev);
    [DllImport(Lib, EntryPoint = "cuCtxPushCurrent_v2")] internal static extern int cuCtxPushCurrent(IntPtr ctx);
    [DllImport(Lib, EntryPoint = "cuCtxPopCurrent_v2")] internal static extern int cuCtxPopCurrent(out IntPtr ctx);

    [DllImport(Lib)] internal static extern int cuModuleLoadData(out IntPtr module, IntPtr image);
    [DllImport(Lib)] internal static extern int cuModuleGetFunction(out IntPtr fn, IntPtr module, string name);

    [DllImport(Lib, EntryPoint = "cuMemAlloc_v2")] internal static extern int cuMemAlloc(out IntPtr ptr, ulong size);
    [DllImport(Lib, EntryPoint = "cuMemFree_v2")] internal static extern int cuMemFree(IntPtr ptr);
    [DllImport(Lib, EntryPoint = "cuMemcpyHtoD_v2")] internal static extern int cuMemcpyHtoD(IntPtr dst, IntPtr src, ulong count);
    [DllImport(Lib, EntryPoint = "cuMemcpyDtoH_v2")] internal static extern int cuMemcpyDtoH(IntPtr dst, IntPtr src, ulong count);

    [DllImport(Lib)] internal static extern int cuLaunchKernel(
        IntPtr fn, uint gx, uint gy, uint gz, uint bx, uint by, uint bz,
        uint sharedMemBytes, IntPtr stream, IntPtr kernelParams, IntPtr extra);

    [DllImport(Lib)] internal static extern int cuCtxSynchronize();
    [DllImport(Lib)] internal static extern int cuGetErrorString(int err, out IntPtr str);

    internal static string Message(int code)
    {
        cuGetErrorString(code, out IntPtr p);
        return p == IntPtr.Zero ? code.ToString() : Marshal.PtrToStringAnsi(p) ?? code.ToString();
    }
}

/// <summary>
/// GPU 加速的执行层：CUDA 上下文 + PTX JIT + kernel 调度 + 设备缓冲复用。
///
/// 设计要点：
///   · 进程级单例，初始化只做一次；任何一步失败都安静降级（Available=false），
///     上层回落到 WIC / WPF 的既有路径，功能不受影响。
///   · PTX 是文本，加载失败时按 sm_86 → sm_75 → sm_70 依次改写 .target 重试，
///     从而覆盖 Volta 及之后的绝大多数 NVIDIA 显卡。
///   · CUDA 上下文是线程相关的：每次调用先 PushCurrent 再 PopCurrent，
///     并用一把全局锁串行化，保证预载 / 主视图 / 过渡动画多线程调用时安全。
///   · 设备缓冲按容量复用，避免每次 cuMemAlloc 的系统调用开销。
/// </summary>
public static class GpuAccel
{
    private static readonly object Gate = new();

    private static bool _tried;
    private static bool _ok;
    private static IntPtr _ctx;

    private static IntPtr _fnMitchell;
    private static IntPtr _fnBilinear;
    private static IntPtr _fnGaussH;
    private static IntPtr _fnGaussV;

    private static IntPtr _bufA;
    private static IntPtr _bufB;
    private static IntPtr _bufW;
    private static ulong _capA;
    private static ulong _capB;
    private static ulong _capW;

    private static readonly IntPtr[] Slots = new IntPtr[6];
    private static IntPtr _slotMem;

    private static string _device = "";
    private static string _failReason = "";

    /// <summary>CUDA 是否可用（首次访问时完成初始化）。</summary>
    public static bool Available
    {
        get { Ensure(); return _ok; }
    }

    /// <summary>形如 "NVIDIA GeForce RTX 3080 Laptop GPU · 16GB · sm_8.6"。</summary>
    public static string DeviceName
    {
        get { Ensure(); return _device; }
    }

    public static string FailReason
    {
        get { Ensure(); return _failReason; }
    }

    /// <summary>"CUDA" 或 "CPU"。</summary>
    public static string Backend
    {
        get { Ensure(); return _ok ? "CUDA" : "CPU"; }
    }

    public static void Touch() => Ensure();

    // ---- 轻量诊断日志：只记初始化与异常，正常路径几乎不写盘 ----

    private static int _okCount;
    private static int _blurOnly;
    private static int _failLogged;

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PIKAN", "gpu.log");

    private static void Log(string line)
    {
        try
        {
            string dir = Path.GetDirectoryName(LogPath) ?? "";
            if (dir.Length > 0)
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
        catch
        {
            // 日志写不了就算了，不能影响看图
        }
    }

    // ==================================================================
    // 初始化
    // ==================================================================

    private static void Ensure()
    {
        if (_tried)
        {
            return;
        }

        lock (Gate)
        {
            if (_tried)
            {
                return;
            }

            _tried = true;
            _ok = Init();
        }
    }

    private static bool Init()
    {
        int e = CudaNative.cuInit(0);
        if (e != 0) return Fail($"cuInit 失败：{CudaNative.Message(e)}");

        e = CudaNative.cuDeviceGetCount(out int count);
        if (e != 0 || count <= 0) return Fail("没有检测到 NVIDIA GPU");

        e = CudaNative.cuDeviceGet(out int dev, 0);
        if (e != 0) return Fail($"cuDeviceGet 失败：{CudaNative.Message(e)}");

        IntPtr nm = Marshal.AllocHGlobal(256);
        CudaNative.cuDeviceGetName(nm, 256, dev);
        string name = Marshal.PtrToStringAnsi(nm) ?? "NVIDIA GPU";
        Marshal.FreeHGlobal(nm);

        CudaNative.cuDeviceTotalMem_v2(out ulong total, dev);
        CudaNative.cuDeviceGetAttribute(out int ccMaj, 75, dev);
        CudaNative.cuDeviceGetAttribute(out int ccMin, 76, dev);
        CudaNative.cuDeviceGetAttribute(out int sms, 16, dev);
        CudaNative.cuDriverGetVersion(out int ver);

        e = CudaNative.cuCtxCreate(out _ctx, 0, dev);
        if (e != 0) return Fail($"创建 CUDA 上下文失败：{CudaNative.Message(e)}");

        string ptx = LoadPtx();
        if (ptx.Length == 0) return Fail("找不到 Kernels/pikan.ptx");

        // 按架构从新到旧尝试：PTX 是文本，改 .target 就能让驱动为老卡重新 JIT
        IntPtr mod = IntPtr.Zero;
        foreach (string arch in new[] { "sm_86", "sm_75", "sm_70" })
        {
            IntPtr p = Marshal.StringToHGlobalAnsi(ptx.Replace(".target sm_86", ".target " + arch));
            int r = CudaNative.cuModuleLoadData(out IntPtr m, p);
            Marshal.FreeHGlobal(p);
            if (r == 0)
            {
                mod = m;
                break;
            }

            if (arch == "sm_70")
            {
                return Fail($"PTX JIT 失败：{CudaNative.Message(r)}（需要 Volta 及以上架构）");
            }
        }

        if (!Resolve(mod, "resize_mitchell", ref _fnMitchell) ||
            !Resolve(mod, "resize_bilinear", ref _fnBilinear) ||
            !Resolve(mod, "gauss_h", ref _fnGaussH) ||
            !Resolve(mod, "gauss_v", ref _fnGaussV))
        {
            return false;
        }

        _slotMem = Marshal.AllocHGlobal(Slots.Length * 8);
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i] = IntPtr.Add(_slotMem, i * 8);
        }

        _device = $"{name} · {total / (1024 * 1024 * 1024)}GB · sm_{ccMaj}{ccMin} · {sms} SM · CUDA {ver / 1000}.{(ver % 1000) / 10}";
        Log($"init ok  {_device}");
        return true;
    }

    private static bool Resolve(IntPtr mod, string name, ref IntPtr slot)
    {
        int e = CudaNative.cuModuleGetFunction(out IntPtr fn, mod, name);
        if (e != 0)
        {
            Fail($"解析 kernel {name} 失败：{CudaNative.Message(e)}");
            return false;
        }

        slot = fn;
        return true;
    }

    private static bool Fail(string reason)
    {
        _failReason = reason;
        Log("init fail  " + reason);
        return false;
    }

    /// <summary>给上层用的诊断写入口。</summary>
    public static void WriteLog(string line) => Log(line);

    /// <summary>记录一次失败的 GPU 调用（限流，避免刷屏）。</summary>
    private static void NoteFailure(string what)
    {
        if (_failLogged >= 8)
        {
            return;
        }

        _failLogged++;
        Log($"call fail  {what}  (第 {_failLogged} 次，之后不再记录)");
    }

    private static string LoadPtx()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Kernels", "pikan.ptx"),
            Path.Combine(AppContext.BaseDirectory, "pikan.ptx")
        };

        foreach (string c in candidates)
        {
            try
            {
                if (File.Exists(c))
                {
                    return File.ReadAllText(c);
                }
            }
            catch
            {
                // 换下一个候选路径
            }
        }

        return "";
    }

    // ==================================================================
    // kernel 调用
    // ==================================================================

    private static bool Prepare()
    {
        Ensure();
        if (!_ok) return false;
        return CudaNative.cuCtxPushCurrent(_ctx) == 0;
    }

    private static void Done()
    {
        CudaNative.cuCtxPopCurrent(out _);
    }

    private static void SetSlot(int i, ulong v) => Marshal.WriteInt64(Slots[i], (long)v);

    private static bool Launch(IntPtr fn, uint gx, uint gy, uint bx, uint by)
    {
        GCHandle pin = GCHandle.Alloc(Slots, GCHandleType.Pinned);
        try
        {
            int e = CudaNative.cuLaunchKernel(fn, gx, gy, 1, bx, by, 1, 0,
                                             IntPtr.Zero, pin.AddrOfPinnedObject(), IntPtr.Zero);
            if (e != 0) return false;
            return CudaNative.cuCtxSynchronize() == 0;
        }
        finally
        {
            pin.Free();
        }
    }

    private static bool EnsureBuf(ref IntPtr buf, ref ulong cap, ulong need)
    {
        if (cap >= need && buf != IntPtr.Zero)
        {
            return true;
        }

        if (buf != IntPtr.Zero)
        {
            CudaNative.cuMemFree(buf);
            buf = IntPtr.Zero;
            cap = 0;
        }

        // 留一点余量，避免尺寸小幅变化时反复重分配
        ulong alloc = need + (need / 4) + 65536;
        if (CudaNative.cuMemAlloc(out IntPtr p, alloc) != 0)
        {
            return false;
        }

        buf = p;
        cap = alloc;
        return true;
    }

    /// <summary>
    /// GPU 重采样。src 为 BGRA32（iw×ih×4），dst 为 ow×oh×4 的输出缓冲。
    /// highQuality=true 走 Mitchell-Netravali（带 area sampling），false 走双线性。
    /// </summary>
    public static bool Resize(byte[] src, int iw, int ih, byte[] dst, int ow, int oh, bool highQuality = true)
    {
        bool ok = ResizeCore(src, iw, ih, dst, ow, oh, highQuality);
        if (ok)
        {
            _okCount++;
            if (_okCount <= 3 || _okCount % 100 == 0)
            {
                Log($"resize ok  #{_okCount}  {iw}x{ih} -> {ow}x{oh}");
            }
        }
        else
        {
            NoteFailure($"resize {iw}x{ih} -> {ow}x{oh}");
        }

        return ok;
    }

    private static bool ResizeCore(byte[] src, int iw, int ih, byte[] dst, int ow, int oh, bool highQuality)
    {
        if (iw <= 0 || ih <= 0 || ow <= 0 || oh <= 0) return false;

        lock (Gate)
        {
            if (!Prepare()) return false;
            try
            {
                ulong nin = (ulong)src.LongLength;
                ulong nout = (ulong)dst.LongLength;
                if (!EnsureBuf(ref _bufA, ref _capA, nin)) return false;
                if (!EnsureBuf(ref _bufB, ref _capB, nout)) return false;

                GCHandle hi = GCHandle.Alloc(src, GCHandleType.Pinned);
                GCHandle ho = GCHandle.Alloc(dst, GCHandleType.Pinned);
                try
                {
                    if (CudaNative.cuMemcpyHtoD(_bufA, hi.AddrOfPinnedObject(), nin) != 0) return false;

                    SetSlot(0, (ulong)_bufA);
                    SetSlot(1, (ulong)_bufB);
                    SetSlot(2, (uint)iw);
                    SetSlot(3, (uint)ih);
                    SetSlot(4, (uint)ow);
                    SetSlot(5, (uint)oh);

                    if (!Launch(highQuality ? _fnMitchell : _fnBilinear,
                                (uint)((ow + 15) / 16), (uint)((oh + 15) / 16), 16, 16))
                    {
                        return false;
                    }

                    return CudaNative.cuMemcpyDtoH(ho.AddrOfPinnedObject(), _bufB, nout) == 0;
                }
                finally
                {
                    hi.Free();
                    ho.Free();
                }
            }
            finally
            {
                Done();
            }
        }
    }

    /// <summary>
    /// GPU 分离高斯模糊：src → 水平(tmp) → 垂直(dst)，三块都是 w×h×4。
    /// tmp 可以为 null（内部复用另一块设备缓冲时仍需要，故要求调用方给出）。
    /// </summary>
    public static bool Blur(byte[] src, byte[] tmp, byte[] dst, int w, int h, double sigma)
    {
        bool ok = BlurCore(src, tmp, dst, w, h, sigma);
        if (ok)
        {
            _blurOnly++;
            if (_blurOnly <= 3 || _blurOnly % 100 == 0)
            {
                Log($"blur ok  #{_blurOnly}  {w}x{h} sigma={sigma:F1}");
            }
        }
        else
        {
            NoteFailure($"blur {w}x{h} sigma={sigma:F1}");
        }

        return ok;
    }

    private static bool BlurCore(byte[] src, byte[] tmp, byte[] dst, int w, int h, double sigma)
    {
        if (w <= 0 || h <= 0 || sigma <= 0) return false;

        float[] wt = BuildWeights(sigma, out int r);

        lock (Gate)
        {
            if (!Prepare()) return false;
            try
            {
                ulong n = (ulong)src.LongLength;
                ulong nw = (ulong)(wt.Length * 4);
                if (!EnsureBuf(ref _bufA, ref _capA, n)) return false;
                if (!EnsureBuf(ref _bufB, ref _capB, n)) return false;
                if (!EnsureBuf(ref _bufW, ref _capW, nw)) return false;

                GCHandle hi = GCHandle.Alloc(src, GCHandleType.Pinned);
                GCHandle ht = GCHandle.Alloc(tmp, GCHandleType.Pinned);
                GCHandle ho = GCHandle.Alloc(dst, GCHandleType.Pinned);
                GCHandle hw = GCHandle.Alloc(wt, GCHandleType.Pinned);
                try
                {
                    if (CudaNative.cuMemcpyHtoD(_bufA, hi.AddrOfPinnedObject(), n) != 0) return false;
                    if (CudaNative.cuMemcpyHtoD(_bufW, hw.AddrOfPinnedObject(), nw) != 0) return false;

                    SetSlot(0, (ulong)_bufA);
                    SetSlot(1, (ulong)_bufB);
                    SetSlot(2, (ulong)_bufW);
                    SetSlot(3, (uint)w);
                    SetSlot(4, (uint)h);
                    SetSlot(5, (uint)r);
                    if (!Launch(_fnGaussH, (uint)((w + 15) / 16), (uint)((h + 15) / 16), 16, 16)) return false;

                    SetSlot(0, (ulong)_bufB);
                    SetSlot(1, (ulong)_bufA);
                    if (!Launch(_fnGaussV, (uint)((w + 15) / 16), (uint)((h + 15) / 16), 16, 16)) return false;

                    if (CudaNative.cuMemcpyDtoH(ht.AddrOfPinnedObject(), _bufB, n) != 0) return false;
                    return CudaNative.cuMemcpyDtoH(ho.AddrOfPinnedObject(), _bufA, n) == 0;
                }
                finally
                {
                    hi.Free();
                    ht.Free();
                    ho.Free();
                    hw.Free();
                }
            }
            finally
            {
                Done();
            }
        }
    }

    /// <summary>高斯权重：半径取 3σ，并归一化使权重和为 1。</summary>
    internal static float[] BuildWeights(double sigma, out int r)
    {
        r = Math.Clamp((int)Math.Ceiling(3 * sigma), 1, 64);
        float[] w = new float[2 * r + 1];
        double sum = 0;
        for (int k = -r; k <= r; k++)
        {
            double v = Math.Exp(-(k * k) / (2 * sigma * sigma));
            w[k + r] = (float)v;
            sum += v;
        }

        for (int i = 0; i < w.Length; i++)
        {
            w[i] = (float)(w[i] / sum);
        }

        return w;
    }
}
