using ImageFacade.Backends;
using ImageFacade.ImageSharp;
using Xunit;

namespace ImageFacade.Tests.ImageSharp;

/// <summary>
/// ImageSharp 后端（M5）的端到端行为 + 与软后端的一致性。
/// 同一份源码被 <c>ImageFacade.ImageSharp3.Tests</c>（ImageSharp 3.1.12 + Drawing 2.1.3）与
/// <c>ImageFacade.ImageSharp2.Tests</c>（ImageSharp 2.1.13 + Drawing 1.0.0）各编一遍。
/// </summary>
public sealed class ImageSharpBackendTests
{
    private static ImageEngine CreateEngine()
        => new(new ImageSharpBackend());

    /// <summary>
    /// 折行用的切片版测量必须与 string 版一致（<c>TextMeasurer.MeasureAdvance(ReadOnlySpan&lt;char&gt;, …)</c>
    /// 与 string 重载同一条整形路径），否则省下的分配会换来不同的折行位置。
    /// </summary>
    [Fact]
    public void MeasureText_Slice_Overload_Matches_String_Overload()
    {
        using var backend = new ImageSharpBackend();
        using IFrame frame = backend.Create(8, 8, null);

        var style = new TextStyle { Font = FontRef.BuiltIn, Size = 24 };
        string[] samples = ["Wg文字", "中文 Ab", "a", "W", "A😀b", "abcdefghij"];

        foreach (string text in samples)
        {
            var spec = new TextSpec(text, PointF.Empty, style);
            Assert.True(backend.TryMeasureText(frame, in spec, out TextMetrics fromString));
            Assert.True(backend.TryMeasureText(frame, text.AsSpan(), in style, out TextMetrics fromSlice));

            Assert.Equal(fromString.Width, fromSlice.Width);
            Assert.Equal(fromString.Ascent, fromSlice.Ascent);
            Assert.Equal(fromString.Descent, fromSlice.Descent);
            Assert.Equal(fromString.LineGap, fromSlice.LineGap);
        }
    }

    [Fact]
    public void Load_Reports_Transparency()
    {
        using var engine = CreateEngine();

        using (FacadeImage transparent = engine.Create(8, 8, null))
        {
            byte[] png = transparent.Encode(new SaveOptions { Format = ImageFormat.Png });
            using FacadeImage loaded = engine.Load(png);

            Assert.True(loaded.Metadata.HasTransparency);
        }

        using (FacadeImage opaque = engine.Create(8, 8, Rgba32.White))
        {
            byte[] png = opaque.Encode(new SaveOptions { Format = ImageFormat.Png });
            using FacadeImage loaded = engine.Load(png);

            Assert.False(loaded.Metadata.HasTransparency);
        }

        using (FacadeImage opaque = engine.Create(8, 8, Rgba32.White))
        {
            byte[] jpeg = opaque.Encode(new SaveOptions { Format = ImageFormat.Jpeg });
            using FacadeImage loaded = engine.Load(jpeg);

            Assert.False(loaded.Metadata.HasAlpha);
            Assert.False(loaded.Metadata.HasTransparency);
        }
    }

    private static int CountInk(FacadeImage image)
    {
        byte[] pixels = image.CopyPixels();
        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 128 && pixels[i + 1] < 128 && pixels[i + 2] < 128)
            {
                ink++;
            }
        }

        return ink;
    }

    private static (int Left, int Top, int Right, int Bottom) InkBounds(FacadeImage image)
    {
        byte[] pixels = image.CopyPixels();
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = -1;
        int bottom = -1;

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int offset = ((y * image.Width) + x) * 4;
                if (pixels[offset] == 255 && pixels[offset + 1] == 255 && pixels[offset + 2] == 255)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return (left, top, right, bottom);
    }

    [Fact]
    public void Create_And_GetPixel_RoundTrips()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, new Rgba32(0x20, 0x60, 0xC0));

        Assert.Equal("imagesharp", image.BackendName);
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
        Assert.InRange(pixel.R, 125, 130);
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
    public void Dash_Line_Draws_Fewer_Pixels_Than_Solid()
    {
        using var engine = CreateEngine();
        using FacadeImage solid = engine.Create(120, 20, Rgba32.White);
        using FacadeImage dashed = engine.Create(120, 20, Rgba32.White);

        solid.DrawLine(new PointF(0, 10), new PointF(120, 10), Rgba32.Black, width: 4);
        dashed.DrawLine(
            new PointF(0, 10),
            new PointF(120, 10),
            Rgba32.Black,
            width: 4,
            new StrokeStyle { DashPattern = [4f, 4f] });

        int solidInk = CountInk(solid);
        int dashedInk = CountInk(dashed);

        Assert.True(solidInk > 0);
        Assert.True(dashedInk < solidInk, $"虚线墨迹 {dashedInk} 应少于实线 {solidInk}");
    }

    [Fact]
    public void DrawEllipse_And_Polygon_Fill()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(120, 80, Rgba32.White);

        image.FillEllipse(new RectF(10, 10, 40, 40), Rgba32.Green);
        image.FillPolygon([new PointF(70, 10), new PointF(110, 10), new PointF(90, 50)], Rgba32.Blue);

        Assert.Equal(Rgba32.Green, image.GetPixel(30, 30));
        Assert.Equal(Rgba32.Blue, image.GetPixel(90, 25));
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

        int ink = CountInk(image);
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

        var (left, _, right, _) = InkBounds(image);

        Assert.True(right >= 0);
        Assert.InRange(right - left + 1, 1, (int)MathF.Ceiling(measured.Width) + 2);
    }

    [Fact]
    public void Baseline_Anchor_Sits_Above_TopLeft_Anchor()
    {
        using var engine = CreateEngine();
        using FacadeImage topLeft = engine.Create(200, 100, Rgba32.White);
        using FacadeImage baseline = engine.Create(200, 100, Rgba32.White);

        topLeft.DrawText("Ag", new PointF(10, 50), new TextStyle { Size = 24, Color = Rgba32.Black });
        baseline.DrawText(
            "Ag",
            new PointF(10, 50),
            new TextStyle { Size = 24, Color = Rgba32.Black, Anchor = TextAnchor.Baseline });

        int topLeftTop = InkBounds(topLeft).Top;
        int baselineTop = InkBounds(baseline).Top;

        Assert.True(baselineTop < topLeftTop, $"Baseline 墨迹顶 {baselineTop} 应在 TopLeft 墨迹顶 {topLeftTop} 之上");
        Assert.InRange(topLeftTop - baselineTop, 10, 40);
    }

    [Fact]
    public void MultiLine_Text_Uses_Font_Line_Height()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(200, 160, Rgba32.White);

        var style = new TextStyle { Font = FontRef.BuiltIn, Size = 20, Color = Rgba32.Black };
        SizeF measured = image.MeasureText("第一行\n第二行", style);
        image.DrawText("第一行\n第二行", new PointF(4, 4), style);

        var (_, top, _, bottom) = InkBounds(image);
        Assert.True(measured.Height > style.Size, $"多行高度 {measured.Height} 应大于单倍字号");
        Assert.True(bottom - top > style.Size, "两行文字应占两行高度以上");
    }

    [Fact]
    public void Resize_Pad_Works_On_ImageSharp()
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
    public void Png_RoundTrip_Keeps_Alpha()
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
    public void AcquirePixels_Writes_Back_To_Image()
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
