using System.Numerics;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>浮点坐标点。与 ImageSharp 的 <c>PointF</c> 对齐。</summary>
internal readonly struct PointF : IEquatable<PointF>
{
    public PointF(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }

    public float Y { get; }

    public static PointF Empty => default;

    public static PointF operator +(PointF left, PointF right) => new(left.X + right.X, left.Y + right.Y);

    public static PointF operator -(PointF left, PointF right) => new(left.X - right.X, left.Y - right.Y);

    public static PointF operator *(PointF point, float scale) => new(point.X * scale, point.Y * scale);

    public PointF Transform(Matrix3x2 matrix) => new(
        (X * matrix.M11) + (Y * matrix.M21) + matrix.M31,
        (X * matrix.M12) + (Y * matrix.M22) + matrix.M32);

    public bool Equals(PointF other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is PointF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString() => $"({X}, {Y})";

    public static bool operator ==(PointF left, PointF right) => left.Equals(right);

    public static bool operator !=(PointF left, PointF right) => !left.Equals(right);
}

/// <summary>浮点矩形（左上角 + 尺寸），<see cref="IPath.Bounds"/> 与裁剪计算用。</summary>
internal readonly struct RectF : IEquatable<RectF>
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

    public static RectF Empty => default;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>向外取整到像素网格，光栅化裁剪用。</summary>
    public Rectangle ToBoundingRectangle() => new(
        (int)MathF.Floor(Left),
        (int)MathF.Floor(Top),
        (int)MathF.Ceiling(Right) - (int)MathF.Floor(Left),
        (int)MathF.Ceiling(Bottom) - (int)MathF.Floor(Top));

    public bool Equals(RectF other)
        => X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is RectF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public override string ToString() => $"({X}, {Y}, {Width}, {Height})";

    public static bool operator ==(RectF left, RectF right) => left.Equals(right);

    public static bool operator !=(RectF left, RectF right) => !left.Equals(right);
}
