namespace ImageFacade;

/// <summary>浮点尺寸（排版测量结果、缩放中间值用）。</summary>
public readonly struct SizeF : IEquatable<SizeF>
{
    public SizeF(float width, float height)
    {
        Width = width;
        Height = height;
    }

    public float Width { get; }

    public float Height { get; }

    public static SizeF Empty => default;

    public bool Equals(SizeF other) => Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is SizeF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Width, Height);

    public static bool operator ==(SizeF left, SizeF right) => left.Equals(right);

    public static bool operator !=(SizeF left, SizeF right) => !left.Equals(right);

    public override string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Width}x{Height}");
}
