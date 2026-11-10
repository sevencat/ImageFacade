namespace ImageFacade;

/// <summary>整数尺寸（像素）。</summary>
public readonly struct Size : IEquatable<Size>
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

    public bool Equals(Size other) => Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is Size other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Width, Height);

    public static bool operator ==(Size left, Size right) => left.Equals(right);

    public static bool operator !=(Size left, Size right) => !left.Equals(right);

    public override string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Width}x{Height}");
}
