using System.Runtime.InteropServices;

namespace PIKAN;

/// <summary>
/// Direct3D 9Ex 的最小封装。
///
/// 为什么是 D3D9 而不是 D3D11：WPF 的 D3DImage 只接受 IDirect3DSurface9，
/// 而 CUDA 又可以直接往 D3D9 纹理里写。两者一对接，「GPU 算完」到「上屏」
/// 就不需要再回主机内存绕一圈（省掉回读 + BitmapSource 重建 + WPF 重新上传纹理）。
///
/// 整个 DirectX 只用到 4 个方法，所以手写 vtable 调用，
/// 不引入任何 NuGet 依赖，便携版体积不受影响。
/// </summary>
internal static class D3d9
{
    private const uint D3dSdkVersion = 32;                 // D3D_SDK_VERSION
    private const uint DevTypeHal = 1;                     // D3DDEVTYPE_HAL
    private const uint CreateHardwareVertexProcessing = 0x40;
    private const uint CreateMultithreaded = 0x4;
    private const uint CreateFpuPreserve = 0x2;            // 不让 D3D 改 FPU 精度（WPF 要求）
    private const uint SwapEffectDiscard = 1;
    private const uint FmtA8R8G8B8 = 21;
    private const uint FmtUnknown = 0;
    private const uint PoolDefault = 0;
    private const uint UsageRenderTarget = 1;

    // ---- 入口 ----

    [DllImport("d3d9.dll", EntryPoint = "Direct3DCreate9Ex")]
    private static extern int Direct3DCreate9Ex_(uint sdkVersion, out IntPtr d3d);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    /// <summary>创建 IDirect3D9Ex。失败返回 Zero。</summary>
    public static IntPtr Create()
    {
        int hr = Direct3DCreate9Ex_(D3dSdkVersion, out IntPtr d3d);
        return hr < 0 || d3d == IntPtr.Zero ? IntPtr.Zero : d3d;
    }

    /// <summary>
    /// 找到指定厂商的适配器序号（NVIDIA = 0x10DE），找不到返回 -1。
    ///
    /// 双显卡笔记本上 D3D9 的 0 号适配器往往是集成显卡，而 CUDA 只认 NVIDIA 独显，
    /// 拿集显的 device 去建 CUDA 上下文会直接失败（实测报 unknown error）。
    /// </summary>
    public static int FindAdapter(IntPtr d3d, uint vendorId)
    {
        if (d3d == IntPtr.Zero)
        {
            return -1;
        }

        IntPtr fnCount = Vt(d3d, VtGetAdapterCount);
        IntPtr fnIdent = Vt(d3d, VtGetAdapterIdentifier);
        if (fnCount == IntPtr.Zero || fnIdent == IntPtr.Zero)
        {
            return -1;
        }

        uint n = Marshal.GetDelegateForFunctionPointer<GetAdapterCountFn>(fnCount)(d3d);
        GetAdapterIdentifierFn ident = Marshal.GetDelegateForFunctionPointer<GetAdapterIdentifierFn>(fnIdent);

        System.Text.StringBuilder sb = new();
        sb.Append($"count={n}");
        int found = -1;

        for (uint i = 0; i < n && i < 8; i++)
        {
            AdapterIdentifier9 ai = new()
            {
                Driver = new byte[512],
                Description = new byte[512],
                DeviceName = new byte[32],
            };

            int hr = ident(d3d, i, 0, ref ai);
            string desc = System.Text.Encoding.ASCII.GetString(ai.Description).TrimEnd('\0');
            if (desc.Length > 40)
            {
                desc = desc[..40];
            }

            sb.Append($" | {i}: hr={hr} vendor=0x{ai.VendorId:X4} {desc}");
            if (hr >= 0 && ai.VendorId == vendorId && found < 0)
            {
                found = (int)i;
            }
        }

        LastDiag = sb.ToString();
        return found;
    }

    /// <summary>适配器枚举的诊断串（厂商、描述），排查双显卡问题时用。</summary>
    public static string LastDiag { get; private set; } = "";

    /// <summary>
    /// 创建 D3D9Ex 设备。窗口用桌面窗口做焦点窗口即可——我们只是要一个能开纹理的
    /// 设备，真正的显示由 WPF 的 D3DImage 负责。
    /// </summary>
    public static bool CreateDevice(IntPtr d3d, int adapter, out IntPtr device)
    {
        device = IntPtr.Zero;
        if (d3d == IntPtr.Zero || adapter < 0)
        {
            return false;
        }

        PresentParameters pp = new()
        {
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = FmtUnknown,
            BackBufferCount = 1,
            MultiSampleType = 0,
            MultiSampleQuality = 0,
            SwapEffect = SwapEffectDiscard,
            hDeviceWindow = GetDesktopWindow(),
            Windowed = 1,
            EnableAutoDepthStencil = 0,
            AutoDepthStencilFormat = 0,
            Flags = 0,
            FullScreenRefreshRateInHz = 0,
            PresentationInterval = 0,
        };

        IntPtr fn = Vt(d3d, VtCreateDeviceEx);
        if (fn == IntPtr.Zero)
        {
            return false;
        }

        int hr = Marshal.GetDelegateForFunctionPointer<CreateDeviceExFn>(fn)(
            d3d, (uint)adapter, DevTypeHal, pp.hDeviceWindow,
            CreateHardwareVertexProcessing | CreateMultithreaded | CreateFpuPreserve,
            ref pp, IntPtr.Zero, out IntPtr dev);

        if (hr < 0 || dev == IntPtr.Zero)
        {
            return false;
        }

        device = dev;
        return true;
    }

    /// <summary>
    /// 建一张可当 CUDA 目标、也可当 D3DImage 后备缓冲的纹理。
    /// 必须是 D3DPOOL_DEFAULT + D3DUSAGE_RENDERTARGET，CUDA 注册时才认。
    /// </summary>
    public static bool CreateTexture(IntPtr device, int w, int h, out IntPtr tex)
    {
        tex = IntPtr.Zero;
        if (device == IntPtr.Zero || w <= 0 || h <= 0)
        {
            return false;
        }

        IntPtr fn = Vt(device, VtCreateTexture);
        if (fn == IntPtr.Zero)
        {
            return false;
        }

        int hr = Marshal.GetDelegateForFunctionPointer<CreateTextureFn>(fn)(
            device, (uint)w, (uint)h, 1, UsageRenderTarget, FmtA8R8G8B8, PoolDefault,
            out IntPtr t, IntPtr.Zero);

        if (hr < 0 || t == IntPtr.Zero)
        {
            return false;
        }

        tex = t;
        return true;
    }

    /// <summary>取纹理的 0 级表面 —— 交给 D3DImage.SetBackBuffer 的就是它。</summary>
    public static IntPtr GetSurfaceLevel(IntPtr tex)
    {
        if (tex == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr fn = Vt(tex, VtGetSurfaceLevel);
        if (fn == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        int hr = Marshal.GetDelegateForFunctionPointer<GetSurfaceLevelFn>(fn)(
            tex, 0, out IntPtr surf);
        return hr < 0 ? IntPtr.Zero : surf;
    }

    public static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero)
        {
            return;
        }

        IntPtr fn = Vt(obj, VtRelease);
        if (fn != IntPtr.Zero)
        {
            Marshal.GetDelegateForFunctionPointer<ReleaseFn>(fn)(obj);
        }

        obj = IntPtr.Zero;
    }

    // ---- COM vtable ----
    //
    // 索引按 IDirect3D9Ex / IDirect3DDevice9 / IDirect3DTexture9 的声明顺序（含 IUnknown 前三个）。

    private const int VtRelease = 2;
    private const int VtGetAdapterCount = 4;   // IDirect3D9
    private const int VtGetAdapterIdentifier = 5;
    private const int VtCreateDeviceEx = 20;   // IDirect3D9Ex
    private const int VtCreateTexture = 23;    // IDirect3DDevice9
    private const int VtGetSurfaceLevel = 18;  // IDirect3DTexture9

    private static IntPtr Vt(IntPtr obj, int index) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj) + index * IntPtr.Size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PresentParameters
    {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public uint BackBufferFormat;
        public uint BackBufferCount;
        public uint MultiSampleType;
        public uint MultiSampleQuality;
        public uint SwapEffect;
        public IntPtr hDeviceWindow;
        public int Windowed;
        public int EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreenRefreshRateInHz;
        public uint PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterIdentifier9
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] Driver;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] Description;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] DeviceName;
        public uint DriverVersionLow;
        public int DriverVersionHigh;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public Guid DeviceIdentifier;
        public uint WhqlLevel;
        public uint Pad;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetAdapterCountFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterIdentifierFn(IntPtr self, uint adapter, uint flags, ref AdapterIdentifier9 id);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceExFn(
        IntPtr self, uint adapter, uint devType, IntPtr focusWindow, uint behaviorFlags,
        ref PresentParameters pp, IntPtr fullscreenDisplayMode, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureFn(
        IntPtr self, uint w, uint h, uint levels, uint usage, uint format, uint pool,
        out IntPtr tex, IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSurfaceLevelFn(IntPtr self, uint level, out IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);
}
