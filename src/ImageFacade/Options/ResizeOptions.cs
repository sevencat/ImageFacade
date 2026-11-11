namespace ImageFacade;

/// <summary>六种 Resize 模式，语义与 ImageSharp 的 <c>ResizeMode</c> 一致（plan §3.3）。</summary>
public enum ResizeMode
{
    /// <summary>直接拉伸到目标尺寸，忽略原始比例（默认）。</summary>
    Stretch = 0,

    /// <summary>等比放大到覆盖目标框，再居中裁剪。</summary>
    Crop = 1,

    /// <summary>等比缩放到完全放进目标框，四周用 <see cref="ResizeOptions.PadColor"/> 补齐（输出严格等于目标尺寸）。</summary>
    Pad = 2,

    /// <summary>
    /// 同 <see cref="Pad"/>，但**只缩小不放大**：源图比目标框小时保持原尺寸居中补边，
    /// 补齐后仍是目标尺寸。
    /// </summary>
    BoxPad = 3,

    /// <summary>等比缩放到完全放进目标框，输出就是缩放后的尺寸（不含补边）。</summary>
    Max = 4,

    /// <summary>等比缩放到覆盖目标框，输出就是缩放后的尺寸（不裁剪）。</summary>
    Min = 5,
}

/// <summary>采样核。</summary>
public enum Sampler
{
    Nearest = 0,
    Box = 1,
    Bilinear = 2,
    Bicubic = 3,
    Lanczos2 = 4,
    Lanczos3 = 5,
    MitchellNetravali = 6,
    CatmullRom = 7,
}

public sealed class ResizeOptions
{
    public int Width { get; init; }

    public int Height { get; init; }

    public ResizeMode Mode { get; init; } = ResizeMode.Stretch;

    public Sampler Sampler { get; init; } = Sampler.Bicubic;

    /// <summary>Crop 之外的补边颜色。</summary>
    public Rgba32 PadColor { get; init; } = Rgba32.Transparent;
}
