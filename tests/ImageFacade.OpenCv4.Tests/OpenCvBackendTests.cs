using ImageFacade.Backends;
using ImageFacade.OpenCvSharp;
using ImageFacade.SdcbSimdCodecs;
using Xunit;

namespace ImageFacade.Tests.OpenCv;

/// <summary>
/// OpenCV 后端（M6）的端到端行为：原生能画的（不透明形状/几何/编解码）必须真机跑通，
/// 原生画不了的（半透明、虚线、文字）必须**老老实实返回 false** 并走门面回退。
/// 同一份源码被 <c>ImageFacade.OpenCv4.Tests</c>（OpenCvSharp4 4.13）与
/// <c>ImageFacade.OpenCv5.Tests</c>（OpenCvSharp5 5.0）各编一遍。
/// </summary>
public sealed class OpenCvBackendTests
{
    // 显式注册一次：ModuleInitializer 只在软后端程序集被加载后生效，测试顺序不保证
    static OpenCvBackendTests() => SoftwareBackend.UseAsFallback();

    private static ImageEngine CreateEngine(FallbackMode fallback = FallbackMode.Disabled, Action<FallbackEvent>? onFallback = null)
        => new(
            new OpenCvBackend(),
            new ImageEngineOptions { Fallback = fallback, OnFallback = onFallback });

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

    [Fact]
    public void Text_Support_Follows_OpenCv_Version()
    {
        var events = new List<FallbackEvent>();
        using var engine = CreateEngine(FallbackMode.PerOperation, events.Add);
        using FacadeImage image = engine.Create(200, 80, Rgba32.White);

        var style = new TextStyle { Size = 32, Color = Rgba32.Black };

#if OPENCV5
        Assert.True(engine.Backend.Capabilities.HasFlag(BackendCapabilities.DrawTextUnicode));

        SizeF measured = image.MeasureText("中文Ab", style);
        image.DrawText("中文Ab", new PointF(8, 8), style);

        Assert.True(measured.Width > 0f);
        Assert.True(measured.Height > 0f);
        Assert.True(CountInk(image) > 50, "嵌入字体的托管光栅化应画出中文墨迹");
        Assert.Empty(events);
#else
        // OpenCV 4 没有字体模块（contrib 扩展缺省视为不存在）：能力表里没有文字，一律交给回退
        Assert.False(engine.Backend.Capabilities.HasFlag(BackendCapabilities.DrawTextUnicode));

        image.DrawText("中文Ab", new PointF(8, 8), style);

        Assert.True(CountInk(image) > 50, "回退到软后端后应有文字墨迹");
        Assert.Contains(events, e => e.Feature == "DrawText" && e.FromBackend == "opencv" && e.ToBackend == "software");
#endif
    }

#if OPENCV5
    [Fact]
    public void DrawText_Reuses_Provided_Font_Instance()
    {
        var events = new List<FallbackEvent>();
        using var engine = CreateEngine(FallbackMode.PerOperation, events.Add);
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
        Assert.Empty(events);
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
#endif

    [Fact]
    public void Dash_Falls_Back_To_Software_And_Reports_Event()
    {
        var events = new List<FallbackEvent>();
        using var engine = CreateEngine(FallbackMode.PerOperation, events.Add);
        using FacadeImage image = engine.Create(120, 40, Rgba32.White);

        image.DrawLine(
            new PointF(0, 20),
            new PointF(120, 20),
            Rgba32.Black,
            width: 4,
            new StrokeStyle { DashPattern = [4f, 4f] });

        Assert.True(CountInk(image) > 0, "回退到软后端后应画出虚线");
        Assert.Contains(events, e => e.Feature == "DrawLine" && e.FromBackend == "opencv" && e.ToBackend == "software");
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

    // ---- 与软后端的一致性（只比原生能力覆盖的绘制）----

    private static (int Left, int Top, int Right, int Bottom, Rgba32 Rect, Rgba32 Line, Rgba32 Ellipse, SizeF Measure) RenderShapes(IImageBackend backend)
    {
        using var engine = new ImageEngine(backend, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(200, 120, Rgba32.White);

        image
            .FillRectangle(new RectF(20, 20, 60, 40), new Rgba32(0x20, 0x60, 0xC0))
            .DrawLine(new PointF(10, 100), new PointF(190, 100), Rgba32.Red, width: 4)
            .FillEllipse(new RectF(120, 20, 60, 40), Rgba32.Green);

        // 文字两边都走同一套嵌入字体光栅化，测量与墨迹应当一致（仅 OpenCV 5）
        SizeF measure = default;
#if OPENCV5
        var style = new TextStyle { Size = 20, Color = Rgba32.Black };
        measure = image.MeasureText("Ag中文", style);
        image.DrawText("Ag中文", new PointF(20, 60), style);
#endif

        var (left, top, right, bottom) = InkBounds(image);
        return (left, top, right, bottom, image.GetPixel(50, 40), image.GetPixel(100, 100), image.GetPixel(150, 40), measure);
    }

    [Fact]
    public void OpenCv_Agrees_With_Software_Backend_On_Shapes()
    {
        var openCv = RenderShapes(new OpenCvBackend());
        var software = RenderShapes(new SoftwareBackend());

        Assert.Equal(new Rgba32(0x20, 0x60, 0xC0), openCv.Rect);
        Assert.Equal(Rgba32.Red, openCv.Line);
        Assert.Equal(Rgba32.Green, openCv.Ellipse);

        Assert.True(Math.Abs(openCv.Left - software.Left) <= 2, $"左边界 {openCv.Left} vs {software.Left}");
        Assert.True(Math.Abs(openCv.Top - software.Top) <= 2, $"上边界 {openCv.Top} vs {software.Top}");
        Assert.True(Math.Abs(openCv.Right - software.Right) <= 2, $"右边界 {openCv.Right} vs {software.Right}");
        Assert.True(Math.Abs(openCv.Bottom - software.Bottom) <= 2, $"下边界 {openCv.Bottom} vs {software.Bottom}");
#if OPENCV5
        Assert.True(
            Math.Abs(openCv.Measure.Width - software.Measure.Width) <= 2f,
            $"文字宽测量 {openCv.Measure.Width} vs {software.Measure.Width}");
#endif
    }
}
