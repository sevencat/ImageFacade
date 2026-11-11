using System.Collections.Concurrent;

namespace ImageFacade.Pipeline;

/// <summary>
/// 池化的 <c>float</c> 缓冲，避免 640×640×3（约 4.9 MB）每帧进 LOH。
/// </summary>
/// <remarks>
/// <para>
/// 用私有池而不是 <c>ArrayPool&lt;T&gt;.Shared</c>：后者的最大桶只有 1,048,576 个元素，
/// 装不下 640×640×3 = 1,228,800，会退化成每帧新分配。
/// </para>
/// <para>
/// 私有池由本类型自己维护（最多 <see cref="MaxPooledArrays"/> 块、单块不超过
/// <see cref="MaxPooledLength"/> 个元素），因此 <see cref="Trim"/> 能真正释放常驻内存——
/// <c>ArrayPool&lt;T&gt;</c> 没有公开的清理入口。
/// </para>
/// </remarks>
public sealed class TensorBuffer : IDisposable
{
    private const int MaxPooledArrays = 4;

    private const int MaxPooledLength = 2_000_000;

    private static readonly ConcurrentStack<float[]> Pool = new();

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

        return new TensorBuffer(RentArray(elementCount), elementCount);
    }

    /// <summary>
    /// 清空池缓存，释放常驻内存（批量推理结束后调用，或内存吃紧时调用）。
    /// 已租出的缓冲不受影响，照常可用。
    /// </summary>
    public static void Trim() => Pool.Clear();

    /// <summary>当前池里缓存了几块缓冲（诊断 / 测试用）。</summary>
    public static int PooledCount => Pool.Count;

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

        // 超大数组不进池：留着只会把常驻内存钉在高水位
        if (array.Length <= MaxPooledLength && Pool.Count < MaxPooledArrays)
        {
            Pool.Push(array);
        }
    }

    private static float[] RentArray(int elementCount)
    {
        // 池里的块不保证长度一致：足够大就复用，不够就丢掉找下一块
        while (Pool.TryPop(out float[]? candidate))
        {
            if (candidate.Length >= elementCount)
            {
                return candidate;
            }
        }

        return new float[elementCount];
    }
}
