namespace ImageFacade.Software.Rendering.Text;

/// <summary>排版选项。</summary>
internal readonly record struct TextLayoutOptions
{
    /// <summary>最大行宽（像素），超过则换行；0 表示不自动换行（只按 \n 断行）。</summary>
    public float MaxWidth { get; init; }

    /// <summary>行高（像素），0 表示使用字体默认行高。</summary>
    public float LineHeight { get; init; }

    /// <summary>行内对齐方式，参照 <see cref="MaxWidth"/>（为 0 时用最宽行）计算每行偏移。</summary>
    public TextAlign Align { get; init; }
}

/// <summary>
/// 一个已定位的字形。<see cref="X"/>/<see cref="Y"/> 是位图左上角相对排版原点的像素坐标，
/// <see cref="Line"/> 是它所属的行号（从 0 开始，对齐与逐行绘制时用）。
/// </summary>
internal readonly record struct PositionedGlyph(GlyphMask Mask, int X, int Y, int Line = 0);

/// <summary>排版结果：可直接交给外部绘制，或取 <see cref="Glyphs"/> 自己混合。</summary>
internal sealed class TextLayout
{
    public TextLayout(
        IReadOnlyList<PositionedGlyph> glyphs,
        float width,
        float height,
        float ascent,
        float lineHeight,
        int lineCount,
        IReadOnlyList<float> lineWidths)
    {
        Glyphs = glyphs;
        Width = width;
        Height = height;
        Ascent = ascent;
        LineHeight = lineHeight;
        LineCount = lineCount;
        LineWidths = lineWidths;
    }

    /// <summary>已定位的非空字形（空格等不占位）。</summary>
    public IReadOnlyList<PositionedGlyph> Glyphs { get; }

    /// <summary>排版宽度（像素），即最宽行的自然前进宽度。</summary>
    public float Width { get; }

    /// <summary>排版高度（像素）。</summary>
    public float Height { get; }

    /// <summary>第一行基线相对排版原点的 y 偏移。</summary>
    public float Ascent { get; }

    /// <summary>行高（像素）。</summary>
    public float LineHeight { get; }

    /// <summary>行数。</summary>
    public int LineCount { get; }

    /// <summary>每行的自然前进宽度（像素），长度 = <see cref="LineCount"/>。</summary>
    public IReadOnlyList<float> LineWidths { get; }
}
