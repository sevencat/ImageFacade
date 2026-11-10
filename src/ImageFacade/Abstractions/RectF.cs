namespace ImageFacade;

/// <summary>轴对齐矩形（左上角 + 宽高，像素单位，corner 坐标）。</summary>
public readonly struct RectF : IEquatable<RectF>
{
    public RectF(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public RectF(PointF location, SizeF size)
        : this(location.X, location.Y, size.Width, size.Height)
    {
    }

    public float X { get; }

    public float Y { get; }

    public float Width { get; }

    public float Height { get; }

    public float Left => X;

    public float Top => Y;

    public float Right => X + Width;

    public float Bottom => Y + Height;

    public PointF Location => new(X, Y);

    public SizeF Size => new(Width, Height);

    public PointF Center => new(X + (Width / 2f), Y + (Height / 2f));

    public bool IsEmpty => Width <= 0f || Height <= 0f;

    public static RectF Empty => default;

    public static RectF FromLTRB(float left, float top, float right, float bottom)
        => new(left, top, right - left, bottom - top);

    public bool Contains(float x, float y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool Contains(PointF point) => Contains(point.X, point.Y);

    /// <summary>与 <paramref name="other"/> 求交；无交集时返回 <see cref="Empty"/>（宽高为 0）。</summary>
    public RectF Intersect(RectF other)
    {
        float left = MathF.Max(Left, other.Left);
        float top = MathF.Max(Top, other.Top);
        float right = MathF.Min(Right, other.Right);
        float bottom = MathF.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? Empty : FromLTRB(left, top, right, bottom);
    }

    public bool Equals(RectF other)
        => X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is RectF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public static bool operator ==(RectF left, RectF right) => left.Equals(right);

    public static bool operator !=(RectF left, RectF right) => !left.Equals(right);

    public override string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{X}, {Y}, {Width}x{Height}]");
}
