namespace ImageFacade.Software.Rendering;

internal sealed partial class Canvas
{
    /// <summary>
    /// 缩放到指定尺寸（不保持比例，等价于 <see cref="ResizeMode.Stretch"/>）。返回新画布，原画布不变。
    /// </summary>
    public Canvas Resize(int width, int height) => Resize(width, height, KnownResamplers.Bicubic);

    /// <summary>缩放到指定尺寸（不保持比例），指定重采样核。</summary>
    public Canvas Resize(int width, int height, IResampler sampler)
        => Resize(new ResizeOptions { Size = new Size(width, height), Mode = ResizeMode.Stretch, Sampler = sampler });

    /// <summary>缩放到指定尺寸（不保持比例）。</summary>
    public Canvas Resize(Size size) => Resize(size.Width, size.Height);

    /// <summary>缩放到指定尺寸（不保持比例），指定重采样核。</summary>
    public Canvas Resize(Size size, IResampler sampler) => Resize(size.Width, size.Height, sampler);

    /// <summary>
    /// 按 <see cref="ResizeOptions"/> 缩放：可指定保持比例的方式（Max/Min/Crop/Pad/BoxPad/Stretch）、
    /// 采样核与留白颜色。返回新画布，原画布不变。语义对齐 ImageSharp 的 <c>ResizeProcessor</c>。
    /// </summary>
    public Canvas Resize(ResizeOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Resizer.Resize(this, options);
    }
}
