using System.Buffers;
using ImageFacade.Backends;
using ImageFacade.Internal;

namespace ImageFacade;

/// <summary>
/// 门面入口：持有一个后端，负责创建 / 加载图像。后端不具备的能力直接抛
/// <see cref="FeatureNotSupportedException"/>，不做任何跨后端回退。
/// 可跨线程共享（后端本身无状态）；<see cref="FacadeImage"/> 不可。
/// </summary>
public sealed class ImageEngine : IDisposable
{
    private static ImageEngine? s_default;

    private bool _disposed;

    public ImageEngine(IImageBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        Backend = backend;
    }

    public IImageBackend Backend { get; }

    /// <summary>可选全局默认，必须显式赋值——门面不做任何自动探测。</summary>
    public static ImageEngine? Default
    {
        get => s_default;
        set => s_default = value;
    }

    public bool CanDecode(ImageFormat format) => Backend.SupportedDecodeFormats.Contains(format);

    public bool CanEncode(ImageFormat format) => Backend.SupportedEncodeFormats.Contains(format);

    // ---- 加载 ----

    public FacadeImage Load(string path, LoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] data = File.ReadAllBytes(path);
        return Load(data.AsSpan(), options);
    }

    public FacadeImage Load(Stream stream, LoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is MemoryStream { Position: 0 } seekable && seekable.TryGetBuffer(out ArraySegment<byte> segment))
        {
            return Load(segment.AsSpan(), options);
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Load(new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, (int)buffer.Length), options);
    }

    public FacadeImage Load(ReadOnlySpan<byte> data, LoadOptions? options = null)
    {
        ThrowIfDisposed();

        if (data.IsEmpty)
        {
            throw new ArgumentException("图像数据为空。", nameof(data));
        }

        LoadOptions load = options ?? new LoadOptions();
        if (load.FrameIndex != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LoadOptions.FrameIndex), load.FrameIndex, "v1 只处理第 0 帧。");
        }

        // 第一道闸：能读到头部就在解码前拒绝，避免按声明尺寸分配一大块像素
        CheckDeclaredDimensions(data, load.MaxPixels);

        IFrame frame;
        ImageMetadata metadata;
        try
        {
            frame = Backend.Decode(data, new DecodeRequest(load.AutoOrient, load.FrameIndex), out metadata);
        }
        catch (FeatureNotSupportedException)
        {
            throw;
        }
        catch (NotSupportedException ex)
        {
            // 后端用 NotSupportedException 表示"这个格式/特性我干不了"：统一成门面的能力异常
            throw new FeatureNotSupportedException("Decode", Backend.Name, ex);
        }
        catch (Exception ex) when (ex is not ImageBackendException and not ArgumentException)
        {
            throw new ImageBackendException(Backend.Name, "解码失败。", ex);
        }

        // 第二道闸：嗅探不认识的格式（或头部与实际不符）在这里兜底，超限的帧就地释放
        EnsureWithinLimit(frame, load.MaxPixels);
        return new FacadeImage(this, Backend, frame, metadata);
    }

    // ---- 异步加载（I/O 真异步；解码走后端异步接口，否则 Task.Run） ----

    /// <summary>从文件加载（<see cref="File.ReadAllBytesAsync(string, CancellationToken)"/> + 异步解码）。</summary>
    public async Task<FacadeImage> LoadAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();

        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await LoadAsync(data.AsMemory(), options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FacadeImage> LoadAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();

        if (stream is MemoryStream { Position: 0 } seekable && seekable.TryGetBuffer(out ArraySegment<byte> segment))
        {
            return await LoadAsync(new ReadOnlyMemory<byte>(segment.Array!, segment.Offset, segment.Count), options, cancellationToken)
                .ConfigureAwait(false);
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await LoadAsync(new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, (int)buffer.Length), options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 从内存加载。后端实现了 <see cref="IAsyncImageBackend"/> 就走它的异步解码，
    /// 否则用 <see cref="Task.Run(Action, CancellationToken)"/> 包同步解码。
    /// </summary>
    public async Task<FacadeImage> LoadAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (data.IsEmpty)
        {
            throw new ArgumentException("图像数据为空。", nameof(data));
        }

        LoadOptions load = options ?? new LoadOptions();
        if (load.FrameIndex != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LoadOptions.FrameIndex), load.FrameIndex, "v1 只处理第 0 帧。");
        }

        var request = new DecodeRequest(load.AutoOrient, load.FrameIndex);

        CheckDeclaredDimensions(data.Span, load.MaxPixels);

        DecodedImage decoded;
        try
        {
            decoded = Backend is IAsyncImageBackend asyncBackend
                ? await asyncBackend.DecodeAsync(data, request, cancellationToken).ConfigureAwait(false)
                : await AsyncBridge.Run(DecodeLocally, cancellationToken).ConfigureAwait(false);
        }
        catch (FeatureNotSupportedException)
        {
            throw;
        }
        catch (NotSupportedException ex)
        {
            throw new FeatureNotSupportedException("Decode", Backend.Name, ex);
        }
        catch (Exception ex) when (ex is not ImageBackendException and not ArgumentException and not OperationCanceledException)
        {
            throw new ImageBackendException(Backend.Name, "解码失败。", ex);
        }

        EnsureWithinLimit(decoded.Frame, load.MaxPixels);
        return new FacadeImage(this, Backend, decoded.Frame, decoded.Metadata);

        DecodedImage DecodeLocally()
        {
            IFrame frame = Backend.Decode(data.Span, request, out ImageMetadata metadata);
            return new DecodedImage(frame, metadata);
        }
    }

    // ---- 创建 / 导入 ----

    public FacadeImage Create(int width, int height, Rgba32? background = null)
    {
        ThrowIfDisposed();
        ValidateSize(width, height);

        IFrame frame = Backend.Create(width, height, background);
        var metadata = new ImageMetadata(
            ImageFormat.Unknown,
            width,
            height,
            orientation: 0,
            hasAlpha: background is null || background.Value.A < 255);

        return new FacadeImage(this, Backend, frame, metadata);
    }

    public FacadeImage FromPixels(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        PixelFormat format = PixelFormat.Rgba32)
    {
        ThrowIfDisposed();
        ValidateSize(width, height);

        int bytesPerPixel = format.GetBytesPerPixel();
        int minimumStride = width * bytesPerPixel;
        if (stride < minimumStride)
        {
            throw new ArgumentException($"stride 太小（{stride} < {minimumStride}）。", nameof(stride));
        }

        long required = (((long)height - 1) * stride) + minimumStride;
        if (pixels.Length < required)
        {
            throw new ArgumentException($"pixels 缓冲区太小（需要 {required} 字节，实际 {pixels.Length}）。", nameof(pixels));
        }

        IFrame frame = Backend.Create(width, height, background: null);
        try
        {
            int canonicalStride = checked(width * 4);
            if (format == PixelFormat.Rgba32)
            {
                Backend.ImportPixels(frame, pixels, stride);
            }
            else
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(checked(canonicalStride * height));
                try
                {
                    Span<byte> canonical = rented.AsSpan(0, canonicalStride * height);
                    PixelConverter.Convert(pixels, stride, format, canonical, canonicalStride, PixelFormat.Rgba32, width, height);
                    Backend.ImportPixels(frame, canonical, canonicalStride);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        return new FacadeImage(this, Backend, frame, new ImageMetadata(ImageFormat.Unknown, width, height, 0, hasAlpha: true));
    }

    /// <summary>
    /// 指针入口（ffmpeg <c>AVFrame</c>、相机缓冲、UI 位图）。
    /// <paramref name="wrap"/> 为 true 且格式是规范 RGBA32、后端支持接管时才真正零拷贝。
    /// </summary>
    public unsafe FacadeImage FromPixels(
        IntPtr pixels,
        int width,
        int height,
        int stride,
        PixelFormat format = PixelFormat.Rgba32,
        bool wrap = false)
    {
        ThrowIfDisposed();
        ValidateSize(width, height);

        if (pixels == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        if (stride < width * format.GetBytesPerPixel())
        {
            throw new ArgumentException($"stride 太小（{stride} < {width * format.GetBytesPerPixel()}）。", nameof(stride));
        }

        if (wrap && format == PixelFormat.Rgba32
            && Backend.TryWrapPixels(pixels, width, height, stride, ownsMemory: false, out IFrame wrapped))
        {
            return new FacadeImage(this, Backend, wrapped, new ImageMetadata(ImageFormat.Unknown, width, height, 0, hasAlpha: true));
        }

        var source = new ReadOnlySpan<byte>((void*)pixels, checked(stride * height));
        return FromPixels(source, width, height, stride, format);
    }

    internal static void ValidateSize(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "宽度必须大于 0。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "高度必须大于 0。");
        }

        if ((long)width * height * 4 > int.MaxValue)
        {
            throw new ArgumentException($"尺寸过大：{width}x{height} 的 RGBA 缓冲超过 int.MaxValue 字节。");
        }
    }

    /// <summary>
    /// 解码前的第一道闸：从头部读声明尺寸，超限直接拒绝（此时还没分配任何像素缓冲）。
    /// 嗅探不到的格式静默放过，由 <see cref="EnsureWithinLimit"/> 兜底。
    /// </summary>
    private static void CheckDeclaredDimensions(ReadOnlySpan<byte> data, long maxPixels)
    {
        if (maxPixels <= 0)
        {
            return;
        }

        if (ImageHeaderReader.TryReadDimensions(data, out int width, out int height)
            && (long)width * height > maxPixels)
        {
            throw new ImageSizeLimitExceededException(width, height, maxPixels);
        }
    }

    /// <summary>解码后的第二道闸：超限的帧就地释放，绝不把巨型对象交给调用方。</summary>
    private static void EnsureWithinLimit(IFrame frame, long maxPixels)
    {
        if (maxPixels <= 0 || (long)frame.Width * frame.Height <= maxPixels)
        {
            return;
        }

        int width = frame.Width;
        int height = frame.Height;
        frame.Dispose();
        throw new ImageSizeLimitExceededException(width, height, maxPixels);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ImageEngine));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Backend.Dispose();
    }
}
