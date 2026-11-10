namespace ImageFacade.Software.Rendering;


/// <summary>
/// 描边画笔：颜色、线宽与描边样式。命名与 ImageSharp.Drawing 的 <c>IPen</c> 对齐。
/// <para>
/// 除 <see cref="Color"/> 与 <see cref="Width"/> 外都带默认实现，因此只关心颜色的自定义画笔
/// 仍然可以只实现这两个成员，行为等同 <see cref="Pen"/> 的默认值（圆角连接 + 圆头端点 + 实线）。
/// 虚线图案的长度单位是**线宽倍数**（线宽 4、图案 <c>[3, 2]</c> 表示 12 px 实、8 px 虚）。
/// </para>
/// </summary>
internal interface IPen
{
    /// <summary>线条颜色。</summary>
    Color Color { get; }

    /// <summary>线宽（画布像素，必须为正）。描边时的实际宽度会被当前变换的缩放因子放大。</summary>
    float Width { get; }

    /// <summary>转角连接方式。默认 <see cref="ImageFacade.Software.Rendering.LineJoin.Round"/>。</summary>
    LineJoin LineJoin => LineJoin.Round;

    /// <summary>开放路径的端点形状。默认 <see cref="ImageFacade.Software.Rendering.LineCap.Round"/>。</summary>
    LineCap LineCap => LineCap.Round;

    /// <summary>
    /// 尖角的最大长度 / 半线宽。小于该比值才画 miter，否则退回 bevel。默认 4（与 SVG 一致）。
    /// 只在 <see cref="LineJoin"/> 为 <see cref="ImageFacade.Software.Rendering.LineJoin.Miter"/> 时生效。
    /// </summary>
    float MiterLimit => 4f;

    /// <summary>
    /// 虚线图案：偶数个非负长度，交替表示「实 / 虚」，单位是线宽倍数。奇数长度视为重复一遍
    /// （`[4]` 等价于 `[4, 4]`）。<c>null</c> 或空数组表示实线（默认）。
    /// 返回的数组由画笔持有，调用方不要修改。
    /// </summary>
    float[]? DashPattern => null;

    /// <summary>虚线图案的起始偏移（线宽倍数，可为负）。默认 0。</summary>
    float DashOffset => 0f;
}


/// <summary>最常用的画笔：单一颜色 + 线宽 + 可选样式。样式通过对象初始化器设置。</summary>
/// <example>
/// <code>
/// var pen = new Pen(Color.Black, 4f)
/// {
///     LineJoin = LineJoin.Miter,
///     MiterLimit = 2f,
///     LineCap = LineCap.Butt,
///     DashPattern = [6f, 3f],
/// };
/// </code>
/// </example>
internal sealed class Pen : IPen
{
    private readonly float[]? _dashPattern;
    private readonly LineJoin _lineJoin = LineJoin.Round;
    private readonly LineCap _lineCap = LineCap.Round;
    private readonly float _miterLimit = 4f;
    private readonly float _dashOffset;

    public Pen(Color color, float width = 1f)
    {
        if (!(width > 0f) || float.IsInfinity(width))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "线宽必须是有限正数。");
        }

        Color = color;
        Width = width;
    }

    public Color Color { get; }

    public float Width { get; }

    public LineJoin LineJoin
    {
        get => _lineJoin;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "未知的连接方式。");
            }

            _lineJoin = value;
        }
    }

    public LineCap LineCap
    {
        get => _lineCap;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "未知的端点形状。");
            }

            _lineCap = value;
        }
    }

    public float MiterLimit
    {
        get => _miterLimit;
        init
        {
            if (!(value >= 1f) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MiterLimit 必须 ≥ 1 且有限。");
            }

            _miterLimit = value;
        }
    }

    public float[]? DashPattern
    {
        get => _dashPattern;
        init => _dashPattern = NormalizeDashPattern(value);
    }

    public float DashOffset
    {
        get => _dashOffset;
        init
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "虚线偏移必须是有限数。");
            }

            _dashOffset = value;
        }
    }

    /// <summary>校验并复制图案；奇数长度按 SVG 语义重复一遍，保证「实 / 虚」交替闭合。</summary>
    private static float[]? NormalizeDashPattern(float[]? pattern)
    {
        if (pattern is null || pattern.Length == 0)
        {
            return null;
        }

        double total = 0d;
        foreach (float length in pattern)
        {
            if (!(length >= 0f) || float.IsInfinity(length))
            {
                throw new ArgumentOutOfRangeException(nameof(pattern), length, "虚线长度必须是非负有限数。");
            }

            total += length;
        }

        if (!(total > 0d))
        {
            throw new ArgumentException("虚线图案不能全为 0。", nameof(pattern));
        }

        int length2 = pattern.Length % 2 == 0 ? pattern.Length : pattern.Length * 2;
        var copy = new float[length2];
        for (int i = 0; i < length2; i++)
        {
            copy[i] = pattern[i % pattern.Length];
        }

        return copy;
    }
}
