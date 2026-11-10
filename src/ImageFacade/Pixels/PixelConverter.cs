using System.Runtime.InteropServices;

namespace ImageFacade;

/// <summary>
/// 规范像素与常见布局之间的逐行转换（忽略 padding，只写有效像素）。
/// 这是 <c>FromPixels</c> / <c>CopyPixelsTo</c> 的底层实现，也单独公开给采集/UI 适配层。
/// </summary>
public static class PixelConverter
{
    public static void Convert(
        ReadOnlySpan<byte> source,
        int sourceStride,
        PixelFormat sourceFormat,
        Span<byte> destination,
        int destinationStride,
        PixelFormat destinationFormat,
        int width,
        int height)
    {
        Validate(source.Length, sourceStride, sourceFormat, destination.Length, destinationStride, destinationFormat, width, height);

        int sourceBpp = sourceFormat.GetBytesPerPixel();
        int destinationBpp = destinationFormat.GetBytesPerPixel();
        int sourceRowBytes = width * sourceBpp;
        int destinationRowBytes = width * destinationBpp;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> sourceRow = source.Slice(y * sourceStride, sourceRowBytes);
            Span<byte> destinationRow = destination.Slice(y * destinationStride, destinationRowBytes);

            if (sourceFormat == destinationFormat)
            {
                sourceRow.CopyTo(destinationRow);
                continue;
            }

            ConvertRow(sourceRow, destinationRow, sourceFormat, destinationFormat, width);
        }
    }

    public static void Convert(
        IntPtr source,
        int sourceStride,
        PixelFormat sourceFormat,
        IntPtr destination,
        int destinationStride,
        PixelFormat destinationFormat,
        int width,
        int height)
    {
        if (source == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        int sourceLength = checked(sourceStride * height);
        int destinationLength = checked(destinationStride * height);

        unsafe
        {
            Convert(
                new ReadOnlySpan<byte>((void*)source, sourceLength),
                sourceStride,
                sourceFormat,
                new Span<byte>((void*)destination, destinationLength),
                destinationStride,
                destinationFormat,
                width,
                height);
        }
    }

    private static void ConvertRow(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        PixelFormat sourceFormat,
        PixelFormat destinationFormat,
        int width)
    {
        int sourceBpp = sourceFormat.GetBytesPerPixel();
        int destinationBpp = destinationFormat.GetBytesPerPixel();

        for (int x = 0; x < width; x++)
        {
            Rgba32 color = ReadPixel(source, x * sourceBpp, sourceFormat);
            WritePixel(destination, x * destinationBpp, destinationFormat, color);
        }
    }

    private static Rgba32 ReadPixel(ReadOnlySpan<byte> row, int offset, PixelFormat format) => format switch
    {
        PixelFormat.Rgba32 => new Rgba32(row[offset], row[offset + 1], row[offset + 2], row[offset + 3]),
        PixelFormat.Bgra32 => new Rgba32(row[offset + 2], row[offset + 1], row[offset], row[offset + 3]),
        PixelFormat.Rgb24 => new Rgba32(row[offset], row[offset + 1], row[offset + 2]),
        PixelFormat.Bgr24 => new Rgba32(row[offset + 2], row[offset + 1], row[offset]),
        PixelFormat.Gray8 => new Rgba32(row[offset], row[offset], row[offset]),
        PixelFormat.Bgra32Premultiplied => Unpremultiply(row[offset + 2], row[offset + 1], row[offset], row[offset + 3]),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知像素格式。"),
    };

    private static void WritePixel(Span<byte> row, int offset, PixelFormat format, Rgba32 color)
    {
        switch (format)
        {
            case PixelFormat.Rgba32:
                row[offset] = color.R;
                row[offset + 1] = color.G;
                row[offset + 2] = color.B;
                row[offset + 3] = color.A;
                break;

            case PixelFormat.Bgra32:
                row[offset] = color.B;
                row[offset + 1] = color.G;
                row[offset + 2] = color.R;
                row[offset + 3] = color.A;
                break;

            case PixelFormat.Rgb24:
                row[offset] = color.R;
                row[offset + 1] = color.G;
                row[offset + 2] = color.B;
                break;

            case PixelFormat.Bgr24:
                row[offset] = color.B;
                row[offset + 1] = color.G;
                row[offset + 2] = color.R;
                break;

            case PixelFormat.Gray8:
                row[offset] = ToLuma(color);
                break;

            case PixelFormat.Bgra32Premultiplied:
                byte alpha = color.A;
                row[offset] = Premultiply(color.B, alpha);
                row[offset + 1] = Premultiply(color.G, alpha);
                row[offset + 2] = Premultiply(color.R, alpha);
                row[offset + 3] = alpha;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "未知像素格式。");
        }
    }

    /// <summary>BT.601 亮度，整数近似：<c>(77r + 151g + 28b) &gt;&gt; 8</c>。</summary>
    internal static byte ToLuma(Rgba32 color)
        => (byte)(((color.R * 77) + (color.G * 151) + (color.B * 28)) >> 8);

    internal static byte Premultiply(byte channel, byte alpha)
        => alpha == 255 ? channel : (byte)((channel * alpha + 127) / 255);

    internal static Rgba32 Unpremultiply(byte r, byte g, byte b, byte a)
    {
        if (a == 255 || a == 0)
        {
            return new Rgba32(r, g, b, a);
        }

        return new Rgba32(
            (byte)Math.Min(255, (r * 255 + (a / 2)) / a),
            (byte)Math.Min(255, (g * 255 + (a / 2)) / a),
            (byte)Math.Min(255, (b * 255 + (a / 2)) / a),
            a);
    }

    private static void Validate(
        int sourceLength,
        int sourceStride,
        PixelFormat sourceFormat,
        int destinationLength,
        int destinationStride,
        PixelFormat destinationFormat,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "宽高必须大于 0。");
        }

        int sourceMinStride = width * sourceFormat.GetBytesPerPixel();
        int destinationMinStride = width * destinationFormat.GetBytesPerPixel();

        if (sourceStride < sourceMinStride)
        {
            throw new ArgumentException($"sourceStride 太小（{sourceStride} < {sourceMinStride}）。", nameof(sourceStride));
        }

        if (destinationStride < destinationMinStride)
        {
            throw new ArgumentException($"destinationStride 太小（{destinationStride} < {destinationMinStride}）。", nameof(destinationStride));
        }

        long required = ((long)height - 1) * sourceStride + sourceMinStride;
        if (sourceLength < required)
        {
            throw new ArgumentException($"source 缓冲区太小（需要 {required} 字节，实际 {sourceLength}）。", nameof(sourceLength));
        }

        long available = ((long)height - 1) * destinationStride + destinationMinStride;
        if (destinationLength < available)
        {
            throw new ArgumentException($"destination 缓冲区太小（需要 {available} 字节，实际 {destinationLength}）。", nameof(destinationLength));
        }
    }

    /// <summary>供后端实现复用：把一块指针内存拷成托管数组（自底向上时由调用方先修正指针）。</summary>
    public static byte[] CopyFromPtr(IntPtr source, int stride, int height)
    {
        if (source == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var buffer = new byte[checked(stride * height)];
        Marshal.Copy(source, buffer, 0, buffer.Length);
        return buffer;
    }
}
