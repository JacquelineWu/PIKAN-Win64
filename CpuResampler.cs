using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Windows.Media.Imaging;

namespace PIKAN;

/// <summary>
/// CPU 侧高质量重采样与模糊：Mitchell-Netravali 三次核（与 PTX kernel 同一套系数），
/// 分离式两趟（水平 → 垂直），AVX2 + FMA 实现；没有 AVX2 的机器退化为标量版。
///
/// 定位是 **GPU 不可用时的质量回退**。原来只能落到 WIC 的 TransformedBitmap
///（双线性，缩小大图时明显走样）；现在非 NVIDIA 机器也能拿到与 GPU 一致的 Mitchell 画质。
///
/// 与 GPU kernel 对齐的细节：
///   · 缩放比 s = 输入/输出，滤波宽度 fs = max(s, 1)（放大时不额外锐化）
///   · 中心 c = (o + 0.5) * s - 0.5，窗口 [floor(c - 2fs), ceil(c + 2fs)]
///   · taps 上限 16，超出则以 round(c) 为中心重新开窗
///   · |t| ≥ 2 权重为 0，最后按权重和归一化
/// </summary>
public static class CpuResampler
{
    /// <summary>总开关：默认开。关掉后回落到 WIC 双线性，便于 A/B 对比画质与耗时。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>AVX2 + FMA 是否可用（几乎每颗 x64 桌面 CPU 都有）。</summary>
    public static bool SimdSupported => Avx2.IsSupported && Fma.IsSupported && Sse2.IsSupported;

    /// <summary>状态栏 / 诊断用：当前 CPU 走的是哪条路。</summary>
    public static string ModeName => !Enabled ? "WIC" : SimdSupported ? "AVX2" : "标量";

    /// <summary>taps 上限。Mitchell 实际只用 16，高斯模糊（半径大时）会用到 31。</summary>
    private const int MaxTaps = 32;

    private const int MitchellCap = 16;   // 与 GPU kernel 的 16 taps 上限一致

    // ==================================================================
    // 权重表
    // ==================================================================

    /// <summary>
    /// 一整条轴的采样表：每个输出点给出窗口起点、taps 数与归一化权重。
    /// 下标已 clamp 到 [0, ins-1]，内层循环不必再判边界。
    ///
    /// Idx / W 是**紧凑打包**的（第 o 个输出点的数据从 Off[o] 起连续 Count[o] 个），
    /// 不是按 MaxTaps 跨步存——跨步存会让每个输出点跨好几条 cache line，
    /// 实测这一项就占了将近一半的时间。
    /// </summary>
    private sealed class TapTable
    {
        public int[] Start = [];
        public int[] Count = [];
        public int[] Off = [];
        public int[] Idx = [];
        public float[] W = [];
    }

    private static readonly object TableGate = new();

    private static readonly Dictionary<long, TapTable> MitchellCache = new();

    private static readonly Dictionary<int, TapTable> GaussCache = new();

    private static TapTable MitchellTable(int ins, int outs)
    {
        long key = ((long)ins << 32) | (uint)outs;
        lock (TableGate)
        {
            if (MitchellCache.TryGetValue(key, out TapTable? hit))
            {
                return hit;
            }
        }

        TapTable t = new()
        {
            Start = new int[outs],
            Count = new int[outs],
            Off = new int[outs],
        };

        List<int> idx = new(outs * 10);
        List<float> wt = new(outs * 10);

        double s = (double)ins / outs;
        double fs = Math.Max(s, 1.0);

        for (int o = 0; o < outs; o++)
        {
            double c = (o + 0.5) * s - 0.5;
            double half = 2.0 * fs;

            int lo = (int)Math.Floor(c - half);
            int hi = (int)Math.Ceiling(c + half);
            int n = hi - lo + 1;

            if (n > MitchellCap)
            {
                lo = (int)Math.Round(c) - MitchellCap / 2;
                n = MitchellCap;
            }

            t.Start[o] = lo;
            t.Count[o] = n;
            t.Off[o] = idx.Count;

            double sum = 0;
            for (int k = 0; k < n; k++)
            {
                double w = MitchellWeight(((lo + k) - c) / fs);
                wt.Add((float)w);
                sum += w;
            }

            for (int k = 0; k < n; k++)
            {
                wt[wt.Count - n + k] = sum > 1e-9 ? (float)(wt[wt.Count - n + k] / sum) : 0f;

                int src = lo + k;
                idx.Add(src < 0 ? 0 : src > ins - 1 ? ins - 1 : src);
            }
        }

        t.Idx = idx.ToArray();
        t.W = wt.ToArray();

        lock (TableGate)
        {
            MitchellCache[key] = t;
        }

        return t;
    }

    /// <summary>
    /// Mitchell-Netravali（B = C = 1/3）。分段三次多项式，|t| ≥ 2 为 0。
    /// 系数与 PTX 里的浮点常量逐一对应：7/6, -2, 8/9 · -7/18, 2, -10/3, 16/9。
    /// </summary>
    private static double MitchellWeight(double t)
    {
        double a = Math.Abs(t);
        if (a >= 2.0)
        {
            return 0.0;
        }

        double a2 = a * a;
        double a3 = a2 * a;

        return a < 1.0
            ? (7.0 / 6.0) * a3 - 2.0 * a2 + (8.0 / 9.0)
            : (-7.0 / 18.0) * a3 + 2.0 * a2 - (10.0 / 3.0) * a + (16.0 / 9.0);
    }

    /// <summary>高斯权重表（同尺寸，用于模糊）：半径 3σ，最多 MaxTaps 个 taps。</summary>
    private static TapTable GaussTable(int size, double sigma)
    {
        int r = Math.Max(1, (int)Math.Ceiling(3.0 * sigma));
        int n = Math.Min(2 * r + 1, MaxTaps);
        int key = (size << 8) | (int)Math.Round(sigma * 8);

        lock (TableGate)
        {
            if (GaussCache.TryGetValue(key, out TapTable? hit))
            {
                return hit;
            }
        }

        TapTable t = new()
        {
            Start = new int[size],
            Count = new int[size],
            Off = new int[size],
        };

        List<int> idx = new(size * n);
        List<float> wt = new(size * n);

        double denom = 2.0 * sigma * sigma;

        for (int o = 0; o < size; o++)
        {
            int lo = o - n / 2;
            t.Start[o] = lo;
            t.Count[o] = n;
            t.Off[o] = idx.Count;

            double sum = 0;
            int at = wt.Count;
            for (int k = 0; k < n; k++)
            {
                double d = (lo + k) - o;
                double w = Math.Exp(-(d * d) / denom);
                wt.Add((float)w);
                sum += w;
            }

            for (int k = 0; k < n; k++)
            {
                wt[at + k] = (float)(wt[at + k] / sum);

                int src = lo + k;
                idx.Add(src < 0 ? 0 : src > size - 1 ? size - 1 : src);
            }
        }

        t.Idx = idx.ToArray();
        t.W = wt.ToArray();

        lock (TableGate)
        {
            GaussCache[key] = t;
        }

        return t;
    }

    // ==================================================================
    // 对外接口
    // ==================================================================

    /// <summary>
    /// BGRA32 重采样：src(iw×ih) → dst(ow×oh)。两趟 Mitchell，AVX2/FMA。
    /// 失败（尺寸非法 / 缓冲不足）返回 false，调用方继续回落 WIC。
    /// </summary>
    public static bool Resize(byte[] src, int iw, int ih, byte[] dst, int ow, int oh)
    {
        if (!Enabled || iw <= 0 || ih <= 0 || ow <= 0 || oh <= 0)
        {
            return false;
        }

        if (src.Length < iw * ih * 4 || dst.Length < ow * oh * 4)
        {
            return false;
        }

        TapTable tx = MitchellTable(iw, ow);
        TapTable ty = MitchellTable(ih, oh);

        long accH = 0;
        long accV = 0;
        Stopwatch sw = new();

        float[] rowF = ArrayPool<float>.Shared.Rent(iw * 4);
        try
        {
            // 分块：一次只把「当前这批输出行需要的输入行」算成中间结果，
            // 中间缓冲保持在 L2/L3 量级，避免整张图 20MB 级别的来回搬运。
            double sy = (double)ih / oh;
            int tile = Math.Clamp((int)(96 / Math.Max(1.0, sy)), 1, oh);

            for (int y0 = 0; y0 < oh; y0 += tile)
            {
                int y1 = Math.Min(oh, y0 + tile);

                int in0 = int.MaxValue;
                int in1 = int.MinValue;
                for (int y = y0; y < y1; y++)
                {
                    if (ty.Start[y] < in0)
                    {
                        in0 = ty.Start[y];
                    }

                    if (ty.Start[y] + ty.Count[y] - 1 > in1)
                    {
                        in1 = ty.Start[y] + ty.Count[y] - 1;
                    }
                }

                in0 = Math.Clamp(in0, 0, ih - 1);
                in1 = Math.Clamp(in1, 0, ih - 1);
                int rows = in1 - in0 + 1;

                float[] mid = ArrayPool<float>.Shared.Rent(rows * ow * 4);
                try
                {
                    sw.Restart();
                    for (int y = in0; y <= in1; y++)
                    {
                        PassH(src, iw, y, rowF, mid, (y - in0) * ow * 4, ow, tx);
                    }

                    accH += sw.ElapsedTicks;

                    sw.Restart();
                    for (int y = y0; y < y1; y++)
                    {
                        PassV(mid, in0, rows, ow, dst, y * ow * 4, ow, ty, y);
                    }

                    accV += sw.ElapsedTicks;
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(mid);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rowF);
            LastHorizontalMs = accH * 1000.0 / Stopwatch.Frequency;
            LastVerticalMs = accV * 1000.0 / Stopwatch.Frequency;
        }

        return true;
    }

    /// <summary>上一次 Resize 里水平趟的耗时（诊断用）。</summary>
    public static double LastHorizontalMs { get; private set; }

    /// <summary>上一次 Resize 里垂直趟的耗时（诊断用）。</summary>
    public static double LastVerticalMs { get; private set; }

    /// <summary>
    /// 分离高斯模糊：src → 水平(mid) → 垂直(dst)，三块都是 w×h×4。
    /// 供 GPU 不可用时顶上（GPU 版本在 GpuAccel.Blur）。tmp 未使用，留着对齐 GPU 版签名。
    /// </summary>
    public static bool Blur(byte[] src, byte[]? tmp, byte[] dst, int w, int h, double sigma)
    {
        if (!Enabled || w <= 0 || h <= 0 || sigma <= 0.01)
        {
            return false;
        }

        if (src.Length < w * h * 4 || dst.Length < w * h * 4)
        {
            return false;
        }

        TapTable t = GaussTable(w, sigma);

        float[] rowF = ArrayPool<float>.Shared.Rent(w * 4);
        float[] mid = ArrayPool<float>.Shared.Rent(w * h * 4);
        try
        {
            for (int y = 0; y < h; y++)
            {
                PassH(src, w, y, rowF, mid, y * w * 4, w, t);
            }

            for (int y = 0; y < h; y++)
            {
                PassV(mid, 0, h, w, dst, y * w * 4, w, t, y);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mid);
            ArrayPool<float>.Shared.Return(rowF);
        }

        return true;
    }

    // ==================================================================
    // 两趟
    // ==================================================================

    /// <summary>
    /// 水平趟：一行交织 BGRA 字节 → 一行交织 float（长度 ow×4），写到 dst[dstOff..]。
    /// 源行先整体转成 float（一次转换反复被 taps 引用），再对每个输出点做加权累加。
    /// 输出点是逐个算的——每个输出点的窗口起点不同，没法在 x 方向打包成向量，
    /// 这里靠 4 个通道填满 128 位（B,G,R,A 正好一个像素）。
    /// </summary>
    private static void PassH(byte[] src, int iw, int y, float[] rowF, float[] dst, int dstOff, int ow, TapTable tx)
    {
        ToFloatRow(src, y * iw * 4, rowF, iw);

        ref float rp = ref MemoryMarshal.GetArrayDataReference(rowF);
        ref float dp = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(dst), dstOff);

        if (!SimdSupported)
        {
            for (int x = 0; x < ow; x++)
            {
                int b = tx.Off[x];
                int n = tx.Count[x];

                float c0 = 0, c1 = 0, c2 = 0, c3 = 0;
                for (int k = 0; k < n; k++)
                {
                    float w = tx.W[b + k];
                    int o = tx.Idx[b + k] * 4;
                    c0 += Unsafe.Add(ref rp, o) * w;
                    c1 += Unsafe.Add(ref rp, o + 1) * w;
                    c2 += Unsafe.Add(ref rp, o + 2) * w;
                    c3 += Unsafe.Add(ref rp, o + 3) * w;
                }

                int q = x * 4;
                Unsafe.Add(ref dp, q) = c0;
                Unsafe.Add(ref dp, q + 1) = c1;
                Unsafe.Add(ref dp, q + 2) = c2;
                Unsafe.Add(ref dp, q + 3) = c3;
            }

            return;
        }

        // 四条独立累加链：FMA 的累加是串行依赖（延迟 4 周期/条），
        // 摊到 4 条链上就能让乱序执行把延迟藏掉——实测这一改快了 2 倍多。
        for (int x = 0; x < ow; x++)
        {
            int b = tx.Off[x];
            int n = tx.Count[x];

            Vector128<float> a0 = Vector128<float>.Zero;
            Vector128<float> a1 = Vector128<float>.Zero;
            Vector128<float> a2 = Vector128<float>.Zero;
            Vector128<float> a3 = Vector128<float>.Zero;

            int k = 0;
            for (; k + 4 <= n; k += 4)
            {
                a0 = Fma.MultiplyAdd(Vector128.Create(tx.W[b + k]),
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref rp, tx.Idx[b + k] * 4)), a0);
                a1 = Fma.MultiplyAdd(Vector128.Create(tx.W[b + k + 1]),
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref rp, tx.Idx[b + k + 1] * 4)), a1);
                a2 = Fma.MultiplyAdd(Vector128.Create(tx.W[b + k + 2]),
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref rp, tx.Idx[b + k + 2] * 4)), a2);
                a3 = Fma.MultiplyAdd(Vector128.Create(tx.W[b + k + 3]),
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref rp, tx.Idx[b + k + 3] * 4)), a3);
            }

            for (; k < n; k++)
            {
                a0 = Fma.MultiplyAdd(Vector128.Create(tx.W[b + k]),
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref rp, tx.Idx[b + k] * 4)), a0);
            }

            (a0 + a1 + a2 + a3).StoreUnsafe(ref Unsafe.Add(ref dp, x * 4));
        }
    }

    /// <summary>
    /// 垂直趟：从中间结果（float，按块内行存放；in0 是块内第一行的全局行号）
    /// 累加出一整行输出像素，取整 clamp 后写成交织 BGRA 字节。
    /// 同一输出行的权重对所有 x 都一样，所以这里一次算 2 个像素（8 条 float 通道）。
    /// </summary>
    private static void PassV(float[] mid, int in0, int rows, int mw, byte[] dst, int dstOff, int ow, TapTable ty, int y)
    {
        int b = ty.Off[y];
        int n = ty.Count[y];

        ref float mp = ref MemoryMarshal.GetArrayDataReference(mid);
        ref byte dp = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(dst), dstOff);

        int x = 0;

        if (SimdSupported)
        {
            Vector256<int> zero = Vector256<int>.Zero;
            Vector256<int> vmax = Vector256.Create(255);
            Vector256<float> half = Vector256.Create(0.5f);

            for (; x + 2 <= ow; x += 2)
            {
                // 同样拆成两条链，避免 FMA 累加的串行延迟主导
                Vector256<float> a0 = Vector256<float>.Zero;
                Vector256<float> a1 = Vector256<float>.Zero;

                int k = 0;
                for (; k + 2 <= n; k += 2)
                {
                    int r0 = ty.Idx[b + k] - in0;
                    int r1 = ty.Idx[b + k + 1] - in0;
                    a0 = Fma.MultiplyAdd(Vector256.Create(ty.W[b + k]),
                        Vector256.LoadUnsafe(ref Unsafe.Add(ref mp, r0 * mw * 4 + x * 4)), a0);
                    a1 = Fma.MultiplyAdd(Vector256.Create(ty.W[b + k + 1]),
                        Vector256.LoadUnsafe(ref Unsafe.Add(ref mp, r1 * mw * 4 + x * 4)), a1);
                }

                for (; k < n; k++)
                {
                    int row = ty.Idx[b + k] - in0;
                    a0 = Fma.MultiplyAdd(Vector256.Create(ty.W[b + k]),
                        Vector256.LoadUnsafe(ref Unsafe.Add(ref mp, row * mw * 4 + x * 4)), a0);
                }

                Vector256<float> acc = a0 + a1;

                // 取整（+0.5 后截断）→ clamp 到 [0,255] → 8 个 int32 压成 8 字节。
                // 注意 packssdw 是「取 a 的低 4 个 + b 的低 4 个」，所以 b 必须送进去
                // 高半部分，直接填同一个向量会把前 4 个值重复一遍（每两个像素画成一样）。
                Vector256<int> iv = Avx2.Min(
                    Avx2.Max(Avx.ConvertToVector256Int32(Avx.Add(acc, half)), zero), vmax);
                Vector256<int> hiHalf = Avx2.Permute2x128(iv, iv, 0x01);
                Vector256<short> s16 = Avx2.PackSignedSaturate(iv, hiHalf);
                Vector256<byte> b8 = Avx2.PackUnsignedSaturate(s16, s16);

                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref dp, x * 4),
                    b8.GetLower().AsUInt64().GetElement(0));
            }
        }

        for (; x < ow; x++)
        {
            float c0 = 0, c1 = 0, c2 = 0, c3 = 0;
            for (int k = 0; k < n; k++)
            {
                float w = ty.W[b + k];
                int o = (ty.Idx[b + k] - in0) * mw * 4 + x * 4;
                c0 += Unsafe.Add(ref mp, o) * w;
                c1 += Unsafe.Add(ref mp, o + 1) * w;
                c2 += Unsafe.Add(ref mp, o + 2) * w;
                c3 += Unsafe.Add(ref mp, o + 3) * w;
            }

            int q = x * 4;
            Unsafe.Add(ref dp, q) = ClampByte(c0);
            Unsafe.Add(ref dp, q + 1) = ClampByte(c1);
            Unsafe.Add(ref dp, q + 2) = ClampByte(c2);
            Unsafe.Add(ref dp, q + 3) = ClampByte(c3);
        }
    }

    private static byte ClampByte(float v)
    {
        int i = (int)(v + 0.5f);
        return i < 0 ? (byte)0 : i > 255 ? (byte)255 : (byte)i;
    }

    /// <summary>
    /// 诊断用：完全照抄 GPU kernel 的 2D（非分离）卷积，标量实现。
    /// 用来判定分离版与 GPU 的差异到底是「分离本身不等价」还是「实现写错了」。
    /// </summary>
    private static void Scalar2D(byte[] src, int iw, int ih, byte[] dst, int ow, int oh)
    {
        TapTable tx = MitchellTable(iw, ow);
        TapTable ty = MitchellTable(ih, oh);

        for (int y = 0; y < oh; y++)
        {
            int yb = ty.Off[y];
            int ny = ty.Count[y];

            for (int x = 0; x < ow; x++)
            {
                int xb = tx.Off[x];
                int nx = tx.Count[x];

                float b0 = 0, g0 = 0, r0 = 0, a0 = 0, ws = 0;

                for (int j = 0; j < ny; j++)
                {
                    float wy = ty.W[yb + j];
                    int yi = ty.Idx[yb + j] * iw;

                    for (int i = 0; i < nx; i++)
                    {
                        float w = wy * tx.W[xb + i];
                        int o = (yi + tx.Idx[xb + i]) * 4;
                        b0 += src[o] * w;
                        g0 += src[o + 1] * w;
                        r0 += src[o + 2] * w;
                        a0 += src[o + 3] * w;
                        ws += w;
                    }
                }

                int q = (y * ow + x) * 4;
                if (ws <= 0)
                {
                    continue;   // 与 GPU 一致：权重和为 0 就不写
                }

                dst[q] = ClampByte(b0 / ws);
                dst[q + 1] = ClampByte(g0 / ws);
                dst[q + 2] = ClampByte(r0 / ws);
                dst[q + 3] = ClampByte(a0 / ws);
            }
        }
    }

    private static double MeanDiff(byte[] a, byte[] b)
    {
        long s = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            s += Math.Abs(a[i] - b[i]);
        }

        return n == 0 ? 0 : (double)s / n;
    }

    // ==================================================================
    // 自检（--cmp）
    // ==================================================================

    /// <summary>
    /// 解一张图，分别用 GPU（PTX kernel）与 CPU（AVX2）重采样到同一尺寸，
    /// 逐字节比对并各自计时。结果一行文本，调用方写进 gpu.log。
    /// 用途：确认两套实现的数值一致——算法是同一套 Mitchell，但浮点路径完全不同。
    /// path 可以是图片文件或目录（取目录里第一张图）。
    /// </summary>
    public static string SelfTest(string? path)
    {
        try
        {
            return SelfTestCore(path);
        }
        catch (Exception ex)
        {
            return "自检异常：" + ex.Message;
        }
    }

    private static string SelfTestCore(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "未指定路径";
        }

        string? file = path;

        if (Directory.Exists(file))
        {
            file = Directory.EnumerateFiles(file).FirstOrDefault(Decoder.IsImage);
        }

        if (file is null || !File.Exists(file))
        {
            return "找不到图片：" + path;
        }

        DecodedImage? img = Decoder.Decode(file, 1536);
        if (img is null)
        {
            return "解码失败：" + file;
        }

        BitmapSource src = img.Source;
        int iw = src.PixelWidth;
        int ih = src.PixelHeight;
        int ow = Math.Max(1, iw * 640 / 1000);
        int oh = Math.Max(1, ih * 640 / 1000);

        byte[]? inBuf = GpuResampler.Pixels(src);
        if (inBuf is null)
        {
            return "取像素失败：" + file;
        }

        // ---- GPU：先跑一次预热（首次含 CUDA 初始化 + PTX JIT），再计时稳态 ----
        byte[] g = new byte[ow * oh * 4];
        Stopwatch sw = Stopwatch.StartNew();
        bool gpuOk = GpuAccel.Resize(inBuf, iw, ih, g, ow, oh, true);
        long gpuFirstMs = sw.ElapsedMilliseconds;

        sw.Restart();
        GpuAccel.Resize(inBuf, iw, ih, g, ow, oh, true);
        long gpuMs = sw.ElapsedMilliseconds;

        // ---- CPU：同样先跑一次预热（首次含 JIT + 缓冲分配），再取稳态 ----
        byte[] c = new byte[ow * oh * 4];
        sw.Restart();
        bool cpuOk = Resize(inBuf, iw, ih, c, ow, oh);
        long cpuFirstMs = sw.ElapsedMilliseconds;

        sw.Restart();
        int rounds = 3;
        for (int i = 0; i < rounds; i++)
        {
            Resize(inBuf, iw, ih, c, ow, oh);
        }

        long cpuMs = sw.ElapsedMilliseconds / rounds;

        if (!cpuOk)
        {
            return $"CPU 重采样失败 {iw}x{ih} → {ow}x{oh}";
        }

        string head = $"{Path.GetFileName(file)} {iw}x{ih} → {ow}x{oh} · 模式 {ModeName} · " +
                      $"GPU 首次 {gpuFirstMs}ms / 稳态 {gpuMs}ms(ok={gpuOk}) · " +
                      $"CPU 首次 {cpuFirstMs}ms / 稳态 {cpuMs}ms";

        if (!gpuOk)
        {
            return head + " · GPU 不可用，只验证了 CPU 路径";
        }

        long sum = 0;
        int max = 0;
        int big = 0;
        int bigPx = 0;
        int edgePx = 0;       // 差异大的像素里，有多少落在离边界 4 像素内
        double sq = 0;
        List<string> where = new();

        for (int p = 0; p < ow * oh; p++)
        {
            int x = p % ow;
            int y = p / ow;
            int i = p * 4;
            bool bigHere = false;

            for (int ch = 0; ch < 4; ch++)
            {
                int d = Math.Abs(c[i + ch] - g[i + ch]);
                sum += d;
                sq += (double)d * d;
                if (d > max)
                {
                    max = d;
                }

                if (d > 32)
                {
                    big++;
                    bigHere = true;
                }
            }

            if (bigHere)
            {
                bigPx++;
                bool edge = x < 4 || x >= ow - 4 || y < 4 || y >= oh - 4;
                if (edge)
                {
                    edgePx++;
                }

                if (where.Count < 6)
                {
                    where.Add($"({x},{y}){(edge ? "边" : "内")}");
                }
            }
        }

        // ---- WIC 双线性（原来 GPU 不可用时的回退）作为基准 ----
        // TransformedBitmap 是延迟渲染的，必须真把像素拷出来才算它的账
        sw.Restart();
        byte[]? wicBuf = null;
        BitmapSource? wic = GpuResampler.WicResize(src, ow, oh);
        if (wic is not null)
        {
            wicBuf = GpuResampler.Pixels(wic);
        }

        long wicMs = sw.ElapsedMilliseconds;

        double mean = (double)sum / c.Length;
        double mse = sq / c.Length;
        double psnr = mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        double bigPct = 100.0 * big / c.Length;
        string spots = where.Count == 0 ? "" : $" · 样例 {string.Join(" ", where)}";

        // 2D 标量版（照抄 GPU 算法）作为参照：它能区分「分离写法不等价」与「AVX2 写错了」
        byte[] ref2d = new byte[ow * oh * 4];
        Scalar2D(inBuf, iw, ih, ref2d, ow, oh);
        double refVsGpu = MeanDiff(ref2d, g);
        double refVsAvx = MeanDiff(ref2d, c);

        // 顺带量一下 WIC 与 GPU 差多远（双线性 vs Mitchell，本该差得比较明显）
        string wicDiff = "";
        if (wicBuf is not null && wicBuf.Length == g.Length)
        {
            long ws = 0;
            for (int i = 0; i < wicBuf.Length; i++)
            {
                ws += Math.Abs(wicBuf[i] - g[i]);
            }

            wicDiff = $"（与 GPU 平均差 {(double)ws / wicBuf.Length:F2}）";
        }

        return $"{head} · WIC 双线性 {wicMs}ms{wicDiff} · 与 GPU 差异 平均 {mean:F2} 最大 {max} · " +
               $"PSNR {psnr:F1} dB · 差>32 的像素 {bigPx} ({bigPct:F2}%)，其中靠边 {edgePx}{spots} · " +
               $"CPU 分项 水平 {LastHorizontalMs:F1}ms / 垂直 {LastVerticalMs:F1}ms · " +
               $"2D 标量参照：与 GPU 差 {refVsGpu:F2}，与 AVX2 分离版差 {refVsAvx:F2}";
    }

    /// <summary>整行交织 BGRA 字节 → 交织 float。AVX2 下每次转 4 个像素。</summary>
    private static void ToFloatRow(byte[] src, int srcOff, float[] dst, int iw)
    {
        ref byte sp = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), srcOff);
        ref float dp = ref MemoryMarshal.GetArrayDataReference(dst);

        int x = 0;

        if (Avx2.IsSupported)
        {
            for (; x + 4 <= iw; x += 4)
            {
                // 16 字节 = 4 个像素；pmovzxbd 一次吃掉低 8 字节（2 个像素）→ 8 个 int32
                Vector128<byte> four = Vector128.LoadUnsafe(ref Unsafe.Add(ref sp, x * 4));

                Vector256<int> lo = Avx2.ConvertToVector256Int32(four);
                Vector256<int> hi = Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(four, 8));

                Avx.ConvertToVector256Single(lo).StoreUnsafe(ref Unsafe.Add(ref dp, x * 4));
                Avx.ConvertToVector256Single(hi).StoreUnsafe(ref Unsafe.Add(ref dp, x * 4 + 8));
            }
        }

        for (; x < iw; x++)
        {
            int s = x * 4;
            Unsafe.Add(ref dp, s) = Unsafe.Add(ref sp, s);
            Unsafe.Add(ref dp, s + 1) = Unsafe.Add(ref sp, s + 1);
            Unsafe.Add(ref dp, s + 2) = Unsafe.Add(ref sp, s + 2);
            Unsafe.Add(ref dp, s + 3) = Unsafe.Add(ref sp, s + 3);
        }
    }
}
