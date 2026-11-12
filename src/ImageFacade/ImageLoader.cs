namespace ImageFacade;

/// <summary>
/// 静态加载入口：走 <see cref="ImageEngine.Default"/>，<c>ImageLoader.LoadJpg("in.jpg")</c> 一行加载。
/// </summary>
/// <remarks>
/// 解码按**内容嗅探**、不认扩展名：文件是什么格式就按什么格式解，
/// <c>LoadJpg</c> / <c>LoadPng</c> 这些名字只是为了调用处可读，语义与 <c>Load</c> 完全一致
/// （尺寸上限、<see cref="ImageSizeLimitExceededException"/> 等错误语义也一样）。
/// <see cref="ImageEngine.Default"/> 未赋值时抛 <see cref="InvalidOperationException"/>——门面不做任何自动探测。
/// </remarks>
public static class ImageLoader
{
    private static ImageEngine Engine => ImageEngine.Default
        ?? throw new InvalidOperationException(
            "ImageEngine.Default 尚未设置。请先赋值（例如 ImageEngine.Default = new ImageEngine(new SkiaBackend())），"
            + "再使用 ImageLoader 的静态加载入口。");

    // ---- JPEG ----

    public static FacadeImage LoadJpg(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadJpg(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadJpg(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadJpgAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadJpgAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadJpgAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);

    // ---- PNG ----

    public static FacadeImage LoadPng(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadPng(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadPng(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadPngAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadPngAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadPngAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);

    // ---- WebP ----

    public static FacadeImage LoadWebP(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadWebP(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadWebP(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadWebPAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadWebPAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadWebPAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);

    // ---- BMP ----

    public static FacadeImage LoadBmp(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadBmp(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadBmp(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadBmpAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadBmpAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadBmpAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);

    // ---- GIF ----

    public static FacadeImage LoadGif(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadGif(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadGif(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadGifAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadGifAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadGifAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);

    // ---- TIFF ----

    public static FacadeImage LoadTiff(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadTiff(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadTiff(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadTiffAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadTiffAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadTiffAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);

    // ---- ICO ----

    public static FacadeImage LoadIco(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadIco(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadIco(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadIcoAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadIcoAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadIcoAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);

    // ---- QOI ----

    public static FacadeImage LoadQoi(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadQoi(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadQoi(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadQoiAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadQoiAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadQoiAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);

    // ---- PNM ----

    public static FacadeImage LoadPnm(string path, LoadOptions? options = null) => Engine.Load(path, options);

    public static FacadeImage LoadPnm(Stream stream, LoadOptions? options = null) => Engine.Load(stream, options);

    public static FacadeImage LoadPnm(ReadOnlySpan<byte> data, LoadOptions? options = null) => Engine.Load(data, options);

    public static Task<FacadeImage> LoadPnmAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(path, options, cancellationToken);

    public static Task<FacadeImage> LoadPnmAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(stream, options, cancellationToken);

    public static Task<FacadeImage> LoadPnmAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Engine.LoadAsync(data, options, cancellationToken);
}
