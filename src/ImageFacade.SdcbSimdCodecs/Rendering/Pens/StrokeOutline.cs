using System.Runtime.InteropServices;

namespace ImageFacade.SdcbSimdCodecs.Rendering;


/// <summary>
/// 描边轮廓生成：把一条折线变成「可以按 <see cref="FillRule.NonZero"/> 填充的闭合轮廓」。
/// <para>
/// 做法是给每条线段生成一个 2r 宽的四边形，再按 <see cref="StrokeStyle"/> 在顶点补连接、
/// 在开放端点补端帽；所有子路径统一绕向，NonZero 的并集就是描边结果。
/// </para>
/// <para>
/// 这样做的代价是轮廓边数约为直线段数的 2 + 连接/端帽的几何（圆盘 8–64 段、miter 一个四边形），
/// 换来的是不必写第二套「描边专用光栅化器」——AA、裁剪、笔刷混合全部复用
/// <see cref="ScanlineRasterizer"/> 那一套。虚线只是把折线先切成若干开段，再跑同一条路径。
/// </para>
/// </summary>
internal static class StrokeOutline
{
    /// <summary>圆盘离散化的弦高容差（像素）。0.15 在 8 位覆盖率上已经看不出多边形感。</summary>
    private const float ChordTolerance = 0.15f;

    private const float DegenerateLengthSquared = 1e-6f;

    /// <summary>叉积小于该值视为共线：直着走不需要额外连接几何。</summary>
    private const float CollinearEpsilon = 1e-5f;

    /// <summary>图案边界 / 已消耗长度的判定容差（像素）。</summary>
    private const float BoundaryEpsilon = 1e-5f;

    internal static Path Build(FlattenedPath source, float width, in StrokeStyle style)
    {
        float radius = width * 0.5f;
        var builder = new PathBuilder();

        int[] starts = source.ContourStarts;
        PointF[] points = source.Points;

        for (int c = 0; c + 1 < starts.Length; c++)
        {
            int begin = starts[c];
            int end = starts[c + 1];
            int raw = end - begin;
            if (raw <= 0)
            {
                continue;
            }

            // CloseFigure 会把起点补到末尾，据此判断子路径是否闭合
            bool closed = raw >= 2 && SamePoint(points[begin], points[end - 1]);
            int count = closed ? raw - 1 : raw;

            if (count == 1)
            {
                // 退化成一个点：只有圆头/方头才该留下一点墨水
                if (style.Cap != LineCap.Butt)
                {
                    AddDisk(builder, points[begin], radius);
                }

                continue;
            }

            ReadOnlySpan<PointF> contour = points.AsSpan(begin, count);

            if (style.DashPattern is { Length: > 0 })
            {
                EmitDashed(builder, contour, closed, radius, style);
            }
            else
            {
                AddSolid(builder, contour, closed, radius, style);
            }
        }

        return builder.Build();
    }

    /// <summary>给一条完整的折线（或闭合子路径）生成描边几何。</summary>
    private static void AddSolid(
        PathBuilder builder,
        ReadOnlySpan<PointF> points,
        bool closed,
        float radius,
        in StrokeStyle style)
    {
        int count = points.Length;
        int segments = closed ? count : count - 1;

        for (int i = 0; i < segments; i++)
        {
            AddQuad(builder, points[i], points[(i + 1) % count], radius);
        }

        if (closed)
        {
            // 闭合路径的每个顶点都是连接（包括起点）
            for (int i = 0; i < count; i++)
            {
                AddJoin(
                    builder,
                    points[(i + count - 1) % count],
                    points[i],
                    points[(i + 1) % count],
                    radius,
                    style);
            }

            return;
        }

        for (int i = 1; i + 1 < count; i++)
        {
            AddJoin(builder, points[i - 1], points[i], points[i + 1], radius, style);
        }

        if (count >= 2)
        {
            AddCap(builder, points[0], points[1], radius, style.Cap);
            AddCap(builder, points[count - 1], points[count - 2], radius, style.Cap);
        }
    }

    /// <summary>顶点连接。<paramref name="vertex"/> 的外侧由两条边的叉积决定。</summary>
    private static void AddJoin(
        PathBuilder builder,
        PointF previous,
        PointF vertex,
        PointF next,
        float radius,
        in StrokeStyle style)
    {
        float inX = vertex.X - previous.X;
        float inY = vertex.Y - previous.Y;
        float outX = next.X - vertex.X;
        float outY = next.Y - vertex.Y;

        float inLengthSquared = (inX * inX) + (inY * inY);
        float outLengthSquared = (outX * outX) + (outY * outY);

        if (inLengthSquared < DegenerateLengthSquared || outLengthSquared < DegenerateLengthSquared)
        {
            // 重复点没有切向；补一个圆盘最稳（任一连接方式下都落在描边内部或正好补上）
            AddDisk(builder, vertex, radius);
            return;
        }

        float inLength = MathF.Sqrt(inLengthSquared);
        float outLength = MathF.Sqrt(outLengthSquared);
        float ux = inX / inLength;
        float uy = inY / inLength;
        float vx = outX / outLength;
        float vy = outY / outLength;

        float cross = (ux * vy) - (uy * vx);

        // 共线（含 180° 折返）不需要额外几何：两个四边形在顶点处已经贴合
        if (MathF.Abs(cross) < CollinearEpsilon)
        {
            return;
        }

        // 外侧单位法向：左转（cross > 0）取右边，右转取左边
        float side = cross > 0f ? 1f : -1f;
        float n1x = side * uy;
        float n1y = -side * ux;
        float n2x = side * vy;
        float n2y = -side * vx;

        PointF outerIn = new(vertex.X + (n1x * radius), vertex.Y + (n1y * radius));
        PointF outerOut = new(vertex.X + (n2x * radius), vertex.Y + (n2y * radius));

        switch (style.Join)
        {
            case LineJoin.Round:
                AddDisk(builder, vertex, radius);
                break;

            case LineJoin.Bevel:
                AddTriangle(builder, vertex, outerIn, outerOut);
                break;

            default:
                AddMiter(builder, vertex, outerIn, outerOut, n1x, n1y, n2x, n2y, radius, style.MiterLimit);
                break;
        }
    }

    private static void AddMiter(
        PathBuilder builder,
        PointF vertex,
        PointF outerIn,
        PointF outerOut,
        float n1x,
        float n1y,
        float n2x,
        float n2y,
        float radius,
        float miterLimit)
    {
        // 两条外侧偏移线的交点：m = vertex + r * (n1 + n2) / (1 + n1·n2)
        float denominator = 1f + (n1x * n2x) + (n1y * n2y);
        if (denominator < CollinearEpsilon)
        {
            AddTriangle(builder, vertex, outerIn, outerOut);
            return;
        }

        float miterX = vertex.X + (radius * (n1x + n2x) / denominator);
        float miterY = vertex.Y + (radius * (n1y + n2y) / denominator);

        float dx = miterX - vertex.X;
        float dy = miterY - vertex.Y;
        float limit = radius * miterLimit;

        // 尖角超过 MiterLimit 就退回 bevel，否则锐角会甩出几十像素的长刺
        if ((dx * dx) + (dy * dy) > limit * limit)
        {
            AddTriangle(builder, vertex, outerIn, outerOut);
            return;
        }

        Span<PointF> quad = stackalloc PointF[4];
        quad[0] = vertex;
        quad[1] = outerIn;
        quad[2] = new PointF(miterX, miterY);
        quad[3] = outerOut;
        AddClosed(builder, quad);
    }

    /// <summary>端点形状。<paramref name="previous"/> 是端点内侧的相邻点，用于求切向。</summary>
    private static void AddCap(PathBuilder builder, PointF end, PointF previous, float radius, LineCap cap)
    {
        if (cap == LineCap.Butt)
        {
            return;
        }

        float dx = end.X - previous.X;
        float dy = end.Y - previous.Y;
        float lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared < DegenerateLengthSquared)
        {
            if (cap != LineCap.Butt)
            {
                AddDisk(builder, end, radius);
            }

            return;
        }

        if (cap == LineCap.Round)
        {
            AddDisk(builder, end, radius);
            return;
        }

        // Square：沿切向向外延伸半个线宽
        float inverseLength = 1f / MathF.Sqrt(lengthSquared);
        float ux = dx * inverseLength;
        float uy = dy * inverseLength;

        float nx = -uy * radius;
        float ny = ux * radius;
        float ex = ux * radius;
        float ey = uy * radius;

        Span<PointF> quad = stackalloc PointF[4];
        quad[0] = new PointF(end.X + nx, end.Y + ny);
        quad[1] = new PointF(end.X + nx + ex, end.Y + ny + ey);
        quad[2] = new PointF(end.X - nx + ex, end.Y - ny + ey);
        quad[3] = new PointF(end.X - nx, end.Y - ny);
        AddClosed(builder, quad);
    }

    /// <summary>
    /// 按虚线图案把折线切成若干开段，每段独立描边（所以每段两端都有端帽）。
    /// 闭合轮廓上跨过起点的两段会重新拼成一段；整圈都在实线段内时按闭合轮廓处理（不补端帽）。
    /// </summary>
    private static void EmitDashed(
        PathBuilder builder,
        ReadOnlySpan<PointF> points,
        bool closed,
        float radius,
        in StrokeStyle style)
    {
        float[] pattern = style.DashPattern!;

        double total = 0d;
        for (int i = 0; i < pattern.Length; i++)
        {
            total += pattern[i];
        }

        if (!(total > 0d))
        {
            return;
        }

        // 相位：DashOffset 为正表示图案整体往前推（与 SVG 的 dashoffset 同向）
        double phase = style.DashOffset % total;
        if (phase < 0d)
        {
            phase += total;
        }

        int index = 0;
        double remaining = pattern[0];
        while (phase >= remaining)
        {
            phase -= remaining;
            index = (index + 1) % pattern.Length;
            remaining = pattern[index];
        }

        remaining -= phase;
        bool on = (index & 1) == 0;
        bool startedOn = on;

        var runs = new List<List<PointF>>();
        List<PointF>? run = on ? new List<PointF>() : null;
        if (run != null)
        {
            Append(run, points[0]);
        }

        int count = points.Length;
        int segments = closed ? count : count - 1;

        for (int s = 0; s < segments; s++)
        {
            PointF a = points[s];
            PointF b = points[(s + 1) % count];
            float segmentLength = Distance(a, b);
            if (!(segmentLength > 0f))
            {
                continue;
            }

            float consumed = 0f;

            while (consumed < segmentLength)
            {
                if (remaining <= BoundaryEpsilon)
                {
                    Advance(pattern, ref index, ref remaining, ref on);
                    if (on)
                    {
                        run = new List<PointF>();
                        Append(run, Interpolate(a, b, consumed / segmentLength));
                    }
                    else if (run != null)
                    {
                        runs.Add(run);
                        run = null;
                    }

                    continue;
                }

                float step = (float)Math.Min(remaining, segmentLength - consumed);
                consumed = MathF.Min(consumed + step, segmentLength);
                remaining -= step;

                PointF cut = Interpolate(a, b, consumed / segmentLength);
                if (on)
                {
                    Append(run!, cut);
                }

                if (remaining <= BoundaryEpsilon && consumed < segmentLength)
                {
                    if (on)
                    {
                        runs.Add(run!);
                        run = null;
                    }

                    Advance(pattern, ref index, ref remaining, ref on);

                    if (on)
                    {
                        run = new List<PointF>();
                        Append(run, cut);
                    }
                }
            }

            if (on)
            {
                run ??= new List<PointF>();
                Append(run, b);
            }
        }

        if (on && run != null)
        {
            runs.Add(run);
        }

        if (closed && startedOn && on && runs.Count > 0)
        {
            if (runs.Count >= 2)
            {
                // 最后一段以 points[0] 结尾，第一段以 points[0] 开头：是同一条虚线的两半
                List<PointF> head = runs[^1];
                List<PointF> tail = runs[0];
                if (head.Count > 0 && tail.Count > 0 && SamePoint(head[^1], tail[0]))
                {
                    head.RemoveAt(head.Count - 1);
                    head.AddRange(tail);
                    runs[0] = head;
                    runs.RemoveAt(runs.Count - 1);
                }
            }

            List<PointF> merged = runs[0];
            if (merged.Count > 1 && SamePoint(merged[0], merged[^1]))
            {
                // 整圈都在实线内：按闭合轮廓描边，起点处不补端帽
                merged.RemoveAt(merged.Count - 1);
                AddSolid(builder, CollectionsMarshal.AsSpan(merged), true, radius, style);
                return;
            }
        }

        foreach (List<PointF> dashed in runs)
        {
            if (dashed.Count > 0)
            {
                AddSolid(builder, CollectionsMarshal.AsSpan(dashed), false, radius, style);
            }
        }
    }

    private static void Advance(float[] pattern, ref int index, ref double remaining, ref bool on)
    {
        // 0 长度的图案项要连续跳过去；guard 防止全 0（构造时已挡，但自定义 IPen 可能绕过）
        for (int guard = 0; guard <= pattern.Length * 2; guard++)
        {
            index = (index + 1) % pattern.Length;
            remaining = pattern[index];
            on = !on;

            if (remaining > 0d)
            {
                return;
            }
        }
    }

    private static void AddQuad(PathBuilder builder, PointF a, PointF b, float radius)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < DegenerateLengthSquared)
        {
            return;
        }

        float inverseLength = 1f / MathF.Sqrt(lengthSquared);
        float nx = -dy * inverseLength * radius;
        float ny = dx * inverseLength * radius;

        Span<PointF> quad = stackalloc PointF[4];
        quad[0] = new PointF(a.X + nx, a.Y + ny);
        quad[1] = new PointF(b.X + nx, b.Y + ny);
        quad[2] = new PointF(b.X - nx, b.Y - ny);
        quad[3] = new PointF(a.X - nx, a.Y - ny);

        AddClosed(builder, quad);
    }

    private static void AddTriangle(PathBuilder builder, PointF a, PointF b, PointF c)
    {
        Span<PointF> triangle = stackalloc PointF[3];
        triangle[0] = a;
        triangle[1] = b;
        triangle[2] = c;
        AddClosed(builder, triangle);
    }

    private static void AddDisk(PathBuilder builder, PointF center, float radius)
    {
        int segments = DiskSegments(radius);
        Span<PointF> ring = stackalloc PointF[segments];

        for (int i = 0; i < segments; i++)
        {
            float angle = MathF.Tau * i / segments;
            ring[i] = new PointF(
                center.X + (MathF.Cos(angle) * radius),
                center.Y + (MathF.Sin(angle) * radius));
        }

        AddClosed(builder, ring);
    }

    /// <summary>按弦高容差选圆盘段数：段数越多越圆，但边数线性增长。</summary>
    private static int DiskSegments(float radius)
    {
        float cosine = 1f - (ChordTolerance / radius);
        if (cosine <= -1f)
        {
            return 8;
        }

        int segments = (int)MathF.Ceiling(MathF.PI / MathF.Acos(cosine));
        return Math.Clamp(segments, 8, 64);
    }

    /// <summary>
    /// 追加一条闭合子路径，并把绕向统一成「正面积」。
    /// <para>
    /// 这一步是正确性的前提：NonZero 下绕向相反的轮廓重叠处环绕数相消，会在描边接缝上挖出洞。
    /// </para>
    /// </summary>
    private static void AddClosed(PathBuilder builder, ReadOnlySpan<PointF> points)
    {
        if (points.Length < 3)
        {
            return;
        }

        float twiceArea = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            PointF current = points[i];
            PointF next = points[(i + 1) % points.Length];
            twiceArea += (current.X * next.Y) - (next.X * current.Y);
        }

        builder.MoveTo(points[0]);

        if (twiceArea >= 0f)
        {
            for (int i = 1; i < points.Length; i++)
            {
                builder.LineTo(points[i]);
            }
        }
        else
        {
            for (int i = points.Length - 1; i >= 1; i--)
            {
                builder.LineTo(points[i]);
            }
        }

        builder.CloseFigure();
    }

    private static void Append(List<PointF> points, PointF point)
    {
        if (points.Count != 0 && SamePoint(points[^1], point))
        {
            return;
        }

        points.Add(point);
    }

    private static PointF Interpolate(PointF a, PointF b, float t)
        => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    private static float Distance(PointF a, PointF b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool SamePoint(PointF a, PointF b) => a.X == b.X && a.Y == b.Y;
}
