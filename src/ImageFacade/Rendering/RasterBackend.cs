using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using ImageFacade.Backends;
using ImageFacade.Text.Engine;

namespace ImageFacade.Rendering;

/// <summary>
/// 核心自带的光栅后端：只做建图 / 画 / 交像素，<b>不带任何编解码</b>。
/// 主包单独引用时由 <see cref="ImageEngine"/> 的无参构造使用；其他后端（软后端、OpenCV）
/// 通过内部可见性复用它的绘制实现。
/// </summary>
/// <remarks>
/// 帧内部是<b>预乘</b> Rgba32；直通与预乘的转换只发生在 <see cref="ImportPixels"/> /
/// <see cref="TryCopyPixels(IFrame, Span{byte}, int)"/> 这两个边界上。
/// </remarks>
internal sealed class RasterBackend : IImageBackend
{
    private readonly ConcurrentDictionary<FontRef, Font> _fonts = new();

    public string Name => "raster";

    public Version Version => typeof(RasterBackend).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public BackendCapabilities Capabilities =>
        BackendCapabilities.Resize
        | BackendCapabilities.DrawShapes
        | BackendCapabilities.DrawPoints
        | BackendCapabilities.DrawText
        | BackendCapabilities.DrawTextUnicode
        | BackendCapabilities.AlphaBlending
        | BackendCapabilities.Dash
        | BackendCapabilities.ExifOrientation
        | BackendCapabilities.AotCompatible;

    // 编解码：没有——核心不带任何 codec
    public IReadOnlySet<ImageFormat> SupportedDecodeFormats => FrozenSet<ImageFormat>.Empty;

    public IReadOnlySet<ImageFormat> SupportedEncodeFormats => FrozenSet<ImageFormat>.Empty;

    public IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
        => throw new FeatureNotSupportedException("Decode", Name);

    public void Encode(IFrame frame, Stream destination, in EncodeRequest request)
        => throw new FeatureNotSupportedException("Encode", Name);

    // ---- 生命周期 ----

    public IFrame Create(int width, int height, Rgba32? background)
        => new RasterFrame(Canvas.Create(width, height, background is { } color ? ToColor(color) : null));

    public bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame)
    {
        frame = null!;
        return false;
    }

    public IFrame Clone(IFrame frame)
    {
        var source = (RasterFrame)frame;
        return new RasterFrame(source.Canvas.Clone());
    }

    // ---- 几何 ----

    public bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result)
    {
        var source = (RasterFrame)frame;
        Canvas resized = source.Canvas.Resize(request.Width, request.Height, ToResampler(request.Sampler));
        result = new RasterFrame(resized);
        return true;
    }

    public bool TryDrawFrame(IFrame destination, IFrame source, ImageFacade.PointF offset)
    {
        var target = (RasterFrame)destination;
        var layer = (RasterFrame)source;
        int targetWidth = target.Canvas.Width;
        int targetHeight = target.Canvas.Height;
        int stride = targetWidth * 4;

        byte[] targetPixels = PixelBufferPool.Shared.Rent(stride * targetHeight);
        byte[] sourcePixels = PixelBufferPool.Shared.Rent(layer.Canvas.Width * 4 * layer.Canvas.Height);
        try
        {
            target.Canvas.CopyToStraight(targetPixels, stride);
            layer.Canvas.CopyToStraight(sourcePixels, layer.Canvas.Width * 4);

            Blend(
                sourcePixels,
                layer.Canvas.Width,
                layer.Canvas.Height,
                targetPixels,
                targetWidth,
                targetHeight,
                (int)MathF.Round(offset.X),
                (int)MathF.Round(offset.Y));

            Import(target.Canvas, targetPixels, stride);
            return true;
        }
        finally
        {
            PixelBufferPool.Shared.Return(targetPixels);
            PixelBufferPool.Shared.Return(sourcePixels);
        }
    }

    // ---- 绘制 ----

    public bool TryDrawPoint(IFrame frame, in PointSpec spec)
    {
        if (!IsFinite(spec.Point) || !IsValidRadius(spec.Radius))
        {
            return true; // 无效几何按"跳过"处理，不写坏像素
        }

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).Fill(
            ToColor(spec.Color),
            new CirclePolygon(new PointF(spec.Point.X, spec.Point.Y), spec.Radius));
        return true;
    }

    public bool TryDrawLine(IFrame frame, in LineSpec spec)
    {
        if (!IsFinite(spec.From) || !IsFinite(spec.To) || !IsValidWidth(spec.Width))
        {
            return true;
        }

        IPath path = new PathBuilder()
            .MoveTo(spec.From.X, spec.From.Y)
            .LineTo(spec.To.X, spec.To.Y)
            .Build();

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).Draw(CreatePen(spec.Color, spec.Width, spec.Style), path);
        return true;
    }

    public bool TryDrawPolyline(IFrame frame, in PolylineSpec spec)
    {
        if (spec.Points.Length < 2 || !IsValidWidth(spec.Width) || !AllFinite(spec.Points))
        {
            return true;
        }

        var builder = new PathBuilder();
        builder.MoveTo(spec.Points[0].X, spec.Points[0].Y);
        for (int i = 1; i < spec.Points.Length; i++)
        {
            builder.LineTo(spec.Points[i].X, spec.Points[i].Y);
        }

        if (spec.Closed)
        {
            builder.CloseFigure();
        }

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).Draw(CreatePen(spec.Color, spec.Width, spec.Style), builder.Build());
        return true;
    }

    public bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec)
    {
        if (!IsValidWidth(spec.Width) || spec.Rect.IsEmpty || !IsFinite(spec.Rect))
        {
            return true;
        }

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).Draw(
            CreatePen(spec.Color, spec.Width, spec.Style),
            new RectangularPolygon(spec.Rect.X, spec.Rect.Y, spec.Rect.Width, spec.Rect.Height));
        return true;
    }

    public bool TryFillRectangle(IFrame frame, in FilledRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect))
        {
            return true;
        }

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).Fill(
            ToColor(spec.Color),
            new RectangularPolygon(spec.Rect.X, spec.Rect.Y, spec.Rect.Width, spec.Rect.Height));
        return true;
    }

    public bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec)
    {
        if (!IsValidWidth(spec.Width) || spec.Bounds.IsEmpty || !IsFinite(spec.Bounds))
        {
            return true;
        }

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).Draw(
            CreatePen(spec.Color, spec.Width, spec.Style),
            CreateEllipse(spec.Bounds));
        return true;
    }

    public bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds))
        {
            return true;
        }

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).Fill(ToColor(spec.Color), CreateEllipse(spec.Bounds));
        return true;
    }

    public bool TryFillPolygon(IFrame frame, in PolygonSpec spec)
    {
        if (spec.Points.Length < 3 || !AllFinite(spec.Points))
        {
            return true;
        }

        var builder = new PathBuilder();
        builder.MoveTo(spec.Points[0].X, spec.Points[0].Y);
        for (int i = 1; i < spec.Points.Length; i++)
        {
            builder.LineTo(spec.Points[i].X, spec.Points[i].Y);
        }

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).Fill(ToColor(spec.Color), builder.CloseFigure().Build());
        return true;
    }

    // ---- 文字 ----

    public bool TryDrawText(IFrame frame, in TextSpec spec)
    {
        if (spec.Text.Length == 0 || !IsFinite(spec.Origin))
        {
            return true;
        }

        // FontFace 优先：调用方传了已加载的字体就直接复用，不再走 FontRef 解析/缓存
        Font font = spec.Style.FontFace ?? ResolveFont(spec.Style.Font);
        float baselineY = spec.Style.Anchor == TextAnchor.Baseline
            ? spec.Origin.Y
            : spec.Origin.Y + font.GetAscent(spec.Style.Size);

        // 折行与对齐已由门面完成：这里只画一行
        var options = new TextOptions
        {
            Font = font,
            Size = spec.Style.Size,
            Color = ToColor(spec.Style.Color),
            LineHeight = spec.Style.LineHeight,
            MaxWidth = 0f,
            Align = TextAlign.Left,
        };

        var target = (RasterFrame)frame;
        new DrawingContext(target.Canvas).DrawText(spec.Text, new PointF(spec.Origin.X, baselineY), options);
        return true;
    }

    public bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
        => TryMeasureTextCore(spec.Style, spec.Text.AsSpan(), out metrics);

    /// <summary>切片版：托管字体栈的 <see cref="Font.Measure(ReadOnlySpan{char}, float)"/> 逐码点累加，直接吃切片。</summary>
    public bool TryMeasureText(IFrame frame, ReadOnlySpan<char> text, in TextStyle style, out TextMetrics metrics)
        => TryMeasureTextCore(style, text, out metrics);

    private bool TryMeasureTextCore(TextStyle style, ReadOnlySpan<char> text, out TextMetrics metrics)
    {
        Font font = style.FontFace ?? Font.BuiltIn;
        try
        {
            if (style.FontFace is null)
            {
                font = ResolveFont(style.Font);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            metrics = default;
            return false;
        }

        float size = style.Size;
        float ascent = font.GetAscent(size);
        float descent = MathF.Abs(font.GetDescent(size));
        float lineHeight = font.GetLineHeight(size);
        float gap = MathF.Max(0f, lineHeight - ascent - descent);

        metrics = new TextMetrics(font.Measure(text, size), ascent, descent, gap);
        return true;
    }

    // ---- 像素 ----

    public void Clear(IFrame frame, Rgba32 color)
    {
        var target = (RasterFrame)frame;
        target.Canvas.Clear(ToColor(color));
    }

    public bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color)
    {
        var target = (RasterFrame)frame;
        Color value = target.Canvas.GetColor(x, y);
        color = new Rgba32(value.R, value.G, value.B, value.A);
        return true;
    }

    public bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride)
    {
        var source = (RasterFrame)frame;
        source.Canvas.CopyToStraight(destination, destinationStride);
        return true;
    }

    /// <summary>
    /// 带着门面给的 alpha 提示导出：<c>assumeOpaque == true</c>（门面查过，整图 alpha 全 255）时
    /// <see cref="Canvas.CopyToStraight(Span{byte}, int, bool?)"/> 不再顺手判透明、也不做逐像素反预乘
    /// （全不透明时它本身就是一次整块拷贝）。<c>null</c> 交回自动探测，结果完全一致。
    /// </summary>
    public bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride, bool? assumeOpaque)
    {
        var source = (RasterFrame)frame;
        source.Canvas.CopyToStraight(destination, destinationStride, assumeOpaque);
        return true;
    }

    /// <summary>
    /// 直接扫 Canvas 自己那块内存（预乘不改 alpha 的值），不走 <see cref="TryCopyPixels(IFrame, Span{byte}, int)"/>：
    /// 后者要整图反预乘一遍，为了判断"有没有透明"再搬一次纯属浪费。
    /// </summary>
    public bool TryGetTransparency(IFrame frame, out bool hasTransparency)
    {
        hasTransparency = !((RasterFrame)frame).Canvas.IsFullyOpaque();
        return true;
    }

    public void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride)
    {
        var target = (RasterFrame)frame;
        Import(target.Canvas, source, sourceStride);
    }

    public bool TryAcquirePixels(IFrame frame, out PixelLease lease)
    {
        var target = (RasterFrame)frame;
        int width = target.Canvas.Width;
        int height = target.Canvas.Height;
        int stride = width * 4;
        byte[] buffer = PixelBufferPool.Shared.Rent(stride * height);
        target.Canvas.CopyToStraight(buffer, stride);

        Canvas canvas = target.Canvas;
        int returned = 0;
        lease = new PixelLease(
            buffer.AsSpan(0, stride * height),
            stride,
            () =>
            {
                Import(canvas, buffer.AsSpan(0, stride * height), stride);

                // 第二层保护：PixelLease 被按值复制后各自的 _disposed 标志互相独立，
                // 只有这个引用捕获的计数器能保证缓冲只还回池子一次。
                if (Interlocked.Exchange(ref returned, 1) == 0)
                {
                    PixelBufferPool.Shared.Return(buffer);
                }
            });
        return true;
    }

    public void Dispose()
    {
    }

    // ---- 转换 ----

    private static void Import(Canvas canvas, ReadOnlySpan<byte> straight, int sourceStride)
    {
        int rowBytes = canvas.Width * 4;
        if (sourceStride == rowBytes && canvas.Stride == rowBytes)
        {
            PixelOps.Premultiply(straight[..(rowBytes * canvas.Height)], canvas.Buffer);
            return;
        }

        for (int y = 0; y < canvas.Height; y++)
        {
            ReadOnlySpan<byte> sourceRow = straight.Slice(y * sourceStride, rowBytes);
            PixelOps.Premultiply(sourceRow, canvas.GetRow(y)[..rowBytes]);
        }
    }

    private static void Blend(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        int placeX,
        int placeY)
    {
        int startX = Math.Max(0, placeX);
        int startY = Math.Max(0, placeY);
        int endX = Math.Min(destinationWidth, placeX + sourceWidth);
        int endY = Math.Min(destinationHeight, placeY + sourceHeight);

        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int s = (((y - placeY) * sourceWidth) + (x - placeX)) * 4;
                int d = ((y * destinationWidth) + x) * 4;
                byte alpha = source[s + 3];
                if (alpha == 0)
                {
                    continue;
                }

                if (alpha == 255)
                {
                    destination[d] = source[s];
                    destination[d + 1] = source[s + 1];
                    destination[d + 2] = source[s + 2];
                    destination[d + 3] = 255;
                    continue;
                }

                int inverse = 255 - alpha;
                destination[d] = (byte)(((source[s] * alpha) + (destination[d] * inverse) + 127) / 255);
                destination[d + 1] = (byte)(((source[s + 1] * alpha) + (destination[d + 1] * inverse) + 127) / 255);
                destination[d + 2] = (byte)(((source[s + 2] * alpha) + (destination[d + 2] * inverse) + 127) / 255);
                destination[d + 3] = (byte)Math.Min(255, alpha + ((destination[d + 3] * inverse) / 255));
            }
        }
    }

    private static Color ToColor(Rgba32 color) => Color.FromRgba(color.R, color.G, color.B, color.A);

    private static IPath CreateEllipse(ImageFacade.RectF bounds)
        => new EllipsePolygon(
            bounds.X + (bounds.Width / 2f),
            bounds.Y + (bounds.Height / 2f),
            bounds.Width,
            bounds.Height);

    private static Pen CreatePen(Rgba32 color, float width, ImageFacade.StrokeStyle? style)
    {
        ImageFacade.StrokeStyle resolved = style ?? ImageFacade.StrokeStyle.Default;
        return new Pen(ToColor(color), width)
        {
            LineCap = resolved.Cap switch
            {
                ImageFacade.LineCap.Square => LineCap.Square,
                ImageFacade.LineCap.Round => LineCap.Round,
                _ => LineCap.Butt,
            },
            LineJoin = resolved.Join switch
            {
                ImageFacade.LineJoin.Bevel => LineJoin.Bevel,
                ImageFacade.LineJoin.Miter => LineJoin.Miter,
                _ => LineJoin.Round,
            },
            MiterLimit = resolved.MiterLimit,
            DashPattern = resolved.DashPattern,
            DashOffset = resolved.DashOffset,
        };
    }

    private static IResampler ToResampler(Sampler sampler) => sampler switch
    {
        Sampler.Nearest => KnownResamplers.NearestNeighbour,
        Sampler.Box => KnownResamplers.Box,
        Sampler.Bilinear => KnownResamplers.Bilinear,
        Sampler.Bicubic => KnownResamplers.Bicubic,
        Sampler.Lanczos2 => KnownResamplers.Lanczos2,
        Sampler.Lanczos3 => KnownResamplers.Lanczos3,
        Sampler.MitchellNetravali => KnownResamplers.MitchellNetravali,
        Sampler.CatmullRom => KnownResamplers.CatmullRom,
        _ => KnownResamplers.Bicubic,
    };

    private Font ResolveFont(FontRef reference)
    {
        if (reference.IsBuiltIn)
        {
            return Font.BuiltIn;
        }

        return _fonts.GetOrAdd(reference, static font =>
        {
            if (font.Data is { } data)
            {
                return Font.Load(data.ToArray());
            }

            if (font.FilePath is { } path)
            {
                return Font.LoadFromFile(path);
            }

            // 复制进来的渲染层没有系统字体管理器：指定字体族时统一退回内置字体（misc §Z）
            return Font.BuiltIn;
        });
    }

    private static bool IsFinite(ImageFacade.PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static bool IsFinite(ImageFacade.RectF rect)
        => float.IsFinite(rect.X) && float.IsFinite(rect.Y) && float.IsFinite(rect.Width) && float.IsFinite(rect.Height);

    private static bool AllFinite(ReadOnlySpan<ImageFacade.PointF> points)
    {
        foreach (ImageFacade.PointF point in points)
        {
            if (!IsFinite(point))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidWidth(float width) => width > 0f && float.IsFinite(width);

    private static bool IsValidRadius(float radius) => radius > 0f && float.IsFinite(radius);
}

/// <summary>核心光栅的帧：包一个 <see cref="Canvas"/>（**预乘** Rgba32）。</summary>
internal sealed class RasterFrame : IFrame
{
    internal RasterFrame(Canvas canvas) => Canvas = canvas;

    internal Canvas Canvas { get; }

    public int Width => Canvas.Width;

    public int Height => Canvas.Height;

    public void Dispose() => Canvas.Dispose();
}
