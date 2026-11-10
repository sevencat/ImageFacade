using System.Buffers;

namespace ImageFacade.Software.Rendering;

/// <summary>
/// A8 覆盖率缓冲（每像素 0–255），是光栅化与混合之间的唯一接口。
/// 底层数组来自 <see cref="ArrayPool{T}.Shared"/>，用完必须 Dispose。
/// </summary>
internal sealed class CoverageBuffer : IDisposable
{
    private byte[] _data;

    public CoverageBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Stride = width;
        _data = ArrayPool<byte>.Shared.Rent(width * height);
        _data.AsSpan(0, width * height).Clear();
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>整块缓冲（长度可能大于 <c>Stride * Height</c>，来自池）。</summary>
    public byte[] Data => _data;

    public Span<byte> GetRow(int y) => _data.AsSpan(y * Stride, Width);

    public void Clear() => _data.AsSpan(0, Stride * Height).Clear();

    public void Dispose()
    {
        byte[] data = _data;
        _data = Array.Empty<byte>();
        if (data.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }
}
