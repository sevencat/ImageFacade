namespace ImageFacade;

/// <summary>
/// 像素级几何变换（直通 RGBA32）。不涉及渲染，可被任何后端复用，保证各后端 EXIF 转正结果一致。
/// </summary>
public static class PixelTransforms
{
    /// <summary>
    /// 按 EXIF 方向（1..8）转正，返回新缓冲与转正后的尺寸。
    /// <paramref name="orientation"/> 不在 1..8 时原样返回（拷贝一份，避免调用方拿到的数组与参数共享）。
    /// </summary>
    public static byte[] ApplyOrientation(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int orientation,
        out int outputWidth,
        out int outputHeight)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "宽高必须大于 0。");
        }

        int required = checked(width * height * 4);
        if (source.Length < required)
        {
            throw new ArgumentException($"source 缓冲区太小（需要 {required} 字节，实际 {source.Length}）。", nameof(source));
        }

        if (orientation is <= 1 or > 8)
        {
            outputWidth = width;
            outputHeight = height;
            return source[..required].ToArray();
        }

        bool swapsDimensions = orientation is >= 5;
        outputWidth = swapsDimensions ? height : width;
        outputHeight = swapsDimensions ? width : height;

        var destination = new byte[required];
        int destinationStride = outputWidth * 4;
        int sourceStride = width * 4;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (int targetX, int targetY) = Map(x, y, width, height, orientation);
                int sourceOffset = (y * sourceStride) + (x * 4);
                int destinationOffset = (targetY * destinationStride) + (targetX * 4);

                destination[destinationOffset] = source[sourceOffset];
                destination[destinationOffset + 1] = source[sourceOffset + 1];
                destination[destinationOffset + 2] = source[sourceOffset + 2];
                destination[destinationOffset + 3] = source[sourceOffset + 3];
            }
        }

        return destination;
    }

    private static (int X, int Y) Map(int x, int y, int width, int height, int orientation) => orientation switch
    {
        2 => (width - 1 - x, y),                    // 水平镜像
        3 => (width - 1 - x, height - 1 - y),       // 旋转 180°
        4 => (x, height - 1 - y),                   // 垂直镜像
        5 => (y, x),                                // 转置
        6 => (height - 1 - y, x),                   // 顺时针 90°
        7 => (height - 1 - y, width - 1 - x),       // 反转置
        8 => (y, width - 1 - x),                    // 逆时针 90°
        _ => (x, y),
    };
}
