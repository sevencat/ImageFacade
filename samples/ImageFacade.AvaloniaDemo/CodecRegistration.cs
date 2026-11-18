using System.Runtime.CompilerServices;
using ImageFacade.Backends;
using Sdcb.SimdCodecs.Png;

namespace ImageFacade.AvaloniaDemo;

/// <summary>
/// 软后端（<see cref="SdcbSimdCodecs.SoftwareBackend"/>）本身不自带任何格式，
/// PNG 编解码器由使用方注册；这里只在"保存图片"这条路上用到。
/// </summary>
/// <remarks>
/// 不想引入编解码时可以不注册任何 codec：绘制 / 文字照常可用，只有 <c>Load</c> / <c>Save</c>
/// 会抛 <see cref="FeatureNotSupportedException"/>。那种情况下存图改走 Avalonia 自己的编码器，
/// 见 <c>MainWindow.SaveFrameWithAvalonia</c>（该方法保留作示例，当前没有调用）。
/// 也就是说：<b>要么</b>像这里注册 Sdcb 的编解码器、用 <see cref="FacadeImageSaveExtensions"/> 存，
/// <b>要么</b>一个编解码包都不引、把位图交给 Avalonia 存，两条路选一条即可。
/// </remarks>
internal static class CodecRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        ImageBackendBase.RegisterCodec(
            ImageFormat.Png,
            PngImageDecoder.Registration,
            PngImageEncoder.Registration);
    }
}
