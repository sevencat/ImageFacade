namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>目标框与源图比例不一致时的处理方式，语义与 ImageSharp 的 <c>ResizeMode</c> 一致。</summary>
internal enum ResizeMode
{
    /// <summary>等比缩放覆盖目标框，再居中裁剪掉多余部分（输出尺寸 = 目标尺寸）。</summary>
    Crop,

    /// <summary>等比缩放装进目标框（允许放大），留白用 <see cref="ResizeOptions.PadColor"/> 填充。</summary>
    Pad,

    /// <summary>只缩小不放大：装进目标框后留白，尺寸更小时不会被拉大。</summary>
    BoxPad,

    /// <summary>等比缩放到完全装进目标框，输出尺寸可能小于目标框（不做留白）。</summary>
    Max,

    /// <summary>等比缩放到覆盖目标框，输出尺寸可能大于目标框（不做裁剪）。</summary>
    Min,

    /// <summary>不保持比例，直接拉伸到目标尺寸。</summary>
    Stretch,
}

/// <summary>
/// 重采样参数，字段命名与 ImageSharp 的 <c>ResizeOptions</c> 对齐，方便迁移。
/// </summary>
internal sealed class ResizeOptions
{
    /// <summary>目标框尺寸；<see cref="ResizeMode.Max"/>/<see cref="ResizeMode.Min"/> 下是上限而非最终尺寸。</summary>
    public Size Size { get; set; }

    /// <summary>比例不一致时的处理方式，默认 <see cref="ResizeMode.Max"/>（与 ImageSharp 一致）。</summary>
    public ResizeMode Mode { get; set; } = ResizeMode.Max;

    /// <summary>重采样核，默认 <see cref="KnownResamplers.Bicubic"/>。</summary>
    public IResampler Sampler { get; set; } = KnownResamplers.Bicubic;

    /// <summary>
    /// 是否在线性光空间做重采样（ImageSharp 的 Compand）。v1 不支持，设为 <c>true</c> 会抛
    /// <see cref="NotSupportedException"/>，而不是默默按 sRGB 处理。
    /// </summary>
    public bool Compand { get; set; }

    /// <summary><see cref="ResizeMode.Pad"/>/<see cref="ResizeMode.BoxPad"/> 的留白颜色，默认全透明。</summary>
    public Color PadColor { get; set; } = Color.Transparent;
}
