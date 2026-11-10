using ImageFacade.Backends;
using ImageFacade.OpenCvSharp;
using Xunit;
using Cv2 = OpenCvSharp.Cv2;
using CvMat = OpenCvSharp.Mat;
using CvMatType = global::OpenCvSharp.MatType;
using CvRect = OpenCvSharp.Rect;
using CvScalar = OpenCvSharp.Scalar;

namespace ImageFacade.Tests.OpenCv;

/// <summary>
/// OpenCV 后端（M6）的端到端行为：原生能画的（不透明形状/几何/编解码/文字）必须真机跑通，
/// 原生画不了的（半透明形状、虚线）抛 <see cref="FeatureNotSupportedException"/>。
/// 文字由核心的托管字形光栅化提供，4/5 一致。
/// 同一份源码被 <c>ImageFacade.OpenCv4.Tests</c>（OpenCvSharp4 4.13）与
/// <c>ImageFacade.OpenCv5.Tests</c>（OpenCvSharp5 5.0）各编一遍。
/// </summary>
public sealed class OpenCvBackendTests
{
    private static ImageEngine CreateEngine() => new(new OpenCvBackend());

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

    [Fact]
    public void Create_And_GetPixel_RoundTrips_Bgra()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(8, 8, new Rgba32(0x11, 0x22, 0x33));

        Assert.Equal("opencv", image.BackendName);
        Assert.Equal(new Rgba32(0x11, 0x22, 0x33), image.GetPixel(0, 0));
        Assert.Equal(new Rgba32(0x11, 0x22, 0x33), image.GetPixel(7, 7));
    }

    [Fact]
    public void Create_With_Transparent_Background_Keeps_Alpha_Zero()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(4, 4, null);

        Assert.Equal(0, image.GetPixel(2, 2).A);
    }

    [Fact]
    public void FillRectangle_Opaque_Keeps_Channel_Order()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        image.FillRectangle(new RectF(4, 4, 10, 10), new Rgba32(0x40, 0x80, 0xC0));

        Assert.Equal(new Rgba32(0x40, 0x80, 0xC0), image.GetPixel(8, 8));
        Assert.Equal(Rgba32.White, image.GetPixel(1, 1));
    }

    [Fact]
    public void SemiTransparent_Drawing_Is_Rejected()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        var error = Assert.Throws<FeatureNotSupportedException>(
            () => image.FillRectangle(new RectF(0, 0, 20, 20), new Rgba32(0, 0, 0, 128)));

        Assert.Contains("opencv", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dash_Is_Rejected()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(60, 20, Rgba32.White);

        Assert.Throws<FeatureNotSupportedException>(
            () => image.DrawLine(
                new PointF(0, 10),
                new PointF(60, 10),
                Rgba32.Black,
                width: 3,
                new StrokeStyle { DashPattern = [4f, 4f] }));
    }

    [Fact]
    public void DrawLine_Is_Solid_In_Interior()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(100, 50, Rgba32.White);

        image.DrawLine(new PointF(10, 20.5f), new PointF(90, 20.5f), Rgba32.Red, width: 4);

        Assert.Equal(Rgba32.Red, image.GetPixel(50, 20));
        Assert.Equal(Rgba32.White, image.GetPixel(50, 5));
    }

    [Fact]
    public void FillEllipse_And_Polygon()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(120, 80, Rgba32.White);

        image.FillEllipse(new RectF(10, 10, 40, 40), Rgba32.Green);
        image.FillPolygon([new PointF(70, 10), new PointF(110, 10), new PointF(90, 50)], Rgba32.Blue);

        Assert.Equal(Rgba32.Green, image.GetPixel(30, 30));
        Assert.Equal(Rgba32.Blue, image.GetPixel(90, 25));
        Assert.Equal(Rgba32.White, image.GetPixel(2, 2));
    }

    [Theory]
    [InlineData("中文Ab")]
    [InlineData("Hello")]
    [InlineData("标 签: 42")]
    public void Text_Renders_Unicode_And_Ascii(string text)
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(240, 80, Rgba32.White);

        var style = new TextStyle { Size = 32, Color = Rgba32.Black };

        Assert.True(engine.Backend.Capabilities.HasFlag(BackendCapabilities.DrawTextUnicode));

        SizeF measured = image.MeasureText(text, style);
        image.DrawText(text, new PointF(8, 8), style);

        Assert.True(measured.Width > 0f);
        Assert.True(measured.Height > 0f);
        Assert.True(CountInk(image) > 50, "核心字形光栅化应画出墨迹");
    }

    [Fact]
    public void DrawText_Reuses_Provided_Font_Instance()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(200, 80, Rgba32.White);

        // 传字体实例：不再走 FontRef 解析，也就不会重新解析字体表
        Font builtIn = Font.BuiltIn;
        var style = new TextStyle { Size = 32, Color = Rgba32.Black, FontFace = builtIn };

        Assert.Same(Font.BuiltIn, builtIn);
        SizeF measured = image.MeasureText("中文Ab", style);
        image.DrawText("中文Ab", new PointF(8, 8), style);
        image.DrawText("中文Ab", new PointF(8, 40), style);

        Assert.True(measured.Width > 0f);
        Assert.True(CountInk(image) > 100);
    }

    [Fact]
    public void DrawText_Honors_SemiTransparent_Color()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(200, 80, Rgba32.White);

        image.DrawText("A", new PointF(8, 8), new TextStyle { Size = 48, Color = new Rgba32(0, 0, 0, 128) });

        byte[] pixels = image.CopyPixels();
        int darkest = 255;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            darkest = Math.Min(darkest, pixels[i]);
        }

        Assert.InRange(darkest, 100, 170);
    }

    [Fact]
    public void Resize_Pad_Works()
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
    public void Png_RoundTrip_Preserves_Pixels_And_Alpha()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, null);
        image.FillRectangle(new RectF(4, 4, 8, 8), new Rgba32(0, 128, 255));

        byte[] png = image.Encode(new SaveOptions { Format = ImageFormat.Png });
        using FacadeImage loaded = engine.Load(png);

        Assert.Equal(ImageFormat.Png, loaded.SourceFormat);
        Assert.Equal(new Rgba32(0, 128, 255), loaded.GetPixel(8, 8));
        Assert.Equal(0, loaded.GetPixel(0, 0).A);
    }

    [Fact]
    public void Load_SixteenBit_Png_Is_Normalized_To_EightBit()
    {
        using var engine = CreateEngine();

        // 16 位 BGRA：不按 1/257 缩放会饱和成一片白，且每像素 8 字节会让 width*4 全部算错
        using var deep = new CvMat(4, 4, CvMatType.CV_16UC4, new CvScalar(32768, 16384, 65535, 65535));
        Assert.Equal(2L, deep.ElemSize1());

        Cv2.ImEncode(".png", deep, out byte[] png);
        using FacadeImage loaded = engine.Load(png);

        Rgba32 pixel = loaded.GetPixel(2, 2);
        Assert.InRange(pixel.R, (byte)253, (byte)255);   // 65535 / 257 = 255
        Assert.InRange(pixel.G, (byte)62, (byte)65);     // 16384 / 257 ≈ 63.7
        Assert.InRange(pixel.B, (byte)126, (byte)129);   // 32768 / 257 ≈ 127.5
        Assert.Equal((byte)255, pixel.A);
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

    // ---- Mat 互操作（SharedMat / SubMat / CloneMat） ----

    [Fact]
    public void SharedMat_Writes_Through_To_The_Image()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, Rgba32.White);

        CvMat shared = image.SharedMat();
        Assert.Equal(16, shared.Cols);
        Assert.Equal(16, shared.Rows);

        // Scalar 的 v0..v2 依次是 B/G/R，v3 是 alpha（这里要显式给 255，否则画出来是透明的）
        Cv2.Rectangle(shared, new CvRect(4, 4, 8, 8), new CvScalar(0, 0, 255, 255), thickness: -1);

        Assert.Equal(new Rgba32(255, 0, 0, 255), image.GetPixel(8, 8));
        Assert.Equal(Rgba32.White, image.GetPixel(0, 0));
    }

    [Fact]
    public void SubMat_Shares_Pixels_And_Only_Touches_The_Roi()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, Rgba32.White);

        using (CvMat roi = image.SubMat(4, 4, 6, 6))
        {
            Assert.Equal(6, roi.Cols);
            Assert.Equal(6, roi.Rows);
            roi.SetTo(new CvScalar(0, 255, 0, 255));
        }

        Assert.Equal(new Rgba32(0, 255, 0, 255), image.GetPixel(4, 4));
        Assert.Equal(new Rgba32(0, 255, 0, 255), image.GetPixel(9, 9));
        Assert.Equal(Rgba32.White, image.GetPixel(10, 10));
    }

    [Theory]
    [InlineData(-1, 0, 4, 4)]
    [InlineData(0, -1, 4, 4)]
    [InlineData(0, 0, 0, 4)]
    [InlineData(0, 0, 4, 0)]
    [InlineData(12, 0, 8, 4)]
    [InlineData(0, 12, 4, 8)]
    public void SubMat_Out_Of_Bounds_Throws(int x, int y, int width, int height)
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, Rgba32.White);

        Assert.Throws<ArgumentOutOfRangeException>(() => image.SubMat(x, y, width, height));
    }

    [Fact]
    public void CloneMat_Is_Independent_Of_The_Image()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(16, 16, Rgba32.White);

        using CvMat copy = image.CloneMat();
        Assert.Equal(16, copy.Cols);

        copy.SetTo(new CvScalar(0, 0, 255, 255));

        Assert.Equal(Rgba32.White, image.GetPixel(8, 8));
        Assert.True(copy.Data != IntPtr.Zero);
    }

    [Fact]
    public void SharedMat_After_Dispose_Throws()
    {
        using var engine = CreateEngine();
        FacadeImage image = engine.Create(4, 4, Rgba32.White);
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => image.SharedMat());
    }

}
