namespace ImageFacade.Software.Rendering;


/// <summary>
/// 描边几何生成需要的样式参数。虚线长度在这里已经换算成**像素**（乘了实际线宽），
/// 因为图案单位是「线宽倍数」，线宽又会跟着变换缩放。
/// </summary>
internal readonly struct StrokeStyle
{
    internal StrokeStyle(
        LineJoin join,
        LineCap cap,
        float miterLimit,
        float[]? dashPattern,
        float dashOffset)
    {
        Join = join;
        Cap = cap;
        MiterLimit = miterLimit;
        DashPattern = dashPattern;
        DashOffset = dashOffset;
    }

    internal LineJoin Join { get; }

    internal LineCap Cap { get; }

    internal float MiterLimit { get; }

    /// <summary>虚线长度（像素，实 / 虚交替）；<c>null</c> 表示实线。</summary>
    internal float[]? DashPattern { get; }

    /// <summary>虚线起始偏移（像素，可为负）。</summary>
    internal float DashOffset { get; }

    /// <summary>把画笔里的样式换算成像素单位；奇数长度的图案按 SVG 语义重复一遍。</summary>
    internal static StrokeStyle Create(
        LineJoin join,
        LineCap cap,
        float miterLimit,
        float[]? dashPattern,
        float dashOffset,
        float patternUnit)
    {
        float[]? scaled = null;

        if (dashPattern is { Length: > 0 })
        {
            int length = dashPattern.Length % 2 == 0 ? dashPattern.Length : dashPattern.Length * 2;
            scaled = new float[length];
            for (int i = 0; i < length; i++)
            {
                scaled[i] = dashPattern[i % dashPattern.Length] * patternUnit;
            }
        }

        return new StrokeStyle(join, cap, miterLimit, scaled, dashOffset * patternUnit);
    }
}
