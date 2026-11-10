using System.Numerics;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 圆弧支持：SVG 端点参数化的 <c>A</c> 命令（<c>rx ry rotation large-arc sweep x y</c>）。
/// <para>
/// 弧在构建阶段就按 ≤90° 拆成若干段三次贝塞尔写入命令序列，
/// 因此展平器与光栅化器永远只面对折线与 cubic，没有 arc 分支。
/// </para>
/// </summary>
internal sealed partial class PathBuilder
{
    private const float QuarterPi = MathF.PI * 0.5f;
    private const float Epsilon = 1e-6f;

    /// <summary>
    /// 从当前点画一段椭圆弧到 <paramref name="end"/>。
    /// </summary>
    /// <param name="end">弧的终点（端点参数化，不是圆心）。</param>
    /// <param name="radius">椭圆的 <c>rx</c>/<c>ry</c>；会自动放大到刚好能连接两端点。</param>
    /// <param name="rotationDegrees">椭圆相对 x 轴的旋转角（度）。</param>
    /// <param name="isLargeArc">是否走大于 180° 的那一段。</param>
    /// <param name="sweepClockwise">是否按屏幕坐标（y 向下）的顺时针方向走。</param>
    public PathBuilder ArcTo(PointF end, SizeF radius, float rotationDegrees, bool isLargeArc, bool sweepClockwise)
    {
        EnsureFigure();

        PointF start = _current;
        if (start == end)
        {
            return this;
        }

        float rx = MathF.Abs(radius.Width);
        float ry = MathF.Abs(radius.Height);
        if (rx < Epsilon || ry < Epsilon)
        {
            return LineTo(end);
        }

        float phi = rotationDegrees * MathF.PI / 180f;
        float cosPhi = MathF.Cos(phi);
        float sinPhi = MathF.Sin(phi);

        // 1. 把两端点搬到椭圆本地坐标（中点为中心）
        float halfDx = (start.X - end.X) * 0.5f;
        float halfDy = (start.Y - end.Y) * 0.5f;
        float x1 = (cosPhi * halfDx) + (sinPhi * halfDy);
        float y1 = (-sinPhi * halfDx) + (cosPhi * halfDy);

        // 2. 半径太小无法连接时按 SVG 规范等比放大
        float lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
        if (lambda > 1f)
        {
            float scale = MathF.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        // 3. 求圆心（本地坐标）
        float rx2 = rx * rx;
        float ry2 = ry * ry;
        float x12 = x1 * x1;
        float y12 = y1 * y1;
        float denominator = (rx2 * y12) + (ry2 * x12);
        float numerator = (rx2 * ry2) - denominator;

        float factor = denominator <= 0f ? 0f : MathF.Sqrt(MathF.Max(0f, numerator / denominator));
        if (isLargeArc == sweepClockwise)
        {
            factor = -factor;
        }

        float cxLocal = factor * rx * y1 / ry;
        float cyLocal = -factor * ry * x1 / rx;

        float cx = (cosPhi * cxLocal) - (sinPhi * cyLocal) + ((start.X + end.X) * 0.5f);
        float cy = (sinPhi * cxLocal) + (cosPhi * cyLocal) + ((start.Y + end.Y) * 0.5f);

        // 4. 起止角与扫过角
        float ux = (x1 - cxLocal) / rx;
        float uy = (y1 - cyLocal) / ry;
        float vx = (-x1 - cxLocal) / rx;
        float vy = (-y1 - cyLocal) / ry;

        float startAngle = MathF.Atan2(uy, ux);
        float sweepAngle = MathF.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));

        if (!sweepClockwise && sweepAngle > 0f)
        {
            sweepAngle -= MathF.Tau;
        }
        else if (sweepClockwise && sweepAngle < 0f)
        {
            sweepAngle += MathF.Tau;
        }

        if (MathF.Abs(sweepAngle) < Epsilon)
        {
            return LineTo(end);
        }

        // 5. 每段 ≤90°：单段三次贝塞尔在 90° 内的最大径向误差约 0.027%，远低于 1/256 像素
        int segments = (int)MathF.Ceiling(MathF.Abs(sweepAngle) / QuarterPi);
        float step = sweepAngle / segments;

        for (int i = 0; i < segments; i++)
        {
            float a0 = startAngle + (step * i);
            float a1 = a0 + step;

            PointF p0 = EllipsePoint(cx, cy, rx, ry, cosPhi, sinPhi, a0);
            PointF p1 = EllipsePoint(cx, cy, rx, ry, cosPhi, sinPhi, a1);

            // alpha = 4/3 * tan(Δ/4)，把切线长度换算成控制点偏移
            float alpha = 4f / 3f * MathF.Tan(step * 0.25f);

            PointF t0 = EllipseTangent(rx, ry, cosPhi, sinPhi, a0);
            PointF t1 = EllipseTangent(rx, ry, cosPhi, sinPhi, a1);

            PointF c1 = new(p0.X + (t0.X * alpha), p0.Y + (t0.Y * alpha));
            PointF c2 = new(p1.X - (t1.X * alpha), p1.Y - (t1.Y * alpha));

            // 最后一段强制落在精确端点上，避免累积浮点误差留下 1/1000 px 的缝
            CubicTo(c1, c2, i == segments - 1 ? end : p1);
        }

        return this;
    }

    /// <summary>圆弧（<c>rx == ry == <paramref name="radius"/></c>、无旋转）的便捷重载。</summary>
    public PathBuilder ArcTo(PointF end, float radius, bool isLargeArc = false, bool sweepClockwise = true)
        => ArcTo(end, new SizeF(radius, radius), 0f, isLargeArc, sweepClockwise);

    /// <summary>圆弧便捷重载。</summary>
    public PathBuilder ArcTo(float x, float y, float radius, bool isLargeArc = false, bool sweepClockwise = true)
        => ArcTo(new PointF(x, y), new SizeF(radius, radius), 0f, isLargeArc, sweepClockwise);

    private static PointF EllipsePoint(
        float cx,
        float cy,
        float rx,
        float ry,
        float cosPhi,
        float sinPhi,
        float angle)
    {
        float x = rx * MathF.Cos(angle);
        float y = ry * MathF.Sin(angle);
        return new PointF(
            cx + (x * cosPhi) - (y * sinPhi),
            cy + (x * sinPhi) + (y * cosPhi));
    }

    private static PointF EllipseTangent(float rx, float ry, float cosPhi, float sinPhi, float angle)
    {
        float dx = -rx * MathF.Sin(angle);
        float dy = ry * MathF.Cos(angle);
        return new PointF((dx * cosPhi) - (dy * sinPhi), (dx * sinPhi) + (dy * cosPhi));
    }
}
