namespace ImageFacade.Backends;

/// <summary>
/// 单行文字的度量。<see cref="Width"/> 是 advance 宽（排版用），<see cref="LineHeight"/> 是行高。
/// </summary>
/// <remarks>
/// 这个类型是 M1 对 plan §5.1 的补充：门面要做折行 / 对齐 / 多行间距，必须知道行高与 ascent，
/// 而"只返回一个 SizeF"无法区分"墨迹高度"和"行高"。
/// </remarks>
public readonly struct TextMetrics
{
    public TextMetrics(float width, float ascent, float descent, float lineGap = 0f)
    {
        Width = width;
        Ascent = ascent;
        Descent = descent;
        LineGap = lineGap;
    }

    /// <summary>advance 宽（不是墨迹包围盒）。</summary>
    public float Width { get; }

    /// <summary>基线上方高度（正数）。</summary>
    public float Ascent { get; }

    /// <summary>基线下方高度（正数）。</summary>
    public float Descent { get; }

    public float LineGap { get; }

    /// <summary>行高 = ascent + descent + lineGap。</summary>
    public float LineHeight => Ascent + Descent + LineGap;
}
