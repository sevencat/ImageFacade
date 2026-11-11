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
/// 解码出的图像超过 <see cref="LoadOptions.MaxPixels"/>，或从流读入的编码数据超过
/// <see cref="LoadOptions.MaxEncodedBytes"/>。
/// </summary>
/// <remarks>
/// 单独一个类型是为了让调用方能把"图太大"和"图损坏"分开处理（例如 HTTP 413 对 400）。
/// 像素维度：<see cref="Width"/> / <see cref="Height"/> 是**声明值**（能读到头部时来自头部，
/// 否则来自已解出的帧）。字节维度：<see cref="Width"/> / <see cref="Height"/> /
/// <see cref="MaxPixels"/> 均为 0，改用 <see cref="MaxEncodedBytes"/> / <see cref="EncodedBytes"/>。
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

    /// <summary>
    /// 编码字节数超限（还没解码）。此时 <see cref="Width"/> / <see cref="Height"/> /
    /// <see cref="MaxPixels"/> 都是 0——尺寸未知，也不该由它们表达。
    /// </summary>
    public ImageSizeLimitExceededException(long maxEncodedBytes, long actualEncodedBytes)
        : base($"编码数据已读入 {actualEncodedBytes} 字节，超过上限 {maxEncodedBytes} 字节。"
            + "如是可信输入，请调大 LoadOptions.MaxEncodedBytes（0 表示不限制）。")
    {
        MaxEncodedBytes = maxEncodedBytes;
        EncodedBytes = actualEncodedBytes;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>当时生效的上限（像素数）；字节维度超限时为 0。</summary>
    public long MaxPixels { get; }

    /// <summary>当时生效的编码字节上限；像素维度超限时为 0。</summary>
    public long MaxEncodedBytes { get; }

    /// <summary>触发异常时已经读入的字节数；像素维度超限时为 0。</summary>
    public long EncodedBytes { get; }
}

/// <summary>
/// 保存到文件失败。<see cref="FacadeImage.Save(string, SaveOptions?)"/> 不做"临时文件 + 改名"，
/// 而是直接写目标路径；中途失败会把已写了一半的文件删掉，避免留下半截图片。
/// </summary>
/// <remarks>
/// <para>
/// <b>注意</b>：目标路径原有的文件在保存开始时就被截断，失败后**不会恢复**——这是"直接保存"的代价。
/// 需要保留原件就由调用方自己先备份，或改用 <see cref="FacadeImage.Encode"/> 拿字节后自行落盘。
/// </para>
/// <para>
/// <see cref="FileDeleted"/> 为 <c>true</c> 表示半截文件已被删除；为 <c>false</c> 表示删除也失败了，
/// 目标路径可能残留一个不完整的文件。
/// </para>
/// </remarks>
public sealed class ImageSaveFailedException : Exception
{
    public ImageSaveFailedException(string path, bool fileDeleted, Exception innerException)
        : base(BuildMessage(path, fileDeleted, innerException), innerException)
    {
        Path = path;
        FileDeleted = fileDeleted;
    }

    /// <summary>写入失败的目标路径（调用方给的原样值）。</summary>
    public string Path { get; }

    /// <summary>半截文件是否已删除。<c>false</c> 表示删除失败、可能残留。</summary>
    public bool FileDeleted { get; }

    /// <summary>删除目标文件并构造异常；删除结果如实反映在 <see cref="FileDeleted"/> 上。</summary>
    internal static ImageSaveFailedException For(string path, Exception inner)
        => new(path, TryDeleteFile(path), inner);

    private static string BuildMessage(string path, bool deleted, Exception inner)
        => $"保存到 '{path}' 失败：{inner.Message} "
            + (deleted
                ? "已删除写了一半的目标文件。"
                : "目标文件删除失败，可能残留不完整的内容。");

    /// <summary>删掉半截文件；删不掉返回 false（不让清理失败盖住真正的异常）。</summary>
    internal static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return !File.Exists(path);
    }
}
