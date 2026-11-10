using System.Buffers;
using ImageFacade.Backends;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using SdcbImageFormat = Sdcb.SimdCodecs.Abstractions.ImageFormat;
using SdcbPixelFormat = Sdcb.SimdCodecs.Abstractions.PixelFormat;

namespace ImageFacade.Backends;

/// <summary>
/// 只负责"加载 / 保存"的后端基类：解码与编码统一走 Sdcb.SimdCodecs 2.0（Abstractions + 按格式包）。
/// </summary>
/// <remarks>
/// <para>
/// 所有后端共用的编解码语义都在这里，派生类只需要给出"帧"（<see cref="Create"/>）、
/// 像素读写（<see cref="TryCopyPixels"/> / <see cref="ImportPixels"/>）与绘制能力（默认全部不支持）。
/// </para>
/// <para>
/// 接入新格式：加一个 <c>Sdcb.SimdCodecs.Xxx</c> 包，把它的 <c>XxxImageDecoder.Registration</c>
/// 加进 <see cref="Decoders"/>，并在 <see cref="Encode"/> 里补一个分支。
/// </para>
/// </remarks>
public abstract class ImageBackendBase : IImageBackend
{
    private static readonly ImageDecoderRegistration[] Decoders =
    [
        PngImageDecoder.Registration,
        JpegImageDecoder.Registration,
    ];

    private static readonly IReadOnlySet<ImageFormat> DecodableFormats = new HashSet<ImageFormat>
    {
        ImageFormat.Png,
        ImageFormat.Jpeg,
    };

    private static readonly IReadOnlySet<ImageFormat> EncodableFormats = new HashSet<ImageFormat>
    {
        ImageFormat.Png,
        ImageFormat.Jpeg,
    };

    public abstract string Name { get; }

    public abstract Version Version { get; }

    public abstract BackendCapabilities Capabilities { get; }

    public virtual IReadOnlySet<ImageFormat> SupportedDecodeFormats => DecodableFormats;

    public virtual IReadOnlySet<ImageFormat> SupportedEncodeFormats => EncodableFormats;

    // ---- 派生类必须提供 ----

    public abstract IFrame Create(int width, int height, Rgba32? background);

    public abstract IFrame Clone(IFrame frame);

    public abstract void Clear(IFrame frame, Rgba32 color);

    public abstract bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color);

    public abstract bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride);

    public abstract void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride);

    // ---- 默认不支持，按需覆盖 ----

    public virtual bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame)
    {
        frame = null!;
        return false;
    }

    public virtual bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result)
    {
        result = null!;
        return false;
    }

    public virtual bool TryDrawFrame(IFrame destination, IFrame source, PointF offset) => false;

    public virtual bool TryDrawPoint(IFrame frame, in PointSpec spec) => false;

    public virtual bool TryDrawLine(IFrame frame, in LineSpec spec) => false;

    public virtual bool TryDrawPolyline(IFrame frame, in PolylineSpec spec) => false;

    public virtual bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec) => false;

    public virtual bool TryFillRectangle(IFrame frame, in FilledRectSpec spec) => false;

    public virtual bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec) => false;

    public virtual bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec) => false;

    public virtual bool TryFillPolygon(IFrame frame, in PolygonSpec spec) => false;

    public virtual bool TryDrawText(IFrame frame, in TextSpec spec) => false;

    public virtual bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
    {
        metrics = default;
        return false;
    }

    public virtual bool TryAcquirePixels(IFrame frame, out PixelLease lease)
    {
        lease = default;
        return false;
    }

    // ---- 编解码 ----

    public IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
    {
        ImageDecoderRegistration codec = FindDecoder(data)
            ?? throw new ImageBackendException(Name, "无法识别的图像格式，或该格式的解码器未注册。");

        if (!codec.TryReadInfo(data, out ImageInfo info))
        {
            throw new ImageBackendException(Name, "读取图像头失败：数据不完整或已损坏。");
        }

        if (codec.TryDecodeInto is not { } decodeInto)
        {
            throw new ImageBackendException(Name, $"解码器 {info.Format} 不支持直写目标缓冲。");
        }

        int width = info.Width;
        int height = info.Height;
        int stride = width * 4;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(checked(stride * height));
        try
        {
            Span<byte> pixels = buffer.AsSpan(0, stride * height);
            if (!decodeInto(data, pixels, stride, SdcbPixelFormat.Rgba32, null, default))
            {
                throw new ImageBackendException(Name, $"解码失败：{info.Format}。");
            }

            int orientation = (int)info.Orientation;
            if (request.AutoOrient && orientation is > 1 and <= 8)
            {
                (byte[] rotated, int rotatedWidth, int rotatedHeight) = OrientationTransform.Apply(pixels, width, height, orientation);
                IFrame oriented = Import(rotated, rotatedWidth, rotatedHeight);
                metadata = new ImageMetadata(
                    MapFormat(info.Format),
                    rotatedWidth,
                    rotatedHeight,
                    orientation,
                    hasAlpha: info.Channels == 4);
                return oriented;
            }

            IFrame frame = Import(pixels, width, height);
            metadata = new ImageMetadata(MapFormat(info.Format), width, height, orientation, hasAlpha: info.Channels == 4);
            return frame;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Encode(IFrame frame, Stream destination, in EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(destination);

        int width = frame.Width;
        int height = frame.Height;
        int stride = width * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(checked(stride * height));
        try
        {
            Span<byte> straight = pixels.AsSpan(0, stride * height);
            if (!TryCopyPixels(frame, straight, stride))
            {
                throw new ImageBackendException(Name, "导出像素失败，无法编码。");
            }

            byte[] encoded = request.Format switch
            {
                ImageFormat.Png => PngWriter.Encode(straight, width, height, new PngWriteOptions()),
                ImageFormat.Jpeg => JpegEncoder.Encode(
                    straight,
                    width,
                    height,
                    4,
                    new JpegEncodeOptions { Quality = Math.Clamp(request.Quality, 1, 100) }),
                _ => throw new NotSupportedException($"软后端目前只支持 PNG / JPEG 编码（请求：{request.Format}）。"),
            };

            destination.Write(encoded, 0, encoded.Length);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            throw new ImageBackendException(Name, $"编码为 {request.Format} 失败。", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private IFrame Import(ReadOnlySpan<byte> pixels, int width, int height)
    {
        IFrame frame = Create(width, height, background: null);
        try
        {
            ImportPixels(frame, pixels, width * 4);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static ImageDecoderRegistration? FindDecoder(ReadOnlySpan<byte> data)
    {
        foreach (ImageDecoderRegistration codec in Decoders)
        {
            if (codec.CanDecode(data))
            {
                return codec;
            }
        }

        return null;
    }

    private static ImageFormat MapFormat(SdcbImageFormat format) => format switch
    {
        SdcbImageFormat.Png => ImageFormat.Png,
        SdcbImageFormat.Jpeg => ImageFormat.Jpeg,
        SdcbImageFormat.Bmp => ImageFormat.Bmp,
        SdcbImageFormat.Gif => ImageFormat.Gif,
        SdcbImageFormat.Webp => ImageFormat.WebP,
        SdcbImageFormat.Tiff => ImageFormat.Tiff,
        SdcbImageFormat.Ico => ImageFormat.Ico,
        SdcbImageFormat.Qoi => ImageFormat.Qoi,
        SdcbImageFormat.Pnm => ImageFormat.Pnm,
        _ => ImageFormat.Unknown,
    };

    public virtual void Dispose()
    {
    }
}
