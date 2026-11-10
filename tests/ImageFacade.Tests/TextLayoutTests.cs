using Xunit;

namespace ImageFacade.Tests;

public sealed class TextLayoutTests
{
    [Fact]
    public void Wraps_At_MaxWidth()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(100, 100, Rgba32.White);

        var style = new TextStyle { Size = 10, MaxWidth = 25 };
        SizeF size = image.MeasureText("abcdefghij", style);

        Assert.Equal(25f, size.Width);
        Assert.Equal(20f, size.Height);   // 2 行 × 行高 10
    }

    [Fact]
    public void Wraps_Chinese_Per_Character()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(100, 100, Rgba32.White);

        var style = new TextStyle { Size = 10, MaxWidth = 25 };
        SizeF size = image.MeasureText("一二三四五六七八九十", style);

        Assert.Equal(25f, size.Width);
        Assert.Equal(20f, size.Height);
    }

    [Fact]
    public void Hard_Newlines_Are_Preserved()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(100, 100, Rgba32.White);

        SizeF size = image.MeasureText("a\nbb", new TextStyle { Size = 10 });

        Assert.Equal(10f, size.Width);
        Assert.Equal(20f, size.Height);
    }

    [Fact]
    public void DrawText_Center_Align_Offsets_By_Half()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.DrawText("ab", PointF.Empty, new TextStyle
        {
            Size = 10,
            MaxWidth = 20,
            Align = TextAlign.Center,
        });

        Assert.Equal(Rgba32.White, image.GetPixel(1, 3));
        Assert.Equal(Rgba32.Black, image.GetPixel(6, 3));
        Assert.Equal(Rgba32.White, image.GetPixel(16, 3));
    }

    [Fact]
    public void DrawText_Baseline_Anchor_Subtracts_Ascent()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.DrawText("ab", new PointF(0, 20), new TextStyle
        {
            Size = 10,
            Anchor = TextAnchor.Baseline,
        });

        // ascent = 0.8 × 字号 = 8 → 墨迹落在 y = 12..22
        Assert.Equal(Rgba32.White, image.GetPixel(0, 11));
        Assert.Equal(Rgba32.Black, image.GetPixel(0, 13));
    }

    [Fact]
    public void MultiLine_Uses_Explicit_LineHeight()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(40, 80, Rgba32.White);

        image.DrawText("a\nb", PointF.Empty, new TextStyle { Size = 10, LineHeight = 20 });

        Assert.Equal(Rgba32.Black, image.GetPixel(0, 3));
        Assert.Equal(Rgba32.White, image.GetPixel(0, 13));
        Assert.Equal(Rgba32.Black, image.GetPixel(0, 23));
    }

    [Fact]
    public void MeasureText_Uses_Fallback_When_Native_Cannot_Measure()
    {
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Fallback = FallbackMode.PerOperation,
        });

        using FacadeImage image = engine.Create(100, 40, Rgba32.White);
        SizeF size = image.MeasureText("abcd", new TextStyle { Size = 10 });

        Assert.Equal(20f, size.Width);
        Assert.Equal(10f, size.Height);
        Assert.Equal("native", image.BackendName);
    }
}
