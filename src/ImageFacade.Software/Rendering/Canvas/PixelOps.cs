using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ImageFacade.Software.Rendering;

/// <summary>
/// 像素级的定点运算与格式转换。画布内部是预乘 Rgba32，这里是唯一的转换边界。
/// </summary>
internal static class PixelOps
{
    // 把 4 个像素的 alpha 复制到各自的 RGBA 四个通道（ushort 视角下索引 3 与 7）
    private static readonly Vector128<ushort> AlphaChannelIndex = Vector128.Create(
        (ushort)3, (ushort)3, (ushort)3, (ushort)3, (ushort)7, (ushort)7, (ushort)7, (ushort)7);

    // 选中 alpha 通道本身：预乘不改 alpha，算完用原值覆盖回去
    private static readonly Vector128<ushort> AlphaChannelMask = Vector128.Create(
        (ushort)0, (ushort)0, (ushort)0, (ushort)0xFFFF, (ushort)0, (ushort)0, (ushort)0, (ushort)0xFFFF);

    /// <summary>
    /// x / 255 的定点实现（四舍五入）：<c>t = x + 0x80; (t + (t &gt;&gt; 8)) &gt;&gt; 8</c>。
    /// </summary>
    internal static int Div255(uint x)
    {
        uint t = x + 0x80;
        return (int)((t + (t >> 8)) >> 8);
    }

    /// <summary>8 位乘法：<c>a * b / 255</c>，四舍五入。</summary>
    internal static int Mul255(int a, int b) => Div255((uint)(a * b));

    /// <summary>
    /// 预乘 → 直通：<c>(c * 255 + a / 2) / a</c>。
    /// <paramref name="alpha"/> &lt; 8 的像素直接归零，避免把预乘误差放大成噪点（见 misc §F）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Unpremultiply(byte channel, byte alpha)
    {
        if (alpha < 8)
        {
            return 0;
        }

        if (alpha == 255)
        {
            return channel;
        }

        int v = ((channel * 255) + (alpha >> 1)) / alpha;
        return (byte)(v > 255 ? 255 : v);
    }

    /// <summary>直通 RGBA → 预乘 RGBA（长度必须是 4 的倍数）。</summary>
    public static void Premultiply(ReadOnlySpan<byte> straight, Span<byte> destination)
    {
        if (straight.Length != destination.Length || (straight.Length & 3) != 0)
        {
            throw new ArgumentException("源与目标的长度必须相同且是 4 的倍数。", nameof(destination));
        }

        PremultiplyCore(straight, destination);
    }

    /// <summary>原地预乘。</summary>
    public static void PremultiplyInPlace(Span<byte> pixels)
    {
        if ((pixels.Length & 3) != 0)
        {
            throw new ArgumentException("长度必须是 4 的倍数。", nameof(pixels));
        }

        PremultiplyCore(pixels, pixels);
    }

    /// <summary>
    /// 预乘内核：每轮 4 个像素，<c>Vector128</c> 把字节加宽到 ushort、alpha 广播到 RGBA、
    /// 定点除 255、再把 alpha 换回原值。与标量路径逐字节相同（有单元测试守住）。
    /// <para>源与目标允许是同一块内存（原地预乘时逐块 Load/Store 同一地址）。</para>
    /// </summary>
    private static void PremultiplyCore(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int pixels = source.Length / 4;
        ref readonly byte sourceRef = ref MemoryMarshal.GetReference(source);
        ref byte destinationRef = ref MemoryMarshal.GetReference(destination);

        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= pixels; i += 4)
            {
                Vector128<byte> raw = Vector128.LoadUnsafe(in sourceRef, (nuint)(i * 4));
                Vector128<ushort> low = Vector128.WidenLower(raw);
                Vector128<ushort> high = Vector128.WidenUpper(raw);

                Vector128<ushort> lowPremultiplied = Div255(low * Vector128.Shuffle(low, AlphaChannelIndex));
                Vector128<ushort> highPremultiplied = Div255(high * Vector128.Shuffle(high, AlphaChannelIndex));

                Vector128<ushort> resultLow = Vector128.ConditionalSelect(AlphaChannelMask, low, lowPremultiplied);
                Vector128<ushort> resultHigh = Vector128.ConditionalSelect(AlphaChannelMask, high, highPremultiplied);

                Vector128.StoreUnsafe(Vector128.Narrow(resultLow, resultHigh), ref destinationRef, (nuint)(i * 4));
            }
        }

        for (; i < pixels; i++)
        {
            int o = i * 4;
            byte a = source[o + 3];
            destination[o] = (byte)Mul255(source[o], a);
            destination[o + 1] = (byte)Mul255(source[o + 1], a);
            destination[o + 2] = (byte)Mul255(source[o + 2], a);
            destination[o + 3] = a;
        }
    }

    /// <summary>ushort 通道上的 <c>x / 255</c>，与 <see cref="RowBlender"/> 同一实现。</summary>
    private static Vector128<ushort> Div255(Vector128<ushort> x)
    {
        Vector128<ushort> t = x + Vector128.Create((ushort)0x80);
        return (t + Vector128.ShiftRightLogical(t, 8)) >>> 8;
    }

    /// <summary>预乘 RGBA → 直通 RGBA。</summary>
    public static void Unpremultiply(ReadOnlySpan<byte> premultiplied, Span<byte> destination)
    {
        if (premultiplied.Length != destination.Length || (premultiplied.Length & 3) != 0)
        {
            throw new ArgumentException("源与目标的长度必须相同且是 4 的倍数。", nameof(destination));
        }

        for (int i = 0; i < premultiplied.Length; i += 4)
        {
            byte a = premultiplied[i + 3];
            destination[i] = Unpremultiply(premultiplied[i], a);
            destination[i + 1] = Unpremultiply(premultiplied[i + 1], a);
            destination[i + 2] = Unpremultiply(premultiplied[i + 2], a);
            destination[i + 3] = a;
        }
    }

    /// <summary>导出 BGRA32（straight），给只认 Bgra 的消费方用。</summary>
    public static byte[] ToBgra32(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        var result = new byte[canvas.Width * canvas.Height * 4];
        bool opaque = canvas.IsFullyOpaque();
        for (int y = 0; y < canvas.Height; y++)
        {
            ReadOnlySpan<byte> src = canvas.GetRow(y);
            Span<byte> dst = result.AsSpan(y * canvas.Width * 4, canvas.Width * 4);

            if (opaque)
            {
                // 不透明时直通值就是预乘值，只剩 R/B 互换，连除法都不用
                SwapRedBlue(src, dst);
                continue;
            }

            for (int x = 0, o = 0; x < canvas.Width; x++, o += 4)
            {
                byte a = src[o + 3];
                dst[o] = Unpremultiply(src[o + 2], a);
                dst[o + 1] = Unpremultiply(src[o + 1], a);
                dst[o + 2] = Unpremultiply(src[o], a);
                dst[o + 3] = a;
            }
        }

        return result;
    }

    /// <summary>导出 Rgb24（straight），alpha 被丢弃。</summary>
    public static byte[] ToRgb24(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        var result = new byte[canvas.Width * canvas.Height * 3];
        bool opaque = canvas.IsFullyOpaque();
        for (int y = 0; y < canvas.Height; y++)
        {
            ReadOnlySpan<byte> src = canvas.GetRow(y);
            Span<byte> dst = result.AsSpan(y * canvas.Width * 3, canvas.Width * 3);

            if (opaque)
            {
                StripAlpha(src, dst);
                continue;
            }

            for (int x = 0, s = 0, d = 0; x < canvas.Width; x++, s += 4, d += 3)
            {
                byte a = src[s + 3];
                dst[d] = Unpremultiply(src[s], a);
                dst[d + 1] = Unpremultiply(src[s + 1], a);
                dst[d + 2] = Unpremultiply(src[s + 2], a);
            }
        }

        return result;
    }

    /// <summary>导出 8 位灰度（Rec.601，alpha 忽略），符号距离图/调试用。</summary>
    public static byte[] ToGray8(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        var result = new byte[canvas.Width * canvas.Height];
        bool opaque = canvas.IsFullyOpaque();
        for (int y = 0; y < canvas.Height; y++)
        {
            ReadOnlySpan<byte> src = canvas.GetRow(y);
            Span<byte> dst = result.AsSpan(y * canvas.Width, canvas.Width);

            if (opaque)
            {
                for (int x = 0, s = 0; x < canvas.Width; x++, s += 4)
                {
                    dst[x] = (byte)(((src[s] * 77) + (src[s + 1] * 150) + (src[s + 2] * 29)) >> 8);
                }

                continue;
            }

            for (int x = 0, s = 0; x < canvas.Width; x++, s += 4)
            {
                byte a = src[s + 3];
                int r = Unpremultiply(src[s], a);
                int g = Unpremultiply(src[s + 1], a);
                int b = Unpremultiply(src[s + 2], a);
                dst[x] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
            }
        }

        return result;
    }

    /// <summary>RGBA → RGB 的剔除 alpha：4 像素 16 字节压成 12 字节。</summary>
    private static void StripAlpha(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Vector128<byte> pack = Vector128.Create(
            (byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0xFF, 0xFF, 0xFF, 0xFF);

        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte d = ref MemoryMarshal.GetReference(dst);

        int i = 0;
        int o = 0;

        // 一次写 16 字节但只有 12 字节有效，所以尾部至少留 4 字节给标量
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 20 <= src.Length; i += 16, o += 12)
            {
                Vector128.StoreUnsafe(Vector128.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)i), pack), ref d, (nuint)o);
            }
        }

        for (; i < src.Length; i += 4, o += 3)
        {
            dst[o] = src[i];
            dst[o + 1] = src[i + 1];
            dst[o + 2] = src[i + 2];
        }
    }

    /// <summary>RGBA → BGRA 的通道互换：4 像素一条 shuffle。</summary>
    private static void SwapRedBlue(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Vector128<byte> swap = Vector128.Create(
            (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte d = ref MemoryMarshal.GetReference(dst);

        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= src.Length; i += 16)
            {
                Vector128.StoreUnsafe(Vector128.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)i), swap), ref d, (nuint)i);
            }
        }

        for (; i < src.Length; i += 4)
        {
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i];
            dst[i + 3] = src[i + 3];
        }
    }
}
