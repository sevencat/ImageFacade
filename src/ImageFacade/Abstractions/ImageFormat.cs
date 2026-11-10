namespace ImageFacade;

/// <summary>图像容器格式（按内容识别，不信扩展名）。</summary>
public enum ImageFormat
{
    Unknown = 0,
    Png,
    Jpeg,
    Bmp,
    Gif,
    WebP,
    Tiff,
    Ico,
    Qoi,
    Pnm,
}

public static class ImageFormats
{
    /// <summary>按扩展名推断保存格式（<see cref="SaveOptions.Format"/> 为 null 时使用）。</summary>
    public static ImageFormat FromExtension(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormat.Jpeg,
            ".bmp" or ".dib" => ImageFormat.Bmp,
            ".gif" => ImageFormat.Gif,
            ".webp" => ImageFormat.WebP,
            ".tif" or ".tiff" => ImageFormat.Tiff,
            ".ico" => ImageFormat.Ico,
            ".qoi" => ImageFormat.Qoi,
            ".pnm" or ".ppm" or ".pgm" or ".pbm" => ImageFormat.Pnm,
            _ => ImageFormat.Unknown,
        };
    }

    /// <summary>该格式是否支持 alpha 通道（不支持时门面先合成背景色）。</summary>
    public static bool SupportsAlpha(this ImageFormat format) => format switch
    {
        ImageFormat.Png or ImageFormat.Gif or ImageFormat.WebP or ImageFormat.Tiff or ImageFormat.Ico => true,
        _ => false,
    };
}
