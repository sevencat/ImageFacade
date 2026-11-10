using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ImageFacade.Software.Rendering;

/// <summary>
/// 预乘空间的行混合内核。<c>dst</c> 是画布的一行（预乘 Rgba32），
/// <c>coverage</c> 是逐像素覆盖率（0–255），二者像素数必须一致。
/// <para>标量与 SIMD 两条路径必须逐字节相同（有单元测试守住），SIMD 只是快，不改语义。</para>
/// </summary>
internal static class RowBlender
{
    // 把 4 个覆盖率值展开成「每个像素 4 个通道」的 ushort 通道向量
    private static readonly Vector128<ushort> DupFirstTwo = Vector128.Create((ushort)0, 0, 0, 0, 1, 1, 1, 1);
    private static readonly Vector128<ushort> DupLastTwo = Vector128.Create((ushort)2, 2, 2, 2, 3, 3, 3, 3);

    /// <summary>
    /// 实心颜色按覆盖率 source-over 到目标行。目标行必须是预乘的。
    /// <c>out = src * c + dst * (255 - a) / 255</c>，其中 <c>a = srcA * c / 255</c>。
    /// </summary>
    internal static void OverSolid(Span<byte> dst, Color color, ReadOnlySpan<byte> coverage)
    {
        if (dst.Length != coverage.Length * Canvas.BytesPerPixel)
        {
            throw new ArgumentException("目标行长度必须是 coverage 长度 * 4。", nameof(dst));
        }

        if (coverage.Length >= 4 && Vector128.IsHardwareAccelerated)
        {
            OverSolidVector128(dst, color, coverage);
            return;
        }

        OverSolidScalar(dst, color, coverage);
    }

    internal static void OverSolidScalar(Span<byte> dst, Color color, ReadOnlySpan<byte> coverage)
    {
        if (dst.Length != coverage.Length * Canvas.BytesPerPixel)
        {
            throw new ArgumentException("目标行长度必须是 coverage 长度 * 4。", nameof(dst));
        }

        int srcAlpha = color.A;
        int sr = PixelOps.Mul255(color.R, srcAlpha);
        int sg = PixelOps.Mul255(color.G, srcAlpha);
        int sb = PixelOps.Mul255(color.B, srcAlpha);

        for (int i = 0, o = 0; i < coverage.Length; i++, o += Canvas.BytesPerPixel)
        {
            int c = coverage[i];
            if (c == 0)
            {
                continue;
            }

            int a = PixelOps.Mul255(srcAlpha, c);
            int inv = 255 - a;
            dst[o] = (byte)Math.Min(255, PixelOps.Mul255(sr, c) + PixelOps.Mul255(dst[o], inv));
            dst[o + 1] = (byte)Math.Min(255, PixelOps.Mul255(sg, c) + PixelOps.Mul255(dst[o + 1], inv));
            dst[o + 2] = (byte)Math.Min(255, PixelOps.Mul255(sb, c) + PixelOps.Mul255(dst[o + 2], inv));
            dst[o + 3] = (byte)Math.Min(255, a + PixelOps.Mul255(dst[o + 3], inv));
        }
    }

    /// <summary>
    /// 整行覆盖率恒为 255 的实心色 source-over。此时 <c>a</c> 就是 <c>srcA</c>、源项是常量，
    /// 每像素只剩一次 <c>dst * (255 - a) / 255</c>，比通用内核少一半的 <c>/255</c>。
    /// 结果与 <c>OverSolid(dst, color, 全 255)</c> 逐字节相同。
    /// </summary>
    internal static void OverSolidFullRow(Span<byte> dst, Color color)
    {
        if (dst.Length % Canvas.BytesPerPixel != 0)
        {
            throw new ArgumentException("目标行长度必须是像素数 * 4。", nameof(dst));
        }

        // 源完全透明时 source-over 是恒等变换，连读都不用读。
        if (color.A == 0)
        {
            return;
        }

        if (dst.Length >= Canvas.BytesPerPixel * 4 && Vector128.IsHardwareAccelerated)
        {
            OverSolidFullRowVector128(dst, color);
            return;
        }

        OverSolidFullRowScalar(dst, color);
    }

    internal static void OverSolidFullRowScalar(Span<byte> dst, Color color)
    {
        int sa = color.A;
        int inv = 255 - sa;
        int sr = PixelOps.Mul255(color.R, sa);
        int sg = PixelOps.Mul255(color.G, sa);
        int sb = PixelOps.Mul255(color.B, sa);

        for (int o = 0; o < dst.Length; o += Canvas.BytesPerPixel)
        {
            dst[o] = (byte)Math.Min(255, sr + PixelOps.Mul255(dst[o], inv));
            dst[o + 1] = (byte)Math.Min(255, sg + PixelOps.Mul255(dst[o + 1], inv));
            dst[o + 2] = (byte)Math.Min(255, sb + PixelOps.Mul255(dst[o + 2], inv));
            dst[o + 3] = (byte)Math.Min(255, sa + PixelOps.Mul255(dst[o + 3], inv));
        }
    }

    internal static void OverSolidFullRowVector128(Span<byte> dst, Color color)
    {
        int sa = color.A;
        int sr = PixelOps.Mul255(color.R, sa);
        int sg = PixelOps.Mul255(color.G, sa);
        int sb = PixelOps.Mul255(color.B, sa);

        Vector128<ushort> srcPx = Vector128.Create(
            (ushort)sr, (ushort)sg, (ushort)sb, (ushort)sa,
            (ushort)sr, (ushort)sg, (ushort)sb, (ushort)sa);
        Vector128<ushort> inverse = Vector128.Create((ushort)(255 - sa));
        Vector128<ushort> full = Vector128.Create((ushort)255);

        ref byte dstRef = ref MemoryMarshal.GetReference(dst);
        int i = 0;
        for (; i + 16 <= dst.Length; i += 16)
        {
            Vector128<byte> raw = Vector128.LoadUnsafe(ref dstRef, (nuint)i);
            Vector128<ushort> low = Vector128.WidenLower(raw);
            Vector128<ushort> high = Vector128.WidenUpper(raw);

            Vector128<ushort> resultLow = Vector128.Min(srcPx + Div255(low * inverse), full);
            Vector128<ushort> resultHigh = Vector128.Min(srcPx + Div255(high * inverse), full);

            Vector128.StoreUnsafe(Vector128.Narrow(resultLow, resultHigh), ref dstRef, (nuint)i);
        }

        if (i < dst.Length)
        {
            OverSolidFullRowScalar(dst.Slice(i), color);
        }
    }

    /// <summary>一次处理 4 个像素（16 字节 = 两个 8 通道向量）。</summary>
    internal static void OverSolidVector128(Span<byte> dst, Color color, ReadOnlySpan<byte> coverage)
    {
        int sa = color.A;
        int sr = PixelOps.Mul255(color.R, sa);
        int sg = PixelOps.Mul255(color.G, sa);
        int sb = PixelOps.Mul255(color.B, sa);

        Vector128<ushort> srcPx = Vector128.Create(
            (ushort)sr, (ushort)sg, (ushort)sb, (ushort)sa,
            (ushort)sr, (ushort)sg, (ushort)sb, (ushort)sa);
        Vector128<ushort> srcAlpha = Vector128.Create((ushort)sa);
        Vector128<ushort> full = Vector128.Create((ushort)255);

        ref byte dstRef = ref MemoryMarshal.GetReference(dst);
        ref readonly byte covRef = ref MemoryMarshal.GetReference(coverage);

        int pixels = coverage.Length;
        int i = 0;

        // 一次拖 16 字节 coverage（只用到前 4 个），比 16 条标量插入（pinsrb）便宜得多。
        for (; i + 16 <= pixels; i += 4)
        {
            BlendFour(ref dstRef, i * Canvas.BytesPerPixel, Vector128.LoadUnsafe(in covRef, (nuint)i), srcPx, srcAlpha, full);
        }

        // 尾部不足 16 像素：仍按 4 像素一组走向量，coverage 用安全的逐字节构造，不越界读取
        for (; i + 4 <= pixels; i += 4)
        {
            Vector128<byte> covBytes = Vector128.Create(
                coverage[i], coverage[i + 1], coverage[i + 2], coverage[i + 3],
                (byte)0, (byte)0, (byte)0, (byte)0,
                (byte)0, (byte)0, (byte)0, (byte)0,
                (byte)0, (byte)0, (byte)0, (byte)0);
            BlendFour(ref dstRef, i * Canvas.BytesPerPixel, covBytes, srcPx, srcAlpha, full);
        }

        if (i < pixels)
        {
            OverSolidScalar(dst.Slice(i * Canvas.BytesPerPixel), color, coverage.Slice(i));
        }
    }

    /// <summary>混合 4 个像素；<paramref name="covBytes"/> 的低 4 字节是覆盖率、其余必须是 0。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BlendFour(
        ref byte dst,
        int offset,
        Vector128<byte> covBytes,
        Vector128<ushort> srcPx,
        Vector128<ushort> srcAlpha,
        Vector128<ushort> full)
    {
        Vector128<ushort> covWide = Vector128.WidenLower(covBytes);

        Vector128<byte> raw = Vector128.LoadUnsafe(ref dst, (nuint)offset);
        Vector128<ushort> low = Vector128.WidenLower(raw);
        Vector128<ushort> high = Vector128.WidenUpper(raw);

        Vector128<ushort> covLow = Vector128.Shuffle(covWide, DupFirstTwo);
        Vector128<ushort> covHigh = Vector128.Shuffle(covWide, DupLastTwo);

        Vector128<ushort> resultLow = Blend(low, srcPx, srcAlpha, covLow, full);
        Vector128<ushort> resultHigh = Blend(high, srcPx, srcAlpha, covHigh, full);

        Vector128.StoreUnsafe(Vector128.Narrow(resultLow, resultHigh), ref dst, (nuint)offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Blend(
        Vector128<ushort> dst,
        Vector128<ushort> srcPremultiplied,
        Vector128<ushort> srcAlpha,
        Vector128<ushort> coverage,
        Vector128<ushort> full)
    {
        Vector128<ushort> blended = Div255(srcPremultiplied * coverage);
        Vector128<ushort> inverse = full - Div255(srcAlpha * coverage);
        return Vector128.Min(blended + Div255(dst * inverse), full);
    }

    /// <summary>
    /// 源是连续像素（瓦片/图案）的 source-over。<paramref name="src"/> 与 <paramref name="dst"/> 等长，
    /// <paramref name="coverage"/> 每像素一个字节。
    /// <para>
    /// 预乘不变式保证 <c>sa == 0</c> 时 RGB 也是 0，此时混合结果是恒等的，因此两条路径都不必特判。
    /// </para>
    /// </summary>
    internal static void OverPattern(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<byte> coverage)
    {
        if (src.Length != dst.Length || coverage.Length * Canvas.BytesPerPixel != dst.Length)
        {
            throw new ArgumentException("源、目标、覆盖率长度不匹配。", nameof(coverage));
        }

        if (dst.Length >= 16 && Vector128.IsHardwareAccelerated)
        {
            OverPatternVector128(dst, src, coverage);
            return;
        }

        OverPatternScalar(dst, src, coverage);
    }

    internal static void OverPatternScalar(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<byte> coverage)
    {
        for (int i = 0, o = 0; i < coverage.Length; i++, o += Canvas.BytesPerPixel)
        {
            int c = coverage[i];
            int a = PixelOps.Mul255(src[o + 3], c);
            int inv = 255 - a;

            dst[o] = (byte)Math.Min(255, PixelOps.Mul255(src[o], c) + PixelOps.Mul255(dst[o], inv));
            dst[o + 1] = (byte)Math.Min(255, PixelOps.Mul255(src[o + 1], c) + PixelOps.Mul255(dst[o + 1], inv));
            dst[o + 2] = (byte)Math.Min(255, PixelOps.Mul255(src[o + 2], c) + PixelOps.Mul255(dst[o + 2], inv));
            dst[o + 3] = (byte)Math.Min(255, a + PixelOps.Mul255(dst[o + 3], inv));
        }
    }

    internal static void OverPatternVector128(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<byte> coverage)
    {
        // 8 个 ushort 通道里，两个像素的 A 分别落在索引 3 和 7
        Vector128<ushort> alphaDup = Vector128.Create(
            (ushort)3, (ushort)3, (ushort)3, (ushort)3, (ushort)7, (ushort)7, (ushort)7, (ushort)7);
        Vector128<ushort> full = Vector128.Create((ushort)255);

        ref byte dstRef = ref MemoryMarshal.GetReference(dst);
        ref byte srcRef = ref MemoryMarshal.GetReference(src);

        int i = 0;
        int c = 0;
        for (; i + 16 <= dst.Length; i += 16, c += 4)
        {
            Vector128<byte> srcRaw = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
            Vector128<byte> dstRaw = Vector128.LoadUnsafe(ref dstRef, (nuint)i);

            Vector128<ushort> srcLow = Vector128.WidenLower(srcRaw);
            Vector128<ushort> srcHigh = Vector128.WidenUpper(srcRaw);
            Vector128<ushort> dstLow = Vector128.WidenLower(dstRaw);
            Vector128<ushort> dstHigh = Vector128.WidenUpper(dstRaw);

            byte c0 = coverage[c];
            byte c1 = coverage[c + 1];
            byte c2 = coverage[c + 2];
            byte c3 = coverage[c + 3];

            Vector128<ushort> covLow = Vector128.Create(
                (ushort)c0, (ushort)c0, (ushort)c0, (ushort)c0,
                (ushort)c1, (ushort)c1, (ushort)c1, (ushort)c1);
            Vector128<ushort> covHigh = Vector128.Create(
                (ushort)c2, (ushort)c2, (ushort)c2, (ushort)c2,
                (ushort)c3, (ushort)c3, (ushort)c3, (ushort)c3);

            Vector128<ushort> resultLow = Blend(
                dstLow, srcLow, Vector128.Shuffle(srcLow, alphaDup), covLow, full);
            Vector128<ushort> resultHigh = Blend(
                dstHigh, srcHigh, Vector128.Shuffle(srcHigh, alphaDup), covHigh, full);

            Vector128.StoreUnsafe(Vector128.Narrow(resultLow, resultHigh), ref dstRef, (nuint)i);
        }

        if (i < dst.Length)
        {
            OverPatternScalar(dst.Slice(i), src.Slice(i), coverage.Slice(c));
        }
    }

    /// <summary>ushort 通道上的 <c>x / 255</c>：<c>(t + (t &gt;&gt; 8)) &gt;&gt; 8</c>，<c>t = x + 128</c>。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Div255(Vector128<ushort> x)
    {
        Vector128<ushort> t = x + Vector128.Create((ushort)0x80);
        return (t + Vector128.ShiftRightLogical(t, 8)) >>> 8;
    }
}
