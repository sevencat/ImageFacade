using ImageFacade.Backends;
using ImageFacade.SkiaSharp;
using ImageFacade.SdcbSimdCodecs;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// M4：同一份绘制脚本在不同后端上的几何 / 颜色 / 文字测量必须一致（plan §9 功能验收）。
/// </summary>
public sealed class ConsistencyTests
{
    private static readonly (string Name, Func<IImageBackend> Factory)[] Backends =
    [
        ("software", static () => new SoftwareBackend()),
        ("skia", static () => new SkiaBackend()),
    ];

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

    private sealed record Snapshot(
        string Name,
        SizeF TextMeasure,
        int Left,
        int Top,
        int Right,
        int Bottom,
        Rgba32 RectCenter,
        Rgba32 LineCenter,
        Rgba32 EllipseCenter);

    private static Snapshot Render(IImageBackend backend)
    {
        using var engine = new ImageEngine(backend, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(200, 120, Rgba32.White);

        image
            .FillRectangle(new RectF(20, 20, 60, 40), new Rgba32(0x20, 0x60, 0xC0))
            .DrawLine(new PointF(10, 100), new PointF(190, 100), Rgba32.Red, width: 4)
            .FillEllipse(new RectF(120, 20, 60, 40), Rgba32.Green);

        var style = new TextStyle { Size = 20, Color = Rgba32.Black };
        SizeF measure = image.MeasureText("Ag中文", style);
        image.DrawText("Ag中文", new PointF(20, 60), style);

        var bounds = InkBounds(image);
        return new Snapshot(
            backend.Name,
            measure,
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom,
            image.GetPixel(50, 40),
            image.GetPixel(100, 100),
            image.GetPixel(150, 40));
    }

    [Fact]
    public void All_Backends_Agree_On_Geometry_And_Color()
    {
        Snapshot[] snapshots = Backends.Select(entry => Render(entry.Factory())).ToArray();

        foreach (Snapshot snapshot in snapshots)
        {
            // 同后端内部：几何元素确实画出来了
            Assert.True(snapshot.Right - snapshot.Left > 150, $"{snapshot.Name}: 墨迹太窄");
            Assert.True(snapshot.Bottom - snapshot.Top > 80, $"{snapshot.Name}: 墨迹太矮");

            // 实心区域颜色必须精确一致（避开 AA 边缘）
            Assert.Equal(new Rgba32(0x20, 0x60, 0xC0), snapshot.RectCenter);
            Assert.Equal(Rgba32.Red, snapshot.LineCenter);
            Assert.Equal(Rgba32.Green, snapshot.EllipseCenter);
        }

        Snapshot reference = snapshots[0];
        foreach (Snapshot snapshot in snapshots.Skip(1))
        {
            Assert.True(
                Math.Abs(snapshot.Left - reference.Left) <= 2,
                $"{snapshot.Name} 左边界偏差 {Math.Abs(snapshot.Left - reference.Left)}");
            Assert.True(
                Math.Abs(snapshot.Top - reference.Top) <= 2,
                $"{snapshot.Name} 上边界偏差 {Math.Abs(snapshot.Top - reference.Top)}");
            Assert.True(
                Math.Abs(snapshot.Right - reference.Right) <= 2,
                $"{snapshot.Name} 右边界偏差 {Math.Abs(snapshot.Right - reference.Right)}");
            Assert.True(
                Math.Abs(snapshot.Bottom - reference.Bottom) <= 2,
                $"{snapshot.Name} 下边界偏差 {Math.Abs(snapshot.Bottom - reference.Bottom)}");

            Assert.True(
                Math.Abs(snapshot.TextMeasure.Width - reference.TextMeasure.Width) <= 1f,
                $"{snapshot.Name} 文字宽测量偏差 {Math.Abs(snapshot.TextMeasure.Width - reference.TextMeasure.Width)}");
            Assert.True(
                Math.Abs(snapshot.TextMeasure.Height - reference.TextMeasure.Height) <= 1f,
                $"{snapshot.Name} 文字高测量偏差 {Math.Abs(snapshot.TextMeasure.Height - reference.TextMeasure.Height)}");
        }
    }

    [Fact]
    public void All_Backends_Agree_On_Resize_Geometry()
    {
        foreach ((string name, Func<IImageBackend> factory) in Backends)
        {
            using var engine = new ImageEngine(factory(), new ImageEngineOptions { Fallback = FallbackMode.Disabled });
            using FacadeImage image = engine.Create(100, 50, Rgba32.White);

            image.Resize(new ResizeOptions { Width = 60, Height = 60, Mode = ResizeMode.Pad, PadColor = Rgba32.Red });

            Assert.Equal(60, image.Width);
            Assert.Equal(60, image.Height);
            Assert.Equal(Rgba32.Red, image.GetPixel(0, 0));
            Assert.Equal(Rgba32.White, image.GetPixel(30, 30));
        }
    }

    [Fact]
    public void All_Backends_Keep_Png_Alpha()
    {
        foreach ((string name, Func<IImageBackend> factory) in Backends)
        {
            using var engine = new ImageEngine(factory(), new ImageEngineOptions { Fallback = FallbackMode.Disabled });
            using FacadeImage image = engine.Create(8, 8, null);
            image.FillRectangle(new RectF(0, 0, 4, 8), Rgba32.Red);

            byte[] png = image.Encode(new SaveOptions { Format = ImageFormat.Png });
            using FacadeImage loaded = engine.Load(png);

            Assert.Equal(0, loaded.GetPixel(6, 4).A);
            Assert.Equal(255, loaded.GetPixel(1, 4).A);
        }
    }
}
