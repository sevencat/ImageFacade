using System.Runtime.InteropServices;
using ImageFacade.Backends;

namespace ImageFacade.Internal;

/// <summary>
/// "这张图里到底有没有半透明像素"的通用探测：先问后端（零拷贝、后端自己那块内存），
/// 问不到再借后端的像素视图扫一遍。结果由 <see cref="FacadeImage"/> 缓存进
/// <see cref="ImageMetadata.HasTransparency"/>。
/// </summary>
internal static class AlphaProbe
{
    /// <summary>探测一次；返回 <c>null</c> 表示后端既答不上来、也导不出像素，谁也不知道。</summary>
    public static bool? TryDetect(IImageBackend backend, IFrame frame)
    {
        // 后端能直接回答（软后端的 Canvas 扫一遍、Skia 直接读位图 alpha 字节……）：门面什么都不用做
        if (backend.TryGetTransparency(frame, out bool hasTransparency))
        {
            return hasTransparency;
        }

        // 答不上来就借它的像素视图扫一遍。注意这里会走各后端的 TryAcquirePixels：
        // 零拷贝的实现（ImageSharp / OpenCV）直接扫后端内存；拷一份的实现（Skia / 软后端）
        // 会多两次全图搬运——所以那两个后端都实现了 TryGetTransparency，正常走不到这条路。
        if (!backend.TryAcquirePixels(frame, out PixelLease lease))
        {
            return null;
        }

        try
        {
            return Scan(lease.Pixels, lease.Stride, frame.Width, frame.Height);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>扫一遍规范 RGBA32 像素（顶行优先，行间 <paramref name="stride"/> 字节）。</summary>
    public static bool Scan(ReadOnlySpan<byte> pixels, int stride, int width, int height)
    {
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * stride, rowBytes);

            // 一次比较 4 个字节：只要有一个像素的 alpha 不是 255 就算"有透明"
            foreach (uint word in MemoryMarshal.Cast<byte, uint>(row))
            {
                if ((word & 0xFF000000u) != 0xFF000000u)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
