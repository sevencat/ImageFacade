using ImageFacade.Backends;

namespace ImageFacade.SdcbSimdCodecs;

/// <summary>
/// 软后端（回退基准，也可独立当主角）：绘制 / Resize / 文字来自核心光栅
/// （<see cref="ImageBackendBase"/> 的默认实现），编解码来自 <see cref="ImageBackendBase"/> 的
/// codec 注册；**本包不自带任何格式**——使用方引 <c>Sdcb.SimdCodecs.Xxx</c>
/// （Png / Jpeg / Bmp / Gif / Webp / Tiff …）后调用
/// <see cref="ImageBackendBase.RegisterCodec"/> 注册，注册方式见 README「软后端：按需接入编解码」。
/// 完全不注册时，绘制 / Resize / 文字照常可用，只有 Load / Save 会抛
/// <see cref="FeatureNotSupportedException"/>。
/// </summary>
public sealed class SoftwareBackend : ImageBackendBase
{
    public override string Name => "software";

    public override Version Version => typeof(SoftwareBackend).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public override BackendCapabilities Capabilities =>
        BackendCapabilities.Resize
        | BackendCapabilities.DrawShapes
        | BackendCapabilities.DrawPoints
        | BackendCapabilities.DrawText
        | BackendCapabilities.DrawTextUnicode
        | BackendCapabilities.AlphaBlending
        | BackendCapabilities.Dash
        | BackendCapabilities.ExifOrientation
        | BackendCapabilities.LossyEncode
        | BackendCapabilities.AotCompatible;
}
