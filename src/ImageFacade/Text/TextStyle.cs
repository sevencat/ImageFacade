namespace ImageFacade;

public enum TextAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>绘制原点语义：<see cref="TopLeft"/> 是排版框左上角，<see cref="Baseline"/> 是首行基线。</summary>
public enum TextAnchor
{
    TopLeft = 0,
    Baseline = 1,
}

/// <summary>
/// 文字样式。折行与对齐在门面层做（后端只画单行），所以后端看到的 <see cref="Backends.TextSpec"/>
/// 永远是"一行 + 基线"。<see cref="MaxWidth"/>/<see cref="Align"/> 只是给排版器看的。
/// </summary>
public sealed class TextStyle
{
    public FontRef Font { get; init; } = FontRef.BuiltIn;

    /// <summary>
    /// 已加载的字体实例；非空时**优先于** <see cref="Font"/>，托管文字后端（软件后端 / OpenCV 5 适配）
    /// 直接复用它，不会重新解析字体表。
    /// </summary>
    /// <remarks>
    /// 原生文字后端（Skia / ImageSharp）按 <see cref="Font"/> 解析平台字体，忽略本属性。
    /// 想反复画同一批字又不重复解析时，用 <c>Font.BuiltIn</c> 或自己 <c>Font.Load(...)</c> 一次然后一直传。
    /// </remarks>
    public Font? FontFace { get; init; }

    public float Size { get; init; } = 16f;

    public Rgba32 Color { get; init; } = Rgba32.Black;

    /// <summary>0 = 不折行。</summary>
    public float MaxWidth { get; init; }

    /// <summary>0 = 使用字体的行高。</summary>
    public float LineHeight { get; init; }

    public TextAlign Align { get; init; } = TextAlign.Left;

    public TextAnchor Anchor { get; init; } = TextAnchor.TopLeft;

    public bool Antialias { get; init; } = true;

    internal void Validate()
    {
        if (Size <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(Size), Size, "字号必须大于 0。");
        }

        if (MaxWidth < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxWidth), MaxWidth, "MaxWidth 不能为负（0 表示不折行）。");
        }

        if (LineHeight < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(LineHeight), LineHeight, "LineHeight 不能为负（0 表示用字体行高）。");
        }

        ArgumentNullException.ThrowIfNull(Font, nameof(Font));
    }
}
