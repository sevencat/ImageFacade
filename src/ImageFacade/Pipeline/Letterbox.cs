using System.Buffers;
using System.Runtime.InteropServices;
using ImageFacade.Internal;

namespace ImageFacade.Pipeline;

/// <summary>
/// letterbox：等比缩放到目标框内，四周用 <see cref="LetterboxOptions.PadColor"/> 补齐。
/// </summary>
/// <remarks>
/// <para>返回<b>新图</b>，源图保持原尺寸与未修改内容——检测框要画回源图坐标，所以两者都要留着。</para>
/// <para>
/// 几何与门面的 <see cref="ResizeMode.Pad"/> 完全一致（复用同一套 <c>ResizePlanner</c>），
/// 因此 <see cref="LetterboxInfo"/> 的反算结果与真实像素位置相符。
/// </para>
/// </remarks>
public static class Letterbox
{
    /// <summary>YOLO 惯用的补边灰。</summary>
    public static Rgba32 DefaultPadColor => new(114, 114, 114);

    /// <summary>简便重载：只给目标尺寸。</summary>
    public static FacadeImage Apply(
        this FacadeImage source,
        int width,
        int height,
        out LetterboxInfo info,
        Rgba32? padColor = null,
        bool center = true)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Apply(
            new LetterboxOptions
            {
                Width = width,
                Height = height,
                PadColor = padColor ?? DefaultPadColor,
                Center = center,
            },
            out info);
    }

    /// <summary>按 <paramref name="options"/> 生成 letterbox 后的新图，并输出反算几何。</summary>
    public static FacadeImage Apply(this FacadeImage source, LetterboxOptions options, out LetterboxInfo info)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Width, "目标宽度必须大于 0。");
        }

        if (options.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Height, "目标高度必须大于 0。");
        }

        // 与门面的 Pad 模式共用同一份几何规划，避免"反算与像素对不上"。
        ResizePlan plan = ResizePlanner.Plan(source.Width, source.Height, options.Width, options.Height, ResizeMode.Pad);

        int placeX = options.Center ? plan.PlaceX : 0;
        int placeY = options.Center ? plan.PlaceY : 0;

        info = new LetterboxInfo(
            new Size(source.Width, source.Height),
            new Size(options.Width, options.Height),
            MathF.Min((float)plan.ScaledWidth / source.Width, (float)plan.ScaledHeight / source.Height),
            placeX,
            placeY);

        // 尺寸已匹配且无补边：直接复制，不做一次无意义的缩放。
        if (source.Width == options.Width && source.Height == options.Height && placeX == 0 && placeY == 0)
        {
            return source.Clone();
        }

        using FacadeImage scaled = source.Clone();
        scaled.Resize(new ResizeOptions
        {
            Width = plan.ScaledWidth,
            Height = plan.ScaledHeight,
            Mode = ResizeMode.Stretch,
            Sampler = options.Sampler,
        });

        int scaledStride = plan.ScaledWidth * 4;
        int scaledLength = checked(scaledStride * plan.ScaledHeight);
        byte[] scaledBuffer = ArrayPool<byte>.Shared.Rent(scaledLength);

        int outputStride = options.Width * 4;
        int outputLength = checked(outputStride * options.Height);
        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(outputLength);

        try
        {
            scaled.CopyPixelsTo(scaledBuffer.AsSpan(0, scaledLength), scaledStride);

            Span<byte> target = outputBuffer.AsSpan(0, outputLength);
            Fill(target, options.PadColor);

            ReadOnlySpan<byte> sourcePixels = scaledBuffer.AsSpan(0, scaledLength);
            for (int y = 0; y < plan.ScaledHeight; y++)
            {
                int sourceOffset = y * scaledStride;
                int targetOffset = ((placeY + y) * outputStride) + (placeX * 4);
                sourcePixels.Slice(sourceOffset, scaledStride).CopyTo(target.Slice(targetOffset, scaledStride));
            }

            // FromPixels 会立刻把像素拷进后端帧，缓冲随即归还池，不会被外部持有。
            return source.Engine.FromPixels(target, options.Width, options.Height, outputStride);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scaledBuffer);
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }

    private static void Fill(Span<byte> buffer, Rgba32 color)
    {
        uint packed = (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24));
        MemoryMarshal.Cast<byte, uint>(buffer).Fill(packed);
    }
}
