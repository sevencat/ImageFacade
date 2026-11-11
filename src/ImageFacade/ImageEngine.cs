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

    private readonly bool _ownsBackend;

    private bool _disposed;

    /// <param name="backend">要绑定的后端。</param>
    /// <param name="ownsBackend">
    /// <c>true</c>（默认）时 <see cref="Dispose"/> 会一并释放后端；由 DI 容器托管后端生命周期时传 <c>false</c>，
    /// 否则容器与自己各释放一次。
    /// </param>
    public ImageEngine(IImageBackend backend, bool ownsBackend = true)
    {
        ArgumentNullException.ThrowIfNull(backend);
        Backend = backend;
        _ownsBackend = ownsBackend;
    }

    public IImageBackend Backend { get; }

    /// <summary>供 <see cref="FacadeImage"/> 判断所属引擎是否已释放。</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>可选全局默认，必须显式赋值——门面不做任何自动探测。</summary>
    public static ImageEngine? Default
    {
        get => Volatile.Read(ref s_default);
        set => Volatile.Write(ref s_default, value);
    }

    public bool CanDecode(ImageFormat format) => Backend.SupportedDecodeFormats.Contains(format);

    public bool CanEncode(ImageFormat format) => Backend.SupportedEncodeFormats.Contains(format);

    // ---- 加载 ----

    /// <summary>
    /// 从文件加载：只读内存映射整份文件后直接解码，不再先 <see cref="File.ReadAllBytes(string)"/>
    /// 出一份等长的托管数组（大图省掉一次分配 + 一次拷贝）。
    /// </summary>
    /// <remarks>
    /// 文件长度在建立映射**之前**就检查：空文件抛 <see cref="ArgumentException"/>，
    /// 超过 <see cref="LoadOptions.MaxEncodedBytes"/>（默认 0 = 不限制）或单块缓冲上限
    /// （约 2 GB，<see cref="ReadOnlySpan{T}.Length"/> 是 <see cref="int"/>）抛
    /// <see cref="ImageSizeLimitExceededException"/>，一个字节都不会读。
    /// </remarks>
    public FacadeImage Load(string path, LoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();

        LoadOptions load = options ?? new LoadOptions();
        using MappedFileBuffer file = MappedFileBuffer.Open(path, load.MaxEncodedBytes);
        return Load(file.Span, load);
    }

    public FacadeImage Load(Stream stream, LoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();

        LoadOptions load = options ?? new LoadOptions();
        if (stream is MemoryStream { Position: 0 } seekable && seekable.TryGetBuffer(out ArraySegment<byte> segment))
        {
            // 内存流本来就不用拷贝，但字节上限对"已经是一整块缓冲"同样适用
            EnsureEncodedBytesWithinLimit(segment.Count, load.MaxEncodedBytes);
            return Load(segment.AsSpan(), load);
        }

        // 其它流按块读入池化缓冲：读满上限立刻停手，不把整条流吞进内存
        CheckSeekableLength(stream, load.MaxEncodedBytes);
        byte[] buffer = RentReadBuffer(stream, load.MaxEncodedBytes);
        try
        {
            int length = ReadAll(stream, ref buffer, load.MaxEncodedBytes);
            return Load(new ReadOnlySpan<byte>(buffer, 0, length), load);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
        return FinishLoad(new FacadeImage(this, Backend, frame, metadata), load);
    }

    /// <summary>
    /// 加载收尾：按 <see cref="LoadOptions.DetectTransparency"/> 检查一次"有没有半透明像素"。
    /// 后端能直接回答就问它（<see cref="Backends.IImageBackend.TryGetTransparency"/>），问不到才扫像素；
    /// 两样都不行就留 <c>null</c>（未知）——检查不出来不该让加载失败。
    /// </summary>
    private static FacadeImage FinishLoad(FacadeImage image, LoadOptions load)
    {
        if (!load.DetectTransparency)
        {
            return image;
        }

        // 解码器说这张图连 alpha 通道都没有（JPEG / BMP / 三通道 PNG）：半透明像素不可能存在，
        // 结论直接定死，连后端都不用问——这也是最便宜的"平台已经做好了"。
        if (!image.Metadata.HasAlpha)
        {
            image.SetHasTransparency(false);
            return image;
        }

        image.DetectTransparency();
        return image;
    }

    // ---- 异步加载（I/O 真异步；解码走后端异步接口，否则 Task.Run） ----

    /// <summary>
    /// 从文件异步加载：内存映射（与 <see cref="Load(string, LoadOptions?)"/> 同一条长度检查）+
    /// 后端支持时走异步解码。映射本身是同步的 open + mmap，很快且不读文件内容，所以不包线程池。
    /// </summary>
    public async Task<FacadeImage> LoadAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        LoadOptions load = options ?? new LoadOptions();

        // 映射必须活到解码结束，所以用 using 罩住整个 await（解码器只看本次调用内的这块视图）
        using MappedFileBuffer file = MappedFileBuffer.Open(path, load.MaxEncodedBytes);
        return await LoadAsync(file.Memory, load, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FacadeImage> LoadAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();

        LoadOptions load = options ?? new LoadOptions();
        if (stream is MemoryStream { Position: 0 } seekable && seekable.TryGetBuffer(out ArraySegment<byte> segment))
        {
            EnsureEncodedBytesWithinLimit(segment.Count, load.MaxEncodedBytes);
            return await LoadAsync(new ReadOnlyMemory<byte>(segment.Array!, segment.Offset, segment.Count), load, cancellationToken)
                .ConfigureAwait(false);
        }

        CheckSeekableLength(stream, load.MaxEncodedBytes);

        // 读循环要跨越 await 重新赋值 buffer，所以这里用普通局部变量而不是 ref 参数
        byte[] buffer = RentReadBuffer(stream, load.MaxEncodedBytes);
        try
        {
            int total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    buffer = GrowReadBuffer(buffer, total, load.MaxEncodedBytes);
                }

                int read = await stream
                    .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                EnsureEncodedBytesWithinLimit(total, load.MaxEncodedBytes);
            }

            return await LoadAsync(new ReadOnlyMemory<byte>(buffer, 0, total), load, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
        return FinishLoad(new FacadeImage(this, Backend, decoded.Frame, decoded.Metadata), load);

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

        // 整张图都是同一个颜色：有没有透明像素是由这个颜色直接决定的，不需要扫
        bool translucent = background is null || background.Value.A < 255;
        var metadata = new ImageMetadata(
            ImageFormat.Unknown,
            width,
            height,
            orientation: 0,
            hasAlpha: translucent,
            hasTransparency: translucent);

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

    // ---- 流式读入（带编码字节数上限，planv2 §10.1） ----

    /// <summary>流式读入的初始块大小（与 <see cref="Stream.CopyTo(Stream)"/> 的默认值一致）。</summary>
    private const int InitialReadChunk = 81920;

    /// <summary>
    /// 池化缓冲最多长到 <c>上限 + 1</c> 字节：多读 1 字节就够判定"超限"，不需要真的读完。
    /// 不限制时受 .NET 单数组上限约束。
    /// </summary>
    private static long ReadBufferCap(long maxEncodedBytes)
        => maxEncodedBytes > 0 && maxEncodedBytes < Array.MaxLength ? maxEncodedBytes + 1 : Array.MaxLength;

    private static byte[] RentReadBuffer(Stream stream, long maxEncodedBytes)
    {
        long size = InitialReadChunk;
        if (stream.CanSeek)
        {
            // Length 只是提示：网络流 / 自定义流可能报一个与实际不符的值，真正的上限由读循环累计校验
            long remaining = stream.Length - stream.Position;
            if (remaining > 0 && remaining < size)
            {
                size = remaining;
            }
        }

        size = Math.Min(size, ReadBufferCap(maxEncodedBytes));
        return ArrayPool<byte>.Shared.Rent((int)Math.Clamp(size, 1, Array.MaxLength));
    }

    /// <summary>按块读入，累计超过 <paramref name="maxEncodedBytes"/> 立即抛异常（此时已读的字节数如实上报）。</summary>
    private static int ReadAll(Stream stream, ref byte[] buffer, long maxEncodedBytes)
    {
        int total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                buffer = GrowReadBuffer(buffer, total, maxEncodedBytes);
            }

            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                return total;
            }

            total += read;
            EnsureEncodedBytesWithinLimit(total, maxEncodedBytes);
        }
    }

    /// <summary>容量翻倍并把旧缓冲还回池子；已经到上限（或 .NET 单数组上限）时抛异常。</summary>
    private static byte[] GrowReadBuffer(byte[] buffer, int total, long maxEncodedBytes)
    {
        long grown = Math.Min((long)buffer.Length * 2, ReadBufferCap(maxEncodedBytes));
        if (grown <= buffer.Length)
        {
            throw new ImageSizeLimitExceededException(
                maxEncodedBytes > 0 ? maxEncodedBytes : Array.MaxLength,
                (long)total + 1);
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent((int)grown);
        Buffer.BlockCopy(buffer, 0, bigger, 0, total);
        ArrayPool<byte>.Shared.Return(buffer);
        return bigger;
    }

    /// <summary>
    /// 可寻址流先按 <see cref="Stream.Length"/> 快速拒绝（避免明知超限还去读第一块）。
    /// 这个值不可信（网络流常乱报），真正的上限由读循环累计校验，这里只当一次机会性优化。
    /// </summary>
    private static void CheckSeekableLength(Stream stream, long maxEncodedBytes)
    {
        if (maxEncodedBytes <= 0 || !stream.CanSeek)
        {
            return;
        }

        long remaining = stream.Length - stream.Position;
        if (remaining > maxEncodedBytes)
        {
            throw new ImageSizeLimitExceededException(maxEncodedBytes, remaining);
        }
    }

    private static void EnsureEncodedBytesWithinLimit(long actualBytes, long maxEncodedBytes)
    {
        if (maxEncodedBytes > 0 && actualBytes > maxEncodedBytes)
        {
            throw new ImageSizeLimitExceededException(maxEncodedBytes, actualBytes);
        }
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
        if (_ownsBackend)
        {
            Backend.Dispose();
        }
    }
}
