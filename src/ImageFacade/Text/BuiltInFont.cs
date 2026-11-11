using System.IO.Compression;

namespace ImageFacade;

/// <summary>
/// 内置字体（文泉驿微米黑，Apache-2.0 / GPLv3 + 字体嵌入例外）。
/// 整个解决方案只有这一份字体资源：Skia / ImageSharp / 软后端都从这里取字节。
/// </summary>
public static class BuiltInFont
{
    private const string ResourceName = "ImageFacade.Fonts.WenQuanYiMicroHei.ttf.gz";

    /// <summary>
    /// 用 <see cref="Lazy{T}"/> 而不是手写双检锁：<see cref="ReadOnlyMemory{T}"/> 是"引用 + 长度"的
    /// 多字段结构，非原子发布，普通字段的双检锁可能让另一个线程读到撕裂值。
    /// </summary>
    private static readonly Lazy<ReadOnlyMemory<byte>> LazyData = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>字体文件（TTF）字节；首次访问时解压并缓存。</summary>
    public static ReadOnlyMemory<byte> Data => LazyData.Value;

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
