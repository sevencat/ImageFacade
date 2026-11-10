namespace ImageFacade.Backends;

/// <summary>
/// 后端 SPI（服务提供者接口）。实现者只需关心"把像素画对"，
/// 语义（坐标系、Resize 模式、文字锚点、JPEG 底色）全部由门面定义。
/// </summary>
/// <remarks>
/// <para>
/// <b>硬规则</b>：所有 <c>Try*</c> 必须在**任何像素被修改之前**完成校验；
/// 返回 <c>false</c> 时帧必须保持原样，否则回退会把"画了一半的图"再画一遍（plan §2.2）。
/// </para>
/// <para>实现必须无状态（字体缓存除外，用 <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>）。</para>
/// </remarks>
public interface IImageBackend : IDisposable
{
    /// <summary>短名，如 "skia" / "opencv" / "imagesharp" / "software"。</summary>
    string Name { get; }

    Version Version { get; }

    /// <summary>仅供参考，路由以 <c>Try*</c> 为准。</summary>
    BackendCapabilities Capabilities { get; }

    IReadOnlySet<ImageFormat> SupportedDecodeFormats { get; }

    IReadOnlySet<ImageFormat> SupportedEncodeFormats { get; }

    // ---- 生命周期 ----

    IFrame Create(int width, int height, Rgba32? background);

    /// <summary>尝试零拷贝接管外部缓冲（仅规范 RGBA32）。返回 false 不报错，由门面退化成导入。</summary>
    bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame);

    IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata);

    IFrame Clone(IFrame frame);

    void Encode(IFrame frame, Stream destination, in EncodeRequest request);

    // ---- 几何 ----

    /// <summary>只做"拉伸"；六种模式（Crop/Pad/...）由门面规划。</summary>
    bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result);

    /// <summary>1:1 贴图（Crop/Pad 与 letterbox 用），<paramref name="offset"/> 为目标帧内的左上角偏移。</summary>
    bool TryDrawFrame(IFrame destination, IFrame source, PointF offset);

    // ---- 绘制（返回 false = 未修改任何像素）----

    bool TryDrawPoint(IFrame frame, in PointSpec spec);

    bool TryDrawLine(IFrame frame, in LineSpec spec);

    bool TryDrawPolyline(IFrame frame, in PolylineSpec spec);

    bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec);

    bool TryFillRectangle(IFrame frame, in FilledRectSpec spec);

    bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec);

    bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec);

    bool TryFillPolygon(IFrame frame, in PolygonSpec spec);

    /// <summary>单行、已排版；<see cref="TextSpec.Origin"/> 按 <see cref="TextStyle.Anchor"/> 解释。</summary>
    bool TryDrawText(IFrame frame, in TextSpec spec);

    /// <summary>单行测量（advance 宽 + 字体度量）。门面的 <c>MeasureText</c> 与 <c>DrawText</c> 共用这份结果。</summary>
    bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics);

    // ---- 像素 ----

    void Clear(IFrame frame, Rgba32 color);

    bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color);

    /// <summary>导出规范像素（直通 RGBA32，顶行优先），逐行写 <paramref name="destinationStride"/>，不碰填充字节。</summary>
    bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride);

    /// <summary>从规范像素导入（覆盖全部有效像素）。</summary>
    void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride);

    bool TryAcquirePixels(IFrame frame, out PixelLease lease);
}
