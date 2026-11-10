namespace ImageFacade;

public enum FontStyle
{
    Regular = 0,
    Bold = 1,
    Italic = 2,
    BoldItalic = 3,
}

/// <summary>
/// 字体引用（值语义）。解析顺序：<see cref="Data"/> → <see cref="FilePath"/> → <see cref="Family"/>（系统字体）→
/// <see cref="BuiltIn"/>（内置文泉驿微米黑）。整段缺字时整段回退到内置字体（v1 不做逐字形回退）。
/// </summary>
public sealed class FontRef : IEquatable<FontRef>
{
    private FontRef(string? family, string? filePath, ReadOnlyMemory<byte>? data, FontStyle style, bool isBuiltIn)
    {
        FamilyName = family;
        FilePath = filePath;
        Data = data;
        Style = style;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>系统字体族名；静态工厂 <see cref="Family(string, FontStyle)"/> 与属性重名会冲突，故属性用 -Name 结尾。</summary>
    public string? FamilyName { get; }

    public string? FilePath { get; }

    public ReadOnlyMemory<byte>? Data { get; }

    public FontStyle Style { get; }

    public bool IsBuiltIn { get; }

    public static FontRef BuiltIn { get; } = new(null, null, null, FontStyle.Regular, isBuiltIn: true);

    public static FontRef Family(string name, FontStyle style = FontStyle.Regular)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new FontRef(name, null, null, style, isBuiltIn: false);
    }

    public static FontRef FromFile(string path, FontStyle style = FontStyle.Regular)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FontRef(null, path, null, style, isBuiltIn: false);
    }

    public static FontRef FromData(ReadOnlyMemory<byte> data, FontStyle style = FontStyle.Regular)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("字体数据为空。", nameof(data));
        }

        return new FontRef(null, null, data, style, isBuiltIn: false);
    }

    public bool Equals(FontRef? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return IsBuiltIn == other.IsBuiltIn
            && Style == other.Style
            && string.Equals(FamilyName, other.FamilyName, StringComparison.Ordinal)
            && string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
            && DataEquals(Data, other.Data);
    }

    private static bool DataEquals(ReadOnlyMemory<byte>? a, ReadOnlyMemory<byte>? b)
    {
        if (a.HasValue != b.HasValue)
        {
            return false;
        }

        return !a.HasValue || a.Value.Span.SequenceEqual(b!.Value.Span);
    }

    public override bool Equals(object? obj) => Equals(obj as FontRef);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsBuiltIn);
        hash.Add(Style);
        hash.Add(FamilyName, StringComparer.Ordinal);
        hash.Add(FilePath, StringComparer.Ordinal);
        if (Data is { } data)
        {
            hash.Add(data.Length);
            ReadOnlySpan<byte> span = data.Span;
            // 只取首尾各 16 字节参与哈希，避免每次算全量哈希（内容相等仍由 Equals 判定）。
            int take = Math.Min(16, span.Length);
            for (int i = 0; i < take; i++)
            {
                hash.Add(span[i]);
                hash.Add(span[span.Length - 1 - i]);
            }
        }

        return hash.ToHashCode();
    }

    public override string ToString() => IsBuiltIn
        ? $"BuiltIn({Style})"
        : FamilyName is not null
            ? $"Family({FamilyName},{Style})"
            : FilePath is not null
                ? $"File({FilePath},{Style})"
                : $"Data({Data?.Length ?? 0}B,{Style})";
}
