using ImageFacade.Backends;
using Xunit;

namespace ImageFacade.Tests;

public sealed class RouterTests
{
    [Fact]
    public void Native_Success_Does_Not_Fall_Back()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native");
        var software = new FakeBackend("software");

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(40, 40, Rgba32.White);
        image.DrawLine(new PointF(0, 0), new PointF(39, 39), Rgba32.Black);

        Assert.Empty(events);
        Assert.Equal(1, native.DrawLineCalls);
        Assert.Equal(0, software.DrawLineCalls);
        Assert.Equal(0, image.FallbackCount);
    }

    [Fact]
    public void Text_Falls_Back_PerOperation_And_Keeps_Native_Backend()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Fallback = FallbackMode.PerOperation,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(80, 40, Rgba32.White);
        image.DrawText("hi", new PointF(2, 2), new TextStyle { Size = 10 });

        Assert.Single(events);
        Assert.Equal("native", events[0].FromBackend);
        Assert.Equal("software", events[0].ToBackend);
        Assert.Equal("DrawText", events[0].Feature);
        Assert.Equal("native", image.BackendName);
        Assert.Equal(1, image.FallbackCount);
        Assert.Equal(1, software.DrawTextCalls);

        // 文字确实落在原后端的帧上（白底上出现墨迹）
        Assert.Equal(Rgba32.Black, image.GetPixel(3, 3));
    }

    [Fact]
    public void Adaptive_Switches_After_Threshold()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Fallback = FallbackMode.Adaptive,
            AdaptiveThreshold = 3,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(80, 40, Rgba32.White);
        var style = new TextStyle { Size = 10 };

        image.DrawText("a", new PointF(0, 0), style);
        Assert.Equal("native", image.BackendName);

        image.DrawText("b", new PointF(0, 12), style);
        Assert.Equal("native", image.BackendName);

        image.DrawText("c", new PointF(0, 24), style);
        Assert.Equal("software", image.BackendName);

        Assert.Equal(3, events.Count);
        Assert.Equal(3, image.FallbackCount);

        // 切换之后再画文字已原生，不再产生回退
        image.DrawText("d", new PointF(0, 36), style);
        Assert.Equal(3, events.Count);
        Assert.Equal(4, software.DrawTextCalls);
    }

    [Fact]
    public void Adaptive_Count_Resets_Per_Image()
    {
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Fallback = FallbackMode.Adaptive,
            AdaptiveThreshold = 3,
        });

        var style = new TextStyle { Size = 10 };

        using (FacadeImage first = engine.Create(40, 40, Rgba32.White))
        {
            first.DrawText("a", PointF.Empty, style);
            first.DrawText("b", PointF.Empty, style);
            Assert.Equal("native", first.BackendName);
        }

        using FacadeImage second = engine.Create(40, 40, Rgba32.White);
        Assert.Equal(0, second.FallbackCount);
        second.DrawText("a", PointF.Empty, style);
        Assert.Equal("native", second.BackendName);
    }

    [Fact]
    public void SwitchAndStay_Migrates_On_First_Fallback()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Fallback = FallbackMode.SwitchAndStay,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(60, 40, Rgba32.White);
        var style = new TextStyle { Size = 10 };

        image.DrawText("a", PointF.Empty, style);
        Assert.Equal("software", image.BackendName);
        Assert.Single(events);

        image.DrawText("b", new PointF(0, 12), style);
        Assert.Single(events);
        Assert.Equal("software", image.BackendName);
        Assert.True(image.IsSoftwareSurface);
    }

    [Fact]
    public void Disabled_Throws_FeatureNotSupported()
    {
        var native = new FakeBackend("native") { SupportsText = false };

        using var engine = new ImageEngine(native, new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => image.DrawText("x", PointF.Empty, new TextStyle { Size = 10 }));

        Assert.Equal("DrawText", ex.Feature);
        Assert.Equal("native", ex.BackendName);
        Assert.Equal(FallbackMode.Disabled, ex.Fallback);
    }

    [Fact]
    public void Strict_Throws_Even_When_Fallback_Is_Configured()
    {
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Strict = true,
        });

        using FacadeImage image = engine.Create(20, 20, Rgba32.White);
        Assert.Throws<FeatureNotSupportedException>(() => image.DrawText("x", PointF.Empty, new TextStyle { Size = 10 }));
        Assert.Equal(0, software.DrawTextCalls);
    }

    [Fact]
    public void No_Registered_Software_Backend_Throws_With_Hint()
    {
        SoftwareFallback.Unregister();

        var native = new FakeBackend("native") { SupportsText = false };
        using var engine = new ImageEngine(native);
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => image.DrawText("x", PointF.Empty, new TextStyle { Size = 10 }));

        Assert.Contains("ImageFacade.Software", ex.Message);
    }

    [Fact]
    public void PreferSoftwareRendering_Routes_Drawing_To_Fallback()
    {
        var native = new FakeBackend("native");
        var software = new FakeBackend("software");

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Fallback = FallbackMode.PerOperation,
            PreferSoftwareRendering = true,
        });

        using FacadeImage image = engine.Create(30, 30, Rgba32.White);
        image.DrawLine(new PointF(0, 0), new PointF(29, 29), Rgba32.Black);

        Assert.Equal(0, native.DrawLineCalls);
        Assert.Equal(1, software.DrawLineCalls);
        Assert.Equal(Rgba32.Black, image.GetPixel(0, 0));
    }

    [Fact]
    public void Alpha_Unsupported_Falls_Back()
    {
        var native = new FakeBackend("native") { SupportsAlphaBlending = false };
        var software = new FakeBackend("software");

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Fallback = FallbackMode.PerOperation,
        });

        using FacadeImage image = engine.Create(20, 20, Rgba32.White);
        image.DrawLine(new PointF(0, 0), new PointF(19, 19), new Rgba32(255, 0, 0, 128));

        Assert.Equal(0, native.DrawLineCalls);
        Assert.Equal(1, software.DrawLineCalls);
    }

    [Fact]
    public void UseSoftware_And_UseNative_Migrate_Pixels()
    {
        var native = new FakeBackend("native");
        var software = new FakeBackend("software");

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
        });

        using FacadeImage image = engine.Create(10, 10, Rgba32.White);
        image.UseSoftware();

        Assert.Equal("software", image.BackendName);
        Assert.Equal(Rgba32.White, image.GetPixel(5, 5));

        image.UseNative();
        Assert.Equal("native", image.BackendName);
        Assert.Equal(Rgba32.White, image.GetPixel(5, 5));
    }
}
