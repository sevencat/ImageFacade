namespace ImageFacade;

/// <summary>解码时读取到的元数据（只读，v1 只透传不编辑）。</summary>
public readonly struct ImageMetadata
{
    public ImageMetadata(
        ImageFormat format,
        int width,
        int height,
        int orientation = 0,
        bool hasAlpha = false,
        ReadOnlyMemory<byte> iccProfile = default)
    {
        Format = format;
        Width = width;
        Height = height;
        Orientation = orientation;
        HasAlpha = hasAlpha;
        IccProfile = iccProfile;
    }

    public ImageFormat Format { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// EXIF 方向 1..8；0 表示未知或未提供。已在解码时应用过 <see cref="LoadOptions.AutoOrient"/> 的图，尺寸就是转正后的尺寸。
    /// </summary>
    public int Orientation { get; }

    public bool HasAlpha { get; }

    /// <summary>ICC 配置（后端支持时透传；空表示无）。</summary>
    public ReadOnlyMemory<byte> IccProfile { get; }

    public static ImageMetadata Empty => default;

    public override string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Format} {Width}x{Height}");
}
