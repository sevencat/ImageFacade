namespace ImageFacade;

/// <summary>解码时读取到的元数据（只读，v1 只透传不编辑）。</summary>
public readonly struct ImageMetadata
{
    public ImageMetadata(
        ImageFormat format,
        int width,
        int height,
        int orientation = 0,
        bool hasAlpha = false,
        ReadOnlyMemory<byte> iccProfile = default,
        bool? hasTransparency = null)
    {
        Format = format;
        Width = width;
        Height = height;
        Orientation = orientation;
        HasAlpha = hasAlpha;
        IccProfile = iccProfile;
        HasTransparency = hasTransparency;
    }

    public ImageFormat Format { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// EXIF 方向 1..8；0 表示未知或未提供。已在解码时应用过 <see cref="LoadOptions.AutoOrient"/> 的图，尺寸就是转正后的尺寸。
    /// </summary>
    public int Orientation { get; }

    /// <summary>
    /// 有没有 alpha **通道**——解码器声明的能力位，"可能含透明像素"。
    /// 它不等于"图里真的有半透明像素"（4 通道但全不透明的图很常见），后者看 <see cref="HasTransparency"/>。
    /// </summary>
    public bool HasAlpha { get; }

    /// <summary>
    /// 图里是否**真的存在** alpha &lt; 255 的像素：<c>true</c> 有、<c>false</c> 全不透明、<c>null</c> 没查过。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 由 <see cref="ImageEngine.Load(string, LoadOptions?)"/> / <see cref="ImageEngine.Load(ReadOnlySpan{byte}, LoadOptions?)"/>
    /// 等加载入口在解码后检查一次（<see cref="LoadOptions.DetectTransparency"/>，默认开）；
    /// 能直接回答的后端（<see cref="Backends.IImageBackend.TryGetTransparency"/>）就直接给答案，否则门面扫一遍像素。
    /// </para>
    /// <para>
    /// **任何改像素的操作（绘制 / 清空 / 缩放补边）之后都会退回 <c>null</c> 或按操作重新推导**——
    /// 抗锯齿和半透明画笔随时能把一张"全不透明"变掉。编辑之后要准确值，调用
    /// <see cref="FacadeImage.DetectTransparency(bool)"/> 重算一次。
    /// </para>
    /// </remarks>
    public bool? HasTransparency { get; }

    /// <summary>ICC 配置（后端支持时透传；空表示无）。</summary>
    public ReadOnlyMemory<byte> IccProfile { get; }

    public static ImageMetadata Empty => default;

    /// <summary>换一个 alpha 结论（其余字段不动）；门面在像素变化后用它刷新缓存。</summary>
    internal ImageMetadata WithHasTransparency(bool? hasTransparency)
        => new(Format, Width, Height, Orientation, HasAlpha, IccProfile, hasTransparency);

    public override string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Format} {Width}x{Height}");
}
