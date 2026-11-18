using System.Buffers;

namespace ImageFacade;

/// <summary>
/// 像素缓冲池：把 <see cref="ArrayPool{T}"/> 的最大尺寸从默认的 1 MiB 放宽到 4 MiB，
/// 这样 960×720（约 2.7 MB）这种整帧缓冲能被真正复用，而不是每帧新分配一次进 LOH。
/// </summary>
/// <remarks>
/// 超过 <see cref="MaxArrayLength"/> 的请求（1080p 一帧约 8 MB）仍然每次新分配：
/// 驻留多份 MB 级数组的代价比重新分配更大。
/// </remarks>
internal static class PixelBufferPool
{
    /// <summary>池内最大数组长度：4 MiB。</summary>
    public const int MaxArrayLength = 4 * 1024 * 1024;

    /// <summary>每个尺寸档最多缓存 4 份，4 MiB 档最坏驻留 16 MiB。</summary>
    private const int MaxArraysPerBucket = 4;

    public static ArrayPool<byte> Shared { get; } = ArrayPool<byte>.Create(MaxArrayLength, MaxArraysPerBucket);
}
