using ImageFacade.Backends;
using ImageFacade.SdcbSimdCodecs;
using Xunit;

namespace ImageFacade.SdcbSimdCodecs.Tests;

/// <summary>软后端（复制改造的 ImgProcSharp 渲染层）的端到端行为。</summary>
public sealed class SoftwareBackendTests
{
    private static ImageEngine CreateEngine()
        => new(new SoftwareBackend());

    [Fact]
    public void Encode_Unregistered_Format_Throws_Feature_Not_Supported()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, Rgba32.White);

        // 软后端自带 PNG/JPEG/BMP；GIF 没注册 → 能力表里没有它
        Assert.False(engine.CanEncode(ImageFormat.Gif));
        Assert.False(engine.CanDecode(ImageFormat.Gif));

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => image.Encode(new SaveOptions { Format = ImageFormat.Gif }));
        Assert.Equal("Encode", ex.Feature);
    }

    [Fact]
    public void Decode_Unregistered_Format_Throws_Feature_Not_Supported()
    {
        using var engine = CreateEngine();

        // GIF89a 头：没有任何已注册的解码器认领
        byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00];

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(() => engine.Load(gif));
        Assert.Equal("Decode", ex.Feature);
    }

    [Fact]
    public void Registered_Formats_Are_Exposed_From_The_Registry()
    {
        using var engine = CreateEngine();

        Assert.True(engine.CanDecode(ImageFormat.Png));
        Assert.True(engine.CanDecode(ImageFormat.Jpeg));
        Assert.True(engine.CanDecode(ImageFormat.Bmp));
        Assert.True(engine.CanEncode(ImageFormat.Png));
        Assert.True(engine.CanEncode(ImageFormat.Jpeg));
        Assert.True(engine.CanEncode(ImageFormat.Bmp));
    }

    [Fact]
    public void Create_And_GetPixel_RoundTrips_Straight_Rgba()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, new Rgba32(0x20, 0x60, 0xC0));

        Assert.Equal(new Rgba32(0x20, 0x60, 0xC0), image.GetPixel(0, 0));
        Assert.Equal(new Rgba32(0x20, 0x60, 0xC0), image.GetPixel(7, 7));
    }

    [Fact]
    public void SemiTransparent_Clear_Keeps_Alpha()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(4, 4, null);
        image.Clear(new Rgba32(255, 0, 0, 128));

        Rgba32 pixel = image.GetPixel(1, 1);

        // 预乘/反预乘在 8 位上不可逆，允许 ±1
        Assert.InRange(Math.Abs(pixel.R - 255), 0, 1);
        Assert.InRange(Math.Abs(pixel.G - 0), 0, 1);
        Assert.InRange(Math.Abs(pixel.A - 128), 0, 1);
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
    public void FillRectangle_Covers_Exact_Area()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.FillRectangle(new RectF(10, 10, 20, 20), Rgba32.Blue);

        Assert.Equal(Rgba32.Blue, image.GetPixel(10, 10));
        Assert.Equal(Rgba32.Blue, image.GetPixel(29, 29));
        Assert.Equal(Rgba32.White, image.GetPixel(9, 9));
        Assert.Equal(Rgba32.White, image.GetPixel(30, 30));
    }

    [Fact]
    public void SemiTransparent_Fill_Blends_With_Background()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        image.FillRectangle(new RectF(0, 0, 20, 20), new Rgba32(0, 0, 0, 128));

        Rgba32 pixel = image.GetPixel(10, 10);

        Assert.InRange(pixel.R, 126, 129);
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void DrawPoint_Fills_A_Circle()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.DrawPoint(new PointF(20, 20), Rgba32.Black, radius: 8);

        Assert.Equal(Rgba32.Black, image.GetPixel(20, 20));
        Assert.Equal(Rgba32.White, image.GetPixel(20, 2));
    }

    [Fact]
    public void FillPolygon_Fills_Interior()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        PointF[] triangle =
        [
            new PointF(20, 4),
            new PointF(36, 36),
            new PointF(4, 36),
        ];

        image.FillPolygon(triangle, Rgba32.Green);

        Assert.Equal(Rgba32.Green, image.GetPixel(20, 30));
        Assert.Equal(Rgba32.White, image.GetPixel(2, 2));
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
        // 墨迹宽 ≤ advance 宽，且不会明显超出（plan §9 容差 2px）
        Assert.InRange(maxX - minX + 1, 1, (int)MathF.Ceiling(measured.Width) + 2);
    }

    [Fact]
    public void Resize_Pad_Works_On_Real_Canvas()
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
    public void CopyPixels_Bgra_Swaps_Channels()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(2, 2, new Rgba32(0x20, 0x40, 0x80));

        byte[] bgra = image.CopyPixels(PixelFormat.Bgra32);

        Assert.Equal(new byte[] { 0x80, 0x40, 0x20, 0xFF }, bgra[..4]);
    }

    [Fact]
    public void Png_RoundTrip_Preserves_Pixels_And_Metadata()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, Rgba32.White);
        image.FillRectangle(new RectF(4, 4, 8, 8), new Rgba32(0, 128, 255));

        byte[] png = image.Encode(new SaveOptions { Format = ImageFormat.Png });

        Assert.True(png.Length > 8);
        Assert.Equal(0x89, png[0]);

        using FacadeImage loaded = engine.Load(png);

        Assert.Equal(16, loaded.Width);
        Assert.Equal(16, loaded.Height);
        Assert.Equal(ImageFormat.Png, loaded.SourceFormat);
        Assert.Equal(new Rgba32(0, 128, 255), loaded.GetPixel(8, 8));
        Assert.Equal(Rgba32.White, loaded.GetPixel(0, 0));
    }

    [Fact]
    public void Jpeg_RoundTrip_Is_Visually_Close()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(32, 32, Rgba32.White);
        image.FillRectangle(new RectF(8, 8, 16, 16), new Rgba32(200, 40, 40));

        byte[] jpeg = image.Encode(new SaveOptions { Format = ImageFormat.Jpeg, Quality = 95 });
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);

        using FacadeImage loaded = engine.Load(jpeg);

        Assert.Equal(ImageFormat.Jpeg, loaded.SourceFormat);
        Rgba32 center = loaded.GetPixel(16, 16);
        Assert.InRange(Math.Abs(center.R - 200), 0, 12);
        Assert.InRange(Math.Abs(center.G - 40), 0, 12);
        Assert.InRange(Math.Abs(center.B - 40), 0, 12);
        Assert.Equal(255, center.A);
    }

    [Fact]
    public void Save_Infers_Format_From_Extension()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, Rgba32.Red);

        string path = Path.Combine(Path.GetTempPath(), $"imagefacade-{Guid.NewGuid():N}.png");
        try
        {
            image.Save(path);

            using FacadeImage loaded = engine.Load(path);
            Assert.Equal(8, loaded.Width);
            Assert.Equal(Rgba32.Red, loaded.GetPixel(4, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AcquirePixels_Writes_Back_To_Canvas()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        using (PixelLease lease = image.AcquirePixels())
        {
            lease.Pixels[0] = 10;
            lease.Pixels[1] = 20;
            lease.Pixels[2] = 30;
            lease.Pixels[3] = 255;
        }

        Assert.Equal(new Rgba32(10, 20, 30), image.GetPixel(0, 0));
    }
}
