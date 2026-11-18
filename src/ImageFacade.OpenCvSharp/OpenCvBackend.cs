using System.Collections.Concurrent;
using System.Runtime.Intrinsics;
using ImageFacade.Backends;
using GlyphLayout = ImageFacade.Text.Engine.TextLayout;
using GlyphMask = ImageFacade.Text.Engine.GlyphMask;
using GlyphPositioned = ImageFacade.Text.Engine.PositionedGlyph;
using Cv2 = OpenCvSharp.Cv2;
using CvColorConversion = OpenCvSharp.ColorConversionCodes;
using CvException = OpenCvSharp.OpenCvSharpException;
using CvImageEncodingParam = OpenCvSharp.ImageEncodingParam;
using CvImreadModes = OpenCvSharp.ImreadModes;
using CvImwriteFlags = OpenCvSharp.ImwriteFlags;
using CvInterpolationFlags = OpenCvSharp.InterpolationFlags;
using CvLineTypes = OpenCvSharp.LineTypes;
using CvMat = OpenCvSharp.Mat;
using CvMatType = OpenCvSharp.MatType;
using CvPoint = OpenCvSharp.Point;
using CvPoint2f = OpenCvSharp.Point2f;
using CvRect = OpenCvSharp.Rect;
using CvRotatedRect = OpenCvSharp.RotatedRect;
using CvScalar = OpenCvSharp.Scalar;
using CvSize = OpenCvSharp.Size;
using CvSize2f = OpenCvSharp.Size2f;
using CorePointF = ImageFacade.Rendering.PointF;
using CoreStrokeStyle = ImageFacade.Rendering.StrokeStyle;

namespace ImageFacade.OpenCvSharp;

/// <summary>
/// OpenCvSharp 适配后端（帧 = <c>CV_8UC4</c> 的 <see cref="CvMat"/>，通道顺序 **BGRA**、直通 alpha）。
/// </summary>
/// <remarks>
/// <para>
/// 通道顺序**刻意与门面相反**（BGRA vs RGBA）：OpenCV 的 <c>ImDecode</c> / <c>ImEncode</c> 只认
/// BGR / BGRA，内部若改存 RGBA，每次"打开"和"保存"都要多一次全图转换。代价只在
/// <c>FromPixels</c> / <c>CopyPixelsTo</c> 这一对边界上付（本来就要按 stride 逐行搬运，顺带换通道），
/// 且共用同一个 <c>SwapRedBlue</c>。Skia 用 <c>Rgba8888 Premul</c>、ImageSharp 用 <c>Image&lt;Rgba32&gt;</c>、
/// 软后端用预乘 <c>Rgba32</c>，它们原生就是 RGBA，所以**唯独 OpenCV 是 BGRA 的例外**。
/// </para>
/// <para>
/// 形状绘制：OpenCV 自己的 <c>Fill/Draw/Line</c> 是**覆盖写像素**，没有混合模式。半透明
/// （<c>color.A &lt; 255</c>）的请求因此不走它们直画目标，而是先在形状包围盒大小的 8 位全 0 层上照常画一遍
/// ——AA 边缘自然留下 0~255 的覆盖率——再按覆盖率把颜色逐像素混进目标，和半透明文字同一套公式。
/// 代价只是每个半透明形状一块包围盒大小的单通道缓冲；不透明形状仍直接画，一分钱不多花。
/// </para>
/// <para>
/// <b>虚线是支持的</b>：OpenCV 的 <c>Line/Polylines</c> 没有 dash 图案，这里按核心那套虚线切分规则
/// （<see cref="ImageFacade.Rendering.StrokeOutline.SplitDashes"/>，含 <c>DashOffset</c>）先把轮廓切成
/// 若干实线段，再逐段 <c>Polylines</c>。纯几何，不需要中间位图。
/// 端帽/连接方式仍由 OpenCV 决定（没有圆头/方头），和实线描边的现状一致。
/// </para>
/// <para>
/// 文字：<b>不用 <c>Cv2.PutText</c></b>（Hershey 只有 ASCII、外观差得远）。4/5 都复用核心
/// <see cref="ImageFacade.Font"/> 的"嵌入字体 + 自己解析 TrueType"光栅化，把 A8 覆盖位图按覆盖率
/// 混进 BGRA 的 <c>Mat</c>，中文与半透明文字都能画，不需要任何其他后端。
/// </para>
/// <para>
/// 同一份源码被 <c>ImageFacade.OpenCvSharp</c>（OPENCV4：OpenCvSharp4 4.13）与
/// <c>ImageFacade.OpenCvSharp5</c>（OPENCV5：OpenCvSharp5 5.0）链接编译。实测两代的目标 API
/// （<c>ImDecode/ImEncode/Resize/Line/Circle/Rectangle/Ellipse/FillPoly/Polylines</c>、<c>Mat</c> 的像素访问）
/// 签名一致，5.0 只是多了 <c>CvtColor</c> 的 <c>AlgorithmHint</c> 等可选参数，因此不需要 <c>#if</c> 分叉。
/// </para>
/// </remarks>
public sealed class OpenCvBackend : IImageBackend
{
    private static readonly IReadOnlySet<ImageFormat> Formats = new HashSet<ImageFormat>
    {
        ImageFormat.Png,
        ImageFormat.Jpeg,
        ImageFormat.Bmp,
        ImageFormat.WebP,
    };

    private static readonly Lazy<string> NativeVersion = new(
        static () => Cv2.GetVersionString() ?? "unknown",
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<FontRef, Font> _fonts = new();

    public OpenCvBackend() => EnsureNativeLoaded();

    /// <summary>构造时打一次原生库：缺 runtime 包时给出能直接照做的错误，而不是等第一次绘制炸 P/Invoke（misc §G.8）。</summary>
    private static void EnsureNativeLoaded()
    {
        try
        {
            _ = NativeVersion.Value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            throw new ImageBackendException(
                "opencv",
                "没能加载 OpenCvSharp 原生库：请为目标 RID 引用 OpenCvSharp4.runtime.<rid>（OpenCV 5 用 OpenCvSharp5.runtime.<rid>）。",
                ex);
        }
    }

    public string Name => "opencv";

    public Version Version => typeof(OpenCvBackend).Assembly.GetName().Version ?? new Version(0, 1, 0);

    /// <summary>
    /// 全能力，包括 <see cref="BackendCapabilities.AlphaBlending"/>：半透明形状先画到包围盒大小的
    /// 覆盖率层，再逐像素混进 BGRA（与半透明文字同一条混合路径）。
    /// </summary>
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
        CvScalar fill = background is { } color ? ToScalar(color) : new CvScalar(0, 0, 0, 0);
        return new OpenCvFrame(new CvMat(height, width, CvMatType.CV_8UC4, fill));
    }

    public bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame)
    {
        // 规范像素是直通 RGBA，Mat 内部是 BGRA：接管外部缓冲就得逐行换通道，不算零拷贝
        frame = null!;
        return false;
    }

    public IFrame Clone(IFrame frame)
    {
        var source = (OpenCvFrame)frame;
        return new OpenCvFrame(source.Mat.Clone());
    }

    public IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
    {
        // AutoOrient 交给 OpenCV 自己按 EXIF 转正；不转时要显式 IgnoreOrientation，避免"以为没转其实转了"（misc §G.4）
        CvImreadModes mode = request.AutoOrient
            ? CvImreadModes.Unchanged
            : CvImreadModes.Unchanged | CvImreadModes.IgnoreOrientation;

        CvMat decoded;
        try
        {
            decoded = Cv2.ImDecode(data, mode);
        }
        catch (Exception ex) when (ex is CvException or ArgumentException)
        {
            throw new ImageBackendException(Name, $"解码失败：{ex.Message}", ex);
        }

        bool transferred = false;
        try
        {
            if (decoded.Empty())
            {
                throw new ImageBackendException(Name, "无法识别的图像格式。");
            }

            // 补通道之前看原图有没有 alpha：2 通道（灰度+alpha）/ 4 通道才有，
            // 3 通道与单通道被 ToBgra 补出来的 255 不算"有 alpha 通道"。
            bool hasAlpha = decoded.Channels() is 2 or 4;

            CvMat bgra = ToBgra(decoded);
            transferred = ReferenceEquals(bgra, decoded);

            metadata = new ImageMetadata(SniffFormat(data), bgra.Cols, bgra.Rows, orientation: 0, hasAlpha: hasAlpha);
            return new OpenCvFrame(bgra);
        }
        finally
        {
            if (!transferred)
            {
                decoded.Dispose();
            }
        }
    }

    public void Encode(IFrame frame, Stream destination, in EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var source = (OpenCvFrame)frame;
        string extension = request.Format switch
        {
            ImageFormat.Png => ".png",
            ImageFormat.Jpeg => ".jpg",
            ImageFormat.Bmp => ".bmp",
            ImageFormat.WebP => ".webp",
            _ => throw new NotSupportedException($"OpenCvBackend 不支持编码 {request.Format}。"),
        };

        CvImageEncodingParam[] parameters = request.Format switch
        {
            ImageFormat.Jpeg => [new CvImageEncodingParam(CvImwriteFlags.JpegQuality, Math.Clamp(request.Quality, 1, 100))],
            ImageFormat.WebP => [new CvImageEncodingParam(CvImwriteFlags.WebPQuality, Math.Clamp(request.Quality, 1, 100))],
            ImageFormat.Png when request.CompressionLevel >= 0 =>
                [new CvImageEncodingParam(CvImwriteFlags.PngCompression, Math.Clamp(request.CompressionLevel, 0, 9))],
            _ => [],
        };

        try
        {
            Cv2.ImEncode(extension, source.Mat, out byte[] buffer, parameters);
            destination.Write(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (ex is CvException or ArgumentException)
        {
            throw new ImageBackendException(Name, $"编码为 {request.Format} 失败：{ex.Message}", ex);
        }
    }

    // ---- 几何 ----

    public bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result)
    {
        var source = (OpenCvFrame)frame;
        var resized = new CvMat();
        try
        {
            Cv2.Resize(source.Mat, resized, new CvSize(request.Width, request.Height), 0, 0, MapInterpolation(request.Sampler));
            result = new OpenCvFrame(resized);
            return true;
        }
        catch
        {
            resized.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 1:1 贴图（letterbox 用）。这里仍是**直接覆盖**目标区域（RGBA 直通，不预乘）——
    /// <see cref="BackendCapabilities.AlphaBlending"/> 说的是带 <c>color</c> 参数的绘制，
    /// 贴图按"缩放结果写回目标"的语义走，源图带 alpha 时按原样搬过去。
    /// </summary>
    public bool TryDrawFrame(IFrame destination, IFrame source, PointF offset)
    {
        if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
        {
            return true;
        }

        var target = (OpenCvFrame)destination;
        var layer = (OpenCvFrame)source;

        int offsetX = (int)MathF.Round(offset.X);
        int offsetY = (int)MathF.Round(offset.Y);

        int left = Math.Max(0, offsetX);
        int top = Math.Max(0, offsetY);
        int right = Math.Min(target.Mat.Cols, offsetX + layer.Mat.Cols);
        int bottom = Math.Min(target.Mat.Rows, offsetY + layer.Mat.Rows);

        if (left >= right || top >= bottom)
        {
            return true;
        }

        var targetRegion = new CvRect(left, top, right - left, bottom - top);
        var sourceRegion = new CvRect(left - offsetX, top - offsetY, right - left, bottom - top);

        using var targetRoi = new CvMat(target.Mat, targetRegion);
        using var sourceRoi = new CvMat(layer.Mat, sourceRegion);
        sourceRoi.CopyTo(targetRoi);
        return true;
    }

    // ---- 绘制 ----

    public bool TryDrawPoint(IFrame frame, in PointSpec spec)
    {
        if (!IsFinite(spec.Point) || !IsValid(spec.Radius))
        {
            return false;
        }

        Rgba32 color = spec.Color;
        if (!IsVisible(color))
        {
            return true;
        }

        PointF center = spec.Point;
        bool antialias = spec.Antialias;
        int radius = Math.Max(1, (int)MathF.Round(spec.Radius));
        float extent = radius + 2f;

        var target = (OpenCvFrame)frame;
        DrawColor(
            target.Mat,
            color,
            center.X - extent,
            center.Y - extent,
            center.X + extent,
            center.Y + extent,
            (canvas, origin) => Cv2.Circle(
                canvas,
                Quantize(center, origin),
                radius,
                Ink(canvas, color),
                -1,
                LineType(antialias)));
        return true;
    }

    public bool TryDrawLine(IFrame frame, in LineSpec spec)
    {
        if (!IsFinite(spec.From) || !IsFinite(spec.To) || !IsValid(spec.Width))
        {
            return false;
        }

        Rgba32 color = spec.Color;
        if (!IsVisible(color))
        {
            return true;
        }

        // in 参数不能进 lambda：先把要用的字段抄出来
        PointF from = spec.From;
        PointF to = spec.To;
        float width = spec.Width;
        StrokeStyle? style = spec.Style;
        bool antialias = style?.Antialias ?? true;
        bool dashed = style?.HasDash ?? false;
        int thickness = Thickness(width);

        var target = (OpenCvFrame)frame;
        ExpandPoints([from, to], (thickness * 0.5f) + 2f, out float minX, out float minY, out float maxX, out float maxY);

        DrawColor(target.Mat, color, minX, minY, maxX, maxY, (canvas, origin) =>
        {
            CvScalar ink = Ink(canvas, color);
            if (dashed)
            {
                StrokeDashed(canvas, [from, to], false, ink, width, style!, origin);
                return;
            }

            Cv2.Line(canvas, Quantize(from, origin), Quantize(to, origin), ink, thickness, LineType(antialias));
        });
        return true;
    }

    public bool TryDrawPolyline(IFrame frame, in PolylineSpec spec)
    {
        if (spec.Points.Length < 2 || !IsValid(spec.Width) || !AllFinite(spec.Points))
        {
            return false;
        }

        Rgba32 color = spec.Color;
        if (!IsVisible(color))
        {
            return true;
        }

        // PolylineSpec 是 ref struct，lambda 里只能用抄出来的副本
        PointF[] points = spec.Points.ToArray();
        bool closed = spec.Closed;
        float width = spec.Width;
        StrokeStyle? style = spec.Style;
        bool antialias = style?.Antialias ?? true;
        bool dashed = style?.HasDash ?? false;
        int thickness = Thickness(width);

        var target = (OpenCvFrame)frame;
        ExpandPoints(points, (thickness * 0.5f) + 2f, out float minX, out float minY, out float maxX, out float maxY);

        DrawColor(target.Mat, color, minX, minY, maxX, maxY, (canvas, origin) =>
        {
            CvScalar ink = Ink(canvas, color);
            if (dashed)
            {
                StrokeDashed(canvas, points, closed, ink, width, style!, origin);
                return;
            }

            Cv2.Polylines(canvas, [ToPoints(points, origin)], closed, ink, thickness, LineType(antialias));
        });
        return true;
    }

    public bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect) || !IsValid(spec.Width))
        {
            return false;
        }

        Rgba32 color = spec.Color;
        if (!IsVisible(color))
        {
            return true;
        }

        RectF rect = spec.Rect;
        float width = spec.Width;
        StrokeStyle? style = spec.Style;
        bool antialias = style?.Antialias ?? true;
        bool dashed = style?.HasDash ?? false;
        int thickness = Thickness(width);
        float padding = (thickness * 0.5f) + 2f;

        var target = (OpenCvFrame)frame;
        DrawColor(
            target.Mat,
            color,
            rect.X - padding,
            rect.Y - padding,
            rect.X + rect.Width + padding,
            rect.Y + rect.Height + padding,
            (canvas, origin) =>
            {
                CvScalar ink = Ink(canvas, color);
                if (dashed)
                {
                    StrokeDashed(canvas, RectContour(rect), true, ink, width, style!, origin);
                    return;
                }

                Cv2.Rectangle(canvas, ToRect(rect, origin), ink, thickness, LineType(antialias));
            });
        return true;
    }

    public bool TryFillRectangle(IFrame frame, in FilledRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect))
        {
            return false;
        }

        Rgba32 color = spec.Color;
        if (!IsVisible(color))
        {
            return true;
        }

        RectF rect = spec.Rect;
        var target = (OpenCvFrame)frame;
        DrawColor(
            target.Mat,
            color,
            rect.X - 1f,
            rect.Y - 1f,
            rect.X + rect.Width + 1f,
            rect.Y + rect.Height + 1f,
            (canvas, origin) => Cv2.Rectangle(canvas, ToRect(rect, origin), Ink(canvas, color), -1, CvLineTypes.Link8));
        return true;
    }

    public bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds) || !IsValid(spec.Width))
        {
            return false;
        }

        Rgba32 color = spec.Color;
        if (!IsVisible(color))
        {
            return true;
        }

        RectF bounds = spec.Bounds;
        float width = spec.Width;
        StrokeStyle? style = spec.Style;
        bool antialias = style?.Antialias ?? true;
        bool dashed = style?.HasDash ?? false;
        int thickness = Thickness(width);
        float padding = (thickness * 0.5f) + 2f;

        var target = (OpenCvFrame)frame;
        DrawColor(
            target.Mat,
            color,
            bounds.X - padding,
            bounds.Y - padding,
            bounds.X + bounds.Width + padding,
            bounds.Y + bounds.Height + padding,
            (canvas, origin) =>
            {
                CvScalar ink = Ink(canvas, color);
                if (dashed)
                {
                    StrokeDashed(canvas, EllipseContour(bounds), true, ink, width, style!, origin);
                    return;
                }

                Cv2.Ellipse(canvas, ToRotatedRect(bounds, origin), ink, thickness, LineType(antialias));
            });
        return true;
    }

    public bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds))
        {
            return false;
        }

        Rgba32 color = spec.Color;
        if (!IsVisible(color))
        {
            return true;
        }

        RectF bounds = spec.Bounds;
        var target = (OpenCvFrame)frame;
        DrawColor(
            target.Mat,
            color,
            bounds.X - 1f,
            bounds.Y - 1f,
            bounds.X + bounds.Width + 1f,
            bounds.Y + bounds.Height + 1f,
            (canvas, origin) => Cv2.Ellipse(canvas, ToRotatedRect(bounds, origin), Ink(canvas, color), -1, CvLineTypes.AntiAlias));
        return true;
    }

    public bool TryFillPolygon(IFrame frame, in PolygonSpec spec)
    {
        if (spec.Points.Length < 3 || !AllFinite(spec.Points))
        {
            return false;
        }

        Rgba32 color = spec.Color;
        if (!IsVisible(color))
        {
            return true;
        }

        PointF[] points = spec.Points.ToArray();
        var target = (OpenCvFrame)frame;
        ExpandPoints(points, 1f, out float minX, out float minY, out float maxX, out float maxY);

        DrawColor(
            target.Mat,
            color,
            minX,
            minY,
            maxX,
            maxY,
            (canvas, origin) => Cv2.FillPoly(canvas, [ToPoints(points, origin)], Ink(canvas, color), CvLineTypes.AntiAlias));
        return true;
    }

    // ---- 虚线 ----

    /// <summary>
    /// 虚线描边：OpenCV 的 <c>Line/Polylines</c> 没有 dash 图案，这里按核心那套规则
    /// （<see cref="ImageFacade.Rendering.StrokeOutline.SplitDashes"/>，含 <c>DashOffset</c>、奇数图案重复）
    /// 把轮廓切成若干实线段，再逐段 <c>Polylines</c>。纯几何：不建中间位图，也不碰像素混合。
    /// 端帽仍是 OpenCV 的平头，和实线描边一致。<c>ink</c> 是画到目标图时的真实颜色、画到覆盖率层时的纯白
    /// （见 <see cref="Ink"/>）；<c>origin</c> 是目标图坐标偏移（覆盖率层 = 包围盒左上角，直接画时为 0）。
    /// </summary>
    private static void StrokeDashed(
        CvMat mat,
        ReadOnlySpan<PointF> contour,
        bool closed,
        CvScalar ink,
        float width,
        StrokeStyle style,
        CvPoint origin)
    {
        if (contour.Length < 2)
        {
            return;
        }

        // 公共样式的图案单位是"线宽倍数"，核心的 StrokeStyle 直接按像素吃
        var pixelStyle = CoreStrokeStyle.Create(
            (ImageFacade.Rendering.LineJoin)style.Join,
            (ImageFacade.Rendering.LineCap)style.Cap,
            style.MiterLimit,
            style.DashPattern,
            style.DashOffset,
            width);

        var source = new CorePointF[contour.Length];
        for (int i = 0; i < contour.Length; i++)
        {
            source[i] = new CorePointF(contour[i].X, contour[i].Y);
        }

        int thickness = Thickness(width);
        CvLineTypes lineType = LineType(style.Antialias);

        foreach (CorePointF[] run in ImageFacade.Rendering.StrokeOutline.SplitDashes(source, closed, pixelStyle))
        {
            if (run.Length < 2)
            {
                continue;
            }

            // 整圈落在同一段实线里时核心给回首尾同点的点列：按闭合轮廓画，起点不重复
            bool loop = run.Length > 2 && run[0].X == run[^1].X && run[0].Y == run[^1].Y;
            int count = loop ? run.Length - 1 : run.Length;
            var points = new CvPoint[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = Quantize(new PointF(run[i].X, run[i].Y), origin);
            }

            Cv2.Polylines(mat, [points], loop, ink, thickness, lineType);
        }
    }

    /// <summary>矩形描边轮廓（顺时针四个角，闭合）。</summary>
    private static PointF[] RectContour(RectF rect) =>
    [
        new PointF(rect.X, rect.Y),
        new PointF(rect.X + rect.Width, rect.Y),
        new PointF(rect.X + rect.Width, rect.Y + rect.Height),
        new PointF(rect.X, rect.Y + rect.Height),
    ];

    /// <summary>
    /// 椭圆描边的折线近似：周长上约 2 px 一个点（24~512 段）。虚线切分需要真实折线长度，
    /// 直接 <c>Cv2.Ellipse</c> 没法按弧长切。
    /// </summary>
    private static PointF[] EllipseContour(RectF bounds)
    {
        float radiusX = bounds.Width * 0.5f;
        float radiusY = bounds.Height * 0.5f;
        float centerX = bounds.X + radiusX;
        float centerY = bounds.Y + radiusY;

        int segments = (int)MathF.Ceiling(MathF.Tau * MathF.Max(radiusX, radiusY) / 2f);
        segments = Math.Clamp(segments, 24, 512);

        var points = new PointF[segments];
        for (int i = 0; i < segments; i++)
        {
            float angle = MathF.Tau * i / segments;
            points[i] = new PointF(centerX + (MathF.Cos(angle) * radiusX), centerY + (MathF.Sin(angle) * radiusY));
        }

        return points;
    }

    // ---- 文字 ----

    public bool TryDrawText(IFrame frame, in TextSpec spec)
    {
        if (spec.Text.Length == 0 || !IsFinite(spec.Origin))
        {
            return true;
        }

        Font font;
        GlyphLayout layout;
        try
        {
            // FontFace 优先：复用调用方传进来的实例，不再每次解析字体表
            font = spec.Style.FontFace ?? ResolveFont(spec.Style.Font);
            layout = font.Layout(spec.Text, spec.Style.Size);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        if (layout.Glyphs.Count == 0)
        {
            return true;
        }

        // 排版原点 = 排版框左上角（与软后端/ImageSharp 一致）；Baseline 锚点自己上移 ascent
        int originX = (int)MathF.Round(spec.Origin.X);
        int originY = spec.Style.Anchor == TextAnchor.Baseline
            ? (int)MathF.Round(spec.Origin.Y - layout.Ascent)
            : (int)MathF.Round(spec.Origin.Y);

        var target = (OpenCvFrame)frame;
        BlendGlyphs(target.Mat, layout, originX, originY, spec.Style.Color);
        return true;
    }

    public bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
        => TryMeasureTextCore(spec.Style, spec.Text.AsSpan(), out metrics);

    /// <summary>切片版：托管字体栈的 <see cref="Font.Measure(ReadOnlySpan{char}, float)"/> 逐码点累加，直接吃切片。</summary>
    public bool TryMeasureText(IFrame frame, ReadOnlySpan<char> text, in TextStyle style, out TextMetrics metrics)
        => TryMeasureTextCore(style, text, out metrics);

    private bool TryMeasureTextCore(TextStyle style, ReadOnlySpan<char> text, out TextMetrics metrics)
    {
        Font font;
        try
        {
            font = style.FontFace ?? ResolveFont(style.Font);
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
        var target = (OpenCvFrame)frame;
        target.Mat.SetTo(ToScalar(color));
    }

    public bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color)
    {
        var target = (OpenCvFrame)frame;
        unsafe
        {
            byte* data = (byte*)target.Mat.Data;
            byte* pixel = data + (y * target.Mat.Step()) + (x * 4);
            color = new Rgba32(pixel[2], pixel[1], pixel[0], pixel[3]);
        }

        return true;
    }

    public bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride)
    {
        var source = (OpenCvFrame)frame;
        int width = source.Mat.Cols;
        int height = source.Mat.Rows;
        unsafe
        {
            fixed (byte* target = destination)
            {
                SwapRedBlue(
                    (byte*)source.Mat.Data,
                    (int)source.Mat.Step(),
                    target,
                    destinationStride,
                    width,
                    height);
            }
        }

        return true;
    }

    public void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride)
    {
        var target = (OpenCvFrame)frame;
        int width = target.Mat.Cols;
        int height = target.Mat.Rows;
        unsafe
        {
            fixed (byte* pixels = source)
            {
                SwapRedBlue(
                    pixels,
                    sourceStride,
                    (byte*)target.Mat.Data,
                    (int)target.Mat.Step(),
                    width,
                    height);
            }
        }
    }

    /// <summary>
    /// RGBA32 ↔ BGRA 的通道交换（R↔B，A 直通）。交换是对合运算，导入与导出共用这一份实现。
    /// 两侧各按自己的 stride 走行，因此源/目标可以是不连续的 <c>Mat</c>，也可以是带填充的外部缓冲。
    /// </summary>
    /// <remarks>
    /// 每轮 <c>Vector128</c> 处理 4 个像素（一次 shuffle 换完 R/B），行尾不足 16 字节走标量。
    /// 不做 <c>CvtColor(BGRA2RGBA)</c> 是因为那需要一块中间 Mat，多一次全图拷贝。
    /// </remarks>
    private static unsafe void SwapRedBlue(
        byte* source,
        int sourceStride,
        byte* destination,
        int destinationStride,
        int width,
        int height)
    {
        int rowBytes = width * 4;
        bool vectorized = Vector128.IsHardwareAccelerated;

        // 4 个像素的 R/B 互换：0<->2, 4<->6, 8<->10, 12<->14
        Vector128<byte> swap = Vector128.Create(
            (byte)2, 1, 0, 3,
            (byte)6, 5, 4, 7,
            (byte)10, 9, 8, 11,
            (byte)14, 13, 12, 15);

        for (int y = 0; y < height; y++)
        {
            byte* row = source + (y * sourceStride);
            byte* output = destination + (y * destinationStride);

            int offset = 0;
            if (vectorized)
            {
                ref byte s = ref *row;
                ref byte d = ref *output;
                for (; offset + 16 <= rowBytes; offset += 16)
                {
                    Vector128.StoreUnsafe(
                        Vector128.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)offset), swap),
                        ref d,
                        (nuint)offset);
                }
            }

            for (; offset < rowBytes; offset += 4)
            {
                output[offset] = row[offset + 2];
                output[offset + 1] = row[offset + 1];
                output[offset + 2] = row[offset];
                output[offset + 3] = row[offset + 3];
            }
        }
    }

    public bool TryAcquirePixels(IFrame frame, out PixelLease lease)
    {
        var target = (OpenCvFrame)frame;
        int stride = target.Mat.Cols * 4;
        var buffer = new byte[checked(stride * target.Mat.Rows)];
        TryCopyPixels(target, buffer, stride);

        lease = new PixelLease(buffer, stride, () => ImportPixels(target, buffer, stride));
        return true;
    }

    /// <summary>
    /// 直接扫 Mat 自己的内存（帧固定是 <c>CV_8UC4</c> BGRA，alpha 在每像素第 4 字节），
    /// 不交换通道、不拷一份。
    /// </summary>
    public bool TryGetTransparency(IFrame frame, out bool hasTransparency)
    {
        hasTransparency = false;

        CvMat mat = ((OpenCvFrame)frame).Mat;
        if (mat.Type() != CvMatType.CV_8UC4 || mat.Empty())
        {
            return false;   // 别的位深 / 通道数不猜，交给门面走 TryAcquirePixels 扫
        }

        int rowBytes = mat.Cols * 4;
        int rows = mat.Rows;
        long step = mat.Step();
        unsafe
        {
            byte* data = (byte*)mat.Data;
            for (int y = 0; y < rows; y++)
            {
                byte* row = data + (y * step);
                for (int x = 3; x < rowBytes; x += 4)
                {
                    if (row[x] != 255)
                    {
                        hasTransparency = true;
                        return true;
                    }
                }
            }
        }

        return true;
    }

    public void Dispose()
    {
        // 无状态：字体缓存不存在，Mat 由各自的 OpenCvFrame 释放
    }

    // ---- 内部：通道 ----

    /// <summary>
    /// 解码结果归一成"8 位 + 4 通道 BGRA"。<c>ImDecode(Unchanged)</c> 既保留通道数也保留位深，
    /// 而帧的契约是每像素固定 4 字节，两者都得在这里抹平。
    /// </summary>
    private static CvMat ToBgra(CvMat source)
    {
        // 16 位 PNG 会解出 CV_16UC3/CV_16UC4：Channels() 照样是 3/4，但每像素是 6/8 字节，
        // 直接往下走会让 CopyRows / TryGetPixel / BlendGlyphs 里的 width*4 全部算错。
        if (source.ElemSize1() != 1)
        {
            CvMat normalized = ToEightBit(source);
            CvMat? owned = normalized;
            try
            {
                CvMat result = ConvertChannels(normalized);
                if (ReferenceEquals(result, normalized))
                {
                    owned = null;   // 4 通道：这块 Mat 直接交给帧，别在这里释放
                }

                return result;
            }
            finally
            {
                owned?.Dispose();
            }
        }

        return ConvertChannels(source);
    }

    private static CvMat ConvertChannels(CvMat source)
    {
        switch (source.Channels())
        {
            case 4:
                return source;
            case 3:
                return Convert(source, CvColorConversion.BGR2BGRA);
            case 1:
                return Convert(source, CvColorConversion.GRAY2BGRA);
            case 2:
                return MergeGrayAlpha(source);
            default:
                throw new ImageBackendException("opencv", $"不支持的通道数：{source.Channels()}。");
        }
    }

    /// <summary>
    /// 降到 8 位。深度按 16 位处理（<c>1/257</c>：65535 → 255）——
    /// 直接 <c>ConvertTo</c> 不缩放会饱和成一片白。
    /// </summary>
    private static CvMat ToEightBit(CvMat source)
    {
        var destination = new CvMat();
        try
        {
            source.ConvertTo(destination, CvMatType.CV_8U, 1.0 / 257.0);
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    private static CvMat Convert(CvMat source, CvColorConversion conversion)
    {
        var destination = new CvMat();
        try
        {
            Cv2.CvtColor(source, destination, conversion);
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>gray+alpha（PNG 的 CV_8UC2）：把灰度复制到 B/G/R，第二通道当 alpha。</summary>
    private static CvMat MergeGrayAlpha(CvMat source)
    {
        Cv2.Split(source, out CvMat[] planes);
        try
        {
            var destination = new CvMat();
            try
            {
                Cv2.Merge([planes[0], planes[0], planes[0], planes[1]], destination);
                return destination;
            }
            catch
            {
                destination.Dispose();
                throw;
            }
        }
        finally
        {
            planes.DisposeAll();
        }
    }

    // ---- 内部：绘制辅助 ----

    /// <summary>A = 0 什么都不用画；其余都支持（A &lt; 255 走覆盖率层 + 逐像素混合）。</summary>
    private static bool IsVisible(Rgba32 color) => color.A != 0;

    private static CvScalar ToScalar(Rgba32 color) => new(color.B, color.G, color.R, color.A);

    /// <summary>
    /// 画到哪张图就用哪个墨色：直接画时是真实颜色；画到单通道覆盖率层时必须是纯白，
    /// 这样 AA 打完折剩下的灰度才正好是覆盖率。
    /// </summary>
    private static CvScalar Ink(CvMat canvas, Rgba32 color)
        => canvas.Channels() == 1 ? new CvScalar(255) : ToScalar(color);

    /// <summary>
    /// 统一的形状绘制入口。<paramref name="color"/> 不透明时直接画目标 <c>Mat</c>；
    /// 半透明时先在形状包围盒大小的 8 位全 0 层上照常画一遍（AA 边缘得到 0~255 的覆盖率），
    /// 再按覆盖率把颜色混进目标——和半透明文字同一套逐像素混合，不需要全图临时层。
    /// <para>
    /// <paramref name="paint"/> 收到"画到哪张图"和"世界坐标减多少才是该图坐标"，
    /// 所以同一份绘制代码两条路径都能用（OpenCV 的绘制坐标随层平移，亚像素相位不变）。
    /// </para>
    /// </summary>
    private static void DrawColor(
        CvMat mat,
        Rgba32 color,
        float minX,
        float minY,
        float maxX,
        float maxY,
        Action<CvMat, CvPoint> paint)
    {
        if (color.A == 255)
        {
            paint(mat, default);
            return;
        }

        if (!TryGetLayerBounds(mat, minX, minY, maxX, maxY, out CvRect layer))
        {
            return;
        }

        using var mask = new CvMat(layer.Height, layer.Width, CvMatType.CV_8UC1, new CvScalar(0));
        paint(mask, new CvPoint(layer.X, layer.Y));
        BlendCoverage(mat, mask, layer.X, layer.Y, color);
    }

    /// <summary>形状包围盒 → 整像素矩形并裁剪到画布；与画布没有交集时返回 <c>false</c>。</summary>
    private static bool TryGetLayerBounds(
        CvMat mat,
        float minX,
        float minY,
        float maxX,
        float maxY,
        out CvRect bounds)
    {
        int left = Math.Max(0, (int)MathF.Floor(minX));
        int top = Math.Max(0, (int)MathF.Floor(minY));
        int right = Math.Min(mat.Cols, (int)MathF.Ceiling(maxX));
        int bottom = Math.Min(mat.Rows, (int)MathF.Ceiling(maxY));

        if (right <= left || bottom <= top)
        {
            bounds = default;
            return false;
        }

        bounds = new CvRect(left, top, right - left, bottom - top);
        return true;
    }

    /// <summary>折线外包框（用于推导覆盖率层的大小）。</summary>
    private static void ExpandPoints(
        ReadOnlySpan<PointF> points,
        float padding,
        out float minX,
        out float minY,
        out float maxX,
        out float maxY)
    {
        minX = float.MaxValue;
        minY = float.MaxValue;
        maxX = float.MinValue;
        maxY = float.MinValue;

        foreach (PointF point in points)
        {
            minX = MathF.Min(minX, point.X);
            minY = MathF.Min(minY, point.Y);
            maxX = MathF.Max(maxX, point.X);
            maxY = MathF.Max(maxY, point.Y);
        }

        minX -= padding;
        minY -= padding;
        maxX += padding;
        maxY += padding;
    }

    /// <summary>
    /// 把覆盖率层按 <paramref name="color"/> 混进 BGRA 直通像素，公式与 <see cref="BlendGlyphs"/> 一致：
    /// 颜色线性插值、alpha 按 source-over 累加。目标不透明时（最常见的场景）就是精确的 src-over。
    /// </summary>
    private static unsafe void BlendCoverage(CvMat mat, CvMat mask, int left, int top, Rgba32 color)
    {
        byte* data = (byte*)mat.Data;
        long step = mat.Step();
        byte* coverageData = (byte*)mask.Data;
        long coverageStep = mask.Step();
        float shapeAlpha = color.A / 255f;
        int width = mask.Cols;
        int height = mask.Rows;

        for (int row = 0; row < height; row++)
        {
            byte* coverageRow = coverageData + (row * coverageStep);
            byte* pixel = data + ((top + row) * step) + (left * 4);

            for (int col = 0; col < width; col++, pixel += 4)
            {
                byte coverage = coverageRow[col];
                if (coverage == 0)
                {
                    continue;
                }

                float alpha = (coverage / 255f) * shapeAlpha;
                float inverse = 1f - alpha;
                pixel[0] = BlendChannel(color.B, pixel[0], alpha);
                pixel[1] = BlendChannel(color.G, pixel[1], alpha);
                pixel[2] = BlendChannel(color.R, pixel[2], alpha);
                pixel[3] = (byte)Math.Clamp(MathF.Round((alpha * 255f) + (pixel[3] * inverse)), 0f, 255f);
            }
        }
    }

    /// <summary>corner 坐标 → 像素中心（OpenCV 的整数坐标等价于像素中心，misc §G.2）。</summary>
    private static CvPoint Quantize(PointF point, CvPoint origin = default)
        => new(
            (int)MathF.Round(point.X - 0.5f) - origin.X,
            (int)MathF.Round(point.Y - 0.5f) - origin.Y);

    private static CvRect ToRect(RectF rect, CvPoint origin = default) => new(
        (int)MathF.Round(rect.X - 0.5f) - origin.X,
        (int)MathF.Round(rect.Y - 0.5f) - origin.Y,
        Math.Max(1, (int)MathF.Round(rect.Width)),
        Math.Max(1, (int)MathF.Round(rect.Height)));

    private static CvRotatedRect ToRotatedRect(RectF bounds, CvPoint origin = default)
    {
        PointF center = bounds.Center;
        return new CvRotatedRect(
            new CvPoint2f(center.X - 0.5f - origin.X, center.Y - 0.5f - origin.Y),
            new CvSize2f(bounds.Width, bounds.Height),
            0f);
    }

    private static CvPoint[] ToPoints(ReadOnlySpan<PointF> points, CvPoint origin = default)
    {
        var result = new CvPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            result[i] = Quantize(points[i], origin);
        }

        return result;
    }

    private static int Thickness(float width) => Math.Max(1, (int)MathF.Round(width));

    private static CvLineTypes LineType(bool antialias) => antialias ? CvLineTypes.AntiAlias : CvLineTypes.Link8;

    private static CvInterpolationFlags MapInterpolation(Sampler sampler) => sampler switch
    {
        Sampler.Nearest => CvInterpolationFlags.Nearest,
        Sampler.Box => CvInterpolationFlags.Area,
        Sampler.Bilinear => CvInterpolationFlags.Linear,
        Sampler.Lanczos2 or Sampler.Lanczos3 => CvInterpolationFlags.Lanczos4,
        _ => CvInterpolationFlags.Cubic,
    };

    // ---- 内部：文字 ----

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

            // 渲染层没有系统字体管理器：指定字体族时统一退回内置字体（misc §J.3）
            return Font.BuiltIn;
        });
    }

    /// <summary>
    /// 把 A8 字形覆盖位图按覆盖率混进 BGRA 直通像素。混合在直通空间做，
    /// 所以半透明文字也能正确合成（半透明形状走 <see cref="BlendCoverage"/>，公式相同）。
    /// </summary>
    private static unsafe void BlendGlyphs(CvMat mat, GlyphLayout layout, int originX, int originY, Rgba32 color)
    {
        byte* data = (byte*)mat.Data;
        long step = mat.Step();
        int width = mat.Cols;
        int height = mat.Rows;
        float textAlpha = color.A / 255f;

        foreach (GlyphPositioned glyph in layout.Glyphs)
        {
            GlyphMask mask = glyph.Mask;
            int left = originX + glyph.X;
            int top = originY + glyph.Y;

            int minCol = Math.Max(0, -left);
            int maxCol = Math.Min(mask.Width, width - left);
            int minRow = Math.Max(0, -top);
            int maxRow = Math.Min(mask.Height, height - top);
            if (maxCol <= minCol || maxRow <= minRow)
            {
                continue;
            }

            for (int row = minRow; row < maxRow; row++)
            {
                byte* pixel = data + ((top + row) * step) + ((left + minCol) * 4);
                int coverageIndex = (row * mask.Width) + minCol;

                for (int col = minCol; col < maxCol; col++, pixel += 4)
                {
                    byte coverage = mask.Coverage[coverageIndex++];
                    if (coverage == 0)
                    {
                        continue;
                    }

                    float alpha = (coverage / 255f) * textAlpha;
                    if (alpha >= 1f)
                    {
                        pixel[0] = color.B;
                        pixel[1] = color.G;
                        pixel[2] = color.R;
                        pixel[3] = 255;
                        continue;
                    }

                    float inverse = 1f - alpha;
                    pixel[0] = BlendChannel(color.B, pixel[0], alpha);
                    pixel[1] = BlendChannel(color.G, pixel[1], alpha);
                    pixel[2] = BlendChannel(color.R, pixel[2], alpha);
                    pixel[3] = (byte)Math.Clamp(MathF.Round((alpha * 255f) + (pixel[3] * inverse)), 0f, 255f);
                }
            }
        }
    }

    private static byte BlendChannel(byte source, byte destination, float alpha)
        => (byte)Math.Clamp(MathF.Round((source * alpha) + (destination * (1f - alpha))), 0f, 255f);

    // ---- 内部：格式嗅探 ----

    private static ImageFormat SniffFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return ImageFormat.Png;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return ImageFormat.Bmp;
        }

        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return ImageFormat.WebP;
        }

        return ImageFormat.Unknown;
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
}

/// <summary>OpenCV 帧：包一个 <c>CV_8UC4</c>（BGRA）的 <see cref="CvMat"/>。</summary>
internal sealed class OpenCvFrame : IFrame
{
    internal OpenCvFrame(CvMat mat) => Mat = mat;

    internal CvMat Mat { get; }

    public int Width => Mat.Cols;

    public int Height => Mat.Rows;

    public void Dispose() => Mat.Dispose();
}
