namespace ImageFacade.SdcbSimdCodecs.Rendering;

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
    private readonly List<Crossing> _crossings = new();
    private ushort[] _accumulator = Array.Empty<ushort>();

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

        for (int row = 0; row < clip.Height; row++)
        {
            float rowTop = clip.Y + row;

            while (nextEdge < _edges.Count && _edges[nextEdge].StartRow <= row)
            {
                _active.Add(_edges[nextEdge++]);
            }

            RetireEdges(rowTop);
            accumulator.Clear();

            for (int sub = 0; sub < SubSamples; sub++)
            {
                float scanlineY = rowTop + ((sub + 0.5f) * SubSampleStep);
                CollectCrossings(scanlineY, clip.X);
                FillRow(accumulator, rule);
            }

            Span<byte> destination = output.GetRow(row);
            for (int x = 0; x < clip.Width; x++)
            {
                // 4 次子采样的累加上限是 1020，除以 4 即 0–255；加 2 做四舍五入
                destination[x] = (byte)((accumulator[x] + (SubSamples / 2)) / SubSamples);
            }
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

        // 按起始行排序后，Rasterize 只需顺序推进指针就能把边放进活动表
        _edges.Sort(static (left, right) => left.StartRow.CompareTo(right.StartRow));
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

        _edges.Add(new Edge(x0, yMin, yMax, slope, dy > 0 ? 1 : -1, startRow));
    }

    /// <summary>把已经整条落到当前行上方的边移出活动表（交换删除，顺序无所谓）。</summary>
    private void RetireEdges(float rowTop)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].YMax <= rowTop)
            {
                _active[i] = _active[^1];
                _active.RemoveAt(_active.Count - 1);
            }
        }
    }

    private void CollectCrossings(float scanlineY, int clipX)
    {
        _crossings.Clear();
        foreach (Edge edge in _active)
        {
            if (scanlineY >= edge.YMin && scanlineY < edge.YMax)
            {
                float x = edge.X0 + ((scanlineY - edge.YMin) * edge.Slope);
                _crossings.Add(new Crossing(x - clipX, edge.Direction));
            }
        }

        _crossings.Sort(static (left, right) => left.X.CompareTo(right.X));
    }

    private void FillRow(Span<ushort> accumulator, FillRule rule)
    {
        int winding = 0;
        int count = 0;
        float spanStart = 0f;
        bool inside = false;

        foreach (Crossing crossing in _crossings)
        {
            bool wasInside = inside;
            winding += crossing.Direction;
            count++;
            inside = rule == FillRule.NonZero ? winding != 0 : (count & 1) != 0;

            if (!wasInside && inside)
            {
                spanStart = crossing.X;
            }
            else if (wasInside && !inside)
            {
                AccumulateSpan(accumulator, spanStart, crossing.X);
            }
        }
    }

    private static void AccumulateSpan(Span<ushort> accumulator, float x0, float x1)
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

    private readonly struct Edge
    {
        public Edge(float x0, float yMin, float yMax, float slope, int direction, int startRow)
        {
            X0 = x0;
            YMin = yMin;
            YMax = yMax;
            Slope = slope;
            Direction = direction;
            StartRow = startRow;
        }

        public float X0 { get; }

        public float YMin { get; }

        public float YMax { get; }

        public float Slope { get; }

        /// <summary>+1 = 边向下走，-1 = 向上走（决定环绕数符号）。</summary>
        public int Direction { get; }

        /// <summary>相对裁剪区顶部的起始行号，用于活动边表（按下界入表）。</summary>
        public int StartRow { get; }
    }

    private readonly struct Crossing
    {
        public Crossing(float x, int direction)
        {
            X = x;
            Direction = direction;
        }

        public float X { get; }

        public int Direction { get; }
    }
}
