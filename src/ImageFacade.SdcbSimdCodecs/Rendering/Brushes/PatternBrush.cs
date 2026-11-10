using System.Numerics;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 图案笔刷：把一块 <see cref="Canvas"/> 当瓦片平铺。<c>origin</c> 是瓦片左上角在画布坐标系里的位置
/// （整数像素，允许为负或超出画布），实际采样按取模环绕。
/// <para>
/// 构造时深拷贝瓦片并转成预乘，之后外部修改原瓦片不会影响笔刷；同时避免每次混合作 prefetch。
/// </para>
/// <para>
/// 在非恒等 <see cref="DrawingContext.Transform"/> 下平铺跟随变换（瓦片原点、平铺周期都在用户坐标系里），
/// 采样点先用逆矩阵映射回用户坐标。
/// </para>
/// </summary>
internal sealed class PatternBrush : IBrush
{
    private readonly byte[] _tile;
    private readonly int _tileWidth;
    private readonly int _tileHeight;
    private readonly int _originX;
    private readonly int _originY;
    private readonly Matrix3x2 _inverse;
    private readonly bool _transformed;

    public PatternBrush(Canvas tile, PointF origin)
        : this(
            CopyTile(tile),
            tile.Width,
            tile.Height,
            (int)MathF.Floor(origin.X),
            (int)MathF.Floor(origin.Y),
            Matrix3x2.Identity)
    {
    }

    private PatternBrush(byte[] tile, int tileWidth, int tileHeight, int originX, int originY, Matrix3x2 inverse)
    {
        _tile = tile;
        _tileWidth = tileWidth;
        _tileHeight = tileHeight;
        _originX = originX;
        _originY = originY;
        _inverse = inverse;
        _transformed = !inverse.IsIdentity;
        SolidColor = FindSolidColor();
    }

    public int TileWidth => _tileWidth;

    public int TileHeight => _tileHeight;

    public PointF Origin => new(_originX, _originY);

    /// <summary>瓦片所有像素同色时给出该颜色（让纯色平铺命中纯色快路径），否则为 <see langword="null"/>。</summary>
    internal Color? SolidColor { get; }

    public bool TryGetSolidColor(out Color color)
    {
        color = SolidColor ?? default;
        return SolidColor.HasValue;
    }

    /// <summary>返回一个平铺相位跟随逆矩阵的副本；瓦片数据共享（构造后只读）。</summary>
    internal PatternBrush WithInverseTransform(Matrix3x2 inverse)
        => new(_tile, _tileWidth, _tileHeight, _originX, _originY, inverse);

    public void Blend(Canvas canvas, Rectangle region, ReadOnlySpan<byte> coverage, int coverageStride)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        for (int y = 0; y < region.Height; y++)
        {
            int absoluteY = region.Y + y;

            Span<byte> row = canvas.GetRow(absoluteY)
                .Slice(region.X * Canvas.BytesPerPixel, region.Width * Canvas.BytesPerPixel);
            ReadOnlySpan<byte> coverageRow = coverage.Slice(y * coverageStride, region.Width);

            if (_transformed)
            {
                BlendRowTransformed(row, coverageRow, region.X, absoluteY);
                continue;
            }

            int tileY = Mod(absoluteY - _originY, _tileHeight) * _tileWidth * Canvas.BytesPerPixel;
            int tileRowBytes = _tileWidth * Canvas.BytesPerPixel;

            // 行内瓦片索引是「从起点递增、到边界回绕」：起步取一次模，之后按瓦片内连续段整段混合，
            // 段内不必逐像素取模，也能让内核一次处理多个像素。
            int tileX = Mod(region.X - _originX, _tileWidth) * Canvas.BytesPerPixel;
            int i = 0;
            int o = 0;

            while (i < coverageRow.Length)
            {
                int pixels = Math.Min(coverageRow.Length - i, (tileRowBytes - tileX) / Canvas.BytesPerPixel);

                RowBlender.OverPattern(
                    row.Slice(o, pixels * Canvas.BytesPerPixel),
                    _tile.AsSpan(tileY + tileX, pixels * Canvas.BytesPerPixel),
                    coverageRow.Slice(i, pixels));

                i += pixels;
                o += pixels * Canvas.BytesPerPixel;
                tileX += pixels * Canvas.BytesPerPixel;
                if (tileX == tileRowBytes)
                {
                    tileX = 0;
                }
            }
        }
    }

    private void BlendRowTransformed(Span<byte> row, ReadOnlySpan<byte> coverageRow, int absoluteX, int absoluteY)
    {
        for (int i = 0, o = 0; i < coverageRow.Length; i++, o += Canvas.BytesPerPixel)
        {
            byte c = coverageRow[i];
            if (c == 0)
            {
                continue;
            }

            // 像素中心 → 用户坐标 → 整数格（与不改换时的整数采样等价）
            float px = absoluteX + i + 0.5f;
            float py = absoluteY + 0.5f;
            float userX = (px * _inverse.M11) + (py * _inverse.M21) + _inverse.M31;
            float userY = (px * _inverse.M12) + (py * _inverse.M22) + _inverse.M32;

            int tileX = Mod((int)MathF.Floor(userX) - _originX, _tileWidth) * Canvas.BytesPerPixel;
            int tileY = Mod((int)MathF.Floor(userY) - _originY, _tileHeight) * _tileWidth * Canvas.BytesPerPixel;
            BlendPixel(row, o, tileY + tileX, c);
        }
    }

    private void BlendPixel(Span<byte> row, int offset, int tileOffset, byte coverage)
    {
        int sa = _tile[tileOffset + 3];
        if (sa == 0)
        {
            return;
        }

        int a = PixelOps.Mul255(sa, coverage);
        int inv = 255 - a;

        row[offset] = (byte)Math.Min(255, PixelOps.Mul255(_tile[tileOffset], coverage) + PixelOps.Mul255(row[offset], inv));
        row[offset + 1] = (byte)Math.Min(255, PixelOps.Mul255(_tile[tileOffset + 1], coverage) + PixelOps.Mul255(row[offset + 1], inv));
        row[offset + 2] = (byte)Math.Min(255, PixelOps.Mul255(_tile[tileOffset + 2], coverage) + PixelOps.Mul255(row[offset + 2], inv));
        row[offset + 3] = (byte)Math.Min(255, a + PixelOps.Mul255(row[offset + 3], inv));
    }

    private static byte[] CopyTile(Canvas tile)
    {
        ArgumentNullException.ThrowIfNull(tile);

        // 画布本来就是预乘存储，直接按行拷贝即可
        var buffer = new byte[tile.Width * tile.Height * Canvas.BytesPerPixel];
        int rowBytes = tile.Width * Canvas.BytesPerPixel;
        for (int y = 0; y < tile.Height; y++)
        {
            tile.GetRow(y).CopyTo(buffer.AsSpan(y * rowBytes, rowBytes));
        }

        return buffer;
    }

    private static int Mod(int value, int modulus)
    {
        int r = value % modulus;
        return r < 0 ? r + modulus : r;
    }

    private Color? FindSolidColor()
    {
        uint first = PackAt(0, 0);
        for (int y = 0; y < _tileHeight; y++)
        {
            for (int x = 0; x < _tileWidth; x++)
            {
                if (PackAt(x, y) != first)
                {
                    return null;
                }
            }
        }

        // 瓦片只有预乘数据，反预乘会丢精度；只有完全不透明时才能无误差地还原成纯色
        byte a = _tile[3];
        if (a != 255)
        {
            return null;
        }

        return new Color(_tile[0], _tile[1], _tile[2], 255);
    }

    private uint PackAt(int x, int y)
    {
        int o = ((y * _tileWidth) + x) * Canvas.BytesPerPixel;
        return (uint)(_tile[o] | (_tile[o + 1] << 8) | (_tile[o + 2] << 16) | (_tile[o + 3] << 24));
    }
}
