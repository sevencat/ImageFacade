using ImageFacade.Backends;

namespace ImageFacade.SkiaSharp;

/// <summary>
/// SkiaSharp 适配后端（视频叠加首选后端）。
/// </summary>
/// <remarks>
/// M0 只有契约骨架；M3 实现"帧 = SKBitmap(Rgba8888 + Premul) + SKCanvas"、
/// 原生 codec、SKFont 单行测量与像素边界转换（plan §6.2 / misc §H）。
/// 同一份源码被 <c>ImageFacade.SkiaSharp</c>（SKIA4）与 <c>ImageFacade.SkiaSharp3</c>（SKIA3）链接编译。
/// </remarks>
public sealed class SkiaBackend : IImageBackend
{
    private static readonly IReadOnlySet<ImageFormat> Formats = new HashSet<ImageFormat>
    {
        ImageFormat.Png,
        ImageFormat.Jpeg,
        ImageFormat.Bmp,
        ImageFormat.Gif,
        ImageFormat.WebP,
        ImageFormat.Tiff,
        ImageFormat.Ico,
    };

    public string Name => "skia";

    public Version Version => typeof(SkiaBackend).Assembly.GetName().Version ?? new Version(0, 1, 0);

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
        | BackendCapabilities.IccProfile
        | BackendCapabilities.ZeroCopyPixels
        | BackendCapabilities.AotCompatible;

    public IReadOnlySet<ImageFormat> SupportedDecodeFormats => Formats;

    public IReadOnlySet<ImageFormat> SupportedEncodeFormats => Formats;

    public IFrame Create(int width, int height, Rgba32? background) => throw Todo();

    public bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame) => throw Todo();

    public IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata) => throw Todo();

    public IFrame Clone(IFrame frame) => throw Todo();

    public void Encode(IFrame frame, Stream destination, in EncodeRequest request) => throw Todo();

    public bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result) => throw Todo();

    public bool TryDrawFrame(IFrame destination, IFrame source, PointF offset) => throw Todo();

    public bool TryDrawPoint(IFrame frame, in PointSpec spec) => throw Todo();

    public bool TryDrawLine(IFrame frame, in LineSpec spec) => throw Todo();

    public bool TryDrawPolyline(IFrame frame, in PolylineSpec spec) => throw Todo();

    public bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec) => throw Todo();

    public bool TryFillRectangle(IFrame frame, in FilledRectSpec spec) => throw Todo();

    public bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec) => throw Todo();

    public bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec) => throw Todo();

    public bool TryFillPolygon(IFrame frame, in PolygonSpec spec) => throw Todo();

    public bool TryDrawText(IFrame frame, in TextSpec spec) => throw Todo();

    public bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics) => throw Todo();

    public void Clear(IFrame frame, Rgba32 color) => throw Todo();

    public bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color) => throw Todo();

    public bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride) => throw Todo();

    public void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride) => throw Todo();

    public bool TryAcquirePixels(IFrame frame, out PixelLease lease) => throw Todo();

    public void Dispose()
    {
    }

    private static NotImplementedException Todo()
        => new("SkiaSharp 后端尚未实现（M3，plan §6.2）。");
}
