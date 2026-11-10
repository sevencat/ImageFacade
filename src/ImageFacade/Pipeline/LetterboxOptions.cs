namespace ImageFacade.Pipeline;

/// <summary>letterbox 参数：目标尺寸、补边颜色、是否居中。</summary>
public sealed class LetterboxOptions
{
    /// <summary>目标宽度（模型输入宽），必须大于 0。</summary>
    public int Width { get; init; }

    /// <summary>目标高度（模型输入高），必须大于 0。</summary>
    public int Height { get; init; }

    /// <summary>补边颜色；默认 YOLO 惯用的 <c>(114,114,114)</c>。</summary>
    public Rgba32 PadColor { get; init; } = Letterbox.DefaultPadColor;

    /// <summary>true（默认）把缩放结果居中；false 贴左上角。</summary>
    public bool Center { get; init; } = true;

    /// <summary>缩放采样核；默认 <see cref="Sampler.Bicubic"/>。</summary>
    public Sampler Sampler { get; init; } = Sampler.Bicubic;
}
