namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 路径构建器。方法名与 ImageSharp.Drawing 的 <c>PathBuilder</c> 对齐（<c>CloseFigure</c> 而非 <c>Close</c>）。
/// 构建完成后 <see cref="Build"/> 返回不可变 <see cref="Path"/>，builder 可以继续复用。
/// </summary>
internal sealed partial class PathBuilder
{
    private readonly List<PathCommand> _commands = new();
    private PointF _current;
    private PointF _figureStart;
    private bool _figureOpen;

    /// <summary>开始一条新子路径。</summary>
    public PathBuilder MoveTo(float x, float y) => MoveTo(new PointF(x, y));

    /// <summary>开始一条新子路径。</summary>
    public PathBuilder MoveTo(PointF point)
    {
        _commands.Add(new PathCommand(PathCommandKind.MoveTo, point));
        _current = point;
        _figureStart = point;
        _figureOpen = true;
        return this;
    }

    /// <summary>连线到指定点；没有前序 <c>MoveTo</c> 时自动从该点起笔。</summary>
    public PathBuilder LineTo(float x, float y) => LineTo(new PointF(x, y));

    /// <summary>连线到指定点。</summary>
    public PathBuilder LineTo(PointF point)
    {
        EnsureFigure();
        _commands.Add(new PathCommand(PathCommandKind.LineTo, point));
        _current = point;
        return this;
    }

    /// <summary>二次贝塞尔（内部转成三次，控制点按 2/3 规则换算）。</summary>
    public PathBuilder QuadraticTo(float controlX, float controlY, float x, float y)
        => QuadraticTo(new PointF(controlX, controlY), new PointF(x, y));

    /// <summary>二次贝塞尔。</summary>
    public PathBuilder QuadraticTo(PointF control, PointF end)
    {
        EnsureFigure();
        PointF c1 = new(
            _current.X + ((control.X - _current.X) * 2f / 3f),
            _current.Y + ((control.Y - _current.Y) * 2f / 3f));
        PointF c2 = new(
            end.X + ((control.X - end.X) * 2f / 3f),
            end.Y + ((control.Y - end.Y) * 2f / 3f));
        return CubicTo(c1, c2, end);
    }

    /// <summary>三次贝塞尔。</summary>
    public PathBuilder CubicTo(float control1X, float control1Y, float control2X, float control2Y, float x, float y)
        => CubicTo(new PointF(control1X, control1Y), new PointF(control2X, control2Y), new PointF(x, y));

    /// <summary>三次贝塞尔。</summary>
    public PathBuilder CubicTo(PointF control1, PointF control2, PointF end)
    {
        EnsureFigure();
        _commands.Add(new PathCommand(PathCommandKind.CubicTo, control1, control2, end));
        _current = end;
        return this;
    }

    /// <summary>闭合当前子路径（之后继续画线会从子路径起点开始新的一条）。</summary>
    public PathBuilder CloseFigure()
    {
        if (_figureOpen)
        {
            _commands.Add(new PathCommand(PathCommandKind.Close, _figureStart));
            _current = _figureStart;
        }

        return this;
    }

    /// <summary>连到 <paramref name="point"/>，可选地立即闭合。</summary>
    public PathBuilder AddSegment(PointF point, bool isClosed)
    {
        LineTo(point);
        return isClosed ? CloseFigure() : this;
    }

    /// <summary>生成不可变路径。</summary>
    public Path Build() => new(_commands.ToArray());

    private void EnsureFigure()
    {
        if (!_figureOpen)
        {
            MoveTo(_current);
        }
    }
}
