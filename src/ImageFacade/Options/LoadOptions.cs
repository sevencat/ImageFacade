namespace ImageFacade;

public sealed class LoadOptions
{
    /// <summary>按 EXIF 方向自动转正（用户拿到照片就是正的）。</summary>
    public bool AutoOrient { get; init; } = true;

    /// <summary>只透传 ICC / 方向，不保证 EXIF 往返。解码阶段仅作为提示，门面统一走 <see cref="AutoOrient"/>。</summary>
    public bool PreserveMetadata { get; init; } = true;

    /// <summary>v1 只接受 0（只处理第 0 帧）。</summary>
    public int FrameIndex { get; init; }
}
