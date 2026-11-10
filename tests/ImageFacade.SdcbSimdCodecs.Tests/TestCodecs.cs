using System.Runtime.CompilerServices;
using ImageFacade.Backends;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;

namespace ImageFacade.SdcbSimdCodecs.Tests;

/// <summary>
/// 测试工程以"使用方"的身份注册编解码器：软后端包本身不再自带任何格式，
/// 这里注册的就是 README「软后端：按需接入编解码」里演示的那三个。
/// </summary>
internal static class TestCodecs
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
