using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PIKAN;

/// <summary>
/// 一小把自己需要的 Win32 API —— 目的只有一个：把 WinForms 从依赖里彻底踢出去。
///
/// 为什么值得为三条 API 动一次手术：整个 WinForms（连它那几个设计器程序集一起）在
/// 自包含发布里要占 ~22 MB，而且只要它还在，MSBuild 就硬拦 trimming
/// （error NETSDK1175：Windows Forms is not supported or recommended with trimming enabled）。
/// 砍掉之后既瘦身，又给裁剪解锁道路。下面三条是项目里 WinForms 的全部用量：
///
///   SystemInformation.DoubleClickTime → GetDoubleClickTime
///   Screen.FromPoint().WorkingArea     → MonitorFromPoint + GetMonitorInfo
///   FolderBrowserDialog                → IFileDialog（FOS_PICKFOLDERS）
///
/// 第三条是唯一有点分量的：直接用 COM 调 Vista 起的新式文件夹对话框，也就是 WinForms
/// 那个 FolderBrowserDialog 内部自己走的同一套路径，外观与行为对齐原实现。
/// </summary>
public static class NativeWin32
{
    // ===============================================================
    // ① 系统双击间隔
    // ===============================================================

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>
    /// 系统认定的「同一击」时间窗（毫秒）。WinForms 的 SystemInformation.DoubleClickTime
    /// 内部取的就是它。拿不到就退回 Windows 默认的 500。
    /// </summary>
    public static int DoubleClickTimeMs
    {
        get
        {
            try
            {
                int ms = (int)GetDoubleClickTime();
                return ms > 0 ? ms : 500;
            }
            catch
            {
                return 500;
            }
        }
    }

    // ===============================================================
    // ② 某点所在那块屏幕的工作区（物理像素）
    // ===============================================================

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfo mi);

    /// <summary>
    /// 取 <paramref name="x"/>/<paramref name="y"/>（物理像素坐标）所在显示器的工作区
    /// —— 工作区是扣掉任务栏后的那块，用来把浮层挡在任务栏之内。
    ///
    /// 为什么不用 WPF 的 SystemParameters.WorkArea：那只给主屏，鼠标在副屏上就错了。
    /// 拿不到返回 false，调用方保持在原位即可。
    /// </summary>
    public static bool TryGetWorkArea(int x, int y, out int left, out int top, out int right, out int bottom)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;

        try
        {
            IntPtr mon = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
            if (mon == IntPtr.Zero)
            {
                return false;
            }

            MonitorInfo mi = new() { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(mon, ref mi))
            {
                return false;
            }

            left = mi.Work.Left;
            top = mi.Work.Top;
            right = mi.Work.Right;
            bottom = mi.Work.Bottom;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ===============================================================
    // ③ 文件夹选择对话框（IFileDialog，Vista 起的新式外观）
    // ===============================================================

    // CLSID_FileOpenDialog
    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");

    // IID_IFileDialog
    private static readonly Guid IidFileDialog = new("42f85136-db7e-439c-85f1-e4075d135fc8");

    private const uint ClsctxInprocServer = 1;

    private const uint FosPickFolders = 0x0020;      // 选文件夹而不是文件
    private const uint FosForceFileSystem = 0x0040;  // 只要真实文件系统路径（不要控制面板这类虚拟项）
    private const uint FosPathMustExist = 0x0800;    // 路径必须存在

    private const uint SigdnFilesysPath = 0x80058000; // 要「真实磁盘路径」这一形式的名字

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid, IntPtr outer, uint clsContext, ref Guid iid, out IntPtr ppv);

    /// <summary>
    /// IFileDialog 的 vtable 必须按文档顺序完整声明：COM 是按槽位调用的，
    /// 漏一个、错位一个，后面所有调用都会落到错误的函数指针上（表现为随机崩溃或错的行为）。
    /// 用不到的方法只保留签名做占位，一个都不能删。
    /// </summary>
    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);

        // IFileDialog
        [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr filterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(IntPtr events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int SetDefaultFolder(IntPtr shellItem);
        [PreserveSig] int SetFolder(IntPtr shellItem);
        [PreserveSig] int GetFolder(out IntPtr shellItem);
        [PreserveSig] int GetCurrentSelection(out IntPtr shellItem);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int GetResult(out IntPtr shellItem);
        [PreserveSig] int AddPlace(IntPtr shellItem, int place);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr filter);
    }

    /// <summary>IShellItem：只需要 GetDisplayName 取回路径，其余同样按序占位。</summary>
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindCtx, ref Guid bhId, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr namePtr);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IntPtr other, uint hint, out int order);
    }

    /// <summary>
    /// 弹出文件夹选择对话框，返回选中路径；取消或失败返回 null。
    ///
    /// 与 WinForms 的 FolderBrowserDialog 对齐的两条行为：
    ///   · 把宿主窗口句柄传进去，对话框才会正确锚定到调用它的那个窗口上；
    ///   · 原先传的是 Description + UseDescriptionForTitle = true，落在窗口标题栏上，
    ///     这里直接 SetTitle，效果一致。
    /// </summary>
    public static string? PickFolder(Window owner, string title)
    {
        IntPtr dlgPtr = IntPtr.Zero;
        object? dlgObj = null;

        try
        {
            IntPtr hwnd = new WindowInteropHelper(owner).Handle;

            // readonly 静态字段不能按引用传参（CS0459），这里复制到局部变量再传
            Guid clsid = ClsidFileOpenDialog;
            Guid iid = IidFileDialog;

            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out dlgPtr);
            if (hr < 0 || dlgPtr == IntPtr.Zero)
            {
                return null;
            }

            dlgObj = Marshal.GetObjectForIUnknown(dlgPtr);
            if (dlgObj is not IFileDialog dialog)
            {
                return null;
            }

            dialog.SetOptions(FosPickFolders | FosForceFileSystem | FosPathMustExist);
            dialog.SetTitle(title);

            hr = dialog.Show(hwnd);
            if (hr < 0)
            {
                return null; // 用户点了取消
            }

            if (dialog.GetResult(out IntPtr itemPtr) < 0 || itemPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return PathOfShellItem(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        catch
        {
            // 对话框起不来（COM 被组策略禁用等极端情况）也不能让看图器崩，按取消处理
            return null;
        }
        finally
        {
            if (dlgObj is not null)
            {
                Marshal.FinalReleaseComObject(dlgObj);
            }

            if (dlgPtr != IntPtr.Zero)
            {
                Marshal.Release(dlgPtr);
            }
        }
    }

    /// <summary>从 IShellItem 里取出磁盘路径。回传的指针要用 CoTaskMemFree 释放。</summary>
    internal static string? PathOfShellItem(IntPtr itemPtr)
    {
        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(itemPtr);
            if (obj is not IShellItem item)
            {
                return null;
            }

            if (item.GetDisplayName(SigdnFilesysPath, out IntPtr namePtr) < 0 || namePtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(namePtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
            }
        }
        finally
        {
            if (obj is not null)
            {
                Marshal.FinalReleaseComObject(obj);
            }
        }
    }
}
