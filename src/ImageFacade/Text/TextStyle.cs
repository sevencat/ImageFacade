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

/// <summary>折行位置的搜索方式（<see cref="TextStyle.WrapMode"/>）。</summary>
public enum TextWrapMode
{
    /// <summary>
    /// 逐长度线性扫描：对任意字体都能按"长度递增"找到第一个超宽前缀。默认值，与旧版本逐字符一致。
    /// 测量次数是 O(n²)，长段落 + 窄行宽时是折行的主要开销。
    /// </summary>
    Exact = 0,

    /// <summary>
    /// 二分搜索：**假设同一行内单字符前进宽度随长度单调不减**，把测量次数降到约 O(n log n)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 适用：CJK、等宽字体、按空格断行的常规西文正文——这些场景下"更长的前缀不会更窄"成立。
    /// 不适用：混合字体回退（同一个字符在不同字体里前进宽度不同）、连字脚本（阿拉伯文 / 天城文等）、
    /// kerning 密集的排版；假设不成立时可能与 <see cref="Exact"/> 折出不同的行。
    /// **需要与旧版本严格一致时请保留默认的 <see cref="Exact"/>。**
    /// </para>
    /// <para>
    /// 二分过程中只要发现"更长的前缀反而更窄"，整行自动退回线性扫描，结果与 <see cref="Exact"/> 相同，
    /// 不抛异常；代价是最坏情况比线性扫描多 O(log n) 次测量。探针没覆盖到的位置无法判断，这是可选
    /// 路径的固有边界。
    /// </para>
    /// </remarks>
    Monotonic = 1,
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

    /// <summary>
    /// 折行位置的搜索方式。默认 <see cref="TextWrapMode.Exact"/>（逐长度线性扫描，与旧版本逐字符一致）；
    /// <see cref="TextWrapMode.Monotonic"/> 假定前缀宽度随长度单调不减，用二分把测量次数降到约
    /// O(n log n)，检测到非单调时整行自动退回线性扫描。
    /// </summary>
    /// <remarks>
    /// 两条路径除"断点怎么找"之外的规则完全相同（空白裁剪、<c>\n</c> 硬断行、超长单词、行高、
    /// 末尾空行）。CJK / 等宽 / 常规西文正文适合 <see cref="TextWrapMode.Monotonic"/>；
    /// 艺术字体、连字脚本、kerning 密集的文本建议用 <see cref="TextWrapMode.Exact"/>。
    /// </remarks>
    public TextWrapMode WrapMode { get; init; } = TextWrapMode.Exact;

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