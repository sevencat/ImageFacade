using ImageFacade.Backends;

namespace ImageFacade.OpenCvSharp;

/// <summary>
/// OpenCvSharp 适配后端。
/// </summary>
/// <remarks>
/// M0 只有契约骨架；M6 实现"帧 = Mat(CV_8UC4) + 整数坐标量化 + 不做 alpha 混合"（plan §6.4 / misc §G）。
/// 同一份源码被 <c>ImageFacade.OpenCvSharp</c>（OPENCV4）与 <c>ImageFacade.OpenCvSharp5</c>（OPENCV5）链接编译。
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

    public string Name => "opencv";

    public Version Version => typeof(OpenCvBackend).Assembly.GetName().Version ?? new Version(0, 1, 0);

    /// <summary>注意：**没有** <see cref="BackendCapabilities.AlphaBlending"/>，也没有 CJK 文字。</summary>
    public BackendCapabilities Capabilities =>
        BackendCapabilities.Resize
        | BackendCapabilities.DrawShapes
        | BackendCapabilities.DrawPoints
        | BackendCapabilities.LossyEncode
        | BackendCapabilities.ZeroCopyPixels;

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
        => new("OpenCvSharp 后端尚未实现（M6，plan §6.4）。");
}
