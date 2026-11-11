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
/// 形状绘制受 OpenCV 本身限制：<b>没有 alpha 混合</b>，<c>color.A &lt; 255</c> 的
/// <c>Fill/Draw/Line</c> 一律返回 <c>false</c>，由门面抛 <see cref="FeatureNotSupportedException"/>；
/// 需要半透明形状时在调用方自行合成，或改用 Skia / ImageSharp。
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
    /// 注意：**没有** <see cref="BackendCapabilities.AlphaBlending"/>、<see cref="BackendCapabilities.Dash"/>。
    /// 文字由核心的托管字形光栅化提供（含 CJK），4/5 一致。
    /// </summary>
    public BackendCapabilities Capabilities =>
        BackendCapabilities.Resize
        | BackendCapabilities.DrawShapes
        | BackendCapabilities.DrawPoints
        | BackendCapabilities.DrawText
        | BackendCapabilities.DrawTextUnicode
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

            CvMat bgra = ToBgra(decoded);
            transferred = ReferenceEquals(bgra, decoded);

            metadata = new ImageMetadata(SniffFormat(data), bgra.Cols, bgra.Rows);
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
    /// 1:1 贴图（letterbox 用）。OpenCV 后端没有 alpha 混合能力，这里是**直接覆盖**目标区域（RGBA 直通，不预乘）。
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
        if (!IsFinite(spec.Point) || !IsValid(spec.Radius) || !CanDraw(spec.Color))
        {
            return false;
        }

        var target = (OpenCvFrame)frame;
        int radius = Math.Max(1, (int)MathF.Round(spec.Radius));
        Cv2.Circle(target.Mat, Quantize(spec.Point), radius, ToScalar(spec.Color), -1, LineType(spec.Antialias));
        return true;
    }

    public bool TryDrawLine(IFrame frame, in LineSpec spec)
    {
        if (!IsFinite(spec.From) || !IsFinite(spec.To) || !IsValid(spec.Width))
        {
            return false;
        }

        if (!CanDraw(spec.Color) || (spec.Style?.HasDash ?? false))
        {
            return false;
        }

        var target = (OpenCvFrame)frame;
        bool antialias = spec.Style?.Antialias ?? true;
        Cv2.Line(
            target.Mat,
            Quantize(spec.From),
            Quantize(spec.To),
            ToScalar(spec.Color),
            Thickness(spec.Width),
            LineType(antialias));
        return true;
    }

    public bool TryDrawPolyline(IFrame frame, in PolylineSpec spec)
    {
        if (spec.Points.Length < 2 || !IsValid(spec.Width) || !AllFinite(spec.Points))
        {
            return false;
        }

        if (!CanDraw(spec.Color) || (spec.Style?.HasDash ?? false))
        {
            return false;
        }

        var target = (OpenCvFrame)frame;
        bool antialias = spec.Style?.Antialias ?? true;
        Cv2.Polylines(
            target.Mat,
            [ToPoints(spec.Points)],
            spec.Closed,
            ToScalar(spec.Color),
            Thickness(spec.Width),
            LineType(antialias));
        return true;
    }

    public bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect) || !IsValid(spec.Width))
        {
            return false;
        }

        if (!CanDraw(spec.Color) || (spec.Style?.HasDash ?? false))
        {
            return false;
        }

        var target = (OpenCvFrame)frame;
        bool antialias = spec.Style?.Antialias ?? true;
        Cv2.Rectangle(
            target.Mat,
            ToRect(spec.Rect),
            ToScalar(spec.Color),
            Thickness(spec.Width),
            LineType(antialias));
        return true;
    }

    public bool TryFillRectangle(IFrame frame, in FilledRectSpec spec)
    {
        if (spec.Rect.IsEmpty || !IsFinite(spec.Rect) || !CanDraw(spec.Color))
        {
            return false;
        }

        var target = (OpenCvFrame)frame;
        Cv2.Rectangle(target.Mat, ToRect(spec.Rect), ToScalar(spec.Color), -1, CvLineTypes.Link8);
        return true;
    }

    public bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds) || !IsValid(spec.Width))
        {
            return false;
        }

        if (!CanDraw(spec.Color) || (spec.Style?.HasDash ?? false))
        {
            return false;
        }

        var target = (OpenCvFrame)frame;
        bool antialias = spec.Style?.Antialias ?? true;
        Cv2.Ellipse(
            target.Mat,
            ToRotatedRect(spec.Bounds),
            ToScalar(spec.Color),
            Thickness(spec.Width),
            LineType(antialias));
        return true;
    }

    public bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec)
    {
        if (spec.Bounds.IsEmpty || !IsFinite(spec.Bounds) || !CanDraw(spec.Color))
        {
            return false;
        }

        var target = (OpenCvFrame)frame;
        Cv2.Ellipse(target.Mat, ToRotatedRect(spec.Bounds), ToScalar(spec.Color), -1, CvLineTypes.AntiAlias);
        return true;
    }

    public bool TryFillPolygon(IFrame frame, in PolygonSpec spec)
    {
        if (spec.Points.Length < 3 || !AllFinite(spec.Points) || !CanDraw(spec.Color))
        {
            return false;
        }

        var target = (OpenCvFrame)frame;
        Cv2.FillPoly(target.Mat, [ToPoints(spec.Points)], ToScalar(spec.Color), CvLineTypes.AntiAlias);
        return true;
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
    {
        Font font;
        try
        {
            font = spec.Style.FontFace ?? ResolveFont(spec.Style.Font);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            metrics = default;
            return false;
        }

        float size = spec.Style.Size;
        float ascent = font.GetAscent(size);
        float descent = MathF.Abs(font.GetDescent(size));
        float lineHeight = font.GetLineHeight(size);
        float gap = MathF.Max(0f, lineHeight - ascent - descent);

        metrics = new TextMetrics(font.Measure(spec.Text, size), ascent, descent, gap);
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

    /// <summary>OpenCV 不做 alpha 混合，半透明请求一律交给门面回退。</summary>
    private static bool CanDraw(Rgba32 color) => color.A == 255;

    private static CvScalar ToScalar(Rgba32 color) => new(color.B, color.G, color.R, color.A);

    /// <summary>corner 坐标 → 像素中心（OpenCV 的整数坐标等价于像素中心，misc §G.2）。</summary>
    private static CvPoint Quantize(PointF point)
        => new((int)MathF.Round(point.X - 0.5f), (int)MathF.Round(point.Y - 0.5f));

    private static CvRect ToRect(RectF rect) => new(
        (int)MathF.Round(rect.X - 0.5f),
        (int)MathF.Round(rect.Y - 0.5f),
        Math.Max(1, (int)MathF.Round(rect.Width)),
        Math.Max(1, (int)MathF.Round(rect.Height)));

    private static CvRotatedRect ToRotatedRect(RectF bounds)
    {
        PointF center = bounds.Center;
        return new CvRotatedRect(
            new CvPoint2f(center.X - 0.5f, center.Y - 0.5f),
            new CvSize2f(bounds.Width, bounds.Height),
            0f);
    }

    private static CvPoint[] ToPoints(ReadOnlySpan<PointF> points)
    {
        var result = new CvPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            result[i] = Quantize(points[i]);
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
    /// 所以半透明文字也能正确合成（形状绘制仍不支持 alpha，见 <see cref="CanDraw"/>）。
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
