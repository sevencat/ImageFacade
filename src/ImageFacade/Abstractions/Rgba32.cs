namespace ImageFacade;

/// <summary>
/// 直通（非预乘）8 位 RGBA 颜色。默认值 <c>default</c> 即全透明 <c>(0,0,0,0)</c>。
/// </summary>
public readonly struct Rgba32 : IEquatable<Rgba32>
{
    public Rgba32(byte r, byte g, byte b, byte a = 255)
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

    public Rgba32 WithAlpha(byte a) => new(R, G, B, a);

    /// <summary>由 <c>0xRRGGBB</c> 构造。</summary>
    public static Rgba32 FromHex(uint rgb, byte a = 255)
        => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);

    /// <summary>解析 <c>#RRGGBB</c> / <c>#RRGGBBAA</c> / <c>#RGB</c> / 命名色。</summary>
    public static Rgba32 Parse(string text)
        => TryParse(text, out Rgba32 color)
            ? color
            : throw new FormatException($"无法解析颜色：'{text}'。支持 '#RRGGBB'、'#RRGGBBAA'、'#RGB' 与命名色。");

    public static bool TryParse(string? text, out Rgba32 color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim();

        if (s[0] == '#')
        {
            string hex = s[1..];
            if (hex.Length == 3)
            {
                if (!TryHex(hex[0], out byte r0) || !TryHex(hex[1], out byte g0) || !TryHex(hex[2], out byte b0))
                {
                    return false;
                }

                color = new Rgba32((byte)(r0 * 17), (byte)(g0 * 17), (byte)(b0 * 17));
                return true;
            }

            if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            {
                color = FromHex(rgb);
                return true;
            }

            if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgba))
            {
                color = new Rgba32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
                return true;
            }

            return false;
        }

        return TryParseName(s, out color);

        static bool TryHex(char c, out byte value)
        {
            value = 0;
            if (c is >= '0' and <= '9')
            {
                value = (byte)(c - '0');
                return true;
            }

            if (c is >= 'a' and <= 'f')
            {
                value = (byte)(c - 'a' + 10);
                return true;
            }

            if (c is >= 'A' and <= 'F')
            {
                value = (byte)(c - 'A' + 10);
                return true;
            }

            return false;
        }
    }

    private static bool TryParseName(string name, out Rgba32 color)
    {
        switch (name.ToLowerInvariant())
        {
            case "transparent": color = Transparent; return true;
            case "black": color = Black; return true;
            case "white": color = White; return true;
            case "red": color = Red; return true;
            case "green": color = Green; return true;
            case "blue": color = Blue; return true;
            case "yellow": color = Yellow; return true;
            case "orange": color = Orange; return true;
            case "gray":
            case "grey": color = Gray; return true;
            case "magenta": color = Magenta; return true;
            case "cyan": color = Cyan; return true;
            default: color = default; return false;
        }
    }

    public static Rgba32 Transparent => default;

    public static Rgba32 Black => new(0, 0, 0);

    public static Rgba32 White => new(255, 255, 255);

    public static Rgba32 Red => new(255, 0, 0);

    /// <summary>纯绿 <c>(0,255,0)</c>，即 CSS 的 lime（CSS 的 green 是 <c>(0,128,0)</c>）。</summary>
    public static Rgba32 Green => new(0, 255, 0);

    public static Rgba32 Blue => new(0, 0, 255);

    public static Rgba32 Yellow => new(255, 255, 0);

    public static Rgba32 Orange => new(255, 165, 0);

    public static Rgba32 Gray => new(128, 128, 128);

    public static Rgba32 Magenta => new(255, 0, 255);

    public static Rgba32 Cyan => new(0, 255, 255);

    public bool Equals(Rgba32 other)
        => R == other.R && G == other.G && B == other.B && A == other.A;

    public override bool Equals(object? obj) => obj is Rgba32 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    public static bool operator ==(Rgba32 left, Rgba32 right) => left.Equals(right);

    public static bool operator !=(Rgba32 left, Rgba32 right) => !left.Equals(right);

    public override string ToString()
        => A == 255
            ? $"#{R:X2}{G:X2}{B:X2}"
            : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}
