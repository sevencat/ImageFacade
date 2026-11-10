namespace ImageFacade;

public enum LineCap
{
    Butt = 0,
    Round = 1,
    Square = 2,
}

public enum LineJoin
{
    Miter = 0,
    Bevel = 1,
    Round = 2,
}

/// <summary>
/// 描边样式。后端原生支持就原生画；不支持（例如 OpenCV 没有虚线/斜接连接）时
/// <c>Try*</c> 返回 <c>false</c>，由门面回退到软后端。
/// </summary>
public sealed class StrokeStyle
{
    public static StrokeStyle Default { get; } = new();

    public LineCap Cap { get; init; } = LineCap.Round;

    public LineJoin Join { get; init; } = LineJoin.Round;

    public float MiterLimit { get; init; } = 4f;

    /// <summary>虚线模式，长度按线宽倍数解释；null / 空 = 实线。</summary>
    public float[]? DashPattern { get; init; }

    public float DashOffset { get; init; }

    public bool Antialias { get; init; } = true;

    public bool HasDash => DashPattern is { Length: > 0 };
}
