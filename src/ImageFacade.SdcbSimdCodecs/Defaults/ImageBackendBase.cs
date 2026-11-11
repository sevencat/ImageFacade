using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using ImageFacade.Backends;
using Sdcb.SimdCodecs.Abstractions;
using SdcbImageFormat = Sdcb.SimdCodecs.Abstractions.ImageFormat;
using SdcbImageMetadata = Sdcb.SimdCodecs.Abstractions.ImageMetadata;
using SdcbPixelFormat = Sdcb.SimdCodecs.Abstractions.PixelFormat;

namespace ImageFacade.Backends;

/// <summary>
/// 只负责"加载 / 保存"的后端基类：编解码**只依赖 <c>Sdcb.SimdCodecs.Abstractions</c>** 的抽象
/// （<see cref="ImageDecoderRegistration"/> / <see cref="ImageEncoderRegistration"/>），不引用任何
/// 具体格式包、也不引用 <c>Sdcb.SimdCodecs</c> 聚合门面。具体格式由 <see cref="RegisterCodec"/> 注册。
/// </summary>
/// <remarks>
/// <para>
/// 所有后端共用的编解码语义都在这里，派生类只需要给出"帧"（<see cref="Create"/>）、
/// 像素读写（<see cref="TryCopyPixels(IFrame, Span{byte}, int)"/> / <see cref="ImportPixels"/>）与绘制能力（默认全部不支持）。
/// </para>
/// <para>
/// 本程序集<b>不自带任何编解码器</b>：使用方按需引一个 <c>Sdcb.SimdCodecs.Xxx</c> 包
/// （Png / Jpeg / Bmp / Gif / Webp / Tiff / Ico / Qoi / Pnm / Jpeg2000 / Jbig2，HEIF 需评估专利），
/// 在启动阶段调用一次 <see cref="RegisterCodec"/>（或只注册解码 / 只注册编码）即可，基类不需要改。
/// 没注册的格式不会出现在 <see cref="SupportedDecodeFormats"/> / <see cref="SupportedEncodeFormats"/> 里，
/// 调用时抛 <see cref="FeatureNotSupportedException"/>。
/// </para>
/// </remarks>
public abstract class ImageBackendBase : IImageBackend
{
    private static readonly ConcurrentDictionary<ImageFormat, ImageDecoderRegistration> s_decoders = new();
    private static readonly ConcurrentDictionary<ImageFormat, EncoderEntry> s_encoders = new();
    private static readonly object s_sync = new();

    private static IReadOnlySet<ImageFormat> s_decodeFormats = FrozenSet<ImageFormat>.Empty;
    private static IReadOnlySet<ImageFormat> s_encodeFormats = FrozenSet<ImageFormat>.Empty;

    // ---- 编解码注册 ----

    /// <summary>
    /// 注册一个格式的解码器与编码器。注册是**进程级静态**的，注册一次即可；
    /// 可在任意线程调用，建议在启动阶段（<c>[ModuleInitializer]</c> 或 <c>Main</c> 开头）完成。
    /// </summary>
    /// <param name="format">门面格式。</param>
    /// <param name="decoder">Abstractions 的解码器注册（来自具体格式包，如 <c>PngImageDecoder.Registration</c>）。</param>
    /// <param name="encoder">Abstractions 的编码器注册。</param>
    /// <param name="encodeOptions">
    /// 把门面的 <see cref="EncodeRequest"/> 映射成具体格式的 <see cref="EncodeOptions"/>；
    /// 返回 null 表示用该格式的默认选项。基类不引用任何格式的 options 类型，所以这个工厂由注册方提供。
    /// </param>
    public static void RegisterCodec(
        ImageFormat format,
        ImageDecoderRegistration decoder,
        ImageEncoderRegistration encoder,
        Func<EncodeRequest, EncodeOptions?>? encodeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(encoder);

        s_decoders[format] = decoder;
        s_encoders[format] = new EncoderEntry(encoder, encodeOptions);
        RefreshFormats();
    }

    /// <summary>只注册解码器（编码仍不支持该格式）。</summary>
    public static void RegisterDecoder(ImageFormat format, ImageDecoderRegistration decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        s_decoders[format] = decoder;
        RefreshFormats();
    }

    /// <summary>只注册编码器（解码仍不支持该格式）。</summary>
    public static void RegisterEncoder(
        ImageFormat format,
        ImageEncoderRegistration encoder,
        Func<EncodeRequest, EncodeOptions?>? encodeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        s_encoders[format] = new EncoderEntry(encoder, encodeOptions);
        RefreshFormats();
    }

    /// <summary>清空全部注册（主要给测试用：验证"没注册就抛"）。清空后需要重新注册。</summary>
    public static void ClearCodecs()
    {
        s_decoders.Clear();
        s_encoders.Clear();
        RefreshFormats();
    }

    public abstract string Name { get; }

    public abstract Version Version { get; }

    public abstract BackendCapabilities Capabilities { get; }

    public virtual IReadOnlySet<ImageFormat> SupportedDecodeFormats => s_decodeFormats;

    public virtual IReadOnlySet<ImageFormat> SupportedEncodeFormats => s_encodeFormats;

    // ---- 派生类必须提供 ----

    public abstract IFrame Create(int width, int height, Rgba32? background);

    public abstract IFrame Clone(IFrame frame);

    public abstract void Clear(IFrame frame, Rgba32 color);

    public abstract bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color);

    public abstract bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride);

    /// <summary>
    /// 带 alpha 提示的导出：<paramref name="assumeOpaque"/> 为 <c>true</c> 时调用方**保证**整图 alpha 全是 255。
    /// </summary>
    /// <remarks>
    /// 默认实现直接忽略这个提示（多一次探测，结果一定正确）；能省掉"顺手判一下有没有透明"的实现
    /// 才覆盖它（软后端会把提示转给 <c>Canvas.CopyToStraight</c>，省掉那次全图扫描）。
    /// 提示只有 <see cref="EncodeRequest.AssumeOpaque"/> 一个来源，其他地方一律传 <c>null</c>。
    /// </remarks>
    public virtual bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride, bool? assumeOpaque)
        => TryCopyPixels(frame, destination, destinationStride);

    public abstract void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride);

    // ---- 默认不支持，按需覆盖 ----

    /// <summary>
    /// 默认：不表态（<see cref="IImageBackend.TryGetTransparency"/> 的默认实现返回 false，让门面自己扫像素）。
    /// 能直接看自己帧内存的后端覆盖它，省掉门面那次导出（软后端就是 <c>Canvas.IsFullyOpaque()</c>）。
    /// </summary>
    public virtual bool TryGetTransparency(IFrame frame, out bool hasTransparency)
    {
        hasTransparency = false;
        return false;
    }

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

    /// <summary>
    /// 切片版：默认把切片复制成 string 后转调上面那个重载，所以只重写了 string 版的派生类行为不变。
    /// </summary>
    public virtual bool TryMeasureText(IFrame frame, ReadOnlySpan<char> text, in TextStyle style, out TextMetrics metrics)
        => TryMeasureText(frame, new TextSpec(new string(text), PointF.Empty, style), out metrics);

    public virtual bool TryAcquirePixels(IFrame frame, out PixelLease lease)
    {
        lease = default;
        return false;
    }

    // ---- 编解码 ----

    public IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
    {
        ImageDecoderRegistration codec = FindDecoder(data)
            ?? throw new FeatureNotSupportedException("Decode", Name);

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
                byte[] rotated = PixelTransforms.ApplyOrientation(pixels, width, height, orientation, out int rotatedWidth, out int rotatedHeight);
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

        if (!s_encoders.TryGetValue(request.Format, out EncoderEntry entry))
        {
            throw new FeatureNotSupportedException("Encode", Name);
        }

        int width = frame.Width;
        int height = frame.Height;
        int stride = width * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(checked(stride * height));
        try
        {
            Span<byte> straight = pixels.AsSpan(0, stride * height);

            // 门面查过 alpha 且确定全不透明时，把提示带下来，省掉对齐像素时的那次全图探测
            if (!TryCopyPixels(frame, straight, stride, request.AssumeOpaque))
            {
                throw new ImageBackendException(Name, "导出像素失败，无法编码。");
            }

            EncodeWith(entry, straight, width, height, stride, in request, destination);
        }
        catch (FeatureNotSupportedException)
        {
            throw;
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

    /// <summary>
    /// 用 Abstractions 的编码会话写出一帧：<c>Create → AddFrame → WriteRows → Commit</c>。
    /// 只依赖抽象，具体格式的 writer 不需要出现在这里。
    /// </summary>
    private void EncodeWith(
        EncoderEntry entry,
        ReadOnlySpan<byte> straight,
        int width,
        int height,
        int stride,
        in EncodeRequest request,
        Stream destination)
    {
        // 初值给保守量：PNG 常常要扩容，JPEG 用不到 width*height，按整图预分配在 4K 上等于白占 8 MB
        var writer = new ArrayBufferWriter<byte>(64 * 1024);
        IImageEncoderSession session = entry.Registration.Create(writer, entry.OptionsFactory?.Invoke(request));
        IImageFrameEncoder frame = session.AddFrame(
            width,
            height,
            SdcbPixelFormat.Rgba32,
            ColorEncoding.AssumedSrgb,
            SdcbImageMetadata.Empty,
            duration: null);

        frame.WriteRows(straight, height, stride, CancellationToken.None);
        session.Commit(CancellationToken.None);

        destination.Write(writer.WrittenSpan);
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
        foreach (KeyValuePair<ImageFormat, ImageDecoderRegistration> pair in s_decoders)
        {
            if (pair.Value.CanDecode(data))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static void RefreshFormats()
    {
        lock (s_sync)
        {
            s_decodeFormats = s_decoders.Keys.ToFrozenSet();
            s_encodeFormats = s_encoders.Keys.ToFrozenSet();
        }
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

    private readonly record struct EncoderEntry(
        ImageEncoderRegistration Registration,
        Func<EncodeRequest, EncodeOptions?>? OptionsFactory);
}
