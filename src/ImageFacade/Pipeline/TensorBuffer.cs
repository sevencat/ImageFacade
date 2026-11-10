using System.Buffers;

namespace ImageFacade.Pipeline;

/// <summary>
/// 池化的 <c>float</c> 缓冲，避免 640×640×3（约 4.9 MB）每帧进 LOH。
/// </summary>
/// <remarks>
/// 用的是私有池而不是 <see cref="ArrayPool{T}.Shared"/>：后者的最大桶只有 1,048,576
/// 个元素，装不下 640×640×3 = 1,228,800，会退化成每帧新分配。
/// </remarks>
public sealed class TensorBuffer : IDisposable
{
    private static readonly ArrayPool<float> Pool =
        ArrayPool<float>.Create(maxArrayLength: 2_000_000, maxArraysPerBucket: 4);

    private float[]? _array;
    private readonly int _length;

    private TensorBuffer(float[] array, int length)
    {
        _array = array;
        _length = length;
    }

    /// <summary>租一个至少能装 <paramref name="elementCount"/> 个 <c>float</c> 的缓冲。</summary>
    public static TensorBuffer Rent(int elementCount)
    {
        if (elementCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount), elementCount, "元素个数必须大于 0。");
        }

        return new TensorBuffer(Pool.Rent(elementCount), elementCount);
    }

    /// <summary>有效区间（长度等于 <see cref="Rent"/> 传入的元素个数）。</summary>
    public Span<float> Span
        => (_array ?? throw new ObjectDisposedException(nameof(TensorBuffer))).AsSpan(0, _length);

    /// <summary>同上，但可存进字段、跨 <c>await</c> 使用（例如交给 onnxruntime 的 <c>OrtValue</c>）。</summary>
    public Memory<float> Memory
        => (_array ?? throw new ObjectDisposedException(nameof(TensorBuffer))).AsMemory(0, _length);

    /// <summary>有效元素个数。</summary>
    public int Length => _length;

    /// <summary>归还池；可重复调用。</summary>
    public void Dispose()
    {
        float[]? array = _array;
        if (array is null)
        {
            return;
        }

        _array = null;
        Pool.Return(array);
    }
}
