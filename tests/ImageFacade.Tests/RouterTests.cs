using ImageFacade.Backends;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>路由：后端能做就做，做不了直接抛 <see cref="FeatureNotSupportedException"/>（没有回退）。</summary>
public sealed class RouterTests
{
    private static ImageEngine CreateEngine(FakeBackend backend) => new(backend);

    [Fact]
    public void Native_Success_Does_Not_Throw()
    {
        var backend = new FakeBackend("native");
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.DrawLine(new PointF(0, 0), new PointF(39, 39), Rgba32.Black);

        Assert.Equal(1, backend.DrawLineCalls);
        Assert.Equal("native", image.BackendName);
    }

    [Fact]
    public void Unsupported_Text_Throws_With_Feature_And_Backend()
    {
        var backend = new FakeBackend("native") { SupportsText = false };
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => image.DrawText("hi", new PointF(2, 2), new TextStyle { Size = 10 }));

        Assert.Equal("DrawText", ex.Feature);
        Assert.Equal("native", ex.BackendName);
        Assert.Contains("DrawText", ex.Message);
        Assert.Equal(0, backend.DrawTextCalls);
    }

    [Fact]
    public void Unsupported_Measure_Throws()
    {
        var backend = new FakeBackend("native") { SupportsText = false };
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => image.MeasureText("hi", new TextStyle { Size = 10 }));

        Assert.Equal("MeasureText", ex.Feature);
    }

    [Fact]
    public void Unsupported_Resize_Throws()
    {
        var backend = new FakeBackend("native") { SupportsResize = false };
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(80, 40, Rgba32.White);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(() => image.Resize(40, 20));

        Assert.Equal("Resize", ex.Feature);
        Assert.Equal(80, image.Width);
    }

    [Fact]
    public void Unsupported_Encode_Throws()
    {
        var backend = new FakeBackend("native");
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(8, 8, Rgba32.White);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => image.Encode(new SaveOptions { Format = ImageFormat.Png }));

        Assert.Equal("Encode", ex.Feature);
    }

    [Fact]
    public void SemiTransparent_Shape_Throws_When_AlphaBlending_Unsupported()
    {
        var backend = new FakeBackend("native") { SupportsAlphaBlending = false };
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        var color = new Rgba32(255, 0, 0, 128);
        Assert.Throws<FeatureNotSupportedException>(
            () => image.DrawLine(new PointF(0, 0), new PointF(20, 20), color));
        Assert.Throws<FeatureNotSupportedException>(
            () => image.FillRectangle(new RectF(0, 0, 10, 10), color));
    }

    [Fact]
    public void Opaque_Shape_Still_Works_Without_AlphaBlending()
    {
        var backend = new FakeBackend("native") { SupportsAlphaBlending = false };
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.FillRectangle(new RectF(0, 0, 10, 10), Rgba32.Black);

        Assert.Equal(Rgba32.Black, image.GetPixel(5, 5));
    }

    [Fact]
    public void Empty_Text_Is_NoOp_Even_When_Text_Unsupported()
    {
        var backend = new FakeBackend("native") { SupportsText = false };
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        image.DrawText(string.Empty, PointF.Empty, new TextStyle { Size = 10 });

        Assert.Equal(0, backend.DrawTextCalls);
    }

    [Fact]
    public void Unsupported_Polyline_Throws()
    {
        var backend = new FakeBackend("native");
        using var engine = CreateEngine(backend);
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        Assert.Throws<FeatureNotSupportedException>(
            () => image.DrawPolyline([new PointF(0, 0), new PointF(10, 10)], Rgba32.Black));
    }

    [Fact]
    public void Load_Wraps_Backend_NotSupported_As_Feature_Not_Supported()
    {
        var backend = new FakeBackend("native");
        using var engine = CreateEngine(backend);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => engine.Load([1, 2, 3, 4]));

        Assert.Equal("Decode", ex.Feature);
        Assert.Equal("native", ex.BackendName);
        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    [Fact]
    public void Encode_Wraps_Backend_NotSupported_As_Feature_Not_Supported()
    {
        using var engine = new ImageEngine(new RejectingEncodeBackend());
        using FacadeImage image = engine.Create(8, 8, Rgba32.White);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => image.Encode(new SaveOptions { Format = ImageFormat.Png }));

        Assert.Equal("Encode", ex.Feature);
        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    /// <summary>声明支持 PNG，但运行时拒绝编码（模拟缺编码器）。</summary>
    private sealed class RejectingEncodeBackend : FakeBackend
    {
        private static readonly IReadOnlySet<ImageFormat> Png = new HashSet<ImageFormat> { ImageFormat.Png };

        public RejectingEncodeBackend()
            : base("native")
        {
        }

        public override IReadOnlySet<ImageFormat> SupportedEncodeFormats => Png;

        public override void Encode(IFrame frame, Stream destination, in EncodeRequest request)
            => throw new NotSupportedException("没有可用的 PNG 编码器。");
    }
}
