namespace ImageFacade.Pipeline;

/// <summary>
/// letterbox 的几何信息：把模型坐标反算回源图坐标（以及反向映射）。
/// </summary>
/// <remarks>
/// 映射是"等比缩放 + 平移"：<c>model = source * Scale + Pad</c>。
/// 补边居中时 <see cref="PadLeft"/> / <see cref="PadTop"/> 为正；不居中时两者为 0。
/// </remarks>
public readonly struct LetterboxInfo
{
    internal LetterboxInfo(Size sourceSize, Size modelSize, float scale, int padLeft, int padTop)
    {
        SourceSize = sourceSize;
        ModelSize = modelSize;
        Scale = scale;
        PadLeft = padLeft;
        PadTop = padTop;
    }

    /// <summary>letterbox 之前的源图尺寸。</summary>
    public Size SourceSize { get; }

    /// <summary>letterbox 之后的模型输入尺寸。</summary>
    public Size ModelSize { get; }

    /// <summary>实际缩放比例（源图 → 模型），等于 <c>min(modelW / srcW, modelH / srcH)</c> 的整数化结果。</summary>
    public float Scale { get; }

    /// <summary>左侧补边宽度（像素）。</summary>
    public int PadLeft { get; }

    /// <summary>顶部补边高度（像素）。</summary>
    public int PadTop { get; }

    /// <summary>缩放后、补边前的实际尺寸。</summary>
    public Size ScaledSize => new((int)MathF.Round(SourceSize.Width * Scale), (int)MathF.Round(SourceSize.Height * Scale));

    public bool IsEmpty => Scale <= 0f;

    /// <summary>模型坐标 → 源图坐标。</summary>
    public PointF ToSource(float x, float y)
        => new((x - PadLeft) / Scale, (y - PadTop) / Scale);

    /// <summary>模型坐标下的矩形 → 源图坐标下的矩形（宽高同样按 <see cref="Scale"/> 还原）。</summary>
    public RectF ToSource(RectF modelBox)
        => new(
            (modelBox.X - PadLeft) / Scale,
            (modelBox.Y - PadTop) / Scale,
            modelBox.Width / Scale,
            modelBox.Height / Scale);

    /// <summary>源图坐标 → 模型坐标。</summary>
    public PointF ToModel(float x, float y)
        => new((x * Scale) + PadLeft, (y * Scale) + PadTop);

    /// <summary>源图坐标下的矩形 → 模型坐标下的矩形。</summary>
    public RectF ToModel(RectF sourceBox)
        => new(
            (sourceBox.X * Scale) + PadLeft,
            (sourceBox.Y * Scale) + PadTop,
            sourceBox.Width * Scale,
            sourceBox.Height * Scale);

    public override string ToString()
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"src={SourceSize} model={ModelSize} scale={Scale:0.####} pad=({PadLeft},{PadTop})");
}
