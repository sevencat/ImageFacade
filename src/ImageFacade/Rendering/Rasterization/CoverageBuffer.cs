using System.Buffers;

namespace ImageFacade.Rendering;

/// <summary>
/// A8 覆盖率缓冲（每像素 0–255），是光栅化与混合之间的唯一接口。
/// 底层数组来自 <see cref="ArrayPool{T}.Shared"/>，用完必须 Dispose。
/// </summary>
internal sealed class CoverageBuffer : IDisposable
{
    private byte[] _data;
    private int[]? _rowStart;
    private int[]? _rowEnd;

    public CoverageBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Stride = width;
        _data = PixelBufferPool.Shared.Rent(width * height);
        _data.AsSpan(0, width * height).Clear();
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>整块缓冲（长度可能大于 <c>Stride * Height</c>，来自池）。</summary>
    public byte[] Data => _data;

    /// <summary>
    /// 是否记录了每行的有效列区间。扫描线光栅化会记录，字形掩码等外部填充不会；
    /// 只有记录过才能安全地按区间跳过空列。
    /// </summary>
    public bool HasRowSpans => _rowStart is not null;

    /// <summary>记录一行的有效列区间（左闭右开）；<c>end &lt;= start</c> 表示整行为空。</summary>
    public void SetRowSpan(int y, int start, int end)
    {
        _rowStart ??= new int[Height];
        _rowEnd ??= new int[Height];
        _rowStart[y] = start;
        _rowEnd[y] = end;
    }

    /// <summary>取一行的有效列区间（左闭右开）；未记录过时返回整行。</summary>
    public (int Start, int End) GetRowSpan(int y)
        => _rowEnd is null ? (0, Width) : (_rowStart![y], _rowEnd[y]);

    public Span<byte> GetRow(int y) => _data.AsSpan(y * Stride, Width);

    public void Clear() => _data.AsSpan(0, Stride * Height).Clear();

    public void Dispose()
    {
        byte[] data = _data;
        _data = Array.Empty<byte>();
        if (data.Length > 0)
        {
            PixelBufferPool.Shared.Return(data);
        }
    }
}
