namespace ImageFacade;

/// <summary>第三个后端抛出的异常统一包成它（保留 <see cref="Exception.InnerException"/>）。</summary>
public class ImageBackendException : Exception
{
    public ImageBackendException(string backendName, string message, Exception? innerException = null)
        : base($"[{backendName}] {message}", innerException)
    {
        BackendName = backendName;
    }

    public string BackendName { get; }
}

/// <summary>
/// 后端不具备该能力。库不做跨后端回退，需要另一种渲染时请显式换用支持该能力的后端
/// （例如 OpenCV 不支持半透明形状，可改用 Skia / ImageSharp）。
/// </summary>
public sealed class FeatureNotSupportedException : NotSupportedException
{
    public FeatureNotSupportedException(string feature, string backendName, Exception? innerException = null)
        : base($"后端 '{backendName}' 不支持能力 '{feature}'。请换用支持该能力的后端，或在调用方自行合成。", innerException)
    {
        Feature = feature;
        BackendName = backendName;
    }

    /// <summary>能力名，如 <c>DrawLine</c> / <c>DrawText</c> / <c>Encode</c>。</summary>
    public string Feature { get; }

    public string BackendName { get; }
}

/// <summary>
/// 解码出的图像超过 <see cref="LoadOptions.MaxPixels"/>：小文件声明巨大尺寸的"解压炸弹"就走这条。
/// </summary>
/// <remarks>
/// 单独一个类型是为了让调用方能把"图太大"和"图损坏"分开处理（例如 HTTP 413 对 400）。
/// <see cref="Width"/> / <see cref="Height"/> 是**声明值**：能读到头部时来自头部，否则来自已解出的帧。
/// </remarks>
public sealed class ImageSizeLimitExceededException : Exception
{
    public ImageSizeLimitExceededException(int width, int height, long maxPixels)
        : base($"图像尺寸 {width}x{height}（{(long)width * height} 像素）超过上限 {maxPixels} 像素。"
            + "如是可信输入，请调大 LoadOptions.MaxPixels（0 表示不限制）。")
    {
        Width = width;
        Height = height;
        MaxPixels = maxPixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>当时生效的上限（像素数）。</summary>
    public long MaxPixels { get; }
}
