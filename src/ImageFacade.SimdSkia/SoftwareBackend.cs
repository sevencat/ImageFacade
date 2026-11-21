using System.Buffers;
using System.Collections.Frozen;
using ImageFacade.Backends;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Webp;
using SdcbImageFormat = Sdcb.SimdCodecs.Abstractions.ImageFormat;
using SdcbPixelFormat = Sdcb.SimdCodecs.Abstractions.PixelFormat;

namespace ImageFacade.SimdSkia;

/// <summary>
/// 纯软件后端：绘制 / 文字 / 缩放 / 像素转换全部继承 <see cref="SimdSkiaBackend"/>（SimdSkia 本身就是托管 SIMD 光栅），
/// 只有编解码换成 <c>Sdcb.SimdCodecs</c> 的聚合门面 <see cref="ImageCodecs"/>。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="SimdSkiaBackend"/> 的差别只在"能读写哪些格式"：后者走门面的 <c>SKCodec</c>
/// （PNG / JPEG / BMP / GIF / WebP / ICO 解码，PNG / JPEG / WebP 编码），
/// 这里走 <c>Sdcb.SimdCodecs</c> 的注册表——PNG / JPEG / BMP / GIF / WebP / TIFF / ICO / QOI / PNM 编解码。
/// 能力表不是抄常量，而是类型初始化时直接问 <see cref="ImageCodecs.IsDecodable"/> /
/// <see cref="ImageCodecs.IsEncodable"/>，聚合包支持什么这里就跟着支持什么。
/// </para>
/// <para>
/// <b>不需要注册</b>：聚合包 <c>Sdcb.SimdCodecs</c> 自带全部内置编解码器，所以使用方既不用像
/// <c>ImageFacade.SdcbSimdCodecs</c> 那样逐个 <c>RegisterCodec</c>，也不用操心自己引了哪几个格式包。
/// 两个宿主都间接带着这个聚合包：<c>ImageFacade.SimdSkia</c> 经 <c>SimdSkia.Imaging</c> 传递引用，
/// <c>ImageFacade.Avalonia.SimdAIO</c> 则由合并程序集 <c>Avalonia.SimdAio.dll</c> 内含全部
/// <c>Sdcb.SimdCodecs.*</c>，两边都不需要额外的 NuGet 依赖。
/// </para>
/// <para>
/// 本文件同时被 <c>ImageFacade.Avalonia.SimdAIO</c> 链接编译，改动要兼顾两个工程。
/// </para>
/// </remarks>
public sealed class SoftwareBackend : SimdSkiaBackend
{
    /// <summary>门面枚举 ↔ 聚合包枚举的对照；没列在这里的格式（JPEG 2000 / JBIG2 等）门面没有对应成员。</summary>
    private static readonly (ImageFormat Facade, SdcbImageFormat Codec)[] s_formats =
    [
        (ImageFormat.Png, SdcbImageFormat.Png),
        (ImageFormat.Jpeg, SdcbImageFormat.Jpeg),
        (ImageFormat.Bmp, SdcbImageFormat.Bmp),
        (ImageFormat.Gif, SdcbImageFormat.Gif),
        (ImageFormat.WebP, SdcbImageFormat.Webp),
        (ImageFormat.Tiff, SdcbImageFormat.Tiff),
        (ImageFormat.Ico, SdcbImageFormat.Ico),
        (ImageFormat.Qoi, SdcbImageFormat.Qoi),
        (ImageFormat.Pnm, SdcbImageFormat.Pnm),
    ];

    private static readonly IReadOnlySet<ImageFormat> s_decodeFormats = BuildFormats(ImageCodecs.IsDecodable);

    private static readonly IReadOnlySet<ImageFormat> s_encodeFormats = BuildFormats(ImageCodecs.IsEncodable);

    /// <inheritdoc />
    /// <remarks>与 <c>ImageFacade.SdcbSimdCodecs</c> 的软后端同名，都是"编解码走 Sdcb.SimdCodecs"的那一个。</remarks>
    public override string Name => "software";

    /// <inheritdoc />
    public override IReadOnlySet<ImageFormat> SupportedDecodeFormats => s_decodeFormats;

    /// <inheritdoc />
    public override IReadOnlySet<ImageFormat> SupportedEncodeFormats => s_encodeFormats;

    /// <inheritdoc />
    public override IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
    {
        if (!ImageCodecs.TryReadInfo(data, out ImageInfo info))
        {
            throw new ImageBackendException(Name, "无法识别的图像格式。");
        }

        ImageFormat format = ToFacadeFormat(info.Format);
        if (!s_decodeFormats.Contains(format))
        {
            throw new FeatureNotSupportedException("Decode", Name);
        }

        // 方向只在请求要求自动摆正时才处理：交给聚合包做，省掉自己搬像素
        DecodeOptions? options = request.AutoOrient ? new DecodeOptions { ApplyOrientation = true } : null;
        (int width, int height) = options is null ? (info.Width, info.Height) : options.GetOutputSize(in info);

        int stride = checked(width * 4);
        int length = checked(stride * height);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> pixels = buffer.AsSpan(0, length);
            if (!ImageCodecs.TryDecodeInto(data, pixels, stride, SdcbPixelFormat.Rgba32, options, CancellationToken.None))
            {
                throw new ImageBackendException(Name, $"解码失败：{format}。");
            }

            // 像素已按需摆正，但方向值仍然如实回报（与 SKCodec 那条路径一致）
            metadata = new ImageMetadata(format, width, height, (int)info.Orientation, info.Channels == 4);
            return Import(pixels, width, height);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public override void Encode(IFrame frame, Stream destination, in EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (!s_encodeFormats.Contains(request.Format))
        {
            throw new FeatureNotSupportedException("Encode", Name);
        }

        int width = frame.Width;
        int height = frame.Height;
        int stride = checked(width * 4);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(checked(stride * height));
        try
        {
            Span<byte> straight = buffer.AsSpan(0, stride * height);
            if (!TryCopyPixels(frame, straight, stride))
            {
                throw new ImageBackendException(Name, "导出像素失败，无法编码。");
            }

            ImageCodecs.Encode(
                straight,
                width,
                height,
                SdcbPixelFormat.Rgba32,
                ToCodecFormat(request.Format),
                destination,
                stride,
                CreateEncodeOptions(in request),
                metadata: null,
                CancellationToken.None);
        }
        catch (FeatureNotSupportedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException or IOException)
        {
            throw new ImageBackendException(Name, $"编码为 {request.Format} 失败。", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 有损格式把 <see cref="EncodeRequest.Quality"/> 带下去（门面已保证 1..100），其余格式用编码器默认参数。
    /// </summary>
    private static EncodeOptions? CreateEncodeOptions(in EncodeRequest request) => request.Format switch
    {
        ImageFormat.Jpeg => new JpegEncodeOptions { Quality = request.Quality },
        ImageFormat.WebP => new WebpLossyEncodeOptions { Quality = request.Quality },
        _ => null,
    };

    private static IReadOnlySet<ImageFormat> BuildFormats(Func<SdcbImageFormat, bool> supported)
    {
        var formats = new HashSet<ImageFormat>();
        foreach ((ImageFormat facade, SdcbImageFormat codec) in s_formats)
        {
            if (supported(codec))
            {
                formats.Add(facade);
            }
        }

        return formats.ToFrozenSet();
    }

    private static ImageFormat ToFacadeFormat(SdcbImageFormat format)
    {
        foreach ((ImageFormat facade, SdcbImageFormat codec) in s_formats)
        {
            if (codec == format)
            {
                return facade;
            }
        }

        return ImageFormat.Unknown;
    }

    private static SdcbImageFormat ToCodecFormat(ImageFormat format)
    {
        foreach ((ImageFormat facade, SdcbImageFormat codec) in s_formats)
        {
            if (facade == format)
            {
                return codec;
            }
        }

        throw new FeatureNotSupportedException("Encode", "software");
    }
}
