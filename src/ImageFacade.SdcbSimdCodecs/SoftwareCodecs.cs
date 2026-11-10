using System.Runtime.CompilerServices;
using ImageFacade.Backends;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;

namespace ImageFacade.SdcbSimdCodecs;

/// <summary>
/// 把软后端自带的编解码器注册进 <see cref="ImageBackendBase"/>。
/// 依赖方向：这里（软后端包）引用具体格式包与 Abstractions；基类只认识 Abstractions 的抽象。
/// </summary>
/// <remarks>
/// 引用本程序集时模块初始化器自动执行一次；要在同一进程里换格式集合，可先
/// <see cref="ImageBackendBase.ClearCodecs"/> 再自行注册。
/// </remarks>
internal static class SoftwareCodecs
{
    [ModuleInitializer]
    internal static void Register()
    {
        ImageBackendBase.RegisterCodec(
            ImageFormat.Png,
            PngImageDecoder.Registration,
            PngImageEncoder.Registration);

        ImageBackendBase.RegisterCodec(
            ImageFormat.Jpeg,
            JpegImageDecoder.Registration,
            JpegImageEncoder.Registration,
            static request => new JpegEncodeOptions { Quality = Math.Clamp(request.Quality, 1, 100) });

        ImageBackendBase.RegisterCodec(
            ImageFormat.Bmp,
            BmpImageDecoder.Registration,
            BmpImageEncoder.Registration,
            static _ => new BmpEncodeOptions());
    }
}
