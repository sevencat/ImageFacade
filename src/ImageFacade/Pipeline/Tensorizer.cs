using System.Buffers;

namespace ImageFacade.Pipeline;

/// <summary>
/// 把 <see cref="FacadeImage"/> 转成模型输入张量：内部做 letterbox（源图不变），
/// 再按 <see cref="TensorOptions"/> 写进目标缓冲。
/// </summary>
public static class Tensorizer
{
    /// <summary>
    /// 写入调用方提供的缓冲。<paramref name="destination"/> 至少要 <c>Width * Height * 3</c> 个元素，
    /// 多余的容量可用于批处理。
    /// </summary>
    public static void ToTensor(
        this FacadeImage source,
        TensorOptions options,
        Span<float> destination,
        out LetterboxInfo info)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        int pixelCount = checked(options.Width * options.Height);
        int required = checked(pixelCount * 3);
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"destination 至少需要 {required} 个 float（实际 {destination.Length}）。",
                nameof(destination));
        }

        using FacadeImage padded = source.Apply(
            new LetterboxOptions
            {
                Width = options.Width,
                Height = options.Height,
                PadColor = options.PadColor,
                Center = options.CenterPad,
            },
            out info);

        int stride = options.Width * 4;
        int byteCount = checked(stride * options.Height);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Span<byte> canonical = rented.AsSpan(0, byteCount);
            padded.CopyPixelsTo(canonical, stride);
            Write(canonical, options, destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>便利重载：自己分配返回数组（每帧一次 LOH 分配，热路径请用 <see cref="TensorBuffer"/>）。</summary>
    public static float[] ToTensor(this FacadeImage source, TensorOptions options, out LetterboxInfo info)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var destination = new float[checked(options.Width * options.Height * 3)];
        source.ToTensor(options, destination, out info);
        return destination;
    }

    private static void Write(ReadOnlySpan<byte> pixels, TensorOptions options, Span<float> destination)
    {
        int width = options.Width;
        int height = options.Height;
        int stride = width * 4;
        int pixelCount = width * height;

        float normalization = options.Normalize ? 1f / 255f : 1f;

        ReadOnlySpan<float> mean = options.Mean.Span;
        ReadOnlySpan<float> std = options.Std.Span;
        bool hasMean = !mean.IsEmpty;
        bool hasStd = !std.IsEmpty;
        bool rgb = options.Channels == ChannelOrder.Rgb;
        bool nchw = options.Layout == TensorLayout.Nchw;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + (x * 4);

                // 通道顺序直接决定 c0/c2 取哪两个分量，避免循环里再套一层分支。
                float c0 = (rgb ? pixels[offset] : pixels[offset + 2]) * normalization;
                float c1 = pixels[offset + 1] * normalization;
                float c2 = (rgb ? pixels[offset + 2] : pixels[offset]) * normalization;

                if (hasMean)
                {
                    c0 -= mean[0];
                    c1 -= mean[1];
                    c2 -= mean[2];
                }

                if (hasStd)
                {
                    c0 /= std[0];
                    c1 /= std[1];
                    c2 /= std[2];
                }

                if (nchw)
                {
                    int spatial = (y * width) + x;
                    destination[spatial] = c0;
                    destination[pixelCount + spatial] = c1;
                    destination[(2 * pixelCount) + spatial] = c2;
                }
                else
                {
                    int target = ((y * width) + x) * 3;
                    destination[target] = c0;
                    destination[target + 1] = c1;
                    destination[target + 2] = c2;
                }
            }
        }
    }

    private static void Validate(TensorOptions options)
    {
        if (options.Width <= 0 || options.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Width / Height 必须大于 0。");
        }

        ValidateVector(options.Mean, nameof(options.Mean), requirePositive: false);
        ValidateVector(options.Std, nameof(options.Std), requirePositive: true);
    }

    private static void ValidateVector(ReadOnlyMemory<float> vector, string name, bool requirePositive)
    {
        if (vector.IsEmpty)
        {
            return;
        }

        if (vector.Length != 3)
        {
            throw new ArgumentException($"{name} 长度必须为 3（实际 {vector.Length}）。", name);
        }

        if (!requirePositive)
        {
            return;
        }

        ReadOnlySpan<float> span = vector.Span;
        for (int i = 0; i < 3; i++)
        {
            if (!(span[i] > 0f))
            {
                throw new ArgumentException($"{name}[{i}] 必须大于 0。", name);
            }
        }
    }
}
