namespace ImageFacade.Backends;

/// <summary>
/// 实心圆点：以 <see cref="Point"/> 为圆心、<see cref="Radius"/> 为半径，等价于 <c>DrawingContext.Fill(CirclePolygon)</c>。
/// </summary>
public readonly struct PointSpec
{
    public PointSpec(PointF point, Rgba32 color, float radius = 1.5f, bool antialias = true)
    {
        Point = point;
        Color = color;
        Radius = radius;
        Antialias = antialias;
    }

    public PointF Point { get; }

    public Rgba32 Color { get; }

    public float Radius { get; }

    public bool Antialias { get; }

    public PointF Center => Point;
}

public readonly struct LineSpec
{
    public LineSpec(PointF from, PointF to, Rgba32 color, float width = 1f, StrokeStyle? style = null)
    {
        From = from;
        To = to;
        Color = color;
        Width = width;
        Style = style;
    }

    public PointF From { get; }

    public PointF To { get; }

    public Rgba32 Color { get; }

    public float Width { get; }

    public StrokeStyle? Style { get; }
}

public readonly ref struct PolylineSpec
{
    public PolylineSpec(ReadOnlySpan<PointF> points, Rgba32 color, float width = 1f, bool closed = false, StrokeStyle? style = null)
    {
        Points = points;
        Color = color;
        Width = width;
        Closed = closed;
        Style = style;
    }

    public ReadOnlySpan<PointF> Points { get; }

    public Rgba32 Color { get; }

    public float Width { get; }

    public bool Closed { get; }

    public StrokeStyle? Style { get; }
}

public readonly struct StrokedRectSpec
{
    public StrokedRectSpec(RectF rect, Rgba32 color, float width = 1f, StrokeStyle? style = null)
    {
        Rect = rect;
        Color = color;
        Width = width;
        Style = style;
    }

    public RectF Rect { get; }

    public Rgba32 Color { get; }

    public float Width { get; }

    public StrokeStyle? Style { get; }
}

public readonly struct FilledRectSpec
{
    public FilledRectSpec(RectF rect, Rgba32 color)
    {
        Rect = rect;
        Color = color;
    }

    public RectF Rect { get; }

    public Rgba32 Color { get; }
}

public readonly struct StrokedEllipseSpec
{
    public StrokedEllipseSpec(RectF bounds, Rgba32 color, float width = 1f, StrokeStyle? style = null)
    {
        Bounds = bounds;
        Color = color;
        Width = width;
        Style = style;
    }

    public RectF Bounds { get; }

    public Rgba32 Color { get; }

    public float Width { get; }

    public StrokeStyle? Style { get; }
}

public readonly struct FilledEllipseSpec
{
    public FilledEllipseSpec(RectF bounds, Rgba32 color)
    {
        Bounds = bounds;
        Color = color;
    }

    public RectF Bounds { get; }

    public Rgba32 Color { get; }
}

public readonly ref struct PolygonSpec
{
    public PolygonSpec(ReadOnlySpan<PointF> points, Rgba32 color)
    {
        Points = points;
        Color = color;
    }

    public ReadOnlySpan<PointF> Points { get; }

    public Rgba32 Color { get; }
}

/// <summary>
/// 已经排好版的**单行**文字。
/// </summary>
/// <remarks>
/// <see cref="Origin"/> 的解释由 <see cref="TextStyle.Anchor"/> 决定：
/// <c>TopLeft</c> = 该行排版框左上角（后端自己加 ascent 得到基线）；<c>Baseline</c> = 该行基线起点。
/// 让后端按自己的字体度量换算，比门面猜 ascent 更准（plan §3.4 / misc §J.2）。
/// </remarks>
public readonly struct TextSpec
{
    public TextSpec(string text, PointF origin, TextStyle style)
    {
        Text = text;
        Origin = origin;
        Style = style;
    }

    public string Text { get; }

    public PointF Origin { get; }

    public TextStyle Style { get; }
}
