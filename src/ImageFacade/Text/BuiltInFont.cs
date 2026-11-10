using System.IO.Compression;

namespace ImageFacade;

/// <summary>
/// 内置字体（文泉驿微米黑，Apache-2.0 / GPLv3 + 字体嵌入例外）。
/// 整个解决方案只有这一份字体资源：Skia / ImageSharp / 软后端都从这里取字节。
/// </summary>
public static class BuiltInFont
{
    private const string ResourceName = "ImageFacade.Fonts.WenQuanYiMicroHei.ttf.gz";

    private static ReadOnlyMemory<byte>? _data;

    private static readonly object Gate = new();

    /// <summary>字体文件（TTF）字节；首次访问时解压并缓存。</summary>
    public static ReadOnlyMemory<byte> Data
    {
        get
        {
            if (_data is { } cached)
            {
                return cached;
            }

            lock (Gate)
            {
                if (_data is { } again)
                {
                    return again;
                }

                ReadOnlyMemory<byte> loaded = Load();
                _data = loaded;
                return loaded;
            }
        }
    }

    private static ReadOnlyMemory<byte> Load()
    {
        using Stream? resource = typeof(BuiltInFont).Assembly.GetManifestResourceStream(ResourceName);
        if (resource is null)
        {
            throw new InvalidOperationException(
                $"内置字体资源缺失（{ResourceName}）。请确认 ImageFacade.csproj 里包含 Fonts\\WenQuanYiMicroHei.ttf.gz 的 EmbeddedResource。");
        }

        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }
}
