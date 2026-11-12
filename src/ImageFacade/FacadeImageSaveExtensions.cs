namespace ImageFacade;

/// <summary>
/// 按格式的保存 / 编码扩展：<c>img.SaveJpg("out.jpg")</c>、<c>img.SavePng(stream)</c>，
/// 不用再手写 <c>new SaveOptions { Format = ... }</c>。
/// </summary>
/// <remarks>
/// 全部是 <see cref="FacadeImage.Save(string, SaveOptions?)"/> /
/// <see cref="FacadeImage.SaveAsync(string, SaveOptions?, CancellationToken)"/> /
/// <see cref="FacadeImage.Encode(SaveOptions?)"/> 的薄封装：错误语义（半截文件删除、
/// <see cref="ImageSaveFailedException"/>、<see cref="FeatureNotSupportedException"/>）与底层完全一致。
/// 显式传了 quality / compressionLevel 参数时它优先于 options 里的同名设置。
/// </remarks>
public static class FacadeImageSaveExtensions
{
    // ---- JPEG（有损，quality 1..100） ----

    public static void SaveJpg(this FacadeImage image, string path, int? quality = null, SaveOptions? options = null)
        => image.Save(path, JpegOptions(quality, options));

    public static void SaveJpg(this FacadeImage image, Stream stream, int? quality = null, SaveOptions? options = null)
        => image.Save(stream, JpegOptions(quality, options));

    public static byte[] EncodeJpeg(this FacadeImage image, int? quality = null, SaveOptions? options = null)
        => image.Encode(JpegOptions(quality, options));

    public static Task SaveJpgAsync(this FacadeImage image, string path, int? quality = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, JpegOptions(quality, options), cancellationToken);

    public static Task SaveJpgAsync(this FacadeImage image, Stream stream, int? quality = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, JpegOptions(quality, options), cancellationToken);

    public static Task<byte[]> EncodeJpegAsync(this FacadeImage image, int? quality = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(JpegOptions(quality, options), cancellationToken);

    // ---- PNG（无损，compressionLevel 0..9，-1 = 默认） ----

    public static void SavePng(this FacadeImage image, string path, int? compressionLevel = null, SaveOptions? options = null)
        => image.Save(path, PngOptions(compressionLevel, options));

    public static void SavePng(this FacadeImage image, Stream stream, int? compressionLevel = null, SaveOptions? options = null)
        => image.Save(stream, PngOptions(compressionLevel, options));

    public static byte[] EncodePng(this FacadeImage image, int? compressionLevel = null, SaveOptions? options = null)
        => image.Encode(PngOptions(compressionLevel, options));

    public static Task SavePngAsync(this FacadeImage image, string path, int? compressionLevel = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, PngOptions(compressionLevel, options), cancellationToken);

    public static Task SavePngAsync(this FacadeImage image, Stream stream, int? compressionLevel = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, PngOptions(compressionLevel, options), cancellationToken);

    public static Task<byte[]> EncodePngAsync(this FacadeImage image, int? compressionLevel = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(PngOptions(compressionLevel, options), cancellationToken);

    // ---- WebP（有损，quality 1..100） ----

    public static void SaveWebP(this FacadeImage image, string path, int? quality = null, SaveOptions? options = null)
        => image.Save(path, WebPOptions(quality, options));

    public static void SaveWebP(this FacadeImage image, Stream stream, int? quality = null, SaveOptions? options = null)
        => image.Save(stream, WebPOptions(quality, options));

    public static byte[] EncodeWebP(this FacadeImage image, int? quality = null, SaveOptions? options = null)
        => image.Encode(WebPOptions(quality, options));

    public static Task SaveWebPAsync(this FacadeImage image, string path, int? quality = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, WebPOptions(quality, options), cancellationToken);

    public static Task SaveWebPAsync(this FacadeImage image, Stream stream, int? quality = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, WebPOptions(quality, options), cancellationToken);

    public static Task<byte[]> EncodeWebPAsync(this FacadeImage image, int? quality = null, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(WebPOptions(quality, options), cancellationToken);

    // ---- BMP / GIF / TIFF / ICO / QOI / PNM（没有质量旋钮） ----

    public static void SaveBmp(this FacadeImage image, string path, SaveOptions? options = null)
        => image.Save(path, WithFormat(options, ImageFormat.Bmp));

    public static void SaveBmp(this FacadeImage image, Stream stream, SaveOptions? options = null)
        => image.Save(stream, WithFormat(options, ImageFormat.Bmp));

    public static byte[] EncodeBmp(this FacadeImage image, SaveOptions? options = null)
        => image.Encode(WithFormat(options, ImageFormat.Bmp));

    public static Task SaveBmpAsync(this FacadeImage image, string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, WithFormat(options, ImageFormat.Bmp), cancellationToken);

    public static Task SaveBmpAsync(this FacadeImage image, Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, WithFormat(options, ImageFormat.Bmp), cancellationToken);

    public static Task<byte[]> EncodeBmpAsync(this FacadeImage image, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(WithFormat(options, ImageFormat.Bmp), cancellationToken);

    public static void SaveGif(this FacadeImage image, string path, SaveOptions? options = null)
        => image.Save(path, WithFormat(options, ImageFormat.Gif));

    public static void SaveGif(this FacadeImage image, Stream stream, SaveOptions? options = null)
        => image.Save(stream, WithFormat(options, ImageFormat.Gif));

    public static byte[] EncodeGif(this FacadeImage image, SaveOptions? options = null)
        => image.Encode(WithFormat(options, ImageFormat.Gif));

    public static Task SaveGifAsync(this FacadeImage image, string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, WithFormat(options, ImageFormat.Gif), cancellationToken);

    public static Task SaveGifAsync(this FacadeImage image, Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, WithFormat(options, ImageFormat.Gif), cancellationToken);

    public static Task<byte[]> EncodeGifAsync(this FacadeImage image, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(WithFormat(options, ImageFormat.Gif), cancellationToken);

    public static void SaveTiff(this FacadeImage image, string path, SaveOptions? options = null)
        => image.Save(path, WithFormat(options, ImageFormat.Tiff));

    public static void SaveTiff(this FacadeImage image, Stream stream, SaveOptions? options = null)
        => image.Save(stream, WithFormat(options, ImageFormat.Tiff));

    public static byte[] EncodeTiff(this FacadeImage image, SaveOptions? options = null)
        => image.Encode(WithFormat(options, ImageFormat.Tiff));

    public static Task SaveTiffAsync(this FacadeImage image, string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, WithFormat(options, ImageFormat.Tiff), cancellationToken);

    public static Task SaveTiffAsync(this FacadeImage image, Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, WithFormat(options, ImageFormat.Tiff), cancellationToken);

    public static Task<byte[]> EncodeTiffAsync(this FacadeImage image, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(WithFormat(options, ImageFormat.Tiff), cancellationToken);

    public static void SaveIco(this FacadeImage image, string path, SaveOptions? options = null)
        => image.Save(path, WithFormat(options, ImageFormat.Ico));

    public static void SaveIco(this FacadeImage image, Stream stream, SaveOptions? options = null)
        => image.Save(stream, WithFormat(options, ImageFormat.Ico));

    public static byte[] EncodeIco(this FacadeImage image, SaveOptions? options = null)
        => image.Encode(WithFormat(options, ImageFormat.Ico));

    public static Task SaveIcoAsync(this FacadeImage image, string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, WithFormat(options, ImageFormat.Ico), cancellationToken);

    public static Task SaveIcoAsync(this FacadeImage image, Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, WithFormat(options, ImageFormat.Ico), cancellationToken);

    public static Task<byte[]> EncodeIcoAsync(this FacadeImage image, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(WithFormat(options, ImageFormat.Ico), cancellationToken);

    public static void SaveQoi(this FacadeImage image, string path, SaveOptions? options = null)
        => image.Save(path, WithFormat(options, ImageFormat.Qoi));

    public static void SaveQoi(this FacadeImage image, Stream stream, SaveOptions? options = null)
        => image.Save(stream, WithFormat(options, ImageFormat.Qoi));

    public static byte[] EncodeQoi(this FacadeImage image, SaveOptions? options = null)
        => image.Encode(WithFormat(options, ImageFormat.Qoi));

    public static Task SaveQoiAsync(this FacadeImage image, string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, WithFormat(options, ImageFormat.Qoi), cancellationToken);

    public static Task SaveQoiAsync(this FacadeImage image, Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, WithFormat(options, ImageFormat.Qoi), cancellationToken);

    public static Task<byte[]> EncodeQoiAsync(this FacadeImage image, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(WithFormat(options, ImageFormat.Qoi), cancellationToken);

    public static void SavePnm(this FacadeImage image, string path, SaveOptions? options = null)
        => image.Save(path, WithFormat(options, ImageFormat.Pnm));

    public static void SavePnm(this FacadeImage image, Stream stream, SaveOptions? options = null)
        => image.Save(stream, WithFormat(options, ImageFormat.Pnm));

    public static byte[] EncodePnm(this FacadeImage image, SaveOptions? options = null)
        => image.Encode(WithFormat(options, ImageFormat.Pnm));

    public static Task SavePnmAsync(this FacadeImage image, string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(path, WithFormat(options, ImageFormat.Pnm), cancellationToken);

    public static Task SavePnmAsync(this FacadeImage image, Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.SaveAsync(stream, WithFormat(options, ImageFormat.Pnm), cancellationToken);

    public static Task<byte[]> EncodePnmAsync(this FacadeImage image, SaveOptions? options = null, CancellationToken cancellationToken = default)
        => image.EncodeAsync(WithFormat(options, ImageFormat.Pnm), cancellationToken);

    // ---- 组装 ----

    private static SaveOptions JpegOptions(int? quality, SaveOptions? options)
        => WithFormat(options, ImageFormat.Jpeg, quality);

    private static SaveOptions PngOptions(int? compressionLevel, SaveOptions? options)
        => WithFormat(options, ImageFormat.Png, compressionLevel: compressionLevel);

    private static SaveOptions WebPOptions(int? quality, SaveOptions? options)
        => WithFormat(options, ImageFormat.WebP, quality);

    /// <summary>复制 options 并固定格式；显式传入的 quality / compressionLevel 优先。</summary>
    private static SaveOptions WithFormat(SaveOptions? options, ImageFormat format, int? quality = null, int? compressionLevel = null)
        => new()
        {
            Format = format,
            Quality = quality ?? options?.Quality ?? 90,
            CompressionLevel = compressionLevel ?? options?.CompressionLevel ?? -1,
            PreserveMetadata = options?.PreserveMetadata ?? false,
            BackgroundColor = options?.BackgroundColor ?? Rgba32.White,
        };
}
