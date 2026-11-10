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
    public EncodeRequest(ImageFormat format, int quality = 90, int compressionLevel = -1, bool preserveMetadata = false)
    {
        Format = format;
        Quality = quality;
        CompressionLevel = compressionLevel;
        PreserveMetadata = preserveMetadata;
    }

    public ImageFormat Format { get; }

    /// <summary>有损格式质量 1..100。</summary>
    public int Quality { get; }

    /// <summary>PNG zlib 级别 0..9，-1 = 后端默认。</summary>
    public int CompressionLevel { get; }

    public bool PreserveMetadata { get; }
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
