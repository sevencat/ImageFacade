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

    /// <summary>
    /// 单行测量的**切片版**：<paramref name="text"/> 可以只是调用方缓冲区里的一段，不必是独立的 string。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 折行时门面要量的是"段落从 <c>start</c> 到 <c>end</c>"的每个候选前缀，长度递增地量一遍。
    /// 走 <see cref="TextSpec"/> 那条路每个候选都得先 <c>Substring</c> 出一份 string，一行的候选数量
    /// 是 O(n)，整段就是 O(n²) 个短命字符串；直接吃切片可以全部省掉。
    /// </para>
    /// <para>
    /// 默认实现把切片 <c>new string(...)</c> 后转调 <see cref="TryMeasureText(IFrame, in TextSpec, out TextMetrics)"/>，
    /// 所以**结果与旧路径完全一致**，已有后端不必改动。底层库本身就支持切片的后端应当重写它
    /// （Skia 的 <c>SKFont.MeasureText(ReadOnlySpan&lt;char&gt;)</c>、SixLabors.Fonts 的
    /// <c>TextMeasurer.MeasureAdvance(ReadOnlySpan&lt;char&gt;, …)</c>、本仓库托管字体栈的
    /// <see cref="Font.Measure(ReadOnlySpan{char}, float)"/>），重写后这条路径零分配。
    /// </para>
    /// </remarks>
    bool TryMeasureText(IFrame frame, ReadOnlySpan<char> text, in TextStyle style, out TextMetrics metrics)
    {
        var spec = new TextSpec(new string(text), PointF.Empty, style);
        return TryMeasureText(frame, in spec, out metrics);
    }

    // ---- 像素 ----

    void Clear(IFrame frame, Rgba32 color);

    bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color);

    /// <summary>导出规范像素（直通 RGBA32，顶行优先），逐行写 <paramref name="destinationStride"/>，不碰填充字节。</summary>
    bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride);

    /// <summary>从规范像素导入（覆盖全部有效像素）。</summary>
    void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride);

    bool TryAcquirePixels(IFrame frame, out PixelLease lease);
}
