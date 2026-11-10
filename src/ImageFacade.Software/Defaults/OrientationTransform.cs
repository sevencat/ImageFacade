namespace ImageFacade.Backends;

/// <summary>
/// EXIF 方向 1..8 → 像素重排（直通 RGBA32，纯搬运，不涉及渲染）。
/// </summary>
internal static class OrientationTransform
{
    /// <summary>按 <paramref name="orientation"/> 转正，返回新缓冲与新尺寸。</summary>
    public static (byte[] Pixels, int Width, int Height) Apply(ReadOnlySpan<byte> source, int width, int height, int orientation)
    {
        if (orientation is <= 1 or > 8)
        {
            return (source.ToArray(), width, height);
        }

        bool swap = orientation is >= 5;
        int targetWidth = swap ? height : width;
        int targetHeight = swap ? width : height;
        var destination = new byte[checked(targetWidth * targetHeight * 4)];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (int targetX, int targetY) = Map(x, y, width, height, orientation);
                int sourceOffset = ((y * width) + x) * 4;
                int destinationOffset = ((targetY * targetWidth) + targetX) * 4;
                destination[destinationOffset] = source[sourceOffset];
                destination[destinationOffset + 1] = source[sourceOffset + 1];
                destination[destinationOffset + 2] = source[sourceOffset + 2];
                destination[destinationOffset + 3] = source[sourceOffset + 3];
            }
        }

        return (destination, targetWidth, targetHeight);
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
