namespace ImageFacade.Software.Rendering;

/// <summary>整数尺寸（像素）。与 ImageSharp 的 <c>Size</c> 对齐，用于 <see cref="ResizeOptions"/>。</summary>
internal readonly struct Size : IEquatable<Size>
{
    public Size(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public static Size Empty => default;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static Size operator *(Size size, int factor) => new(size.Width * factor, size.Height * factor);

    public bool Equals(Size other) => Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is Size other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Width, Height);

    public override string ToString() => $"({Width}, {Height})";

    public static bool operator ==(Size left, Size right) => left.Equals(right);

    public static bool operator !=(Size left, Size right) => !left.Equals(right);
}

/// <summary>浮点尺寸。与 ImageSharp 的 <c>SizeF</c> 对齐。</summary>
internal readonly struct SizeF : IEquatable<SizeF>
{
    public SizeF(float width, float height)
    {
        Width = width;
        Height = height;
    }

    public float Width { get; }

    public float Height { get; }

    public static SizeF Empty => default;

    public static SizeF operator *(SizeF size, float factor) => new(size.Width * factor, size.Height * factor);

    public bool Equals(SizeF other) => Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is SizeF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Width, Height);

    public override string ToString() => $"({Width}, {Height})";

    public static bool operator ==(SizeF left, SizeF right) => left.Equals(right);

    public static bool operator !=(SizeF left, SizeF right) => !left.Equals(right);
}
