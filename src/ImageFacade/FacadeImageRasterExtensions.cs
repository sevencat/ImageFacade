using ImageFacade.Backends;
using ImageFacade.Rendering;

namespace ImageFacade;

/// <summary>
/// 核心光栅后端的托底出口：直接拿到绘制用的那块像素缓冲，省掉一次整幅
/// <c>CopyPixelsTo</c> 的拷贝（例如把画面直接推给播放器的纹理上传路径）。
/// </summary>
/// <remarks>
/// <para>
/// 只有<b>核心光栅</b>（<see cref="FacadeImage.BackendName"/> 为 <c>raster</c>）返回 <c>true</c>。
/// Skia / OpenCV / ImageSharp 后端持有各自的原生位图，没有这块托管缓冲，返回 <c>false</c>——
/// 它们各有自己的零拷贝入口（例如 OpenCV 的 Mat）。
/// </para>
/// <para>
/// <b>内容是预乘 Rgba32</b>：半透明像素的 RGB 已经乘过 alpha，当直通 RGBA 用会偏暗。
/// 行距由 <c>stride</c> 给出（零拷贝包装解码缓冲时可能大于 <c>Width * 4</c>），
/// 有效数据只有前 <see cref="FacadeImage.Height"/> 行；<c>buffer.Length</c> 通常大于
/// <c>stride * Height</c>（缓冲来自池），尾部字节不属于图像，不要当有效像素读。
/// </para>
/// <para>
/// <b>不要缓存返回的数组引用</b>：<see cref="FacadeImage.Resize(ResizeOptions)"/> 等几何操作会整帧换掉，
/// <see cref="FacadeImage.Dispose"/> 会把缓冲归还池里复用。每次要用时重新取。
/// </para>
/// <para>
/// 直接写这块缓冲<b>不会</b>更新 <see cref="FacadeImage.Metadata"/> 里的结论：写过之后
/// <see cref="ImageMetadata.HasTransparency"/> 可能已经过期，需要时用
/// <see cref="FacadeImage.DetectTransparency(bool)"/> 重算。
/// 零拷贝图（<see cref="FacadeImage.IsZeroCopy"/>）返回的就是调用方自己的数组，写入即生效。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// if (image.TryGetRasterBuffer(out byte[] pixels, out int stride))
/// {
///     // 第 y 行第 x 个像素 = pixels[(y * stride) + (x * 4) .. + 4]（预乘）
/// }
/// </code>
/// </example>
public static class FacadeImageRasterExtensions
{
    /// <summary>
    /// 取核心光栅后端的底层缓冲（预乘 Rgba32），不复制。
    /// </summary>
    /// <param name="image">目标图。</param>
    /// <param name="buffer">底层缓冲，长度 ≥ <c>stride * Height</c>。</param>
    /// <param name="stride">行距（字节）。</param>
    /// <returns>
    /// <c>true</c> 表示当前后端是核心光栅；<c>false</c> 表示别的后端，
    /// 此时 <paramref name="buffer"/> 为空数组、<paramref name="stride"/> 为 0。
    /// </returns>
    /// <exception cref="ObjectDisposedException">图已释放，或所属引擎已释放。</exception>
    public static bool TryGetRasterBuffer(this FacadeImage image, out byte[] buffer, out int stride)
    {
        ArgumentNullException.ThrowIfNull(image);

        IFrame frame = image.Frame;   // 图已释放 → ObjectDisposedException

        // 引擎释放后画布可能已经被回收：这时再交出去的是悬挂缓冲（与 FacadeImage.ThrowIfDisposed 同口径）
        if (image.Engine.IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(ImageEngine),
                "所属的 ImageEngine 已释放，这张图不能再使用。请先完成图片操作，再释放引擎。");
        }

        if (frame is RasterFrame raster)
        {
            buffer = raster.Canvas.Buffer;
            stride = raster.Canvas.Stride;
            return true;
        }

        buffer = [];
        stride = 0;
        return false;
    }
}
