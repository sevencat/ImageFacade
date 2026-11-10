using System.Numerics;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>渐变停止点：<c>offset</c> 是 0–1 的位置，超出会被夹住。</summary>
internal readonly struct ColorStop : IEquatable<ColorStop>
{
    public ColorStop(float offset, Color color)
    {
        Offset = offset;
        Color = color;
    }

    public float Offset { get; }

    public Color Color { get; }

    public bool Equals(ColorStop other) => Offset.Equals(other.Offset) && Color.Equals(other.Color);

    public override bool Equals(object? obj) => obj is ColorStop other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Offset, Color);

    public override string ToString() => $"{Color} @ {Offset}";

    public static bool operator ==(ColorStop left, ColorStop right) => left.Equals(right);

    public static bool operator !=(ColorStop left, ColorStop right) => !left.Equals(right);
}

/// <summary>
/// 一行内沿 x 方向的渐变参数游标：<c>U</c> 是当前像素的参数（线性渐变就是 t，径向渐变是到圆心距离的平方），
/// 每前进一个像素做一次二阶差分。
/// <para>
/// 一行的参数只依赖 <c>U</c>、<c>Du</c>、<c>Ddu</c> 三个数，热循环里因此没有点积、除法或矩阵乘：
/// 线性渐变 <c>Ddu = 0</c>（一阶差分），径向渐变是 x 的二次函数（二阶差分）。
/// </para>
/// </summary>
internal struct GradientRowCursor
{
    /// <summary>当前像素的参数值。</summary>
    public float U;

    /// <summary>一阶差分（下一像素相对当前像素的增量）。</summary>
    public float Du;

    /// <summary>二阶差分（每步给 <see cref="Du"/> 的增量），线性渐变为 0。</summary>
    public float Ddu;

    /// <summary>是否对 <see cref="U"/> 取平方根才是渐变参数（径向渐变）。</summary>
    public bool SquareRoot;

    /// <summary>当前像素的渐变参数。开方前夹一下，避免差分误差把 <c>U</c> 推到负数。</summary>
    public readonly float Parameter => SquareRoot ? MathF.Sqrt(U > 0f ? U : 0f) : U;

    /// <summary>前进一个像素。</summary>
    public void Step()
    {
        U += Du;
        Du += Ddu;
    }
}

/// <summary>
/// 渐变笔刷的公共部分：停止点归一化、预乘插值、逐像素混合、可选 4×4 ordered dither。
/// <para>
/// 插值在**预乘空间**做：否则「透明黑 → 不透明白」会在中段出现灰色脏边，
/// 这是所有「渐变叠加在图上」场景最容易踩的坑。
/// </para>
/// </summary>
internal abstract class GradientBrush : IBrush
{
    // 4×4 Bayer 矩阵，用于抖动掉色带：值域 0..15，减 7.5 后归一到 ±0.5 LSB
    private static readonly byte[] BayerMatrix =
    [
        0, 8, 2, 10,
        12, 4, 14, 6,
        3, 11, 1, 9,
        15, 7, 13, 5,
    ];

    private readonly ColorStop[] _stops;
    private readonly float[] _stopsR;
    private readonly float[] _stopsG;
    private readonly float[] _stopsB;
    private readonly float[] _stopsA;

    // 每个区间的预计算：起点、1/跨度、四通道增量。热循环里因此没有除法，也不用每像素扫停止点表
    private readonly float[] _offsets;
    private readonly float[] _segFrom;
    private readonly float[] _segInvSpan;
    private readonly float[] _segR;
    private readonly float[] _segG;
    private readonly float[] _segB;
    private readonly float[] _segA;

    private readonly bool _uniform;
    private readonly Color _uniformColor;

    protected GradientBrush(IEnumerable<ColorStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ColorStop[] array = stops.ToArray();
        if (array.Length == 0)
        {
            throw new ArgumentException("渐变至少需要一个停止点。", nameof(stops));
        }

        if (array.Length == 1)
        {
            // 单点时补一个同色停止点，让后续逻辑不必特判
            array = [array[0], new ColorStop(1f, array[0].Color)];
        }

        Array.Sort(array, static (a, b) => a.Offset.CompareTo(b.Offset));
        _stops = array;

        _stopsR = new float[array.Length];
        _stopsG = new float[array.Length];
        _stopsB = new float[array.Length];
        _stopsA = new float[array.Length];

        bool uniform = true;
        for (int i = 0; i < array.Length; i++)
        {
            Color c = array[i].Color;
            float a = c.A;
            _stopsR[i] = c.R * a / 255f;
            _stopsG[i] = c.G * a / 255f;
            _stopsB[i] = c.B * a / 255f;
            _stopsA[i] = a;
            uniform &= c == array[0].Color;
        }

        _uniform = uniform;
        _uniformColor = array[0].Color;

        int last = array.Length - 1;
        _offsets = new float[array.Length];
        _segFrom = new float[last];
        _segInvSpan = new float[last];
        _segR = new float[last];
        _segG = new float[last];
        _segB = new float[last];
        _segA = new float[last];

        for (int i = 0; i < array.Length; i++)
        {
            _offsets[i] = array[i].Offset;
        }

        for (int i = 0; i < last; i++)
        {
            float from = _offsets[i];
            float span = _offsets[i + 1] - from;
            _segFrom[i] = from;
            _segInvSpan[i] = span > 0f ? 1f / span : 0f;
            _segR[i] = _stopsR[i + 1] - _stopsR[i];
            _segG[i] = _stopsG[i + 1] - _stopsG[i];
            _segB[i] = _stopsB[i + 1] - _stopsB[i];
            _segA[i] = _stopsA[i + 1] - _stopsA[i];
        }
    }

    /// <summary>是否对颜色做 4×4 ordered dither（长渐变上抑制色带，默认关闭）。</summary>
    public bool Dither { get; init; }

    /// <summary>
    /// 画布坐标 → 用户坐标的逆矩阵。非恒等时采样点先映射回去，于是渐变的起止点可以理解成用户坐标系里的坐标
    /// （跟随 <see cref="DrawingContext.Transform"/> 一起旋转缩放）。由 <see cref="DrawingContext"/> 内部设置。
    /// </summary>
    internal Matrix3x2 InverseTransform
    {
        get => _inverse;
        init
        {
            _inverse = value;
            _hasInverse = !value.IsIdentity;
        }
    }

    private readonly Matrix3x2 _inverse = Matrix3x2.Identity;
    private readonly bool _hasInverse;

    /// <summary>是否挂了非恒等逆矩阵（内置派生类搭行游标时用）。</summary>
    internal bool HasInverseTransform => _hasInverse;

    /// <summary>画布 → 用户的逆矩阵（内置派生类搭行游标时用）。</summary>
    internal Matrix3x2 InverseTransformMatrix => _inverse;

    /// <summary>停止点（按 offset 升序）。</summary>
    public IReadOnlyList<ColorStop> Stops => _stops;

    /// <summary>所有停止点同色时返回该颜色，让纯色矩形仍能命中整行快路径。</summary>
    public bool TryGetSolidColor(out Color color)
    {
        color = _uniformColor;
        return _uniform;
    }

    /// <summary>
    /// 返回一个「采样前先把画布坐标映射回用户坐标」的副本（笔刷保持不可变，色表与副本共享计算结果的代价可忽略）。
    /// <para>
    /// 外部派生类默认返回自身，即不跟随变换；内置的线性/径向渐变会正确跟随。
    /// </para>
    /// </summary>
    internal virtual GradientBrush WithInverseTransform(Matrix3x2 inverse) => this;

    /// <summary>
    /// 内置渐变给出一行的参数游标（起点 + 差分），快路径因此没有逐像素虚调用、点积和除法。
    /// 外部派生类默认返回 <c>false</c>，退化为逐像素 <see cref="ParameterAt"/>。
    /// </summary>
    internal virtual bool TryCreateRowCursor(int x0, int y, out GradientRowCursor cursor)
    {
        cursor = default;
        return false;
    }

    public void Blend(Canvas canvas, Rectangle region, ReadOnlySpan<byte> coverage, int coverageStride)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        for (int y = 0; y < region.Height; y++)
        {
            Span<byte> row = canvas.GetRow(region.Y + y)
                .Slice(region.X * Canvas.BytesPerPixel, region.Width * Canvas.BytesPerPixel);
            ReadOnlySpan<byte> coverageRow = coverage.Slice(y * coverageStride, region.Width);
            BlendRow(row, coverageRow, region.X, region.Y + y);
        }
    }

    /// <summary>
    /// 把一行的渐变混进目标行。<paramref name="row"/> 已裁剪到 region，<paramref name="coverageRow"/> 等长。
    /// </summary>
    /// <param name="row">画布的一行（预乘 RGBA）。</param>
    /// <param name="coverageRow">逐像素覆盖率。</param>
    /// <param name="absoluteX">该行第一个像素的画布 x 坐标（给抖动矩阵用的，务必传绝对坐标）。</param>
    /// <param name="absoluteY">该行的画布 y 坐标。</param>
    protected void BlendRow(Span<byte> row, ReadOnlySpan<byte> coverageRow, int absoluteX, int absoluteY)
    {
        if (TryCreateRowCursor(absoluteX, absoluteY, out GradientRowCursor cursor))
        {
            BlendRowIncremental(row, coverageRow, absoluteX, absoluteY, cursor);
            return;
        }

        BlendRowPerPixel(row, coverageRow, absoluteX, absoluteY);
    }

    /// <summary>
    /// 内置渐变的快路径：参数沿 x 走差分，每像素只剩「取参数 → 夹住 → 查区间 → 四通道线性插值」。
    /// </summary>
    private void BlendRowIncremental(
        Span<byte> row,
        ReadOnlySpan<byte> coverageRow,
        int absoluteX,
        int absoluteY,
        GradientRowCursor cursor)
    {
        int segment = 0;
        for (int i = 0, o = 0; i < coverageRow.Length; i++, o += Canvas.BytesPerPixel)
        {
            float t = cursor.Parameter;
            cursor.Step();

            int coverage = coverageRow[i];
            if (coverage == 0)
            {
                continue;
            }

            if (Dither)
            {
                t += GetDitherOffset(absoluteX + i, absoluteY) / 255f;
            }

            SampleColorAt(Math.Clamp(t, 0f, 1f), ref segment, out float sr, out float sg, out float sb, out float sa);
            BlendPixel(row, o, coverage, sr, sg, sb, sa);
        }
    }

    /// <summary>外部派生类的慢路径：保持原来的逐像素 <see cref="ParameterAt"/> 语义。</summary>
    private void BlendRowPerPixel(Span<byte> row, ReadOnlySpan<byte> coverageRow, int absoluteX, int absoluteY)
    {
        for (int i = 0, o = 0; i < coverageRow.Length; i++, o += Canvas.BytesPerPixel)
        {
            int coverage = coverageRow[i];
            if (coverage == 0)
            {
                continue;
            }

            float offset = Dither ? GetDitherOffset(absoluteX + i, absoluteY) : 0f;
            SamplePremultiplied(absoluteX + i, absoluteY, offset, out float sr, out float sg, out float sb, out float sa);
            BlendPixel(row, o, coverage, sr, sg, sb, sa);
        }
    }

    /// <summary>把预乘颜色按覆盖率混进目标行。两个采样路径共用，保证像素结果一致。</summary>
    private static void BlendPixel(Span<byte> row, int o, int coverage, float sr, float sg, float sb, float sa)
    {
        if (coverage == 255 && sa > 254.5f)
        {
            // 全覆盖 + 不透明：混合恒等式的结果就是源色，省掉读-改-写和 4 次 Mul255
            // （与下方通用路径逐字节一致：a = 255、inv = 0、Mul255(x, 255) == x）
            row[o] = ClampByte(sr);
            row[o + 1] = ClampByte(sg);
            row[o + 2] = ClampByte(sb);
            row[o + 3] = 255;
            return;
        }

        int a = PixelOps.Mul255((int)MathF.Round(Math.Clamp(sa, 0f, 255f)), coverage);
        if (a == 0)
        {
            return;
        }

        int inv = 255 - a;
        row[o] = (byte)Math.Min(255, PixelOps.Mul255((int)sr, coverage) + PixelOps.Mul255(row[o], inv));
        row[o + 1] = (byte)Math.Min(255, PixelOps.Mul255((int)sg, coverage) + PixelOps.Mul255(row[o + 1], inv));
        row[o + 2] = (byte)Math.Min(255, PixelOps.Mul255((int)sb, coverage) + PixelOps.Mul255(row[o + 2], inv));
        row[o + 3] = (byte)Math.Min(255, a + PixelOps.Mul255(row[o + 3], inv));
    }

    /// <summary>预乘通道（0–255 的 float）→ 字节，与 <c>(int)value</c> 的截断语义一致。</summary>
    private static byte ClampByte(float value)
        => value <= 0f ? (byte)0 : value >= 255f ? (byte)255 : (byte)(int)value;

    /// <summary>
    /// 采样某个绝对像素的预乘颜色。子类只需回答「这个点落在渐变的哪个位置（0–1）」。
    /// </summary>
    /// <param name="x">绝对像素 x。</param>
    /// <param name="y">绝对像素 y。</param>
    /// <param name="ditherOffset">抖动偏置（以「像素中心」为单位，通常 ±0.5）。</param>
    /// <param name="r">预乘红。</param>
    /// <param name="g">预乘绿。</param>
    /// <param name="b">预乘蓝。</param>
    /// <param name="a">alpha（0–255）。</param>
    protected void SamplePremultiplied(int x, int y, float ditherOffset, out float r, out float g, out float b, out float a)
    {
        int segment = 0;
        SampleColorAt(SampleParameter(x, y, ditherOffset), ref segment, out r, out g, out b, out a);
    }

    /// <summary>画布像素 → 夹到 0–1 的渐变参数（含逆变换与抖动）。</summary>
    private float SampleParameter(int x, int y, float ditherOffset)
    {
        // 像素中心采样：+0.5 避免整像素边界上出现「半像素」偏差
        float sampleX = x + 0.5f;
        float sampleY = y + 0.5f;

        if (_hasInverse)
        {
            float userX = (sampleX * _inverse.M11) + (sampleY * _inverse.M21) + _inverse.M31;
            float userY = (sampleX * _inverse.M12) + (sampleY * _inverse.M22) + _inverse.M32;
            sampleX = userX;
            sampleY = userY;
        }

        float t = ParameterAt(sampleX, sampleY) + (ditherOffset / 255f);
        return Math.Clamp(t, 0f, 1f);
    }

    /// <summary>
    /// 把 0–1 的参数映射成预乘颜色。<paramref name="segment"/> 是跨像素复用的区间游标：
    /// 参数单调前进时只做一次比较，径向渐变先减后增也能靠反向循环退回去。
    /// </summary>
    private void SampleColorAt(float t, ref int segment, out float r, out float g, out float b, out float a)
    {
        int last = _stops.Length - 1;

        while (segment < last - 1 && t > _offsets[segment + 1])
        {
            segment++;
        }

        while (segment > 0 && t < _offsets[segment])
        {
            segment--;
        }

        if (segment >= last)
        {
            r = _stopsR[last];
            g = _stopsG[last];
            b = _stopsB[last];
            a = _stopsA[last];
            return;
        }

        float local = (t - _segFrom[segment]) * _segInvSpan[segment];
        r = _stopsR[segment] + (_segR[segment] * local);
        g = _stopsG[segment] + (_segG[segment] * local);
        b = _stopsB[segment] + (_segB[segment] * local);
        a = _stopsA[segment] + (_segA[segment] * local);
    }

    /// <summary>点 (x, y) 在渐变轴上的归一化位置；可以超出 0–1，由采样端夹住。</summary>
    protected abstract float ParameterAt(float x, float y);

    private static float GetDitherOffset(int x, int y)
    {
        int index = ((y & 3) * 4) + (x & 3);
        return BayerMatrix[index] - 7.5f;
    }
}

/// <summary>
/// 线性渐变：沿 <c>from</c> → <c>to</c> 的方向插值。两个点重合时退化成单色（全部取最后一个停止点）。
/// </summary>
internal sealed class LinearGradientBrush : GradientBrush
{
    private readonly float _fromX;
    private readonly float _fromY;
    private readonly float _dx;
    private readonly float _dy;
    private readonly float _lengthSquared;

    public LinearGradientBrush(PointF from, PointF to, params ColorStop[] stops)
        : base(stops)
    {
        _fromX = from.X;
        _fromY = from.Y;
        _dx = to.X - from.X;
        _dy = to.Y - from.Y;
        _lengthSquared = (_dx * _dx) + (_dy * _dy);
    }

    public PointF From => new(_fromX, _fromY);

    public PointF To => new(_fromX + _dx, _fromY + _dy);

    internal override GradientBrush WithInverseTransform(Matrix3x2 inverse)
        => new LinearGradientBrush(From, To, Stops.ToArray()) { Dither = Dither, InverseTransform = inverse };

    protected override float ParameterAt(float x, float y)
        => _lengthSquared <= 1e-12f
            ? 1f
            : (((x - _fromX) * _dx) + ((y - _fromY) * _dy)) / _lengthSquared;

    internal override bool TryCreateRowCursor(int x0, int y, out GradientRowCursor cursor)
    {
        if (_lengthSquared <= 1e-12f)
        {
            cursor = new GradientRowCursor { U = 1f };
            return true;
        }

        float invLength = 1f / _lengthSquared;
        float sx = x0 + 0.5f;
        float sy = y + 0.5f;

        // t 沿画布 x 方向是仿射的：t(x) = t0 + dt·(x - x0)。有逆矩阵时导数就是逆矩阵第一列。
        float stepX = 1f;
        float stepY = 0f;
        float sampleX = sx;
        float sampleY = sy;

        if (HasInverseTransform)
        {
            Matrix3x2 m = InverseTransformMatrix;
            stepX = m.M11;
            stepY = m.M12;
            sampleX = (sx * m.M11) + (sy * m.M21) + m.M31;
            sampleY = (sx * m.M12) + (sy * m.M22) + m.M32;
        }

        float t0 = (((sampleX - _fromX) * _dx) + ((sampleY - _fromY) * _dy)) * invLength;
        float dt = ((stepX * _dx) + (stepY * _dy)) * invLength;

        cursor = new GradientRowCursor { U = t0, Du = dt };
        return true;
    }
}

/// <summary>
/// 径向渐变：从 <c>center</c> 向外到 <c>radius</c> 插值。支持椭圆（宽高不同），半径 ≤ 0 时退化成单色。
/// </summary>
internal sealed class RadialGradientBrush : GradientBrush
{
    private readonly float _centerX;
    private readonly float _centerY;
    private readonly float _radiusX;
    private readonly float _radiusY;

    public RadialGradientBrush(PointF center, SizeF radius, params ColorStop[] stops)
        : base(stops)
    {
        _centerX = center.X;
        _centerY = center.Y;
        _radiusX = MathF.Abs(radius.Width);
        _radiusY = MathF.Abs(radius.Height);
    }

    public RadialGradientBrush(PointF center, float radius, params ColorStop[] stops)
        : this(center, new SizeF(radius, radius), stops)
    {
    }

    public PointF Center => new(_centerX, _centerY);

    public SizeF Radius => new(_radiusX, _radiusY);

    internal override GradientBrush WithInverseTransform(Matrix3x2 inverse)
        => new RadialGradientBrush(Center, Radius, Stops.ToArray()) { Dither = Dither, InverseTransform = inverse };

    protected override float ParameterAt(float x, float y)
    {
        if (_radiusX <= 1e-6f || _radiusY <= 1e-6f)
        {
            return 1f;
        }

        float nx = (x - _centerX) / _radiusX;
        float ny = (y - _centerY) / _radiusY;
        return MathF.Sqrt((nx * nx) + (ny * ny));
    }

    internal override bool TryCreateRowCursor(int x0, int y, out GradientRowCursor cursor)
    {
        if (_radiusX <= 1e-6f || _radiusY <= 1e-6f)
        {
            cursor = new GradientRowCursor { U = 1f };
            return true;
        }

        float sx = x0 + 0.5f;
        float sy = y + 0.5f;
        float ax = 1f / _radiusX;
        float ay = 1f / _radiusY;

        // nx、ny 沿画布 x 方向是仿射的 → q = nx² + ny² 是 x 的二次函数，用二阶差分推进（每像素一次开方）
        // 没有逆矩阵时 ny 与 x 无关，所以 duy = 0（写成 ay 会把 q 的二次项算成两倍，圆心直接变黑）
        float dux = ax;
        float duy = 0f;
        float nx = (sx - _centerX) * ax;
        float ny = (sy - _centerY) * ay;

        if (HasInverseTransform)
        {
            Matrix3x2 m = InverseTransformMatrix;
            float sampleX = (sx * m.M11) + (sy * m.M21) + m.M31;
            float sampleY = (sx * m.M12) + (sy * m.M22) + m.M32;
            nx = (sampleX - _centerX) * ax;
            ny = (sampleY - _centerY) * ay;
            dux = m.M11 * ax;
            duy = m.M12 * ay;
        }

        float a2 = (dux * dux) + (duy * duy);
        float q0 = (nx * nx) + (ny * ny);
        float dq = (2f * dux * nx) + (2f * duy * ny) + a2;

        cursor = new GradientRowCursor { U = q0, Du = dq, Ddu = 2f * a2, SquareRoot = true };
        return true;
    }
}
