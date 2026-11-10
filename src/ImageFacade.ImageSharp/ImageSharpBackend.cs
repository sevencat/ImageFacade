using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ImageFacade.Backends;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using SixColor = SixLabors.ImageSharp.Color;
using SixFont = SixLabors.Fonts.Font;
using SixFontCollection = SixLabors.Fonts.FontCollection;
using SixFontFamily = SixLabors.Fonts.FontFamily;
using SixFontMetrics = SixLabors.Fonts.FontMetrics;
using SixFontRectangle = SixLabors.Fonts.FontRectangle;
using SixFontStyle = SixLabors.Fonts.FontStyle;
using SixGraphicsOptions = SixLabors.ImageSharp.GraphicsOptions;
using SixHorizontalMetrics = SixLabors.Fonts.HorizontalMetrics;
using SixImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using SixPixel = SixLabors.ImageSharp.PixelFormats.Rgba32;
using SixPointF = SixLabors.ImageSharp.PointF;
using SixRectF = SixLabors.ImageSharp.RectangleF;
using SixResizeMode = SixLabors.ImageSharp.Processing.ResizeMode;
using SixResizeOptions = SixLabors.ImageSharp.Processing.ResizeOptions;
using SixSize = SixLabors.ImageSharp.Size;
using SixSizeF = SixLabors.ImageSharp.SizeF;
using SixSystemFonts = SixLabors.Fonts.SystemFonts;
using SixTextMeasurer = SixLabors.Fonts.TextMeasurer;
using SixTextOptions = SixLabors.Fonts.TextOptions;

namespace ImageFacade.ImageSharp;

/// <summary>
/// SixLabors.ImageSharp 适配后端（纯托管，服务端/批处理定位）。
/// </summary>
/// <remarks>
/// <para>帧 = <see cref="SixImage"/>；绘制走 <c>Mutate</c> + ImageSharp.Drawing 的 <c>Fill</c>/<c>Draw</c> 扩展。</para>
/// <para>
/// 同一份源码被 <c>ImageFacade.ImageSharp</c>（IMAGESHARP3：ImageSharp 3.1.12 + Drawing 2.1.3）与
/// <c>ImageFacade.ImageSharp2</c>（IMAGESHARP2：ImageSharp 2.1.13 + Drawing 1.0.0）链接编译。
/// 实测两套 Drawing 的目标 API（<c>Fill</c>/<c>Draw</c>/<c>DrawLine</c>/<c>DrawText</c>/<c>Pen</c>/<c>RichTextOptions</c>）
/// 完全一致，两套 ImageSharp 的像素与 <c>Resize</c>/<c>AutoOrient</c> API 也一致，因此这里不需要 <c>#if</c> 分叉；
/// 一旦升级主版本编译报错，把差异收敛到 <c>#if IMAGESHARP2</c> 即可。
/// </para>
/// </remarks>
public sealed class ImageSharpBackend : IImageBackend
{
    private static readonly IReadOnlySet<ImageFormat> Formats = new HashSet<ImageFormat>
    {
        ImageFormat.Png,
        ImageFormat.Jpeg,
        ImageFormat.Bmp,
        ImageFormat.Gif,
        ImageFormat.WebP,
        ImageFormat.Tiff,
    };

    private readonly ConcurrentDictionary<FontRef, Lazy<SixFontFamily>> _families = new();
    private readonly ConcurrentDictionary<FontKey, SixFont> _fonts = new();

    private SixFontFamily? _builtIn;

    public string Name => "imagesharp";

    public Version Version => typeof(ImageSharpBackend).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public BackendCapabilities Capabilities =>
        BackendCapabilities.Resize
        | BackendCapabilities.DrawShapes
        | BackendCapabilities.DrawPoints
        | BackendCapabilities.DrawText
        | BackendCapabilities.DrawTextUnicode
        | BackendCapabilities.AlphaBlending
        | BackendCapabilities.Dash
        | BackendCapabilities.LossyEncode
        | BackendCapabilities.ExifOrientation;

    public IReadOnlySet<ImageFormat> SupportedDecodeFormats => Formats;

    public IReadOnlySet<ImageFormat> SupportedEncodeFormats => Formats;

    // ---- 生命周期 ----

    public IFrame Create(int width, int height, Rgba32? background)
    {
        var image = background is { } color
            ? new SixImage(width, height, ToPixel(color))
            : new SixImage(width, height);
        return new ImageSharpFrame(image);
    }

    public bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame)
    {
        // Image<TPixel> 的内存由 MemoryAllocator 管理，无法接管外部指针
        frame = null!;
        return false;
    }

    public IFrame Clone(IFrame frame)
    {
        var source = (ImageSharpFrame)frame;
        return new ImageSharpFrame(source.Image.Clone());
    }

    public IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
    {
        ImageFormat format = ImageFormat.Unknown;
        SixImage image;
        try
        {
            format = MapFormat(SixLabors.ImageSharp.Image.DetectFormat(data).Name);
            image = SixLabors.ImageSharp.Image.Load<SixPixel>(data);
        }
        catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException or NotSupportedException)
        {
            throw new ImageBackendException(Name, $"解码失败：{ex.Message}", ex);
        }

        int orientation = ReadOrientation(image);
        if (request.AutoOrient && orientation is > 1 and <= 8)
        {
            image.Mutate(static context => context.AutoOrient());
        }

        metadata = new ImageMetadata(format, image.Width, image.Height, orientation);
        return new ImageSharpFrame(image);
    }

    public void Encode(IFrame frame, Stream destination, in EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var source = (ImageSharpFrame)frame;
        try
        {
            source.Image.Save(destination, CreateEncoder(request));
        }
        catch (Exception ex) when (ex is not (IOException or OutOfMemoryException or OperationCanceledException))
        {
            throw new ImageBackendException(Name, $"编码为 {request.Format} 失败：{ex.Message}", ex);
        }
    }

    // ---- 几何 ----

    public bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result)
    {
        var source = (ImageSharpFrame)frame;
        var size = new SixSize(request.Width, request.Height);
        IResampler sampler = MapSampler(request.Sampler);
        SixImage resized = source.Image.Clone(context => context.Resize(new SixResizeOptions
        {
            Size = size,
            Mode = SixResizeMode.Stretch,
            Sampler = sampler,
        }));

        result = new ImageSharpFrame(resized);
        return true;
    }

    public bool TryDrawFrame(IFrame destination, IFrame source, PointF offset)
    {
        var target = (ImageSharpFrame)destination;
        var layer = (ImageSharpFrame)source;

        if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
        {
            return true;
        }

        int offsetX = (int)MathF.Round(offset.X);
        int offsetY = (int)MathF.Round(offset.Y);
        int layerWidth = layer.Image.Width;
        int layerHeight = layer.Image.Height;
        int layerStride = layerWidth * 4;

        // 先导成连续直通像素：ProcessPixelRows 的回调里不能捕获 Span，拷贝一次最简单
        var pixels = new byte[checked(layerStride * layerHeight)];
        ExportStraight(layer.Image, pixels, layerStride);

        target.Image.ProcessPixelRows(accessor =>
        {
            int startX = Math.Max(0, -offsetX);
            int endX = Math.Min(layerWidth, accessor.Width - offsetX);

            for (int y = 0; y < layerHeight; y++)
            {
                int targetY = y + offsetY;
                if (targetY < 0 || targetY >= accessor.Height || startX >= endX)
                {
                    continue;
                }

                Span<SixPixel> row = accessor.GetRowSpan(targetY);
                int sourceRow = y * layerStride;
                for (int x = startX; x < endX; x++)
                {
                    int index = sourceRow + (x * 4);
                    byte alpha = pixels[index + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    int targetX = x + offsetX;
                    if (alpha == 255)
                    {
                        row[targetX] = new SixPixel(pixels[index], pixels[index + 1], pixels[index + 2], 255);
                        continue;
                    }

                    SixPixel under = row[targetX];
                    float a = alpha / 255f;
                    row[targetX] = new SixPixel(
                        Blend(pixels[index], under.R, a),
                        Blend(pixels[index + 1], under.G, a),
                        Blend(pixels[index + 2], under.B, a),
                        255);
                }
            }
        });

        return true;
    }

    // ---- 绘制 ----

    public bool TryDrawPoint(IFrame frame, in PointSpec spec)
    {
        if (!IsFinite(spec.Point) || !IsValid(spec.Radius))
        {
            return true;
        }

        var target = (ImageSharpFrame)frame;
        var circle = new EllipsePolygon(ToPoint(spec.Point), spec.Radius);
        DrawingOptions options = DrawOptions(spec.Antialias);
        SixColor color = ToColor(spec.Color);
        target.Image.Mutate(context => context.Fill(options, color, circle));
        return true;
    }

    public bool TryDrawLine(IFrame frame, in LineSpec spec)
    {
        if (!IsFinite(spec.From) || !IsFinite(spec.To) || !IsValid(spec.Width))
        {
            return true;
        }

        var target = (ImageSharpFrame)frame;
        var points = new[] { ToPoint(spec.From), ToPoint(spec.To) };
        Pen pen = BuildPen(spec.Color, spec.Width, spec.Style);
        DrawingOptions options = DrawOptions(spec.Style?.Antialias ?? true);
        target.Image.Mutate(context => context.DrawLine(options, pen, points));
        return true;
    }

    public bool TryDrawPolyline(IFrame frame, in PolylineSpec spec)
    {
        if (spec.Points.Length < 2 || !IsValid(spec.Width) || !AllFinite(spec.Points))
        {
            return true;
        }

        var points = ToPoints(spec.Points);
        var builder = new PathBuilder();
        builder.StartFigure();
        builder.AddLines(points);
        if (spec.Closed)
        {
            builder.CloseFigure();
        }

        IPath path = builder.Build();
        Pen pen = BuildPen(spec.Color, spec.Width, spec.Style);
        DrawingOptions options = DrawOptions(spec.Style?.Antialias ?? true);

        var target = (ImageSharpFrame)frame;
        target.Image.Mutate(context => context.Draw(options, pen, path));
        return true;
    }

    public bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect) || !IsValid(spec.Width))
        {
            return true;
        }

        var polygon = new RectangularPolygon(ToRect(spec.Rect));
        Pen pen = BuildPen(spec.Color, spec.Width, spec.Style);
        DrawingOptions options = DrawOptions(spec.Style?.Antialias ?? true);

        var target = (ImageSharpFrame)frame;
        target.Image.Mutate(context => context.Draw(options, pen, polygon));
        return true;
    }

    public bool TryFillRectangle(IFrame frame, in FilledRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect))
        {
            return true;
        }

        var polygon = new RectangularPolygon(ToRect(spec.Rect));
        DrawingOptions options = DrawOptions(antialias: false);
        SixColor color = ToColor(spec.Color);

        var target = (ImageSharpFrame)frame;
        target.Image.Mutate(context => context.Fill(options, color, polygon));
        return true;
    }

    public bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds) || !IsValid(spec.Width))
        {
            return true;
        }

        var ellipse = ToEllipse(spec.Bounds);
        Pen pen = BuildPen(spec.Color, spec.Width, spec.Style);
        DrawingOptions options = DrawOptions(spec.Style?.Antialias ?? true);

        var target = (ImageSharpFrame)frame;
        target.Image.Mutate(context => context.Draw(options, pen, ellipse));
        return true;
    }

    public bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds))
        {
            return true;
        }

        var ellipse = ToEllipse(spec.Bounds);
        DrawingOptions options = DrawOptions(antialias: true);
        SixColor color = ToColor(spec.Color);

        var target = (ImageSharpFrame)frame;
        target.Image.Mutate(context => context.Fill(options, color, ellipse));
        return true;
    }

    public bool TryFillPolygon(IFrame frame, in PolygonSpec spec)
    {
        if (spec.Points.Length < 3 || !AllFinite(spec.Points))
        {
            return true;
        }

        var polygon = new Polygon(ToPoints(spec.Points));
        DrawingOptions options = DrawOptions(antialias: true);
        SixColor color = ToColor(spec.Color);

        var target = (ImageSharpFrame)frame;
        target.Image.Mutate(context => context.Fill(options, color, polygon));
        return true;
    }

    // ---- 文字 ----

    public bool TryDrawText(IFrame frame, in TextSpec spec)
    {
        if (spec.Text.Length == 0 || !IsFinite(spec.Origin))
        {
            return true;
        }

        var target = (ImageSharpFrame)frame;
        SixFont font;
        try
        {
            font = ResolveFont(spec.Style.Font, spec.Style.Size);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return false;
        }

        // RichTextOptions.Origin 是排版框左上角（实测 3.1.12 + Drawing 2.1.3），Baseline 锚点自己减 ascent
        float baselineOffset = spec.Style.Anchor == TextAnchor.Baseline ? Ascent(font, spec.Style.Size) : 0f;
        var options = new RichTextOptions(font)
        {
            Dpi = 72f,
            Origin = new SixPointF(spec.Origin.X, spec.Origin.Y - baselineOffset),
            WrappingLength = -1f,
        };

        DrawingOptions drawingOptions = DrawOptions(spec.Style.Antialias);
        Brush brush = Brushes.Solid(ToColor(spec.Style.Color));
        string text = spec.Text;

        // 只有 brush + pen 的重载能带 DrawingOptions（v1 只填充不描边，pen 传 null）
        target.Image.Mutate(context => context.DrawText(drawingOptions, options, text, brush, null));
        return true;
    }

    public bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
    {
        try
        {
            SixFont font = ResolveFont(spec.Style.Font, spec.Style.Size);
            var options = new SixTextOptions(font) { Dpi = 72f, WrappingLength = -1f };
            SixFontRectangle advance = SixTextMeasurer.MeasureAdvance(spec.Text, options);

            SixFontMetrics fontMetrics = font.FontMetrics;
            float scale = fontMetrics.UnitsPerEm > 0 ? spec.Style.Size / fontMetrics.UnitsPerEm : 1f;
            SixHorizontalMetrics horizontal = fontMetrics.HorizontalMetrics;

            float ascent = horizontal.Ascender * scale;
            float descent = -horizontal.Descender * scale;
            float lineGap = MathF.Max(0f, horizontal.LineGap * scale);

            if (ascent <= 0f || descent <= 0f)
            {
                (ascent, descent) = FallbackMetrics(spec.Style.Size);
            }

            metrics = new TextMetrics(advance.Width, ascent, descent, lineGap);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            metrics = default;
            return false;
        }
    }

    // ---- 像素 ----

    public void Clear(IFrame frame, Rgba32 color)
    {
        var target = (ImageSharpFrame)frame;
        SixPixel pixel = ToPixel(color);
        target.Image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                accessor.GetRowSpan(y).Fill(pixel);
            }
        });
    }

    public bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color)
    {
        var target = (ImageSharpFrame)frame;
        SixPixel pixel = target.Image[x, y];
        color = new Rgba32(pixel.R, pixel.G, pixel.B, pixel.A);
        return true;
    }

    public bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride)
    {
        ExportStraight(((ImageSharpFrame)frame).Image, destination, destinationStride);
        return true;
    }

    public void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride)
    {
        var target = (ImageSharpFrame)frame;
        SixImage image = target.Image;
        int rowBytes = image.Width * 4;

        if (image.DangerousTryGetSinglePixelMemory(out Memory<SixPixel> memory))
        {
            Span<byte> destination = MemoryMarshal.AsBytes(memory.Span);
            for (int y = 0; y < image.Height; y++)
            {
                source.Slice(y * sourceStride, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
            }

            return;
        }

        // 多段内存：先落到连续缓冲（ProcessPixelRows 的回调里不能捕获 Span）
        var buffer = new byte[checked(rowBytes * image.Height)];
        for (int y = 0; y < image.Height; y++)
        {
            source.Slice(y * sourceStride, rowBytes).CopyTo(buffer.AsSpan(y * rowBytes, rowBytes));
        }

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                buffer.AsSpan(y * rowBytes, rowBytes).CopyTo(MemoryMarshal.AsBytes(accessor.GetRowSpan(y)));
            }
        });
    }

    public bool TryAcquirePixels(IFrame frame, out PixelLease lease)
    {
        var target = (ImageSharpFrame)frame;
        int stride = target.Image.Width * 4;
        var buffer = new byte[checked(stride * target.Image.Height)];
        TryCopyPixels(target, buffer, stride);

        lease = new PixelLease(buffer, stride, () => ImportPixels(target, buffer, stride));
        return true;
    }

    public void Dispose()
    {
        // FontFamily / Font 由 FontCollection 内部持有，不需要显式释放
        _fonts.Clear();
        _families.Clear();
    }

    // ---- 内部：像素 ----

    private static void ExportStraight(SixImage image, Span<byte> destination, int destinationStride)
    {
        int rowBytes = image.Width * 4;

        if (image.DangerousTryGetSinglePixelMemory(out Memory<SixPixel> memory))
        {
            ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(memory.Span);
            for (int y = 0; y < image.Height; y++)
            {
                source.Slice(y * rowBytes, rowBytes).CopyTo(destination.Slice(y * destinationStride, rowBytes));
            }

            return;
        }

        var buffer = new byte[checked(rowBytes * image.Height)];
        image.CopyPixelDataTo(buffer);
        for (int y = 0; y < image.Height; y++)
        {
            buffer.AsSpan(y * rowBytes, rowBytes).CopyTo(destination.Slice(y * destinationStride, rowBytes));
        }
    }

    private static byte Blend(byte source, byte destination, float alpha)
        => (byte)Math.Clamp(MathF.Round(source * alpha + destination * (1f - alpha)), 0f, 255f);

    // ---- 内部：字体 ----

    private SixFont ResolveFont(FontRef reference, float size)
        => _fonts.GetOrAdd(new FontKey(reference, size), key =>
        {
            SixFontFamily family = ResolveFamily(key.Reference);
            return new SixFont(family, key.Size, MapFontStyle(key.Reference.Style));
        });

    private SixFontFamily ResolveFamily(FontRef reference)
        => _families.GetOrAdd(
            reference,
            item => new Lazy<SixFontFamily>(() => LoadFamily(item), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private SixFontFamily LoadFamily(FontRef reference)
    {
        if (!reference.IsBuiltIn)
        {
            try
            {
                if (reference.Data is { } data)
                {
                    var collection = new SixFontCollection();
                    using var stream = new MemoryStream(data.ToArray(), writable: false);
                    return collection.Add(stream);
                }

                if (reference.FilePath is { } path && File.Exists(path))
                {
                    var collection = new SixFontCollection();
                    return collection.Add(path);
                }

                if (reference.FamilyName is { } name && SixSystemFonts.TryGet(name, out SixFontFamily system))
                {
                    return system;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
            {
                // 字体不可用（容器里没有系统字体、文件损坏）→ 整段回退内置字体（misc §J.3）
            }
        }

        return _builtIn ??= CreateBuiltInFamily();
    }

    private static SixFontFamily CreateBuiltInFamily()
    {
        var collection = new SixFontCollection();
        using var stream = new MemoryStream(BuiltInFont.Data.ToArray(), writable: false);
        return collection.Add(stream);
    }

    private static SixFontStyle MapFontStyle(ImageFacade.FontStyle style) => style switch
    {
        ImageFacade.FontStyle.Bold => SixFontStyle.Bold,
        ImageFacade.FontStyle.Italic => SixFontStyle.Italic,
        ImageFacade.FontStyle.BoldItalic => SixFontStyle.BoldItalic,
        _ => SixFontStyle.Regular,
    };

    private static float Ascent(SixFont font, float size)
    {
        SixFontMetrics metrics = font.FontMetrics;
        float scale = metrics.UnitsPerEm > 0 ? size / metrics.UnitsPerEm : 1f;
        float ascent = metrics.HorizontalMetrics.Ascender * scale;
        return ascent > 0f ? ascent : FallbackMetrics(size).Ascent;
    }

    private static (float Ascent, float Descent) FallbackMetrics(float size) => (size * 0.8f, size * 0.2f);

    // ---- 内部：绘制几何与颜色 ----

    private static DrawingOptions DrawOptions(bool antialias)
        => new() { GraphicsOptions = new SixGraphicsOptions { Antialias = antialias } };

    private static Pen BuildPen(Rgba32 color, float width, StrokeStyle? style)
    {
        StrokeStyle resolved = style ?? StrokeStyle.Default;
        SixColor penColor = ToColor(color);
        PenOptions options;

        if (resolved.HasDash)
        {
            float[] pattern = new float[resolved.DashPattern!.Length];
            for (int i = 0; i < pattern.Length; i++)
            {
                pattern[i] = resolved.DashPattern[i] * width;
            }

            options = new PenOptions(penColor, width, pattern);
        }
        else
        {
            options = new PenOptions(penColor, width);
        }

        // 虚线必须用 PatternPen：SolidPen 会忽略 StrokePattern（实测）

        options.EndCapStyle = resolved.Cap switch
        {
            LineCap.Round => EndCapStyle.Round,
            LineCap.Square => EndCapStyle.Square,
            _ => EndCapStyle.Butt,
        };

        // Drawing 没有 Bevel 连接（只有 Miter / Round / Square），按最接近的方角处理
        options.JointStyle = resolved.Join switch
        {
            LineJoin.Round => JointStyle.Round,
            LineJoin.Bevel => JointStyle.Square,
            _ => JointStyle.Miter,
        };

        return resolved.HasDash ? new PatternPen(options) : new SolidPen(options);
    }

    /// <summary>内切于 <paramref name="bounds"/> 的椭圆（<c>EllipsePolygon</c> 的 x/y 是圆心，尺寸才是外接框）。</summary>
    private static EllipsePolygon ToEllipse(RectF bounds)
        => new(ToPoint(bounds.Center), new SixSizeF(bounds.Width, bounds.Height));

    private static SixRectF ToRect(RectF rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static SixPointF ToPoint(PointF point) => new(point.X, point.Y);

    private static SixPointF[] ToPoints(ReadOnlySpan<PointF> points)
    {
        var result = new SixPointF[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            result[i] = ToPoint(points[i]);
        }

        return result;
    }

    private static SixColor ToColor(Rgba32 color) => SixColor.FromRgba(color.R, color.G, color.B, color.A);

    private static SixPixel ToPixel(Rgba32 color) => new(color.R, color.G, color.B, color.A);

    // ---- 内部：编解码 / 采样核 ----

    private static IImageEncoder CreateEncoder(in EncodeRequest request)
    {
        switch (request.Format)
        {
            case ImageFormat.Png:
                return request.CompressionLevel >= 0
                    ? new PngEncoder { CompressionLevel = (PngCompressionLevel)Math.Clamp(request.CompressionLevel, 0, 9) }
                    : new PngEncoder();
            case ImageFormat.Jpeg:
                return new JpegEncoder { Quality = Math.Clamp(request.Quality, 1, 100) };
            case ImageFormat.Bmp:
                return new BmpEncoder();
            case ImageFormat.Gif:
                return new GifEncoder();
            case ImageFormat.WebP:
                return new WebpEncoder { Quality = Math.Clamp(request.Quality, 1, 100) };
            case ImageFormat.Tiff:
                return new TiffEncoder();
            default:
                throw new NotSupportedException($"ImageSharpBackend 不支持编码 {request.Format}。");
        }
    }

    private static IResampler MapSampler(Sampler sampler) => sampler switch
    {
        Sampler.Nearest => KnownResamplers.NearestNeighbor,
        Sampler.Box => KnownResamplers.Box,
        Sampler.Bilinear => KnownResamplers.Triangle,
        Sampler.Bicubic => KnownResamplers.Bicubic,
        Sampler.Lanczos2 => KnownResamplers.Lanczos2,
        Sampler.Lanczos3 => KnownResamplers.Lanczos3,
        Sampler.MitchellNetravali => KnownResamplers.MitchellNetravali,
        Sampler.CatmullRom => KnownResamplers.CatmullRom,
        _ => KnownResamplers.Bicubic,
    };

    private static ImageFormat MapFormat(string name) => name.ToUpperInvariant() switch
    {
        "PNG" => ImageFormat.Png,
        "JPEG" => ImageFormat.Jpeg,
        "BMP" => ImageFormat.Bmp,
        "GIF" => ImageFormat.Gif,
        "WEBP" => ImageFormat.WebP,
        "TIFF" => ImageFormat.Tiff,
        _ => ImageFormat.Unknown,
    };

    private static int ReadOrientation(SixImage image)
    {
        try
        {
            var profile = image.Metadata.ExifProfile;
            if (profile is null)
            {
                return 0;
            }

#if IMAGESHARP2
            // 2.x 只有 GetValue（3.x 换成了 TryGetValue）
            var value = profile.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation);
            return value?.Value ?? 0;
#else
            return profile.TryGetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, out var value) && value is not null
                ? value.Value
                : 0;
#endif
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // 元数据损坏时按"没有方向信息"处理
        }

        return 0;
    }

    // ---- 内部：校验 ----

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

    private readonly record struct FontKey(FontRef Reference, float Size);
}

/// <summary>ImageSharp 帧：包一个 <see cref="SixImage"/>。</summary>
internal sealed class ImageSharpFrame : IFrame
{
    internal ImageSharpFrame(SixImage image) => Image = image;

    internal SixImage Image { get; }

    public int Width => Image.Width;

    public int Height => Image.Height;

    public void Dispose() => Image.Dispose();
}
