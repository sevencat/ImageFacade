using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace ImageFacade.Internal;

/// <summary>
/// 只读内存映射的整份文件：解码直接吃映射出来的这块内存，不再像 <c>File.ReadAllBytes</c>
/// 那样先按文件大小分配一份托管数组（大图省掉一次等长分配 + 一次全量拷贝）。
/// </summary>
/// <remarks>
/// <para>
/// 思路与 Sdcb.SimdCodecs 2.0 的 <c>ByteSource.FromFile</c> 一致：先把长度量出来，
/// 空文件 / 超过单块缓冲上限 / 超过 <see cref="LoadOptions.MaxEncodedBytes"/> 都在**建立映射之前**拒绝
/// （一个字节都不读）；通过之后再把整份文件映射进来，文件内容交给内核分页，
/// 只有解码器真正碰到的页才会被读进内存。
/// </para>
/// <para>
/// 生命周期只有一次解码那么长：<see cref="ImageEngine.Load(string, LoadOptions?)"/> 用它算完就释放，
/// 所以后端**不能**把这块内存（或它的切片）留在帧 / 元数据里——这是
/// <see cref="Backends.IImageBackend.Decode"/> 本来就有的约定（入参只是"本次调用内有效"的视图）。
/// </para>
/// </remarks>
internal sealed unsafe class MappedFileBuffer : MemoryManager<byte>
{
    /// <summary>单个 <see cref="ReadOnlySpan{T}"/> 能表示的长度上限，也是这块视图的上限。</summary>
    private const long MaxMappedBytes = int.MaxValue;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _length;
    private byte* _pointer;
    private bool _disposed;

    private MappedFileBuffer(MemoryMappedFile map, MemoryMappedViewAccessor view, int length)
    {
        _map = map;
        _view = view;
        _length = length;

        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

        // PointerOffset：视图在映射页边界上的偏移（页对齐时是 0）
        _pointer = pointer + view.PointerOffset;
    }

    /// <summary>映射整份文件；长度不符合要求时**在读取任何内容之前**就抛异常。</summary>
    /// <exception cref="ArgumentException">文件长度为 0。</exception>
    /// <exception cref="ImageSizeLimitExceededException">文件长度超过 <paramref name="maxEncodedBytes"/> 或单块缓冲上限。</exception>
    public static MappedFileBuffer Open(string path, long maxEncodedBytes)
    {
        // FileShare.Read：别人还能同时读，但不允许写——写入会让这块只读视图的内容变得不确定
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // 长度和映射来自同一个句柄，量完长度到建立映射之间文件不会被换掉
            long length = stream.Length;
            CheckLength(path, length, maxEncodedBytes);

            MemoryMappedFile map = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: length,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);

            try
            {
                MemoryMappedViewAccessor view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
                try
                {
                    return new MappedFileBuffer(map, view, (int)length);
                }
                catch
                {
                    view.Dispose();
                    throw;
                }
            }
            catch
            {
                map.Dispose();
                throw;
            }
        }
        catch
        {
            // CreateFromFile 成功之后句柄已交给 map（leaveOpen: false），这里再 Dispose 是幂等的；
            // 失败在它之前时，文件句柄就靠这句释放。
            stream.Dispose();
            throw;
        }
    }

    /// <summary>本次解码可以看的字节（只在下一次 <c>Dispose</c> 之前有效）。</summary>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new ReadOnlySpan<byte>(_pointer, _length);
        }
    }

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<byte>(_pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);

        // 映射本身就在进程地址空间里固定住了，不需要 GCHandle 也不用真正 pin；
        // pinnable 传 this 只是让 MemoryHandle 释放时回到 Unpin，语义与池化内存一致。
        return new MemoryHandle(_pointer + elementIndex, default, this);
    }

    public override void Unpin()
    {
        // 映射内存不会移动，无需处理
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 顺序不能反：先还指针，再解除视图与映射，否则调试版会在释放后访问映射内出现访问冲突
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _pointer = null;
        _view.Dispose();
        _map.Dispose();
    }

    private static void CheckLength(string path, long length, long maxEncodedBytes)
    {
        if (length == 0)
        {
            throw new ArgumentException($"图像文件为空：'{path}'。", nameof(path));
        }

        // Span 的长度是 int：超过单块缓冲上限的文件根本没法交给解码器，
        // 所以和 ByteSource.FromFile 一样在映射前拒绝，而不是映射到一半才发现。
        long limit = maxEncodedBytes > 0 ? Math.Min(maxEncodedBytes, MaxMappedBytes) : MaxMappedBytes;
        if (length > limit)
        {
            throw new ImageSizeLimitExceededException(limit, length);
        }
    }
}
