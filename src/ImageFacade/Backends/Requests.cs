namespace ImageFacade.Backends;

public readonly struct DecodeRequest
{
    public DecodeRequest(bool autoOrient = true, int frameIndex = 0)
    {
        AutoOrient = autoOrient;
        FrameIndex = frameIndex;
    }

    public bool AutoOrient { get; }

    /// <summary>v1 只接受 0。</summary>
    public int FrameIndex { get; }
}

public readonly struct EncodeRequest
{
    public EncodeRequest(
        ImageFormat format,
        int quality = 90,
        int compressionLevel = -1,
        bool preserveMetadata = false,
        bool? assumeOpaque = null)
    {
        Format = format;
        Quality = quality;
        CompressionLevel = compressionLevel;
        PreserveMetadata = preserveMetadata;
        AssumeOpaque = assumeOpaque;
    }

    public ImageFormat Format { get; }

    /// <summary>有损格式质量 1..100。</summary>
    public int Quality { get; }

    /// <summary>PNG zlib 级别 0..9，-1 = 后端默认。</summary>
    public int CompressionLevel { get; }

    public bool PreserveMetadata { get; }

    /// <summary>
    /// 提示：这张图**保证**全是 alpha = 255 的像素（<c>null</c> = 不知道，按常规处理）。
    /// </summary>
    /// <remarks>
    /// 来自 <see cref="ImageMetadata.HasTransparency"/> 为 <c>false</c> 的时候。后端可以据此省掉
    /// "对齐像素时顺带判断有没有透明"的探测（软后端就是 <c>Canvas.CopyToStraight</c> 的那次全图扫描），
    /// 也可以完全忽略它——忽略只会慢一点，不会算错。传 <c>true</c> 时后端**不必**再校验。
    /// </remarks>
    public bool? AssumeOpaque { get; }
}

/// <summary>只表示"拉伸到指定尺寸"，六种 Resize 模式由门面层规划（plan §3.3）。</summary>
public readonly struct ResizeRequest
{
    public ResizeRequest(int width, int height, Sampler sampler = Sampler.Bicubic)
    {
        Width = width;
        Height = height;
        Sampler = sampler;
    }

    public int Width { get; }

    public int Height { get; }

    public Sampler Sampler { get; }
}
