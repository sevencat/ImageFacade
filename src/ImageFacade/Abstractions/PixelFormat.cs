namespace ImageFacade;

/// <summary>
/// 像素内存布局。门面默认值永远是 <see cref="Rgba32"/>（直通、非预乘）；BGR/BGRA 必须显式指定（misc §D）。
/// </summary>
public enum PixelFormat
{
    /// <summary>直通 RGBA，每像素 4 字节。门面规范格式。</summary>
    Rgba32 = 0,

    /// <summary>直通 BGRA（Avalonia <c>Bgra8888</c>），每像素 4 字节。</summary>
    Bgra32 = 1,

    /// <summary>RGB，每像素 3 字节，alpha 视为 255。</summary>
    Rgb24 = 2,

    /// <summary>BGR（ffmpeg <c>BGR24</c> / OpenCV <c>Mat</c>），每像素 3 字节，alpha 视为 255。</summary>
    Bgr24 = 3,

    /// <summary>灰度，每像素 1 字节；转 RGBA 时 <c>R=G=B=gray, A=255</c>。</summary>
    Gray8 = 4,

    /// <summary>预乘 BGRA，仅用于输出（Avalonia <c>AlphaFormat.Premul</c>）。</summary>
    Bgra32Premultiplied = 5,
}

public static class PixelFormatExtensions
{
    /// <summary>每像素字节数。</summary>
    public static int GetBytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.Rgba32 => 4,
        PixelFormat.Bgra32 => 4,
        PixelFormat.Bgra32Premultiplied => 4,
        PixelFormat.Rgb24 => 3,
        PixelFormat.Bgr24 => 3,
        PixelFormat.Gray8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知像素格式。"),
    };
}
