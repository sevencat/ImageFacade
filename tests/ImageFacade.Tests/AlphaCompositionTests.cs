using ImageFacade.SdcbSimdCodecs;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// M8：目标格式没有 alpha 时，编码前按 <see cref="SaveOptions.BackgroundColor"/> 合成（plan §3.2）。
/// </summary>
public sealed class AlphaCompositionTests
{
    private static ImageEngine CreateEngine()
        => new(new SoftwareBackend(), new ImageEngineOptions { Fallback = FallbackMode.Disabled });

    [Fact]
    public void Jpeg_Transparent_Area_Uses_Configured_Background()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, null);
        image.FillRectangle(new RectF(0, 0, 8, 16), new Rgba32(255, 0, 0, 128));

        byte[] jpeg = image.Encode(new SaveOptions
        {
            Format = ImageFormat.Jpeg,
            Quality = 95,
            BackgroundColor = new Rgba32(0, 0, 255),
        });

        using FacadeImage loaded = engine.Load(jpeg);

        Rgba32 empty = loaded.GetPixel(12, 8);
        Assert.InRange(empty.B, 240, 255);
        Assert.InRange(empty.R, 0, 15);
        Assert.Equal(255, empty.A);

        Rgba32 blended = loaded.GetPixel(4, 8);
        Assert.InRange(blended.R, 110, 145);
        Assert.InRange(blended.B, 110, 145);
    }

    [Fact]
    public void Jpeg_Default_Background_Is_White()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, null);

        byte[] jpeg = image.Encode(new SaveOptions { Format = ImageFormat.Jpeg, Quality = 100 });
        using FacadeImage loaded = engine.Load(jpeg);

        Rgba32 pixel = loaded.GetPixel(4, 4);
        Assert.InRange(pixel.R, 250, 255);
        Assert.InRange(pixel.G, 250, 255);
        Assert.InRange(pixel.B, 250, 255);
    }

    [Fact]
    public void Png_Keeps_Transparency()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, null);
        image.FillRectangle(new RectF(0, 0, 4, 8), Rgba32.Red);

        byte[] png = image.Encode(new SaveOptions { Format = ImageFormat.Png });
        using FacadeImage loaded = engine.Load(png);

        Assert.Equal(0, loaded.GetPixel(6, 4).A);
        Assert.Equal(255, loaded.GetPixel(1, 4).A);
    }

    [Fact]
    public void Bmp_RoundTrip_Is_Lossless()
    {
        using var engine = CreateEngine();
        Assert.True(engine.CanEncode(ImageFormat.Bmp));

        using FacadeImage image = engine.Create(8, 8, new Rgba32(10, 20, 30));
        image.FillRectangle(new RectF(2, 2, 4, 4), new Rgba32(200, 100, 50));

        byte[] bmp = image.Encode(new SaveOptions { Format = ImageFormat.Bmp });
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);

        using FacadeImage loaded = engine.Load(bmp);

        Assert.Equal(ImageFormat.Bmp, loaded.SourceFormat);
        Assert.Equal(new Rgba32(200, 100, 50), loaded.GetPixel(4, 4));
        Assert.Equal(new Rgba32(10, 20, 30), loaded.GetPixel(0, 0));
    }

    [Fact]
    public void Bmp_Transparent_Area_Uses_Configured_Background()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, null);

        byte[] bmp = image.Encode(new SaveOptions
        {
            Format = ImageFormat.Bmp,
            BackgroundColor = new Rgba32(0, 128, 0),
        });

        using FacadeImage loaded = engine.Load(bmp);

        Rgba32 pixel = loaded.GetPixel(4, 4);
        Assert.Equal(new Rgba32(0, 128, 0), pixel);
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void Opaque_Image_Is_Not_Copied_Before_Encoding()
    {
        // 全不透明时不应额外建临时帧：用假后端记录 Create 次数来验证
        var backend = new CountingBackend();
        using var engine = new ImageEngine(backend, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(8, 8, Rgba32.White);

        int before = backend.CreateCalls;
        image.Encode(new SaveOptions { Format = ImageFormat.Jpeg });

        Assert.Equal(before, backend.CreateCalls);
    }
}

/// <summary>只能编 JPEG 的后端（<c>CreateCalls</c> 由 <see cref="FakeBackend"/> 统一计数）。</summary>
internal sealed class CountingBackend : FakeBackend
{
    private static readonly IReadOnlySet<ImageFormat> JpegOnly = new HashSet<ImageFormat> { ImageFormat.Jpeg };

    public CountingBackend()
        : base("counting")
    {
    }

    public override IReadOnlySet<ImageFormat> SupportedEncodeFormats => JpegOnly;

    public override void Encode(ImageFacade.Backends.IFrame frame, Stream destination, in ImageFacade.Backends.EncodeRequest request)
        => destination.Write([1, 2, 3, 4]);
}
