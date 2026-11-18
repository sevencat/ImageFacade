using System.Runtime.InteropServices;

namespace ImageFacade.Rendering;

/// <summary>
/// 扫描线光栅化：纵向 4 次子采样 + 横向精确覆盖，输出 0–255 的 A8 coverage。
/// <para>
/// 只接受展平后的折线（<see cref="FlattenedPath"/>），曲线、圆弧、二次贝塞尔都在更上层处理完。
/// 子路径按「隐式闭合」参与环绕数计算，与 Canvas/SVG 的填充语义一致。
/// </para>
/// </summary>
internal sealed class ScanlineRasterizer
{
    private const int SubSamples = 4;
    private const float SubSampleStep = 1f / SubSamples;

    private readonly List<Edge> _edges = new();
    private readonly List<Edge> _active = new();
    private ushort[] _accumulator = Array.Empty<ushort>();

    /// <summary>当前行被覆盖的列区间（左闭右开），由 <see cref="AccumulateSpan"/> 累积。</summary>
    private int _rowStart;
    private int _rowEnd;

    /// <summary>
    /// 光栅化一条（可能含多条子路径的）轮廓到输出缓冲。
    /// <c>clip</c> 是输出区域的绝对像素坐标，必须与输出缓冲同尺寸。
    /// </summary>
    public void Rasterize(FlattenedPath path, FillRule rule, Rectangle clip, CoverageBuffer output)
    {
        if (clip.Width != output.Width || clip.Height != output.Height)
        {
            throw new ArgumentException("clip 必须与输出缓冲同尺寸。", nameof(clip));
        }

        if (clip.IsEmpty)
        {
            return;
        }

        BuildEdges(path, clip);
        Span<ushort> accumulator = GetAccumulator(clip.Width);

        // 活动边表：每条边只在 [StartRow, YMax) 的行区间里参与求交，
        // 否则 1000 段描边这种上万条边的轮廓会在每行重复扫全表（O(行 × 边)）。
        _active.Clear();
        int nextEdge = 0;

        // 每行只在「本行真正被覆盖的列」上做清零、量化与混合。
        // 斜线/虚线的包围盒可能接近整块画布，但每行只有几个像素有覆盖率，
        // 按整宽处理会让细长形状的成本由包围盒面积决定（实测能差 30 倍）。
        int previousStart = 0;
        int previousEnd = 0;

        for (int row = 0; row < clip.Height; row++)
        {
            float rowTop = clip.Y + row;

            while (nextEdge < _edges.Count && _edges[nextEdge].StartRow <= row)
            {
                _active.Add(_edges[nextEdge++]);
            }

            RetireEdges(rowTop);

            if (previousEnd > previousStart)
            {
                accumulator.Slice(previousStart, previousEnd - previousStart).Clear();
            }

            _rowStart = int.MaxValue;
            _rowEnd = 0;

            for (int sub = 0; sub < SubSamples; sub++)
            {
                float scanlineY = rowTop + ((sub + 0.5f) * SubSampleStep);
                PrepareActive(scanlineY);
                FillRow(accumulator, rule, scanlineY, clip.X);
            }

            if (_rowEnd <= _rowStart)
            {
                output.SetRowSpan(row, 0, 0);
                previousStart = 0;
                previousEnd = 0;
                continue;
            }

            Span<byte> destination = output.GetRow(row);
            for (int x = _rowStart; x < _rowEnd; x++)
            {
                // 4 次子采样的累加上限是 1020，除以 4 即 0–255；加 2 做四舍五入
                destination[x] = (byte)((accumulator[x] + (SubSamples / 2)) / SubSamples);
            }

            output.SetRowSpan(row, _rowStart, _rowEnd);
            previousStart = _rowStart;
            previousEnd = _rowEnd;
        }
    }

    private void BuildEdges(FlattenedPath path, Rectangle clip)
    {
        _edges.Clear();
        PointF[] points = path.Points;
        int[] starts = path.ContourStarts;

        for (int contour = 0; contour < starts.Length - 1; contour++)
        {
            int start = starts[contour];
            int end = starts[contour + 1];
            if (end - start < 2)
            {
                continue;
            }

            for (int i = start; i < end; i++)
            {
                PointF a = points[i];
                PointF b = i + 1 < end ? points[i + 1] : points[start];
                AddEdge(a, b, clip);
            }
        }

        // 按起始行排序后，Rasterize 只需顺序推进指针就能把边放进活动表。
        // 次级键取 Order：List.Sort 不稳定，只按起始行排会让同一个起始行的边顺序在
        // 不同运行之间不一致，从而影响 x 并列时的混合结果。
        _edges.Sort(static (left, right) => left.StartRow != right.StartRow
            ? left.StartRow.CompareTo(right.StartRow)
            : left.Order.CompareTo(right.Order));
    }

    private void AddEdge(PointF a, PointF b, Rectangle clip)
    {
        float dy = b.Y - a.Y;
        if (MathF.Abs(dy) < 1e-6f)
        {
            return;   // 水平边不产生交点
        }

        float yMin = MathF.Min(a.Y, b.Y);
        float yMax = MathF.Max(a.Y, b.Y);

        if (yMax <= clip.Y || yMin >= clip.Bottom)
        {
            return;
        }

        // 交点公式 x = x0 + (y - yMin) * slope；x0 取 yMin 那一端的 x
        float x0 = dy > 0 ? a.X : b.X;
        float slope = (b.X - a.X) / dy;

        // 边从哪一行开始参与求交（yMin 可能因为被裁剪而落在裁剪区上方）
        int startRow = (int)MathF.Floor(yMin) - clip.Y;
        if (startRow < 0)
        {
            startRow = 0;
        }

        _edges.Add(new Edge(x0, yMin, yMax, slope, dy > 0 ? 1 : -1, startRow, _edges.Count));
    }

    /// <summary>
    /// 把已经整条落到当前行上方的边移出活动表。
    /// 这里必须保持相对顺序（顺序删除后整体压缩），否则下一行的插入排序会退化成 O(n²)。
    /// </summary>
    private void RetireEdges(float rowTop)
    {
        int write = 0;
        int count = _active.Count;
        for (int read = 0; read < count; read++)
        {
            Edge edge = _active[read];
            if (edge.YMax > rowTop)
            {
                _active[write++] = edge;
            }
        }

        if (write < count)
        {
            _active.RemoveRange(write, count - write);
        }
    }

    /// <summary>
    /// 更新活动边在当前扫描线上的 x，并按 x 排序。
    /// <para>
    /// 相邻扫描线之间 x 的顺序几乎不变，所以插入排序的逆序对很少，退化成接近 O(n)；
    /// 排好序后 <see cref="FillRow"/> 直接顺序取用，不必每行重新收集+排序交点。
    /// 超出当前扫描线范围的边也参与排序（x 取起点值），保证被跳过后剩余元素仍然有序。
    /// </para>
    /// </summary>
    private void PrepareActive(float scanlineY)
    {
        Span<Edge> active = CollectionsMarshal.AsSpan(_active);

        for (int i = 0; i < active.Length; i++)
        {
            ref Edge edge = ref active[i];
            float offset = scanlineY - edge.YMin;
            if (offset < 0f)
            {
                offset = 0f;   // 还没真正进入扫描线：按起点 x 参与排序
            }

            edge.X = edge.X0 + (offset * edge.Slope);
        }

        for (int i = 1; i < active.Length; i++)
        {
            Edge current = active[i];
            int j = i - 1;
            // x 完全相等时按建边顺序收尾：并列顺序必须确定，否则 miter 尖端那种
            // 多条边交于同一点的位置，混合结果会随活动表历史顺序变化
            while (j >= 0 && (active[j].X > current.X
                || (active[j].X == current.X && active[j].Order > current.Order)))
            {
                active[j + 1] = active[j];
                j--;
            }

            active[j + 1] = current;
        }
    }

    private void FillRow(Span<ushort> accumulator, FillRule rule, float scanlineY, int clipX)
    {
        int winding = 0;
        int count = 0;
        float spanStart = 0f;
        bool inside = false;

        ReadOnlySpan<Edge> active = CollectionsMarshal.AsSpan(_active);
        for (int i = 0; i < active.Length; i++)
        {
            ref readonly Edge edge = ref active[i];
            if (scanlineY < edge.YMin || scanlineY >= edge.YMax)
            {
                continue;
            }

            float x = edge.X - clipX;
            bool wasInside = inside;
            winding += edge.Direction;
            count++;
            inside = rule == FillRule.NonZero ? winding != 0 : (count & 1) != 0;

            if (!wasInside && inside)
            {
                spanStart = x;
            }
            else if (wasInside && !inside)
            {
                AccumulateSpan(accumulator, spanStart, x);
            }
        }
    }

    private void AccumulateSpan(Span<ushort> accumulator, float x0, float x1)
    {
        if (x1 <= x0)
        {
            return;
        }

        int width = accumulator.Length;
        float left = MathF.Max(x0, 0f);
        float right = MathF.Min(x1, width);
        if (right <= left)
        {
            return;
        }

        int first = (int)MathF.Floor(left);
        int last = Math.Min((int)MathF.Ceiling(right) - 1, width - 1);

        if (first < _rowStart)
        {
            _rowStart = first;
        }

        if (last + 1 > _rowEnd)
        {
            _rowEnd = last + 1;
        }

        for (int x = first; x <= last; x++)
        {
            float overlapLeft = MathF.Max(left, x);
            float overlapRight = MathF.Min(right, x + 1);
            if (overlapRight <= overlapLeft)
            {
                continue;
            }

            int value = (int)(((overlapRight - overlapLeft) * 255f) + 0.5f);
            if (value > 0)
            {
                accumulator[x] += (ushort)value;
            }
        }
    }

    private Span<ushort> GetAccumulator(int width)
    {
        if (_accumulator.Length < width)
        {
            _accumulator = new ushort[width];
        }

        return _accumulator.AsSpan(0, width);
    }

    /// <summary>光栅化用的边。<see cref="X"/> 是当前扫描线上的 x，由 <see cref="PrepareActive"/> 更新。</summary>
    private struct Edge
    {
        public Edge(float x0, float yMin, float yMax, float slope, int direction, int startRow, int order)
        {
            X0 = x0;
            YMin = yMin;
            YMax = yMax;
            Slope = slope;
            Direction = direction;
            StartRow = startRow;
            Order = order;
            X = x0;
        }

        public float X0;

        public float YMin;

        public float YMax;

        public float Slope;

        /// <summary>+1 = 边向下走，-1 = 向上走（决定环绕数符号）。</summary>
        public int Direction;

        /// <summary>相对裁剪区顶部的起始行号，用于活动边表（按下界入表）。</summary>
        public int StartRow;

        /// <summary>建边顺序，仅用于 x 相等时打破并列，保证排序结果确定。</summary>
        public int Order;

        /// <summary>当前扫描线上的 x（绝对坐标），活动表按它排序。</summary>
        public float X;
    }
}
