namespace ImageFacade;

public sealed class SaveOptions
{
    /// <summary>null = 按文件扩展名推断；<see cref="FacadeImage.Encode"/> 时 null 表示 PNG。</summary>
    public ImageFormat? Format { get; init; }

    /// <summary>有损格式质量 1..100。</summary>
    public int Quality { get; init; } = 90;

    /// <summary>PNG zlib 级别 0..9，-1 = 后端默认。</summary>
    public int CompressionLevel { get; init; } = -1;

    public bool PreserveMetadata { get; init; }

    /// <summary>无 alpha 格式（JPEG/BMP）的合成底色。</summary>
    public Rgba32 BackgroundColor { get; init; } = Rgba32.White;
}
