using ImageFacade.Backends;
using ImageFacade.SdcbSimdCodecs.Rendering;

namespace ImageFacade.SdcbSimdCodecs;

/// <summary>
/// 软后端的帧：包一个复制进来的 ImgProcSharp <see cref="Canvas"/>（**预乘** Rgba32）。
/// 规范像素（直通）与预乘之间的转换只发生在边界（<see cref="SoftwareBackend"/>）。
/// </summary>
internal sealed class SoftwareFrame : IFrame
{
    internal SoftwareFrame(Canvas canvas) => Canvas = canvas;

    internal Canvas Canvas { get; }

    public int Width => Canvas.Width;

    public int Height => Canvas.Height;

    public void Dispose() => Canvas.Dispose();
}
