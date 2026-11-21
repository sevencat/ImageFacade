using System.Collections.Concurrent;
using ImageFacade.Backends;
using global::SkiaSharp;

namespace ImageFacade.SimdSkia;

/// <summary>
/// SimdSkia 适配后端：纯托管的 SkiaSharp 兼容实现，无原生库。
/// </summary>
/// <remarks>
/// <para>
/// 帧 = <see cref="SKBitmap"/>（<c>Rgba8888 + Premul</c>）+ 常驻 <see cref="SKCanvas"/>；
/// 由 <see cref="TryWrapPixels"/> 接管的外部缓冲例外，是 <c>Unpremul</c> 且内存在调用方手里。
/// </para>
/// <para>
/// 绑定 SimdSkia <b>0.1.12</b>：它实现的是 SkiaSharp <c>SkiaSharp</c> 命名空间的源码兼容子集，
/// 因此这里与 <c>ImageFacade.SkiaSharp</c> 的代码基本一致，差别只在于下面这些"门面没提供"的成员
/// 走自带的托管实现（不依赖 <c>SkiaSharp</c> 原生库）。与真正的 SkiaSharp 程序集不能同时引用。
/// </para>
/// <para>
/// 本文件同时被 <c>ImageFacade.Avalonia.SimdAIO</c> 链接编译（那里改用
/// <c>Sevencat.Avalonia.SimdAIO</c> 合并程序集，内核与 <c>SkiaSharp</c> 门面都在里面），所以改动要兼顾两个工程。
/// </para>
/// <para>
/// 本类是 <see cref="SoftwareBackend"/> 的基类：后者只把编解码换成 <c>Sdcb.SimdCodecs</c> 聚合门面，
/// 绘制 / 文字 / 缩放完全复用这里的实现，因此 <see cref="Name"/>、格式能力表、<see cref="Decode"/>、
/// <see cref="Encode"/> 都留成可覆写的。
/// </para>
/// <para>
/// 已知限制：CPU 缩放只实现最近邻与双线性（mipmap / 各向异性抛异常），
/// 所以 <see cref="Sampler.Bicubic"/> / Lanczos / Mitchell 等重采样核在这里一律退化为双线性。
/// </para>
/// </remarks>
public class SimdSkiaBackend : IImageBackend
{
    private static readonly IReadOnlySet<ImageFormat> Decodable = new HashSet<ImageFormat>
    {
        ImageFormat.Png,
        ImageFormat.Jpeg,
        ImageFormat.Bmp,
        ImageFormat.Gif,
        ImageFormat.WebP,
        ImageFormat.Ico,
    };

    private static readonly IReadOnlySet<ImageFormat> Encodable = new HashSet<ImageFormat>
    {
        ImageFormat.Png,
        ImageFormat.Jpeg,
        ImageFormat.WebP,
        ImageFormat.Bmp,
        ImageFormat.Gif,
        ImageFormat.Ico,
    };

    private readonly ConcurrentDictionary<FontRef, SKTypeface> _typefaces = new();

    public virtual string Name => "simdskia";

    public Version Version => typeof(SimdSkiaBackend).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public BackendCapabilities Capabilities =>
        BackendCapabilities.Resize
        | BackendCapabilities.DrawShapes
        | BackendCapabilities.DrawPoints
        | BackendCapabilities.DrawText
        | BackendCapabilities.DrawTextUnicode
        | BackendCapabilities.AlphaBlending
        | BackendCapabilities.Dash
        | BackendCapabilities.LossyEncode
        | BackendCapabilities.ExifOrientation
        | BackendCapabilities.AotCompatible;

    public virtual IReadOnlySet<ImageFormat> SupportedDecodeFormats => Decodable;

    public virtual IReadOnlySet<ImageFormat> SupportedEncodeFormats => Encodable;

    // ---- 生命周期 ----

    public IFrame Create(int width, int height, Rgba32? background)
    {
        var frame = new SimdSkiaFrame(new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)));
        if (background is { } color)
        {
            frame.Canvas.Clear(ToColor(color));
        }

        return frame;
    }

    /// <summary>
    /// 零拷贝接管外部 RGBA32 缓冲：用 <c>InstallPixels</c> 把指针直接装进一张
    /// <c>Unpremul</c> 位图，不做任何像素转换。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与真 SkiaSharp 不同，SimdSkia 的纯托管画布支持在<b>非预乘</b>位图上合成（实测结果与
    /// "预乘位图 + <see cref="ImportPixels"/>" 路径逐像素一致），所以直通缓冲能原样接管。
    /// </para>
    /// <para>
    /// <paramref name="ownsMemory"/> 必须为 <c>false</c>：缓冲区由谁分配我们并不知道，
    /// 帧释放时只释放位图外壳（SimdSkia 不会碰外部内存），不负责归还缓冲。
    /// </para>
    /// </remarks>
    public bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame)
    {
        if (pixels == IntPtr.Zero || ownsMemory || width <= 0 || height <= 0 || stride < (long)width * 4)
        {
            frame = null!;
            return false;
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap();
        if (!bitmap.InstallPixels(info, pixels, stride))
        {
            bitmap.Dispose();
            frame = null!;
            return false;
        }

        frame = new SimdSkiaFrame(bitmap);
        return true;
    }

    public IFrame Clone(IFrame frame)
    {
        var source = (SimdSkiaFrame)frame;
        SKBitmap copy = source.Bitmap.Copy() ?? throw new ImageBackendException(Name, "克隆帧失败。");
        return new SimdSkiaFrame(copy);
    }

    public virtual IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
    {
        using SKData encoded = CreateData(data);
        using SKCodec codec = SKCodec.Create(encoded) ?? throw new ImageBackendException(Name, "无法识别的图像格式。");

        SKImageInfo info = new(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        bool transferred = false;
        try
        {
            SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new ImageBackendException(Name, $"解码失败：{result}。");
            }

            // 编码器声明的通道能力：AlphaType 为 Opaque 说明源数据里根本没有 alpha 通道
            // （JPEG / 三通道 PNG），"图里有没有半透明像素"直接就是 false，不用再扫。
            bool hasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque;

            int orientation = (int)codec.EncodedOrigin;
            if (request.AutoOrient && orientation is > 1 and <= 8)
            {
                byte[] straight = ExportStraight(bitmap, out _);
                byte[] rotated = PixelTransforms.ApplyOrientation(
                    straight,
                    bitmap.Width,
                    bitmap.Height,
                    orientation,
                    out int rotatedWidth,
                    out int rotatedHeight);

                metadata = new ImageMetadata(MapFormat(codec.EncodedFormat), rotatedWidth, rotatedHeight, orientation, hasAlpha);
                return Import(rotated, rotatedWidth, rotatedHeight);
            }

            metadata = new ImageMetadata(MapFormat(codec.EncodedFormat), bitmap.Width, bitmap.Height, orientation, hasAlpha);
            var frame = new SimdSkiaFrame(bitmap);
            transferred = true;
            return frame;
        }
        finally
        {
            if (!transferred)
            {
                bitmap.Dispose();
            }
        }
    }

    public virtual void Encode(IFrame frame, Stream destination, in EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var source = (SimdSkiaFrame)frame;
        SKEncodedImageFormat format = request.Format switch
        {
            ImageFormat.Png => SKEncodedImageFormat.Png,
            ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            ImageFormat.WebP => SKEncodedImageFormat.Webp,
            ImageFormat.Bmp => SKEncodedImageFormat.Bmp,
            ImageFormat.Gif => SKEncodedImageFormat.Gif,
            ImageFormat.Ico => SKEncodedImageFormat.Ico,
            _ => throw new NotSupportedException($"SimdSkiaBackend 不支持编码 {request.Format}。"),
        };

        // ICO 的目录项把宽高各存 1 字节（0 表示 256），超过 256 的图标写不出来，
        // 不拦的话 SKImage.Encode 只会返回 null，错误信息落到"编码失败"上看不出原因
        if (request.Format == ImageFormat.Ico && (source.Width > 256 || source.Height > 256))
        {
            throw new ImageBackendException(
                Name,
                $"ICO 的最大边长是 256（当前 {source.Width}x{source.Height}），请先缩放到 256 以内再编码。");
        }

        // 质量只对 JPEG / WebP 有定义，无损格式（PNG / BMP / GIF / ICO）内部会忽略这个参数
        using SKImage image = SKImage.FromBitmap(source.Bitmap);
        using SKData? encoded = image.Encode(format, Math.Clamp(request.Quality, 1, 100));
        if (encoded is null)
        {
            throw new ImageBackendException(Name, $"编码为 {request.Format} 失败。");
        }

        encoded.SaveTo(destination);
    }

    // ---- 几何 ----

    public bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result)
    {
        var source = (SimdSkiaFrame)frame;
        SKBitmap? resized;
        try
        {
            resized = source.Bitmap.Resize(
                new SKImageInfo(request.Width, request.Height, SKColorType.Rgba8888, SKAlphaType.Premul),
                ToSampling(request.Sampler));
        }
        catch (NotSupportedException)
        {
            // 门面的约定是 false = 这个后端做不了，由调用方决定降级还是报错；
            // 采样模式的具体限制不该冒成裸 NotSupportedException。
            result = null!;
            return false;
        }

        if (resized is null)
        {
            result = null!;
            return false;
        }

        result = new SimdSkiaFrame(resized);
        return true;
    }

    public bool TryDrawFrame(IFrame destination, IFrame source, PointF offset)
    {
        var target = (SimdSkiaFrame)destination;
        var layer = (SimdSkiaFrame)source;
        using var paint = new SKPaint { IsAntialias = false };
        target.Canvas.DrawBitmap(layer.Bitmap, offset.X, offset.Y, paint);
        return true;
    }

    // ---- 绘制 ----

    public bool TryDrawPoint(IFrame frame, in PointSpec spec)
    {
        if (!IsFinite(spec.Point) || !IsValid(spec.Radius))
        {
            return true;
        }

        var target = (SimdSkiaFrame)frame;
        using SKPaint paint = FillPaint(spec.Color, spec.Antialias);
        target.Canvas.DrawCircle(spec.Point.X, spec.Point.Y, spec.Radius, paint);
        return true;
    }

    public bool TryDrawLine(IFrame frame, in LineSpec spec)
    {
        if (!IsFinite(spec.From) || !IsFinite(spec.To) || !IsValid(spec.Width))
        {
            return true;
        }

        var target = (SimdSkiaFrame)frame;
        using SKPaint paint = StrokePaint(spec.Color, spec.Width, spec.Style);
        target.Canvas.DrawLine(spec.From.X, spec.From.Y, spec.To.X, spec.To.Y, paint);
        return true;
    }

    public bool TryDrawPolyline(IFrame frame, in PolylineSpec spec)
    {
        if (spec.Points.Length < 2 || !IsValid(spec.Width) || !AllFinite(spec.Points))
        {
            return true;
        }

        using SKPath path = BuildPath(spec.Points, spec.Closed);
        var target = (SimdSkiaFrame)frame;
        using SKPaint paint = StrokePaint(spec.Color, spec.Width, spec.Style);
        target.Canvas.DrawPath(path, paint);
        return true;
    }

    public bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect) || !IsValid(spec.Width))
        {
            return true;
        }

        var target = (SimdSkiaFrame)frame;
        using SKPaint paint = StrokePaint(spec.Color, spec.Width, spec.Style);
        target.Canvas.DrawRect(ToRect(spec.Rect), paint);
        return true;
    }

    public bool TryFillRectangle(IFrame frame, in FilledRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect))
        {
            return true;
        }

        var target = (SimdSkiaFrame)frame;
        using SKPaint paint = FillPaint(spec.Color, antialias: false);
        target.Canvas.DrawRect(ToRect(spec.Rect), paint);
        return true;
    }

    public bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds) || !IsValid(spec.Width))
        {
            return true;
        }

        var target = (SimdSkiaFrame)frame;
        using SKPaint paint = StrokePaint(spec.Color, spec.Width, spec.Style);
        target.Canvas.DrawOval(ToRect(spec.Bounds), paint);
        return true;
    }

    public bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds))
        {
            return true;
        }

        var target = (SimdSkiaFrame)frame;
        using SKPaint paint = FillPaint(spec.Color, antialias: true);
        target.Canvas.DrawOval(ToRect(spec.Bounds), paint);
        return true;
    }

    public bool TryFillPolygon(IFrame frame, in PolygonSpec spec)
    {
        if (spec.Points.Length < 3 || !AllFinite(spec.Points))
        {
            return true;
        }

        using SKPath path = BuildPath(spec.Points, closed: true);
        var target = (SimdSkiaFrame)frame;
        using SKPaint paint = FillPaint(spec.Color, antialias: true);
        target.Canvas.DrawPath(path, paint);
        return true;
    }

    // ---- 文字 ----

    public bool TryDrawText(IFrame frame, in TextSpec spec)
    {
        if (spec.Text.Length == 0 || !IsFinite(spec.Origin))
        {
            return true;
        }

        var target = (SimdSkiaFrame)frame;
        SKTypeface typeface = ResolveTypeface(spec.Style.Font);

        // SimdSkia 不实现网格拟合排版，只接受 Subpixel 定位（SkiaSharp 适配里这里是 false）
        using var font = new SKFont(typeface, spec.Style.Size)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true,
        };

        using SKPaint paint = FillPaint(spec.Style.Color, spec.Style.Antialias);
        float baseline = spec.Style.Anchor == TextAnchor.Baseline
            ? spec.Origin.Y
            : spec.Origin.Y - font.Metrics.Ascent;

        target.Canvas.DrawText(spec.Text, spec.Origin.X, baseline, SKTextAlign.Left, font, paint);
        return true;
    }

    public bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
        => TryMeasureTextCore(spec.Style, spec.Text.AsSpan(), out metrics);

    /// <summary>切片版：<c>SKFont.MeasureText(ReadOnlySpan&lt;char&gt;)</c> 直接吃切片，折行时不再为每个候选分配 string。</summary>
    public bool TryMeasureText(IFrame frame, ReadOnlySpan<char> text, in TextStyle style, out TextMetrics metrics)
        => TryMeasureTextCore(style, text, out metrics);

    private bool TryMeasureTextCore(TextStyle style, ReadOnlySpan<char> text, out TextMetrics metrics)
    {
        try
        {
            SKTypeface typeface = ResolveTypeface(style.Font);
            // 同 TryDrawText：SimdSkia 只支持 Subpixel 定位
            using var font = new SKFont(typeface, style.Size) { Subpixel = true };
            SKFontMetrics fontMetrics = font.Metrics;
            float ascent = MathF.Abs(fontMetrics.Ascent);
            float descent = MathF.Abs(fontMetrics.Descent);
            float leading = MathF.Max(0f, fontMetrics.Leading);

            metrics = new TextMetrics(font.MeasureText(text), ascent, descent, leading);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            metrics = default;
            return false;
        }
    }

    // ---- 像素 ----

    public void Clear(IFrame frame, Rgba32 color)
    {
        var target = (SimdSkiaFrame)frame;
        target.Canvas.Clear(ToColor(color));
    }

    public bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color)
    {
        var target = (SimdSkiaFrame)frame;
        SKColor value = target.Bitmap.GetPixel(x, y);
        color = new Rgba32(value.Red, value.Green, value.Blue, value.Alpha);
        return true;
    }

    public bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride)
    {
        CopyStraight(((SimdSkiaFrame)frame).Bitmap, destination, destinationStride);
        return true;
    }

    /// <summary>
    /// 位图（预乘）→ 直通 RGBA32。走 Skia 自己的 alpha 类型转换（<c>SkConvertPixels</c> 用倒数表 + SIMD，
    /// 比逐像素除法快一个量级）；转换不可用时回退到标量实现。
    /// </summary>
    private static unsafe void CopyStraight(SKBitmap bitmap, Span<byte> destination, int destinationStride)
    {
        using SKPixmap? source = bitmap.PeekPixels();
        if (source is not null)
        {
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            fixed (byte* pointer = destination)
            {
                if (source.ReadPixels(info, (IntPtr)pointer, destinationStride))
                {
                    return;
                }
            }
        }

        CopyStraightScalar(bitmap, destination, destinationStride);
    }

    private static unsafe void CopyStraightScalar(SKBitmap bitmap, Span<byte> destination, int destinationStride)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        int rowBytes = bitmap.RowBytes;
        int width = bitmap.Width;
        int height = bitmap.Height;

        for (int y = 0; y < height; y++)
        {
            byte* row = pixels + (y * rowBytes);
            Span<byte> target = destination.Slice(y * destinationStride, width * 4);
            for (int x = 0; x < width; x++)
            {
                byte* pixel = row + (x * 4);
                byte alpha = pixel[3];
                int offset = x * 4;

                if (alpha is 0 or 255)
                {
                    target[offset] = pixel[0];
                    target[offset + 1] = pixel[1];
                    target[offset + 2] = pixel[2];
                    target[offset + 3] = alpha;
                    continue;
                }

                target[offset] = (byte)Math.Min(255, (pixel[0] * 255 + (alpha / 2)) / alpha);
                target[offset + 1] = (byte)Math.Min(255, (pixel[1] * 255 + (alpha / 2)) / alpha);
                target[offset + 2] = (byte)Math.Min(255, (pixel[2] * 255 + (alpha / 2)) / alpha);
                target[offset + 3] = alpha;
            }
        }
    }

    /// <summary>
    /// 直通 RGBA32 → 位图（预乘）。同样交给 Skia 做 alpha 类型转换，失败回退标量。
    /// </summary>
    public unsafe void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride)
    {
        var target = (SimdSkiaFrame)frame;
        SKBitmap bitmap = target.Bitmap;

        var sourceInfo = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        fixed (byte* pointer = source)
        {
            using var pixmap = new SKPixmap(sourceInfo, (IntPtr)pointer, sourceStride);
            if (pixmap.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes))
            {
                return;
            }
        }

        ImportPixelsScalar(bitmap, source, sourceStride);
    }

    private static unsafe void ImportPixelsScalar(SKBitmap bitmap, ReadOnlySpan<byte> source, int sourceStride)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        int rowBytes = bitmap.RowBytes;
        int width = bitmap.Width;
        int height = bitmap.Height;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = source.Slice(y * sourceStride, width * 4);
            byte* destination = pixels + (y * rowBytes);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                byte alpha = row[offset + 3];
                byte* pixel = destination + offset;

                if (alpha is 0 or 255)
                {
                    pixel[0] = row[offset];
                    pixel[1] = row[offset + 1];
                    pixel[2] = row[offset + 2];
                    pixel[3] = alpha;
                    continue;
                }

                pixel[0] = (byte)((row[offset] * alpha + 127) / 255);
                pixel[1] = (byte)((row[offset + 1] * alpha + 127) / 255);
                pixel[2] = (byte)((row[offset + 2] * alpha + 127) / 255);
                pixel[3] = alpha;
            }
        }
    }

    public bool TryAcquirePixels(IFrame frame, out PixelLease lease)
    {
        var target = (SimdSkiaFrame)frame;
        int stride = target.Bitmap.Width * 4;
        int length = checked(stride * target.Bitmap.Height);
        var buffer = new byte[length];
        TryCopyPixels(frame, buffer, stride);

        lease = new PixelLease(buffer, stride, () => ImportPixels(target, buffer, stride));
        return true;
    }

    /// <summary>
    /// 直接扫位图本身（本后端统一是 <c>Rgba8888</c>，alpha 在每像素第 4 字节）：
    /// 预乘只动 RGB、不动 alpha 的值，所以判断"有没有透明"完全不用导出 / 反预乘。
    /// </summary>
    /// <remarks>
    /// 不碰 <c>SKBitmap.Handle</c>：SimdSkia 是纯托管实现，取原生句柄会直接抛
    /// <c>NotSupportedException</c>（SkiaSharp 适配里用 <c>Handle == IntPtr.Zero</c> 判空，这里不成立）。
    /// </remarks>
    public bool TryGetTransparency(IFrame frame, out bool hasTransparency)
    {
        hasTransparency = false;

        SKBitmap bitmap = ((SimdSkiaFrame)frame).Bitmap;
        if (bitmap.ColorType != SKColorType.Rgba8888)
        {
            return false;   // 别的颜色布局不猜，交给门面走 TryAcquirePixels 扫
        }

        IntPtr pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            return false;
        }

        unsafe
        {
            hasTransparency = HasNonOpaquePixel((byte*)pixels, bitmap.RowBytes, bitmap.Width, bitmap.Height);
        }

        return true;
    }

    private static unsafe bool HasNonOpaquePixel(byte* pixels, int rowBytes, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            byte* row = pixels + ((long)y * rowBytes);
            for (int x = 3; x < width * 4; x += 4)
            {
                if (row[x] != 255)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        // 缓存里只会有本后端自己创建的 typeface；SKTypeface.Default 从不入缓存（见 ResolveTypeface）
        foreach (SKTypeface typeface in _typefaces.Values)
        {
            typeface.Dispose();
        }

        _typefaces.Clear();
    }

    // ---- 内部 ----

    /// <summary>
    /// 把托管字节交给 Skia：用固定指针直接拷贝，省掉 <c>data.ToArray()</c> 那一份中间副本
    /// （4K JPEG 是 8 MB 级别的白拷）。
    /// </summary>
    private static unsafe SKData CreateData(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            return SKData.CreateCopy((IntPtr)pointer, (ulong)data.Length);
        }
    }

    /// <summary>把直通 RGBA（非预乘、行紧密）的像素转成 <see cref="SimdSkiaFrame"/>，供 <see cref="Decode"/> 派生实现复用。</summary>
    protected IFrame Import(ReadOnlySpan<byte> straight, int width, int height)
    {
        var frame = (SimdSkiaFrame)Create(width, height, background: null);
        try
        {
            ImportPixels(frame, straight, width * 4);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static byte[] ExportStraight(SKBitmap bitmap, out int stride)
    {
        stride = bitmap.Width * 4;
        var buffer = new byte[checked(stride * bitmap.Height)];
        CopyStraight(bitmap, buffer, stride);
        return buffer;
    }

    /// <summary>
    /// 解析字体引用，结果全部按 <see cref="FontRef"/> 缓存、由本后端统一释放。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 自备字体（<see cref="FontRef.Data"/> / <see cref="FontRef.FilePath"/>）以前每次绘制都重建
    /// <see cref="SKTypeface"/>（整表解析），视频叠加这类逐帧画字的场景等于按帧付费。现在统一走缓存。
    /// </para>
    /// <para>
    /// 唯一不入缓存的是 <see cref="SKTypeface.Default"/>：它归 Skia 所有，一旦进了字典，
    /// <see cref="Dispose"/> 会把它释放掉，进程内其他使用方随后拿到已释放对象。
    /// </para>
    /// </remarks>
    private SKTypeface ResolveTypeface(FontRef reference)
    {
        if (_typefaces.TryGetValue(reference, out SKTypeface? cached) && cached is not null)
        {
            return cached;
        }

        SKTypeface? created = CreateTypeface(reference);
        if (created is null)
        {
            return SKTypeface.Default;
        }

        SKTypeface stored = _typefaces.GetOrAdd(reference, created);
        if (!ReferenceEquals(stored, created))
        {
            created.Dispose();   // 并发下别家先进了缓存，丢掉自己这份
        }

        return stored;
    }

    private static SKTypeface? CreateTypeface(FontRef reference)
    {
        if (reference.Data is { } data)
        {
            return CreateFromBytes(data.Span);
        }

        if (reference.FilePath is { } path)
        {
            try
            {
                // 不用 SKTypeface.FromFile：它会一直持有文件句柄，缓存期间字体文件就无法更新 / 删除。
                // 读一次字节（几 MB）换句柄释放，注册后不再重复读。
                return CreateFromBytes(File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件不可读：退回内置字体（与 FromFile 返回 null 的行为一致）
            }
        }

        return CreateFromBytes(BuiltInFont.Data.Span);
    }

    private static unsafe SKTypeface? CreateFromBytes(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* pointer = bytes)
        {
            using SKData data = SKData.CreateCopy((IntPtr)pointer, (ulong)bytes.Length);
            return SKTypeface.FromData(data);
        }
    }

    /// <summary>
    /// 折线 / 多边形路径。用 <c>SKPath</c> 的写 API：3.x 的标准写法；
    /// 4.x 把它标记为过时但仍可用，因此按 3.x 编译的包在 4.x 上照常运行。
    /// </summary>
    private static SKPath BuildPath(ReadOnlySpan<PointF> points, bool closed)
    {
        var path = new SKPath();
        path.MoveTo(points[0].X, points[0].Y);
        for (int i = 1; i < points.Length; i++)
        {
            path.LineTo(points[i].X, points[i].Y);
        }

        if (closed)
        {
            path.Close();
        }

        return path;
    }

    private static SKColor ToColor(Rgba32 color) => new(color.R, color.G, color.B, color.A);

    private static SKRect ToRect(RectF rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static SKPaint FillPaint(Rgba32 color, bool antialias) => new()
    {
        Color = ToColor(color),
        IsAntialias = antialias,
        Style = SKPaintStyle.Fill,
    };

    private static SKPaint StrokePaint(Rgba32 color, float width, StrokeStyle? style)
    {
        StrokeStyle resolved = style ?? StrokeStyle.Default;
        var paint = new SKPaint
        {
            Color = ToColor(color),
            IsAntialias = resolved.Antialias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeCap = resolved.Cap switch
            {
                LineCap.Round => SKStrokeCap.Round,
                LineCap.Square => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt,
            },
            StrokeJoin = resolved.Join switch
            {
                LineJoin.Round => SKStrokeJoin.Round,
                LineJoin.Bevel => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter,
            },
            StrokeMiter = resolved.MiterLimit,
        };

        if (resolved.HasDash)
        {
            float[] dashes = new float[resolved.DashPattern!.Length];
            for (int i = 0; i < dashes.Length; i++)
            {
                dashes[i] = resolved.DashPattern[i] * width;
            }

            paint.PathEffect = SKPathEffect.CreateDash(dashes, resolved.DashOffset * width);
        }

        return paint;
    }

    /// <summary>
    /// SimdSkia 的 CPU 缩放只实现最近邻与非 mipmap 的双线性：mipmap / 各向异性会直接抛
    /// <c>NotSupportedException</c>。所以三次与 Lanczos 退化到双线性，保证 <see cref="Sampler.Bicubic"/>
    /// 这条默认路径可用（SkiaSharp 适配里默认走的是 mipmap 线性）。
    /// </summary>
    private static SKSamplingOptions ToSampling(Sampler sampler) => sampler switch
    {
        Sampler.Nearest => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
        _ => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
    };

    private static ImageFormat MapFormat(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ImageFormat.Png,
        SKEncodedImageFormat.Jpeg => ImageFormat.Jpeg,
        SKEncodedImageFormat.Bmp => ImageFormat.Bmp,
        SKEncodedImageFormat.Gif => ImageFormat.Gif,
        SKEncodedImageFormat.Webp => ImageFormat.WebP,
        SKEncodedImageFormat.Ico => ImageFormat.Ico,
        _ => ImageFormat.Unknown,
    };

    private static bool IsFinite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static bool IsFinite(RectF rect)
        => float.IsFinite(rect.X) && float.IsFinite(rect.Y) && float.IsFinite(rect.Width) && float.IsFinite(rect.Height);

    private static bool AllFinite(ReadOnlySpan<PointF> points)
    {
        foreach (PointF point in points)
        {
            if (!IsFinite(point))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValid(float value) => value > 0f && float.IsFinite(value);
}

/// <summary>Skia 帧：位图 + 常驻画布。</summary>
/// <remarks>
/// 位图有两种来源：<see cref="SimdSkiaBackend.Create"/> 自己分配的 <c>Premul</c> 位图，和 <see cref="SimdSkiaBackend.TryWrapPixels"/>
/// 接管外部缓冲的 <c>Unpremul</c> 位图。<see cref="SKBitmap.Dispose"/> 只释放外壳，不释放外部内存。
/// </remarks>
internal sealed class SimdSkiaFrame : IFrame
{
    internal SimdSkiaFrame(SKBitmap bitmap)
    {
        Bitmap = bitmap;
        Canvas = new SKCanvas(bitmap);
    }

    internal SKBitmap Bitmap { get; }

    internal SKCanvas Canvas { get; }

    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;

    public void Dispose()
    {
        Canvas.Dispose();
        Bitmap.Dispose();
    }
}
