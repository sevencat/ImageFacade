using System.Buffers;
using System.Collections.Concurrent;
using ImageFacade.Backends;
using ImageFacade.SdcbSimdCodecs.Rendering;
namespace ImageFacade.SdcbSimdCodecs;

/// <summary>
/// 软后端（回退基准，也可独立当主角）：绘制 / Resize / 文字来自复制改造的 ImgProcSharp 渲染层，
/// 编解码来自 <see cref="ImageBackendBase"/>，但**本包不自带任何格式**——使用方引
/// <c>Sdcb.SimdCodecs.Xxx</c>（Png / Jpeg / Bmp / Gif / Webp / Tiff …）后调用
/// <see cref="ImageBackendBase.RegisterCodec"/> 注册，注册方式见 README「软后端：按需接入编解码」。
/// 完全不注册时，绘制 / Resize / 文字照常可用，只有 Load / Save 会抛
/// <see cref="FeatureNotSupportedException"/>。
/// </summary>
public sealed class SoftwareBackend : ImageBackendBase
{
    private readonly ConcurrentDictionary<FontRef, Font> _fonts = new();

    public override string Name => "software";

    public override Version Version => typeof(SoftwareBackend).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public override BackendCapabilities Capabilities =>
        BackendCapabilities.Resize
        | BackendCapabilities.DrawShapes
        | BackendCapabilities.DrawPoints
        | BackendCapabilities.DrawText
        | BackendCapabilities.DrawTextUnicode
        | BackendCapabilities.AlphaBlending
        | BackendCapabilities.Dash
        | BackendCapabilities.ExifOrientation
        | BackendCapabilities.LossyEncode
        | BackendCapabilities.AotCompatible;

    // ---- 生命周期 ----

    public override IFrame Create(int width, int height, Rgba32? background)
        => new SoftwareFrame(Canvas.Create(width, height, background is { } color ? ToColor(color) : null));

    public override IFrame Clone(IFrame frame)
    {
        var source = (SoftwareFrame)frame;
        return new SoftwareFrame(source.Canvas.Clone());
    }

    // ---- 几何 ----

    public override bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result)
    {
        var source = (SoftwareFrame)frame;
        Canvas resized = source.Canvas.Resize(request.Width, request.Height, ToResampler(request.Sampler));
        result = new SoftwareFrame(resized);
        return true;
    }

    public override bool TryDrawFrame(IFrame destination, IFrame source, PointF offset)
    {
        var target = (SoftwareFrame)destination;
        var layer = (SoftwareFrame)source;
        int targetWidth = target.Canvas.Width;
        int targetHeight = target.Canvas.Height;
        int stride = targetWidth * 4;

        byte[] targetPixels = ArrayPool<byte>.Shared.Rent(stride * targetHeight);
        byte[] sourcePixels = ArrayPool<byte>.Shared.Rent(layer.Canvas.Width * 4 * layer.Canvas.Height);
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
            ArrayPool<byte>.Shared.Return(targetPixels);
            ArrayPool<byte>.Shared.Return(sourcePixels);
        }
    }

    // ---- 绘制 ----

    public override bool TryDrawPoint(IFrame frame, in PointSpec spec)
    {
        if (!IsFinite(spec.Point) || !IsValidRadius(spec.Radius))
        {
            return true; // 无效几何按"跳过"处理，不写坏像素
        }

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).Fill(
            ToColor(spec.Color),
            new CirclePolygon(new Rendering.PointF(spec.Point.X, spec.Point.Y), spec.Radius));
        return true;
    }

    public override bool TryDrawLine(IFrame frame, in LineSpec spec)
    {
        if (!IsFinite(spec.From) || !IsFinite(spec.To) || !IsValidWidth(spec.Width))
        {
            return true;
        }

        IPath path = new PathBuilder()
            .MoveTo(spec.From.X, spec.From.Y)
            .LineTo(spec.To.X, spec.To.Y)
            .Build();

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).Draw(CreatePen(spec.Color, spec.Width, spec.Style), path);
        return true;
    }

    public override bool TryDrawPolyline(IFrame frame, in PolylineSpec spec)
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

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).Draw(CreatePen(spec.Color, spec.Width, spec.Style), builder.Build());
        return true;
    }

    public override bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec)
    {
        if (!IsValidWidth(spec.Width) || spec.Rect.IsEmpty || !IsFinite(spec.Rect))
        {
            return true;
        }

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).Draw(
            CreatePen(spec.Color, spec.Width, spec.Style),
            new RectangularPolygon(spec.Rect.X, spec.Rect.Y, spec.Rect.Width, spec.Rect.Height));
        return true;
    }

    public override bool TryFillRectangle(IFrame frame, in FilledRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect))
        {
            return true;
        }

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).Fill(
            ToColor(spec.Color),
            new RectangularPolygon(spec.Rect.X, spec.Rect.Y, spec.Rect.Width, spec.Rect.Height));
        return true;
    }

    public override bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec)
    {
        if (!IsValidWidth(spec.Width) || spec.Bounds.IsEmpty || !IsFinite(spec.Bounds))
        {
            return true;
        }

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).Draw(
            CreatePen(spec.Color, spec.Width, spec.Style),
            CreateEllipse(spec.Bounds));
        return true;
    }

    public override bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds))
        {
            return true;
        }

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).Fill(ToColor(spec.Color), CreateEllipse(spec.Bounds));
        return true;
    }

    public override bool TryFillPolygon(IFrame frame, in PolygonSpec spec)
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

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).Fill(ToColor(spec.Color), builder.CloseFigure().Build());
        return true;
    }

    // ---- 文字 ----

    public override bool TryDrawText(IFrame frame, in TextSpec spec)
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

        var target = (SoftwareFrame)frame;
        new DrawingContext(target.Canvas).DrawText(spec.Text, new Rendering.PointF(spec.Origin.X, baselineY), options);
        return true;
    }

    public override bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
        => TryMeasureTextCore(spec.Style, spec.Text.AsSpan(), out metrics);

    /// <summary>切片版：托管字体栈的 <see cref="Font.Measure(ReadOnlySpan{char}, float)"/> 逐码点累加，直接吃切片。</summary>
    public override bool TryMeasureText(IFrame frame, ReadOnlySpan<char> text, in TextStyle style, out TextMetrics metrics)
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

    public override void Clear(IFrame frame, Rgba32 color)
    {
        var target = (SoftwareFrame)frame;
        target.Canvas.Clear(ToColor(color));
    }

    public override bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color)
    {
        var target = (SoftwareFrame)frame;
        Rendering.Color value = target.Canvas.GetColor(x, y);
        color = new Rgba32(value.R, value.G, value.B, value.A);
        return true;
    }

    public override bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride)
    {
        var source = (SoftwareFrame)frame;
        source.Canvas.CopyToStraight(destination, destinationStride);
        return true;
    }

    /// <summary>
    /// 带着门面给的 alpha 提示导出：<c>assumeOpaque == true</c>（门面查过，整图 alpha 全 255）时
    /// <see cref="Canvas.CopyToStraight(Span{byte}, int, bool?)"/> 不再顺手判透明、也不做逐像素反预乘
    /// （全不透明时它本身就是一次整块拷贝）。<c>null</c> 交回自动探测，结果完全一致。
    /// </summary>
    public override bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride, bool? assumeOpaque)
    {
        var source = (SoftwareFrame)frame;
        source.Canvas.CopyToStraight(destination, destinationStride, assumeOpaque);
        return true;
    }

    /// <summary>
    /// 直接扫 Canvas 自己那块内存（预乘不改 alpha 的值），不走 <see cref="TryCopyPixels(IFrame, Span{byte}, int)"/>：
    /// 后者要整图反预乘一遍，为了判断"有没有透明"再搬一次纯属浪费。
    /// </summary>
    public override bool TryGetTransparency(IFrame frame, out bool hasTransparency)
    {
        hasTransparency = !((SoftwareFrame)frame).Canvas.IsFullyOpaque();
        return true;
    }

    public override void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride)
    {
        var target = (SoftwareFrame)frame;
        Import(target.Canvas, source, sourceStride);
    }

    public override bool TryAcquirePixels(IFrame frame, out PixelLease lease)
    {
        var target = (SoftwareFrame)frame;
        int width = target.Canvas.Width;
        int height = target.Canvas.Height;
        int stride = width * 4;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(stride * height);
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
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            });
        return true;
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

    private static Rendering.Color ToColor(Rgba32 color) => Rendering.Color.FromRgba(color.R, color.G, color.B, color.A);

    private static IPath CreateEllipse(RectF bounds)
        => new EllipsePolygon(bounds.Center.X, bounds.Center.Y, bounds.Width, bounds.Height);

    private static Pen CreatePen(Rgba32 color, float width, StrokeStyle? style)
    {
        StrokeStyle resolved = style ?? StrokeStyle.Default;
        return new Pen(ToColor(color), width)
        {
            LineCap = resolved.Cap switch
            {
                LineCap.Square => Rendering.LineCap.Square,
                LineCap.Round => Rendering.LineCap.Round,
                _ => Rendering.LineCap.Butt,
            },
            LineJoin = resolved.Join switch
            {
                LineJoin.Bevel => Rendering.LineJoin.Bevel,
                LineJoin.Miter => Rendering.LineJoin.Miter,
                _ => Rendering.LineJoin.Round,
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

    private static bool IsValidWidth(float width) => width > 0f && float.IsFinite(width);

    private static bool IsValidRadius(float radius) => radius > 0f && float.IsFinite(radius);
}
