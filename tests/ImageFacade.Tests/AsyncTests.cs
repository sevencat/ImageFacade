using ImageFacade.Backends;
using ImageFacade.SkiaSharp;
using Xunit;

namespace ImageFacade.Tests;

public sealed class AsyncTests
{
    private static ImageEngine CreateEngine()
        => new(new SkiaBackend());

    [Fact]
    public async Task SaveAsync_And_LoadAsync_RoundTrip()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, Rgba32.White);
        image.FillRectangle(new RectF(2, 2, 4, 4), Rgba32.Red);

        string path = Path.Combine(Path.GetTempPath(), $"imagefacade-async-{Guid.NewGuid():N}.png");
        try
        {
            await image.SaveAsync(path);

            using FacadeImage loaded = await engine.LoadAsync(path);

            Assert.Equal(16, loaded.Width);
            Assert.Equal(Rgba32.Red, loaded.GetPixel(3, 3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EncodeAsync_LoadAsync_Stream_RoundTrip()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, Rgba32.Blue);

        byte[] png = await image.EncodeAsync(new SaveOptions { Format = ImageFormat.Png });
        Assert.True(png.Length > 8);

        using var stream = new MemoryStream(png);
        using FacadeImage loaded = await engine.LoadAsync(stream);

        Assert.Equal(8, loaded.Width);
        Assert.Equal(ImageFormat.Png, loaded.SourceFormat);
        Assert.Equal(Rgba32.Blue, loaded.GetPixel(4, 4));
    }

    [Fact]
    public async Task LoadAsync_PreCancelled_Throws()
    {
        using var engine = CreateEngine();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.LoadAsync(new byte[] { 1, 2, 3, 4 }, null, cts.Token));
    }

    [Fact]
    public async Task EncodeAsync_PreCancelled_Throws()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => image.EncodeAsync(new SaveOptions { Format = ImageFormat.Png }, cts.Token));
    }

    [Fact]
    public async Task Uses_Async_Backend_Interface_When_Available()
    {
        var backend = new AsyncFakeBackend();
        using var engine = new ImageEngine(backend);

        using FacadeImage image = await engine.LoadAsync(new byte[] { 1, 2, 3 });

        Assert.Equal(1, backend.AsyncDecodeCalls);
        Assert.Equal(4, image.Width);
        Assert.Equal(3, image.Height);

        byte[] encoded = await image.EncodeAsync(new SaveOptions { Format = ImageFormat.Png });

        Assert.Equal(1, backend.AsyncEncodeCalls);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, encoded);
    }

    [Fact]
    public async Task Sync_Backend_Is_Wrapped_In_Task_Run()
    {
        var backend = new PngFakeBackend();
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        byte[] encoded = await image.EncodeAsync(new SaveOptions { Format = ImageFormat.Png });

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, encoded);
        Assert.Equal(1, backend.EncodeCalls);
        Assert.True(backend.EncodedOnThreadPool, "同步后端应在线程池上执行（Task.Run 回退）");
    }
}

/// <summary>只能编码 PNG 的同步后端：用来验证"没有异步接口时用 Task.Run"。</summary>
internal sealed class PngFakeBackend : FakeBackend
{
    private static readonly IReadOnlySet<ImageFormat> PngOnly = new HashSet<ImageFormat> { ImageFormat.Png };

    public int EncodeCalls { get; private set; }

    public bool EncodedOnThreadPool { get; private set; }

    public override IReadOnlySet<ImageFormat> SupportedEncodeFormats => PngOnly;

    public override void Encode(IFrame frame, Stream destination, in EncodeRequest request)
    {
        EncodeCalls++;
        EncodedOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
        destination.Write([1, 2, 3, 4]);
    }
}

/// <summary>实现了可选异步 SPI 的后端，用来验证门面优先走异步接口。</summary>
internal sealed class AsyncFakeBackend : FakeBackend, IAsyncImageBackend
{
    private static readonly IReadOnlySet<ImageFormat> PngOnly = new HashSet<ImageFormat> { ImageFormat.Png };

    public AsyncFakeBackend()
        : base("async-fake")
    {
    }

    public int AsyncDecodeCalls { get; private set; }

    public int AsyncEncodeCalls { get; private set; }

    /// <summary>异步路径同样要经过能力表检查，所以这里必须声明 PNG。</summary>
    public override IReadOnlySet<ImageFormat> SupportedEncodeFormats => PngOnly;

    public Task<DecodedImage> DecodeAsync(ReadOnlyMemory<byte> data, DecodeRequest request, CancellationToken cancellationToken)
    {
        AsyncDecodeCalls++;
        cancellationToken.ThrowIfCancellationRequested();

        IFrame frame = Create(4, 3, new Rgba32(1, 2, 3));
        return Task.FromResult(new DecodedImage(frame, new ImageMetadata(ImageFormat.Png, 4, 3)));
    }

    public Task EncodeAsync(IFrame frame, Stream destination, EncodeRequest request, CancellationToken cancellationToken)
    {
        AsyncEncodeCalls++;
        return destination.WriteAsync(new byte[] { 1, 2, 3, 4 }, cancellationToken).AsTask();
    }
}
