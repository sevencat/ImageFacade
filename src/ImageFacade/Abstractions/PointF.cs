namespace ImageFacade;

/// <summary>
/// 像素坐标（corner 坐标：像素 <c>(i,j)</c> 覆盖 <c>[i,i+1)×[j,j+1)</c>，中心在 <c>(i+0.5,j+0.5)</c>）。
/// </summary>
public readonly struct PointF : IEquatable<PointF>
{
    public PointF(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }

    public float Y { get; }

    public static PointF Empty => default;

    public bool Equals(PointF other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is PointF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public static bool operator ==(PointF left, PointF right) => left.Equals(right);

    public static bool operator !=(PointF left, PointF right) => !left.Equals(right);

    public override string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({X}, {Y})");
}
