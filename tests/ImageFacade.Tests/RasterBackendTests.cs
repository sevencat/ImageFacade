using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// 只引主包时的端到端路径：<c>new ImageEngine()</c> 自带核心光栅，能建图 / 画 / 交像素，
/// 但没有编解码。这里同时钉住"公开面不新增 Rendering 内部类型"。
/// </summary>
public sealed class RasterBackendTests
{
    [Fact]
    public void Default_Constructor_Creates_Draws_And_Exports()
    {
        using var engine = new ImageEngine();
        using FacadeImage image = engine.Create(64, 24, Rgba32.Black);

        Assert.Equal("raster", image.BackendName);

        image.DrawText("Hi", new PointF(2, 2), new TextStyle
        {
            Font = FontRef.BuiltIn,
            Size = 16,
            Color = Rgba32.White,
        });

        byte[] pixels = new byte[64 * 24 * 4];
        image.CopyPixelsTo(pixels, 64 * 4);

        Assert.Contains(pixels, value => value != 0);
    }

    [Fact]
    public void Default_Constructor_Draws_Cjk()
    {
        using var engine = new ImageEngine();
        using FacadeImage image = engine.Create(64, 32, Rgba32.Black);

        image.DrawText("中文", new PointF(2, 2), new TextStyle
        {
            Font = FontRef.BuiltIn,
            Size = 16,
            Color = Rgba32.White,
        });

        byte[] pixels = new byte[64 * 32 * 4];
        image.CopyPixelsTo(pixels, 64 * 4);

        Assert.Contains(pixels, value => value != 0);
    }

    [Fact]
    public void SemiTransparent_Fill_Blends_Over_Background()
    {
        using var engine = new ImageEngine();
        using FacadeImage image = engine.Create(8, 8, Rgba32.White);

        image.FillRectangle(new RectF(0, 0, 8, 8), new Rgba32(0, 0, 0, 128));

        Rgba32 pixel = image.GetPixel(4, 4);
        Assert.InRange(pixel.R, 126, 129);
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void Default_Constructor_Has_No_Codecs()
    {
        using var engine = new ImageEngine();
        Assert.False(engine.CanDecode(ImageFormat.Png));
        Assert.False(engine.CanEncode(ImageFormat.Png));

        byte[] encoded = [1, 2, 3];
        Assert.Throws<FeatureNotSupportedException>(() => engine.Load(encoded));
    }

    [Fact]
    public void Rendering_Internals_Are_Not_Public()
    {
        Type[] exported = typeof(ImageEngine).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported, type => type.Name is "RasterBackend" or "RasterFrame" or "Canvas");
    }

    [Fact]
    public void ImportPixels_Reuses_Canvas_And_Overwrites_Previous_Frame()
    {
        using var engine = new ImageEngine();
        using FacadeImage canvas = engine.Create(4, 3, Rgba32.White);

        byte[] black = new byte[4 * 3 * 4];
        for (int i = 3; i < black.Length; i += 4)
        {
            black[i] = 255;   // 不透明黑
        }

        canvas.ImportPixels(black, 4 * 4);

        Rgba32 corner = canvas.GetPixel(3, 2);
        Assert.Equal(0, corner.R);
        Assert.Equal(0, corner.G);
        Assert.Equal(0, corner.B);
        Assert.Equal(255, corner.A);
        Assert.Equal(black, canvas.CopyPixels());   // 全不透明：直通 ↔ 预乘往返应逐字节一致
    }

    [Fact]
    public void ImportPixels_Converts_Bgra_To_Straight_Rgba32()
    {
        using var engine = new ImageEngine();
        using FacadeImage canvas = engine.Create(1, 1, Rgba32.Black);

        byte[] bgra = [10, 20, 30, 255];   // B, G, R, A
        canvas.ImportPixels(bgra, 4, PixelFormat.Bgra32);

        Rgba32 pixel = canvas.GetPixel(0, 0);
        Assert.Equal(30, pixel.R);
        Assert.Equal(20, pixel.G);
        Assert.Equal(10, pixel.B);
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void ImportPixels_Validates_Buffer_Size()
    {
        using var engine = new ImageEngine();
        using FacadeImage canvas = engine.Create(4, 4, Rgba32.Black);

        Assert.Throws<ArgumentException>(() => canvas.ImportPixels(new byte[4 * 4 * 4], stride: 8));
        Assert.Throws<ArgumentException>(() => canvas.ImportPixels(new byte[16], stride: 16));
    }

    [Fact]
    public void ImportPixels_Invalidates_Cached_Transparency()
    {
        using var engine = new ImageEngine();
        using FacadeImage canvas = engine.Create(2, 2, Rgba32.White);

        Assert.False(canvas.DetectTransparency() ?? true);

        byte[] halfTransparent = new byte[2 * 2 * 4];
        for (int i = 0; i < halfTransparent.Length; i += 4)
        {
            halfTransparent[i] = 255;
            halfTransparent[i + 1] = 255;
            halfTransparent[i + 2] = 255;
            halfTransparent[i + 3] = 128;
        }

        canvas.ImportPixels(halfTransparent, 2 * 4);

        Assert.True(canvas.DetectTransparency() ?? false);
    }
}
