namespace ImageFacade.Pipeline;

/// <summary>张量内存布局。</summary>
public enum TensorLayout
{
    /// <summary>通道优先：<c>[C, H, W]</c>，PyTorch / ONNX 常见。</summary>
    Nchw = 0,

    /// <summary>像素优先：<c>[H, W, C]</c>，TensorFlow / TFLite 常见。</summary>
    Nhwc = 1,
}

/// <summary>三通道顺序。</summary>
public enum ChannelOrder
{
    /// <summary>R、G、B（默认）。</summary>
    Rgb = 0,

    /// <summary>B、G、R（OpenCV 上游常见）。</summary>
    Bgr = 1,
}

/// <summary>张量化参数：目标尺寸、布局、通道顺序、归一化。</summary>
/// <remarks>
/// 只支持 3 通道；letterbox 由 <see cref="Tensorizer"/> 内部完成，源图不被修改。
/// </remarks>
public sealed class TensorOptions
{
    /// <summary>模型输入宽度，必须大于 0。</summary>
    public int Width { get; init; }

    /// <summary>模型输入高度，必须大于 0。</summary>
    public int Height { get; init; }

    /// <summary>内存布局；默认 <see cref="TensorLayout.Nchw"/>。</summary>
    public TensorLayout Layout { get; init; } = TensorLayout.Nchw;

    /// <summary>通道顺序；默认 <see cref="ChannelOrder.Rgb"/>。</summary>
    public ChannelOrder Channels { get; init; } = ChannelOrder.Rgb;

    /// <summary>true（默认）先把 0..255 归一到 0..1。</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>逐通道均值，长度必须为 3；为空表示不减。</summary>
    public ReadOnlyMemory<float> Mean { get; init; }

    /// <summary>逐通道标准差，长度必须为 3，元素必须大于 0；为空表示不除。</summary>
    public ReadOnlyMemory<float> Std { get; init; }

    /// <summary>letterbox 补边颜色；默认 <c>(114,114,114)</c>。</summary>
    public Rgba32 PadColor { get; init; } = Letterbox.DefaultPadColor;

    /// <summary>true（默认）补边居中。</summary>
    public bool CenterPad { get; init; } = true;
}
