using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PIKAN;

/// <summary>
/// Win32 注册表 API 的最小 P/Invoke。
///
/// 不用 Microsoft.Win32.Registry：它对 OpenSubKey(writable) 请求的是一整包权限
/// （含 KEY_CREATE_SUB_KEY / READ_CONTROL），实测在这台机器的 UserGpuPreferences
/// 键上会被判 SecurityException；而 RegOpenKeyEx 只请求「读值 + 写值」两项最小
/// 权限是可以过的。权限要多少给多少，多要反而进不去。
/// </summary>
internal static class RegNative
{
    private const string Lib = "advapi32.dll";

    internal const uint KeyQueryValue = 0x0001;
    internal const uint KeySetValue = 0x0002;
    internal const uint RegSz = 1;

    // 注意：必须按无符号 64 位给。写成 new IntPtr(unchecked((int)0x80000001)) 会被
    // 符号扩展成 0xFFFFFFFF80000001，Kernel 那边对不上的句柄直接回 ACCESS_DENIED。
    internal static readonly IntPtr Hkcu = new(unchecked((long)0x80000001L));

    [DllImport(Lib, EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
    internal static extern int OpenKey(IntPtr root, string subKey, uint reserved, uint sam, out IntPtr result);

    [DllImport(Lib, EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    internal static extern int CreateKey(IntPtr root, string subKey, uint reserved, IntPtr cls, uint options,
                                         uint sam, IntPtr secAttr, out IntPtr result, IntPtr disposition);

    [DllImport(Lib, EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    internal static extern int QueryValue(IntPtr key, string name, IntPtr reserved, IntPtr type,
                                          IntPtr data, ref int size);

    [DllImport(Lib, EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
    internal static extern int SetValue(IntPtr key, string name, uint reserved, uint type,
                                        string data, int bytes);

    [DllImport(Lib, EntryPoint = "RegCloseKey")]
    internal static extern int CloseKey(IntPtr key);

    // ---- 令牌诊断：只在写注册表失败时用一次 ----
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationType = 18;
    private const int TokenIsAppContainer = 29;
    private const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport(Lib, SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desired, out IntPtr token);

    [DllImport(Lib, SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info,
                                                   int len, out int needed);

    [DllImport(Lib, CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CloseHandle(IntPtr h);

    internal static string TokenDiag()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr tok) || tok == IntPtr.Zero)
        {
            return "无法打开自身令牌";
        }

        string il = "?";
        try
        {
            GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out int need);
            IntPtr buf = Marshal.AllocHGlobal(need);
            try
            {
                if (GetTokenInformation(tok, TokenIntegrityLevel, buf, need, out _))
                {
                    IntPtr sid = Marshal.ReadIntPtr(buf);
                    if (sid != IntPtr.Zero && ConvertSidToStringSid(sid, out IntPtr s) && s != IntPtr.Zero)
                    {
                        il = Marshal.PtrToStringUni(s) ?? "?";
                        Marshal.FreeHGlobal(s);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception e)
        {
            il = e.GetType().Name;
        }

        int appc = -1;
        int elev = -1;
        IntPtr i4 = Marshal.AllocHGlobal(8);
        try
        {
            if (GetTokenInformation(tok, TokenIsAppContainer, i4, 4, out _))
            {
                appc = Marshal.ReadInt32(i4);
            }

            if (GetTokenInformation(tok, TokenElevationType, i4, 4, out _))
            {
                elev = Marshal.ReadInt32(i4);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(i4);
        }

        CloseHandle(tok);
        return $"完整性={il} AppContainer={appc} ElevationType={elev} User={Environment.UserName}";
    }
}

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
    [DllImport(Lib)] internal static extern int cuMemGetInfo_v2(out ulong free, out ulong total);

    [DllImport(Lib, EntryPoint = "cuCtxCreate_v2")] internal static extern int cuCtxCreate(out IntPtr ctx, uint flags, int dev);
    [DllImport(Lib, EntryPoint = "cuCtxDestroy_v2")] internal static extern int cuCtxDestroy(IntPtr ctx);

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

    // ---- Direct3D 9 互操作：让 kernel 结果直接落进 D3D9 纹理 ----
    //
    // 驱动自带这些导出（实测 CUDA 13.4 / 驱动 617.42 上仍在），
    // 于是「算完即上屏」不必回主机内存：算 → 设备内拷进纹理 → D3DImage 直接显示。

    [DllImport(Lib, EntryPoint = "cuD3D9CtxCreate_v2")]
    internal static extern int cuD3D9CtxCreate(out IntPtr ctx, out int dev, uint flags, IntPtr d3dDevice);

    [DllImport(Lib, EntryPoint = "cuD3D9GetDevices")]
    internal static extern int cuD3D9GetDevices(out uint count, IntPtr devices, uint deviceCount,
                                                IntPtr d3dDevice, uint deviceList);

    [DllImport(Lib)] internal static extern int cuGraphicsD3D9RegisterResource(
        out IntPtr res, IntPtr d3dResource, uint flags);

    [DllImport(Lib)] internal static extern int cuGraphicsUnregisterResource(IntPtr res);

    [DllImport(Lib)] internal static extern int cuGraphicsMapResources(
        uint count, IntPtr resources, IntPtr stream);

    [DllImport(Lib)] internal static extern int cuGraphicsUnmapResources(
        uint count, IntPtr resources, IntPtr stream);

    [DllImport(Lib)] internal static extern int cuGraphicsSubResourceGetMappedArray(
        out IntPtr array, IntPtr res, uint arrayIndex, uint mipLevel);

    [DllImport(Lib, EntryPoint = "cuMemcpy2D_v2")]
    internal static extern int cuMemcpy2D(ref Memcpy2D copy);

    /// <summary>CUDA_MEMCPY2D（64 位布局，共 128 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Memcpy2D
    {
        public ulong SrcXInBytes;
        public ulong SrcY;
        public uint SrcMemoryType;
        public IntPtr SrcHost;
        public ulong SrcDevice;
        public IntPtr SrcArray;
        public ulong SrcPitch;
        public ulong DstXInBytes;
        public ulong DstY;
        public uint DstMemoryType;
        public IntPtr DstHost;
        public ulong DstDevice;
        public IntPtr DstArray;
        public ulong DstPitch;
        public ulong WidthInBytes;
        public ulong Height;
    }

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
    private static ulong _totalMem;

    // ---- Direct3D 9 互操作（零拷贝上屏）----
    private static IntPtr _d3d9;        // IDirect3D9Ex
    private static IntPtr _d3dDevice;   // IDirect3DDevice9Ex
    private static bool _d3dCtx;        // 上下文是否建在 D3D9 设备上

    /// <summary>是否允许走 D3D9 零拷贝路径（--nod3d 关掉，便于 A/B 对比）。</summary>
    public static bool D3dEnabled { get; set; } = true;

    /// <summary>
    /// 是否允许在 D3D9 找不到可互操作适配器时，自动在 Windows 图形首选项里
    /// 给本程序申请高性能 GPU（--nogpupref 关掉）。见 RequestGpuPreference。
    /// </summary>
    public static bool GpuPrefEnabled { get; set; } = true;

    /// <summary>是否真的建成了 D3D9 互操作上下文。只有它为真才能零拷贝上屏。</summary>
    public static bool D3dCtxMode => _d3dCtx;

    /// <summary>IDirect3DDevice9Ex 指针（建纹理用）。</summary>
    public static IntPtr D3dDevice => _d3dDevice;

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

    /// <summary>
    /// 是否已就绪。**不触发初始化** —— CUDA 初始化要 1~2 秒，UI 线程上调用
    /// Available 会被它整个卡住（实测启动冻结 2 秒）。状态栏之类的只读展示用这个，
    /// 初始化由后台的 InitGpuAsync 负责，完成后再刷新一次界面。
    /// </summary>
    public static bool IsReady => _ok;

    /// <summary>设备名（未就绪时是空串），同样不触发初始化。</summary>
    public static string DeviceNameOrEmpty => _device;

    /// <summary>显卡总显存（字节）。显存缓存按它推算自己的配额。</summary>
    public static ulong TotalMemBytes
    {
        get { Ensure(); return _totalMem; }
    }

    /// <summary>
    /// 当前空闲显存（字节），查询失败返回 0。
    /// 配额靠它留出安全余量——浏览器、游戏随时会吃显存，只看总显存会跟它们抢。
    /// </summary>
    public static ulong FreeMemBytes
    {
        get
        {
            Ensure();
            return CudaNative.cuMemGetInfo_v2(out ulong free, out _) == 0 ? free : 0;
        }
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

    internal static void Log(string line)
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

    /// <summary>
    /// 尝试在 D3D9Ex 设备上建立 CUDA 上下文。任一步失败都安静退回普通 CUDA 上下文，
    /// 不影响既有的加速路径——零拷贝只是锦上添花，不是功能依赖。
    /// </summary>
    private static bool TryCreateD3dContext(out int dev)
    {
        dev = -1;
        if (!D3dEnabled)
        {
            Log("d3d: 已被 --nod3d 关闭");
            return false;
        }

        IntPtr d3d = D3d9.Create();
        if (d3d == IntPtr.Zero)
        {
            Log("d3d: Direct3DCreate9Ex 失败，回退纯 CUDA 路径");
            return false;
        }

        // 逐个适配器试，而不是靠厂商 ID 猜。
        //
        // 双显卡笔记本上 D3D9 的适配器列表本身就不稳定：有时会列 NVIDIA 独显，
        // 有时整张表里只剩集显（实测同一台机器两次启动结果不同）。而 vendor ID 也不是
        // 可靠的判据——真正说了算的是 cuD3D9CtxCreate：它成功了才说明这块 GPU
        // 同时被 D3D9 和 CUDA 认同一个物理设备。所以让 CUDA 自己判定最稳。
        uint total = D3d9.AdapterCount(d3d);

        List<int> order = [];
        System.Text.StringBuilder diags = new();

        for (int i = 0; i < (int)total && i < 8; i++)
        {
            uint vendor = D3d9.AdapterVendorId(d3d, (uint)i);
            diags.Append($"#{i}:0x{vendor:X4} ");

            // NVIDIA 优先试，其余排后面：成功的机会最大，也就最快
            if (vendor == 0x10DE)
            {
                order.Insert(0, i);
            }
            else
            {
                order.Add(i);
            }
        }

        Log($"d3d: 枚举到 {total} 个适配器 {diags.ToString().TrimEnd()}");

        foreach (int adapter in order)
        {
            if (!D3d9.CreateDevice(d3d, adapter, out IntPtr dev9))
            {
                Log($"d3d: 适配器 #{adapter} CreateDeviceEx 失败");
                continue;
            }

            int g = CudaNative.cuD3D9GetDevices(out uint cnt, IntPtr.Zero, 0, dev9, 0);
            int r = CudaNative.cuD3D9CtxCreate(out IntPtr ctx, out int d, 0, dev9);

            if (r != 0 || ctx == IntPtr.Zero)
            {
                // CUDA 不认这块设备（多半是集显），换下一个
                Log($"d3d: 适配器 #{adapter} 不可互操作 " +
                    $"(GetDevices={g}/{cnt}) {CudaNative.Message(r)}");
                D3d9.Release(ref dev9);
                continue;
            }

            _ctx = ctx;
            _d3d9 = d3d;
            _d3dDevice = dev9;
            _d3dCtx = true;
            dev = d;
            Log($"d3d: 上下文建在 D3D9Ex 适配器 #{adapter} 上（结果可直接写进纹理）");
            return true;
        }

        D3d9.Release(ref d3d);
        Log("d3d: 没有任何适配器可与 CUDA 互操作，回退纯 CUDA 路径");
        RequestGpuPreference();
        return false;
    }

    /// <summary>
    /// 混合显卡笔记本上独显平时休眠、不接显示输出，而 legacy D3D9 的适配器列表
    /// 只包含「有输出」的适配器——此时 CUDA 和 D3D9 根本遇不到同一块 GPU，
    /// 零拷贝通道无从建立。Windows 图形首选项（设置 → 显示 → 显示卡）能把进程
    /// 固定到高性能 GPU，等价于写 HKCU 的 UserGpuPreferences；写完要重启进程生效，
    /// 所以这一次启动仍走位图路径，下次启动自动进入零拷贝。
    ///
    /// 只在「CUDA 可用但 D3D9 互操作失败」时才会走到这里；单 NVIDIA 卡的台式机
    /// 第一次就能互操作成功，永远不会碰注册表。用户已手动设过的不覆盖。
    /// </summary>
    private static void RequestGpuPreference()
    {
        if (!GpuPrefEnabled)
        {
            return;
        }

        const string sub = @"Software\Microsoft\DirectX\UserGpuPreferences";
        const uint sam = RegNative.KeyQueryValue | RegNative.KeySetValue;

        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0)
            {
                return;
            }

            // 键一般已存在（Windows 自己会建），就不走 CreateKey，少要一份权限
            int r = RegNative.OpenKey(RegNative.Hkcu, sub, 0, sam, out IntPtr key);
            if (r != 0)
            {
                r = RegNative.CreateKey(RegNative.Hkcu, sub, 0, IntPtr.Zero, 0, sam,
                                        IntPtr.Zero, out key, IntPtr.Zero);
            }

            if (r != 0 || key == IntPtr.Zero)
            {
                // 最常见的失败原因是进程完整性级别太低（S-1-16-4096 及以下），
                // 低 IL 进程写不了 HKCU——比如 exe 被打了「低强制标签」时。
                Log($"d3d: 打开图形首选项失败 RegOpenKeyEx={r}（{RegNative.TokenDiag()}）——" +
                    "可手动在「设置 → 系统 → 屏幕 → 显示卡」给本程序选高性能 GPU");
                return;
            }

            try
            {
                // 先读旧值：用户手动设过的就别覆盖
                int size = 0;
                string cur = "";
                if (RegNative.QueryValue(key, exe, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) == 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (RegNative.QueryValue(key, exe, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0)
                        {
                            cur = Marshal.PtrToStringUni(buf) ?? "";
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }

                if (cur.Contains("GpuPreference=2"))
                {
                    return;   // 已经设好了，别动用户的配置
                }

                string val = "GpuPreference=2;";
                int w = RegNative.SetValue(key, exe, 0, RegNative.RegSz, val, (val.Length + 1) * 2);
                if (w != 0)
                {
                    Log($"d3d: 写图形首选项失败 RegSetValueEx={w}（{RegNative.TokenDiag()}）");
                    return;
                }

                Log("d3d: 已在 Windows 图形首选项为本程序申请高性能 GPU，重启后零拷贝生效");
            }
            finally
            {
                RegNative.CloseKey(key);
            }
        }
        catch (Exception ex)
        {
            Log($"d3d: 写图形首选项失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool Init()
    {
        int e = CudaNative.cuInit(0);
        if (e != 0) return Fail($"cuInit 失败：{CudaNative.Message(e)}");

        e = CudaNative.cuDeviceGetCount(out int count);
        if (e != 0 || count <= 0) return Fail("没有检测到 NVIDIA GPU");

        int dev;
        e = CudaNative.cuDeviceGet(out dev, 0);
        if (e != 0) return Fail($"cuDeviceGet 失败：{CudaNative.Message(e)}");

        // 先建一个普通上下文把独显叫醒，再去找 D3D9 互操作的可能。
        //
        // 顺序很关键：混合显卡笔记本上独显平时休眠、不接任何显示输出，
        // 而 legacy D3D9 的适配器列表只包含「有输出」的适配器——此时枚举只能看到集显，
        // 拿它的设备去建 CUDA 上下文自然互操作不了。先把 CUDA 上下文建起来叫醒独显，
        // 再去枚举，NVIDIA 才可能出现在列表里（实测这台机器上有 / 没有差别很大）。
        IntPtr warm = IntPtr.Zero;
        e = CudaNative.cuCtxCreate(out warm, 0, dev);
        if (e != 0) return Fail($"创建 CUDA 上下文失败：{CudaNative.Message(e)}");

        // 注意用临时变量接 TryCreateD3dContext 的返回值：它失败时 out 出来的是 -1，
        // 不能直接覆盖上面 cuDeviceGet 拿到的有效设备号。
        if (TryCreateD3dContext(out int devD3d))
        {
            dev = devD3d;

            // 互操作上下文已由 cuD3D9CtxCreate 接管为当前上下文，暖机用的这份可以退了
            if (CudaNative.cuCtxDestroy(warm) != 0)
            {
                Log("d3d: 暖机上下文销毁失败，切到互操作上下文");
                CudaNative.cuCtxPushCurrent(_ctx);
            }
        }
        else
        {
            _ctx = warm;
        }

        IntPtr nm = Marshal.AllocHGlobal(256);
        CudaNative.cuDeviceGetName(nm, 256, dev);
        string name = Marshal.PtrToStringAnsi(nm) ?? "NVIDIA GPU";
        Marshal.FreeHGlobal(nm);

        CudaNative.cuDeviceTotalMem_v2(out ulong total, dev);
        _totalMem = total;
        CudaNative.cuDeviceGetAttribute(out int ccMaj, 75, dev);
        CudaNative.cuDeviceGetAttribute(out int ccMin, 76, dev);
        CudaNative.cuDeviceGetAttribute(out int sms, 16, dev);
        CudaNative.cuDriverGetVersion(out int ver);

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
        // 1) 先找 exe 旁边的文件：单文件发布时通常没有，但调试时可以直接改 kernel 不重编译
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

        // 2) 回退到嵌入资源：单文件 exe 走这条路（Assembly.Location 为空也照样能读）
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PIKAN.Kernels.pikan.ptx");
            if (s is not null)
            {
                using StreamReader r = new(s);
                return r.ReadToEnd();
            }
        }
        catch
        {
            // 落到空串，由调用方统一报错降级
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
        bool ok = ResizeCore(src, IntPtr.Zero, iw, ih, dst, ow, oh, highQuality);
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

    // ==================================================================
    // 显存常驻：分配 / 上传 / 释放 / 直接拿显存里的图做重采样
    //
    // 目的：预载阶段把解码好的 BGRA 一次性传进显存并常驻，之后每次切图
    // 都直接从这块显存起算，省掉「CopyPixels（CPU 拷几 MB）+ H2D 上传」这两步；
    // 再叠一层结果缓存，来回翻同一张时连 kernel 都不用跑。
    // ==================================================================

    private static int _devHit;
    private static int _devPut;

    /// <summary>在设备上分配 bytes 字节。</summary>
    public static bool DevAlloc(ulong bytes, out IntPtr ptr)
    {
        ptr = IntPtr.Zero;
        if (bytes == 0)
        {
            return false;
        }

        lock (Gate)
        {
            if (!Prepare())
            {
                return false;
            }

            try
            {
                if (CudaNative.cuMemAlloc(out IntPtr p, bytes) != 0)
                {
                    return false;
                }

                ptr = p;
                return true;
            }
            finally
            {
                Done();
            }
        }
    }

    /// <summary>把主机侧的 BGRA 像素上传到已分配的设备缓冲。</summary>
    public static bool DevUpload(IntPtr ptr, byte[] src, int bytes)
    {
        if (ptr == IntPtr.Zero || bytes <= 0 || bytes > src.Length)
        {
            return false;
        }

        lock (Gate)
        {
            if (!Prepare())
            {
                return false;
            }

            try
            {
                GCHandle h = GCHandle.Alloc(src, GCHandleType.Pinned);
                try
                {
                    return CudaNative.cuMemcpyHtoD(ptr, h.AddrOfPinnedObject(), (ulong)bytes) == 0;
                }
                finally
                {
                    h.Free();
                }
            }
            finally
            {
                Done();
            }
        }
    }

    public static void DevFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return;
        }

        lock (Gate)
        {
            if (!Prepare())
            {
                return;
            }

            try
            {
                CudaNative.cuMemFree(ptr);
            }
            finally
            {
                Done();
            }
        }
    }

    /// <summary>源图已在显存里：跳过上传，直接重采样回主机内存。</summary>
    public static bool ResizeDevice(IntPtr devSrc, int iw, int ih, byte[] dst, int ow, int oh, bool highQuality = true)
    {
        bool ok = ResizeCore(null, devSrc, iw, ih, dst, ow, oh, highQuality);
        if (ok)
        {
            _devHit++;
            if (_devHit <= 3 || _devHit % 100 == 0)
            {
                Log($"vram hit #{_devHit}  {iw}x{ih} -> {ow}x{oh}（源在显存，未上传）");
            }
        }
        else
        {
            NoteFailure($"vram resize {iw}x{ih} -> {ow}x{oh}");
        }

        return ok;
    }

    /// <summary>显存缓存的累计计数（上传次数 / 命中次数），bench 收尾汇总用。</summary>
    public static string VramStats => $"上传 {_devPut} 次 · 命中 {_devHit} 次";

    /// <summary>
    /// 记一次显存命中。三条出口（回主机内存 / 留显存 / 写纹理）都要记，
    /// 否则零拷贝路径的命中会被漏掉，看起来像缓存没生效。
    /// </summary>
    public static void NoteHit() => _devHit++;

    /// <summary>记一次「上传入显存」；用于确认预载确实把图放进去了（限流）。</summary>
    public static void NoteUpload(int w, int h, long bytes)
    {
        _devPut++;
        if (_devPut <= 3 || _devPut % 50 == 0)
        {
            Log($"vram put  #{_devPut}  {w}x{h} · {bytes / (1024 * 1024)} MB");
        }
    }

    private static bool ResizeCore(byte[]? src, IntPtr devSrc, int iw, int ih, byte[] dst, int ow, int oh, bool highQuality)
    {
        if (iw <= 0 || ih <= 0 || ow <= 0 || oh <= 0) return false;

        lock (Gate)
        {
            if (!Prepare()) return false;
            try
            {
                if (!PrepareSrc(src, devSrc, out IntPtr inPtr)) return false;
                if (!EnsureBuf(ref _bufB, ref _capB, (ulong)dst.LongLength)) return false;

                GCHandle ho = GCHandle.Alloc(dst, GCHandleType.Pinned);
                try
                {
                    return LaunchResize(inPtr, iw, ih, ow, oh, highQuality) &&
                           CudaNative.cuMemcpyDtoH(ho.AddrOfPinnedObject(), _bufB, (ulong)dst.LongLength) == 0;
                }
                finally
                {
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
    /// 重采样到已注册的 D3D9 纹理：算完只在设备内拷一次，
    /// 不回主机内存、不重建 BitmapSource，WPF 直接拿纹理上屏。
    /// </summary>
    public static bool ResizeToTexture(byte[]? src, IntPtr devSrc, int iw, int ih,
                                       IntPtr res, int ow, int oh, bool highQuality = true)
    {
        if (!_d3dCtx || res == IntPtr.Zero || iw <= 0 || ih <= 0 || ow <= 0 || oh <= 0)
        {
            return false;
        }

        lock (Gate)
        {
            if (!Prepare()) return false;
            try
            {
                if (!PrepareSrc(src, devSrc, out IntPtr inPtr)) return false;
                if (!EnsureBuf(ref _bufB, ref _capB, (ulong)ow * (ulong)oh * 4)) return false;
                return LaunchResize(inPtr, iw, ih, ow, oh, highQuality) &&
                       CopyToTextureCore(res, _bufB, ow, oh);
            }
            finally
            {
                Done();
            }
        }
    }

    /// <summary>
    /// 重采样，但结果留在设备缓冲里不回主机。
    /// 之后由 UI 线程用 CopyResultTo 直接落进 WriteableBitmap 的后备内存，
    /// 于是省掉「中间 byte[] + BitmapSource.Create 再拷一遍」这两道。
    /// </summary>
    public static bool ResizeToDevice(byte[]? src, IntPtr devSrc, int iw, int ih, int ow, int oh, bool highQuality = true)
    {
        if (iw <= 0 || ih <= 0 || ow <= 0 || oh <= 0) return false;

        lock (Gate)
        {
            if (!Prepare()) return false;
            try
            {
                if (!PrepareSrc(src, devSrc, out IntPtr inPtr)) return false;
                if (!EnsureBuf(ref _bufB, ref _capB, (ulong)ow * (ulong)oh * 4)) return false;
                return LaunchResize(inPtr, iw, ih, ow, oh, highQuality);
            }
            finally
            {
                Done();
            }
        }
    }

    /// <summary>
    /// 把上一次 ResizeToDevice 的结果拷到指定的主机地址。
    /// dst 通常是 WriteableBitmap 的 BackBuffer——写入即上屏，不再有中间数组。
    /// </summary>
    public static bool CopyResultTo(IntPtr dst, ulong bytes)
    {
        if (dst == IntPtr.Zero || bytes == 0 || _bufB == IntPtr.Zero) return false;

        lock (Gate)
        {
            if (!Prepare()) return false;
            try
            {
                return CudaNative.cuMemcpyDtoH(dst, _bufB, bytes) == 0;
            }
            finally
            {
                Done();
            }
        }
    }

    /// <summary>源要么已在显存，要么从主机上传一次；返回设备指针。</summary>
    private static bool PrepareSrc(byte[]? src, IntPtr devSrc, out IntPtr inPtr)
    {
        inPtr = devSrc;
        if (inPtr != IntPtr.Zero)
        {
            return true;
        }

        if (src is null)
        {
            return false;
        }

        ulong nin = (ulong)src.LongLength;
        if (!EnsureBuf(ref _bufA, ref _capA, nin)) return false;

        GCHandle hi = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            if (CudaNative.cuMemcpyHtoD(_bufA, hi.AddrOfPinnedObject(), nin) != 0) return false;
        }
        finally
        {
            hi.Free();
        }

        inPtr = _bufA;
        return true;
    }

    private static bool LaunchResize(IntPtr inPtr, int iw, int ih, int ow, int oh, bool highQuality)
    {
        SetSlot(0, (ulong)inPtr);
        SetSlot(1, (ulong)_bufB);
        SetSlot(2, (uint)iw);
        SetSlot(3, (uint)ih);
        SetSlot(4, (uint)ow);
        SetSlot(5, (uint)oh);

        return Launch(highQuality ? _fnMitchell : _fnBilinear,
                      (uint)((ow + 15) / 16), (uint)((oh + 15) / 16), 16, 16);
    }

    // ==================================================================
    // D3D9 纹理的注册 / 写入
    // ==================================================================

    /// <summary>把 D3D9 纹理登记给 CUDA。之后就能往里直接写了。</summary>
    public static bool RegisterTexture(IntPtr tex, out IntPtr res)
    {
        res = IntPtr.Zero;
        if (!_d3dCtx || tex == IntPtr.Zero) return false;

        lock (Gate)
        {
            if (!Prepare()) return false;
            try
            {
                int e = CudaNative.cuGraphicsD3D9RegisterResource(out IntPtr r, tex, 0);
                if (e != 0 || r == IntPtr.Zero)
                {
                    Log($"d3d: 注册纹理失败 {CudaNative.Message(e)}");
                    return false;
                }

                res = r;
                return true;
            }
            finally
            {
                Done();
            }
        }
    }

    public static void UnregisterTexture(IntPtr res)
    {
        if (!_d3dCtx || res == IntPtr.Zero) return;

        lock (Gate)
        {
            if (!Prepare()) return;
            try
            {
                CudaNative.cuGraphicsUnregisterResource(res);
            }
            finally
            {
                Done();
            }
        }
    }

    /// <summary>
    /// 设备缓冲 → 纹理。整段都在显存里完成，主机内存完全不参与。
    /// 纹理必须先用 RegisterTexture 登记过。
    /// </summary>
    public static bool CopyToTexture(IntPtr res, IntPtr devSrc, int w, int h)
    {
        if (!_d3dCtx || res == IntPtr.Zero || devSrc == IntPtr.Zero) return false;

        lock (Gate)
        {
            if (!Prepare()) return false;
            try
            {
                return CopyToTextureCore(res, devSrc, w, h);
            }
            finally
            {
                Done();
            }
        }
    }

    private static bool CopyToTextureCore(IntPtr res, IntPtr devSrc, int w, int h)
    {
        IntPtr box = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(box, res);

            int e = CudaNative.cuGraphicsMapResources(1, box, IntPtr.Zero);
            if (e != 0)
            {
                Log($"d3d: 映射纹理失败 {CudaNative.Message(e)}");
                return false;
            }

            bool ok = false;
            try
            {
                e = CudaNative.cuGraphicsSubResourceGetMappedArray(out IntPtr arr, res, 0, 0);
                if (e != 0 || arr == IntPtr.Zero)
                {
                    Log($"d3d: 取映射数组失败 {CudaNative.Message(e)}");
                    return false;
                }

                CudaNative.Memcpy2D cp = new()
                {
                    SrcMemoryType = 2,                 // CU_MEMORYTYPE_DEVICE
                    SrcDevice = (ulong)devSrc,
                    SrcPitch = (ulong)(w * 4),
                    DstMemoryType = 3,                 // CU_MEMORYTYPE_ARRAY
                    DstArray = arr,
                    WidthInBytes = (ulong)(w * 4),
                    Height = (ulong)h,
                };

                e = CudaNative.cuMemcpy2D(ref cp);
                ok = e == 0;
                if (!ok)
                {
                    Log($"d3d: 写纹理失败 {CudaNative.Message(e)}");
                }
            }
            finally
            {
                CudaNative.cuGraphicsUnmapResources(1, box, IntPtr.Zero);
            }

            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(box);
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
