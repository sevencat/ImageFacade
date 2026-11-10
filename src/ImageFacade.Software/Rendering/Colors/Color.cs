namespace ImageFacade.Software.Rendering;

/// <summary>
/// 8 位直通（非预乘）RGBA 颜色。画布内部存预乘，这个类型只出现在 API 边界。
/// </summary>
internal readonly struct Color : IEquatable<Color>
{
    public Color(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }

    public byte A { get; }

    public static Color Transparent => default;

    public static Color Black => new(0, 0, 0);

    public static Color White => new(255, 255, 255);

    public static Color Red => new(255, 0, 0);

    public static Color Green => new(0, 255, 0);

    public static Color Blue => new(0, 0, 255);

    public static Color Yellow => new(255, 255, 0);

    public static Color Cyan => new(0, 255, 255);

    public static Color Magenta => new(255, 0, 255);

    public static Color Gray => new(128, 128, 128);

    public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

    public static Color FromRgba(byte r, byte g, byte b, byte a) => new(r, g, b, a);

    public static Color FromGray(byte v) => new(v, v, v, 255);

    public Color WithAlpha(byte a) => new(R, G, B, a);

    /// <summary>source-over 合成（非预乘语义）：把 <paramref name="over"/> 画到当前颜色上。</summary>
    public Color Blend(Color over)
    {
        int sa = over.A;
        if (sa == 0)
        {
            return this;
        }

        if (sa == 255)
        {
            return over;
        }

        int da = A;
        int inv = 255 - sa;

        int outA = sa + PixelOps.Div255((uint)(da * inv));
        if (outA == 0)
        {
            return Transparent;
        }

        int r = PixelOps.Div255((uint)(over.R * sa)) + PixelOps.Div255((uint)(PixelOps.Div255((uint)(R * da)) * inv));
        int g = PixelOps.Div255((uint)(over.G * sa)) + PixelOps.Div255((uint)(PixelOps.Div255((uint)(G * da)) * inv));
        int b = PixelOps.Div255((uint)(over.B * sa)) + PixelOps.Div255((uint)(PixelOps.Div255((uint)(B * da)) * inv));

        return new Color(
            PixelOps.Unpremultiply((byte)r, (byte)outA),
            PixelOps.Unpremultiply((byte)g, (byte)outA),
            PixelOps.Unpremultiply((byte)b, (byte)outA),
            (byte)outA);
    }

    public bool Equals(Color other) => R == other.R && G == other.G && B == other.B && A == other.A;

    public override bool Equals(object? obj) => obj is Color other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    public override string ToString() => $"rgba({R}, {G}, {B}, {A})";

    public static bool operator ==(Color left, Color right) => left.Equals(right);

    public static bool operator !=(Color left, Color right) => !left.Equals(right);
}
