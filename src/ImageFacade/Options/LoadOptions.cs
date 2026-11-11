namespace ImageFacade;

public sealed class LoadOptions
{
    /// <summary>按 EXIF 方向自动转正（用户拿到照片就是正的）。</summary>
    public bool AutoOrient { get; init; } = true;

    /// <summary>只透传 ICC / 方向，不保证 EXIF 往返。解码阶段仅作为提示，门面统一走 <see cref="AutoOrient"/>。</summary>
    public bool PreserveMetadata { get; init; } = true;

    /// <summary>v1 只接受 0（只处理第 0 帧）。</summary>
    public int FrameIndex { get; init; }

    /// <summary>
    /// 解码后允许的最大像素数（宽 × 高）。默认 1 亿像素（约 400 MB 的 RGBA 缓冲），
    /// <c>&lt;= 0</c> 表示不限制。
    /// </summary>
    /// <remarks>
    /// 用来阻断"小文件声明巨大尺寸"的解压炸弹：能读到头部时在**解码前**就拒绝，
    /// 读不到头部时在解码后再兜一次底。<see cref="ImageEngine.Create"/> 不受这个上限约束——
    /// 那里的尺寸来自你写的代码，不是外部输入。
    /// </remarks>
    public long MaxPixels { get; init; } = 100_000_000;
}
