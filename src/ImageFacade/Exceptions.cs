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
