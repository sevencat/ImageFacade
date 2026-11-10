namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 把路径命令展平成折线。<see cref="Path"/> 的曲线用自适应细分（de Casteljau 二分），
/// 判据是控制点到弦的距离 + 是否在弦同侧（防 S 形曲线被过早判定为直）。
/// </summary>
internal sealed class PathFlattener
{
    private const int MaxDepth = 16;

    private readonly float _tolerance;
    private readonly float _toleranceSquared;
    private readonly List<PointF> _points = new();
    private readonly List<int> _starts = new();

    private PointF _current;
    private PointF _figureStart;
    private PointF _pendingStart;
    private bool _hasPendingStart;
    private int _contourStart = -1;

    internal PathFlattener(float tolerance)
    {
        _tolerance = MathF.Max(tolerance, 1e-4f);
        _toleranceSquared = _tolerance * _tolerance;
    }

    internal void MoveTo(PointF point)
    {
        _pendingStart = point;
        _hasPendingStart = true;
        _figureStart = point;
        _current = point;
        _contourStart = -1;
    }

    internal void LineTo(PointF point)
    {
        EnsureContour();
        _points.Add(point);
        _current = point;
    }

    internal void CubicTo(PointF control1, PointF control2, PointF end)
        => FlattenCubic(_current, control1, control2, end, 0);

    internal void CloseFigure()
    {
        if (_contourStart >= 0 && _points.Count - _contourStart >= 2 && _current != _figureStart)
        {
            _points.Add(_figureStart);
        }

        // 闭合后继续画线，从子路径起点开始新的一条（与 Canvas/SVG 语义一致）
        _pendingStart = _figureStart;
        _hasPendingStart = true;
        _current = _figureStart;
        _contourStart = -1;
    }

    internal FlattenedPath Build()
    {
        // 未闭合的子路径在填充时同样按闭合处理，这里不做补点，光栅化器负责隐式闭合
        var starts = new int[_starts.Count + 1];
        for (int i = 0; i < _starts.Count; i++)
        {
            starts[i] = _starts[i];
        }

        starts[^1] = _points.Count;
        PointF[] points = _points.ToArray();
        return new FlattenedPath(points, starts, ComputeBounds(points));
    }

    private void EnsureContour()
    {
        if (_contourStart >= 0)
        {
            return;
        }

        if (!_hasPendingStart)
        {
            _pendingStart = _current;
            _hasPendingStart = true;
        }

        _starts.Add(_points.Count);
        _points.Add(_pendingStart);
        _contourStart = _points.Count - 1;
        _hasPendingStart = false;
    }

    private void FlattenCubic(PointF p0, PointF p1, PointF p2, PointF p3, int depth)
    {
        if (depth >= MaxDepth || IsFlat(p0, p1, p2, p3))
        {
            LineTo(p3);
            return;
        }

        PointF p01 = Midpoint(p0, p1);
        PointF p12 = Midpoint(p1, p2);
        PointF p23 = Midpoint(p2, p3);
        PointF p012 = Midpoint(p01, p12);
        PointF p123 = Midpoint(p12, p23);
        PointF middle = Midpoint(p012, p123);

        FlattenCubic(p0, p01, p012, middle, depth + 1);
        FlattenCubic(middle, p123, p23, p3, depth + 1);
    }

    private bool IsFlat(PointF p0, PointF p1, PointF p2, PointF p3)
    {
        float dx = p3.X - p0.X;
        float dy = p3.Y - p0.Y;
        float c1 = ((p1.X - p0.X) * dy) - ((p1.Y - p0.Y) * dx);
        float c2 = ((p2.X - p0.X) * dy) - ((p2.Y - p0.Y) * dx);

        // 控制点在弦的两侧 = 可能形成 S 形，继续细分
        if ((c1 * c2) < 0f)
        {
            return false;
        }

        float lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < 1e-8f)
        {
            return DistanceSquared(p0, p1) <= _toleranceSquared
                   && DistanceSquared(p0, p2) <= _toleranceSquared;
        }

        float inverseLength = 1f / MathF.Sqrt(lengthSquared);
        return MathF.Abs(c1) * inverseLength <= _tolerance
               && MathF.Abs(c2) * inverseLength <= _tolerance;
    }

    private static PointF Midpoint(PointF a, PointF b) => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

    private static float DistanceSquared(PointF a, PointF b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static RectF ComputeBounds(PointF[] points)
    {
        if (points.Length == 0)
        {
            return RectF.Empty;
        }

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        foreach (PointF point in points)
        {
            if (point.X < minX)
            {
                minX = point.X;
            }

            if (point.Y < minY)
            {
                minY = point.Y;
            }

            if (point.X > maxX)
            {
                maxX = point.X;
            }

            if (point.Y > maxY)
            {
                maxY = point.Y;
            }
        }

        return new RectF(minX, minY, maxX - minX, maxY - minY);
    }
}
