using ImageFacade.Backends;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>批量绘制作用域（Mutate / BeginDraw）把多次回退合并成一次整帧往返。</summary>
public sealed class BatchTests
{
    private static TextStyle SmallStyle => new() { Size = 10 };

    [Fact]
    public void Mutate_Coalesces_Fallbacks_Into_One_RoundTrip()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(80, 40, Rgba32.White);
        TextStyle style = SmallStyle;

        image.Mutate(x =>
        {
            for (int i = 0; i < 5; i++)
            {
                x.DrawText("ab", new PointF(2, 2 + (i * 8)), style);
            }
        });

        Assert.Single(events);
        Assert.Equal(1, image.FallbackCount);
        Assert.Equal(5, software.DrawTextCalls);
        Assert.Equal(1, software.CreateCalls);    // 只建一个软帧
        Assert.Equal(1, software.ImportCalls);    // 只导出一次
        Assert.Equal(1, native.ImportCalls);      // 只写回一次
        Assert.Equal("native", image.BackendName);
        Assert.Equal(Rgba32.Black, image.GetPixel(3, 3));
    }

    [Fact]
    public void BeginDraw_Flushes_On_Dispose()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        using (DrawBatch batch = image.BeginDraw())
        {
            batch.Image.DrawText("a", new PointF(2, 2), SmallStyle);
            batch.Image.DrawText("b", new PointF(2, 14), SmallStyle);
        }

        Assert.Single(events);
        Assert.Equal(1, image.FallbackCount);
        Assert.Equal(2, software.DrawTextCalls);
        Assert.Equal(Rgba32.Black, image.GetPixel(3, 3));
    }

    [Fact]
    public void PreferSoftwareRendering_Is_Also_Coalesced()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native");
        var software = new FakeBackend("software");

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            PreferSoftwareRendering = true,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.Mutate(x =>
        {
            for (int i = 0; i < 6; i++)
            {
                x.DrawLine(new PointF(0, i), new PointF(39, i), Rgba32.Black);
            }
        });

        Assert.Single(events);
        Assert.Equal(1, image.FallbackCount);
        Assert.Equal(6, software.DrawLineCalls);
        Assert.Equal(1, software.CreateCalls);
        Assert.Equal(0, native.DrawLineCalls);
    }

    [Fact]
    public void FlushOnNative_Returns_To_Native_For_Supported_Operations()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        using (DrawBatch batch = image.BeginDraw(flushOnNative: true))
        {
            batch.Image.DrawText("a", new PointF(2, 2), SmallStyle);
            batch.Image.DrawLine(new PointF(0, 0), new PointF(20, 20), Rgba32.Red);
        }

        Assert.Single(events);
        Assert.Equal(1, software.DrawTextCalls);
        Assert.Equal(0, software.DrawLineCalls);
        Assert.Equal(1, native.DrawLineCalls);
        Assert.Equal("native", image.BackendName);
        Assert.Equal(Rgba32.Black, image.GetPixel(3, 8));    // 文字留在原位，没被线条覆盖
    }

    [Fact]
    public void Sticky_Mode_Keeps_Native_Operations_On_Soft_Frame()
    {
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions { FallbackBackend = software });
        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        image.Mutate(x =>
        {
            x.DrawText("a", new PointF(2, 2), SmallStyle);
            x.DrawLine(new PointF(0, 30), new PointF(39, 30), Rgba32.Red);
        });

        // 回退之后的操作全部留在软帧上（顺序安全），原生不再参与
        Assert.Equal(1, software.DrawTextCalls);
        Assert.Equal(1, software.DrawLineCalls);
        Assert.Equal(0, native.DrawLineCalls);
        Assert.Equal(Rgba32.Red, image.GetPixel(5, 30));
    }

    [Fact]
    public void SwitchAndStay_Takes_Over_Soft_Frame_Without_Copying_Back()
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

        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        image.Mutate(x =>
        {
            x.DrawText("a", new PointF(2, 2), SmallStyle);
            x.DrawText("b", new PointF(2, 14), SmallStyle);
        });

        Assert.Single(events);
        Assert.Equal("software", image.BackendName);
        Assert.True(image.IsSoftwareSurface);
        Assert.Equal(1, image.FallbackCount);
        Assert.Equal(1, software.CreateCalls);
        Assert.Equal(0, native.ImportCalls);   // 没有写回原后端
    }

    [Fact]
    public void Exception_In_Mutate_Discards_Soft_Frame()
    {
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions { FallbackBackend = software });
        using FacadeImage image = engine.Create(40, 20, Rgba32.White);

        Assert.Throws<InvalidOperationException>(() => image.Mutate(x =>
        {
            x.DrawText("a", new PointF(2, 2), SmallStyle);
            throw new InvalidOperationException("boom");
        }));

        // 半成品不写回，计数也不增加
        Assert.Equal(0, image.FallbackCount);
        Assert.Equal(Rgba32.White, image.GetPixel(3, 3));
        Assert.Equal("native", image.BackendName);
    }

    [Fact]
    public void Strict_Still_Throws_Inside_Mutate()
    {
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Strict = true,
        });

        using FacadeImage image = engine.Create(40, 20, Rgba32.White);

        Assert.Throws<FeatureNotSupportedException>(
            () => image.Mutate(x => x.DrawText("a", new PointF(2, 2), SmallStyle)));

        Assert.Equal(0, software.CreateCalls);
        Assert.Equal(0, image.FallbackCount);
    }

    [Fact]
    public void Nested_Mutate_Flushes_Once()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        image.Mutate(outer =>
        {
            outer.DrawText("a", new PointF(2, 2), SmallStyle);
            outer.Mutate(inner => inner.DrawText("b", new PointF(2, 14), SmallStyle));
        });

        Assert.Single(events);
        Assert.Equal(1, image.FallbackCount);
        Assert.Equal(2, software.DrawTextCalls);
        Assert.Equal(1, native.ImportCalls);
    }

    [Fact]
    public void Resize_Inside_Batch_Flushes_First()
    {
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions { FallbackBackend = software });
        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        using (DrawBatch batch = image.BeginDraw())
        {
            batch.Image.DrawText("a", new PointF(2, 2), SmallStyle);
            batch.Image.Resize(40, 20);
        }

        Assert.Equal(40, image.Width);
        Assert.Equal(20, image.Height);
        Assert.Equal("native", image.BackendName);
    }

    [Fact]
    public void FlushOnNative_Is_Ignored_When_Preferring_Software()
    {
        var events = new List<FallbackEvent>();
        var native = new FakeBackend("native");
        var software = new FakeBackend("software");

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            PreferSoftwareRendering = true,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        using (DrawBatch batch = image.BeginDraw(flushOnNative: true))
        {
            for (int i = 0; i < 5; i++)
            {
                batch.Image.DrawLine(new PointF(0, i), new PointF(39, i), Rgba32.Black);
            }
        }

        // PreferSoftwareRendering 下没有"可回切的原生"，flushOnNative 不应造成反复往返
        Assert.Single(events);
        Assert.Equal(1, software.CreateCalls);
        Assert.Equal(1, image.FallbackCount);
    }

    [Fact]
    public void Real_Backends_Coalesce_Software_Rendering()
    {
        var events = new List<FallbackEvent>();
        using var software = new ImageFacade.SdcbSimdCodecs.SoftwareBackend();
        using var engine = new ImageEngine(new ImageFacade.SkiaSharp.SkiaBackend(), new ImageEngineOptions
        {
            FallbackBackend = software,
            PreferSoftwareRendering = true,
            OnFallback = events.Add,
        });

        using FacadeImage image = engine.Create(64, 48, Rgba32.White);

        image.Mutate(x =>
        {
            for (int i = 0; i < 5; i++)
            {
                x.DrawLine(new PointF(0, 8 + (i * 8)), new PointF(63, 8 + (i * 8)), Rgba32.Black, 4f);
            }
        });

        Assert.Single(events);
        Assert.Equal("skia", image.BackendName);
        Assert.True(image.GetPixel(10, 8).R < 40);      // 线内部实心
        Assert.Equal(Rgba32.White, image.GetPixel(10, 4));   // 线之间留白
        Assert.Equal(Rgba32.White, image.GetPixel(10, 12));
    }

    [Fact]
    public void Batch_Flush_Exposes_Pixels_Before_Scope_Ends()
    {
        var native = new FakeBackend("native") { SupportsText = false };
        var software = new FakeBackend("software") { SupportsText = true };

        using var engine = new ImageEngine(native, new ImageEngineOptions { FallbackBackend = software });
        using FacadeImage image = engine.Create(40, 20, Rgba32.White);

        using DrawBatch batch = image.BeginDraw();
        batch.Image.DrawText("a", new PointF(2, 2), SmallStyle);
        batch.Flush();

        Assert.Equal(Rgba32.Black, image.GetPixel(3, 3));
        Assert.Equal(1, image.FallbackCount);
    }
}
