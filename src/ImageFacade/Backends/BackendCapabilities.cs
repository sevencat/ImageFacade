namespace ImageFacade.Backends;

/// <summary>
/// 后端能力位。**仅供参考**：路由永远以 <c>Try*</c> 的返回值是否成功为准（plan §2.2）。
/// </summary>
[Flags]
public enum BackendCapabilities
{
    None = 0,

    Resize = 1 << 0,

    DrawShapes = 1 << 1,

    DrawPoints = 1 << 2,

    DrawText = 1 << 3,

    /// <summary>非 ASCII（中文）文字。</summary>
    DrawTextUnicode = 1 << 4,

    /// <summary><c>color.A &lt; 255</c> 的绘制（source-over 混合）。</summary>
    AlphaBlending = 1 << 5,

    Dash = 1 << 6,

    LossyEncode = 1 << 7,

    ExifOrientation = 1 << 8,

    IccProfile = 1 << 9,

    ZeroCopyPixels = 1 << 10,

    AotCompatible = 1 << 11,
}
