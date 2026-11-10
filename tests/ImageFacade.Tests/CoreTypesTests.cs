using Xunit;

namespace ImageFacade.Tests;

public sealed class CoreTypesTests
{
    [Fact]
    public void Rgba32_Parse_Hex_And_Named()
    {
        Assert.Equal(new Rgba32(0x11, 0x22, 0x33), Rgba32.Parse("#112233"));
        Assert.Equal(new Rgba32(0x11, 0x22, 0x33, 0x44), Rgba32.Parse("#11223344"));
        Assert.Equal(new Rgba32(0xAA, 0xBB, 0xCC), Rgba32.Parse("#ABC"));
        Assert.Equal(Rgba32.Red, Rgba32.Parse("red"));
        Assert.True(Rgba32.TryParse("not-a-color", out _) == false);
    }

    [Fact]
    public void Rgba32_Default_Is_Transparent()
    {
        Assert.Equal(new Rgba32(0, 0, 0, 0), default(Rgba32));
        Assert.Equal(Rgba32.Transparent, default(Rgba32));
    }

    [Fact]
    public void ImageFormats_FromExtension()
    {
        Assert.Equal(ImageFormat.Png, ImageFormats.FromExtension("a.PNG"));
        Assert.Equal(ImageFormat.Jpeg, ImageFormats.FromExtension("a.jpeg"));
        Assert.Equal(ImageFormat.Unknown, ImageFormats.FromExtension("a.unknown"));
        Assert.True(ImageFormat.Jpeg.SupportsAlpha() == false);
        Assert.True(ImageFormat.Png.SupportsAlpha());
    }

    [Fact]
    public void PixelConverter_Rgba_To_Bgra_Swaps_Channels()
    {
        byte[] source = [10, 20, 30, 40];
        byte[] destination = new byte[4];

        PixelConverter.Convert(source, 4, PixelFormat.Rgba32, destination, 4, PixelFormat.Bgra32, 1, 1);

        Assert.Equal(new byte[] { 30, 20, 10, 40 }, destination);
    }

    [Fact]
    public void PixelConverter_Bgr24_To_Rgba32_Fills_Opaque()
    {
        byte[] source = [30, 20, 10];
        byte[] destination = new byte[4];

        PixelConverter.Convert(source, 3, PixelFormat.Bgr24, destination, 4, PixelFormat.Rgba32, 1, 1);

        Assert.Equal(new byte[] { 10, 20, 30, 255 }, destination);
    }

    [Fact]
    public void PixelConverter_Bgra_Premultiplied_RoundTrip()
    {
        byte[] straight = [200, 100, 50, 128];
        byte[] premultiplied = new byte[4];
        byte[] back = new byte[4];

        PixelConverter.Convert(straight, 4, PixelFormat.Rgba32, premultiplied, 4, PixelFormat.Bgra32Premultiplied, 1, 1);
        PixelConverter.Convert(premultiplied, 4, PixelFormat.Bgra32Premultiplied, back, 4, PixelFormat.Rgba32, 1, 1);

        // 预乘/反预乘在 8 位上不可逆，允许 ±1
        Assert.InRange(Math.Abs(back[0] - 200), 0, 1);
        Assert.InRange(Math.Abs(back[1] - 100), 0, 1);
        Assert.InRange(Math.Abs(back[2] - 50), 0, 1);
        Assert.Equal(128, back[3]);
    }

    [Fact]
    public void PixelConverter_Rejects_Too_Small_Stride()
    {
        byte[] source = new byte[16];
        byte[] destination = new byte[16];

        Assert.Throws<ArgumentException>(() =>
            PixelConverter.Convert(source, 2, PixelFormat.Rgba32, destination, 4, PixelFormat.Rgba32, 2, 2));
    }
}
