namespace ImageFacade.Software.Rendering.Text;

/// <summary>内置字体。</summary>
internal enum BuiltInFont
{
    /// <summary>文泉驿微米黑（WenQuanYi Micro Hei）。</summary>
    WenQuanYiMicroHei,
}

/// <summary>
/// 内置字体数据。
/// </summary>
/// <remarks>
/// 复制改造点（plan §J.1）：**不再内嵌自己的字体资源**，直接取核心程序集的
/// <c>ImageFacade.BuiltInFont.Data</c>，保证整个解决方案只有一份字体字节。
/// </remarks>
internal static class EmbeddedFonts
{
    private static readonly Lazy<byte[]> Cached
        = new(static () => global::ImageFacade.BuiltInFont.Data.ToArray());

    /// <summary>内置字体个数。</summary>
    public static int Count => 1;

    /// <summary>取内置字体的 TTF 字节（已解压）。返回共享缓存数组，调用方不要修改其内容。</summary>
    public static byte[] GetFontData(BuiltInFont font = BuiltInFont.WenQuanYiMicroHei) => font switch
    {
        BuiltInFont.WenQuanYiMicroHei => Cached.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(font), font, "未知的内置字体。"),
    };

    /// <summary>解压后的 TTF 字节数。</summary>
    public static int GetFontSize(BuiltInFont font = BuiltInFont.WenQuanYiMicroHei) => GetFontData(font).Length;

    /// <summary>用内置字体新建 <see cref="TextFont"/>（每次调用都会重新解析字体表）。</summary>
    public static TextFont CreateFont(BuiltInFont font = BuiltInFont.WenQuanYiMicroHei) => TextFont.Load(GetFontData(font));
}
