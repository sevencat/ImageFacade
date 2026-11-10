using ImageFacade.SkiaSharp;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>SkiaSharp 后端（M3）的端到端行为；与软后端跑同一套断言，保证语义一致。</summary>
public sealed class SkiaBackendTests
{
    private static ImageEngine CreateEngine()
        => new(new SkiaBackend());

    [Fact]
    public void Create_And_GetPixel_RoundTrips()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, new Rgba32(0x20, 0x60, 0xC0));

        Assert.Equal("skia", image.BackendName);
        Assert.Equal(new Rgba32(0x20, 0x60, 0xC0), image.GetPixel(0, 0));
        Assert.Equal(new Rgba32(0x20, 0x60, 0xC0), image.GetPixel(7, 7));
    }

    [Fact]
    public void FillRectangle_And_SemiTransparent_Blend()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        image.FillRectangle(new RectF(0, 0, 20, 20), new Rgba32(0, 0, 0, 128));

        Rgba32 pixel = image.GetPixel(10, 10);
        Assert.InRange(pixel.R, 126, 129);
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void DrawLine_Horizontal_Is_Solid_In_Interior()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(100, 50, Rgba32.White);

        image.DrawLine(new PointF(10, 20.5f), new PointF(90, 20.5f), Rgba32.Red, width: 4);

        Assert.Equal(Rgba32.Red, image.GetPixel(50, 20));
        Assert.Equal(Rgba32.White, image.GetPixel(50, 5));
    }

    [Fact]
    public void DrawText_With_BuiltIn_Font_Draws_Chinese()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(200, 80, Rgba32.White);

        var style = new TextStyle { Font = FontRef.BuiltIn, Size = 32, Color = Rgba32.Black };
        SizeF measured = image.MeasureText("中文Ab", style);

        Assert.True(measured.Width > 0f);
        Assert.True(measured.Height > 0f);

        image.DrawText("中文Ab", new PointF(8, 8), style);

        int ink = 0;
        byte[] pixels = image.CopyPixels();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 128)
            {
                ink++;
            }
        }

        Assert.True(ink > 50, $"文字墨迹像素太少：{ink}");
    }

    [Fact]
    public void MeasureText_Is_Consistent_With_Drawn_Ink_Width()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(300, 80, Rgba32.White);

        var style = new TextStyle { Font = FontRef.BuiltIn, Size = 24, Color = Rgba32.Black };
        SizeF measured = image.MeasureText("Wg文字", style);
        image.DrawText("Wg文字", new PointF(0, 0), style);

        int minX = int.MaxValue;
        int maxX = -1;
        byte[] pixels = image.CopyPixels();
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int offset = ((y * image.Width) + x) * 4;
                if (pixels[offset] < 128)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                }
            }
        }

        Assert.True(maxX >= 0);
        Assert.InRange(maxX - minX + 1, 1, (int)MathF.Ceiling(measured.Width) + 2);
    }

    [Fact]
    public void Resize_Pad_Works_On_Skia()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(100, 50, Rgba32.White);

        image.Resize(new ResizeOptions { Width = 60, Height = 60, Mode = ResizeMode.Pad, PadColor = Rgba32.Red });

        Assert.Equal(60, image.Width);
        Assert.Equal(60, image.Height);
        Assert.Equal(Rgba32.Red, image.GetPixel(0, 0));
        Assert.Equal(Rgba32.White, image.GetPixel(30, 30));
    }

    [Fact]
    public void Png_RoundTrip_Preserves_Pixels()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, Rgba32.White);
        image.FillRectangle(new RectF(4, 4, 8, 8), new Rgba32(0, 128, 255));

        byte[] png = image.Encode(new SaveOptions { Format = ImageFormat.Png });
        using FacadeImage loaded = engine.Load(png);

        Assert.Equal(ImageFormat.Png, loaded.SourceFormat);
        Assert.Equal(new Rgba32(0, 128, 255), loaded.GetPixel(8, 8));
        Assert.Equal(Rgba32.White, loaded.GetPixel(0, 0));
    }

    [Fact]
    public void Jpeg_Transparent_Area_Uses_Background()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, null);

        byte[] jpeg = image.Encode(new SaveOptions
        {
            Format = ImageFormat.Jpeg,
            Quality = 95,
            BackgroundColor = new Rgba32(0, 0, 255),
        });

        using FacadeImage loaded = engine.Load(jpeg);

        Rgba32 pixel = loaded.GetPixel(8, 8);
        Assert.InRange(pixel.B, 240, 255);
        Assert.InRange(pixel.R, 0, 15);
    }

    [Fact]
    public void CopyPixels_Bgra_Swaps_Channels()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(2, 2, new Rgba32(0x20, 0x40, 0x80));

        byte[] bgra = image.CopyPixels(PixelFormat.Bgra32);

        Assert.Equal(new byte[] { 0x80, 0x40, 0x20, 0xFF }, bgra[..4]);
    }

    [Fact]
    public void AcquirePixels_Writes_Back_To_Bitmap()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        using (var lease = image.AcquirePixels())
        {
            lease.Pixels[0] = 10;
            lease.Pixels[1] = 20;
            lease.Pixels[2] = 30;
            lease.Pixels[3] = 255;
        }

        Assert.Equal(new Rgba32(10, 20, 30), image.GetPixel(0, 0));
    }
}
