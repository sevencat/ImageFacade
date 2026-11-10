using ImageFacade.Backends;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// ImageSharp 风格的 <c>Mutate</c> / <c>Clone(Action)</c> 语法糖：门面层纯委托，与后端无关。
/// </summary>
public sealed class MutateTests
{
    [Fact]
    public void Mutate_Runs_On_Same_Instance()
    {
        var backend = new FakeBackend("native");
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        FacadeImage returned = image.Mutate(static x => x.DrawLine(PointF.Empty, new PointF(10, 10), Rgba32.Black));

        Assert.Same(image, returned);
        Assert.Equal(1, backend.DrawLineCalls);
        Assert.Equal(Rgba32.Black, image.GetPixel(0, 0));
    }

    [Fact]
    public void Mutate_Can_Be_Chained()
    {
        var backend = new FakeBackend("native");
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        image
            .Mutate(static x => x.DrawLine(PointF.Empty, new PointF(5, 5), Rgba32.Black))
            .Mutate(static x => x.DrawLine(PointF.Empty, new PointF(10, 10), Rgba32.Red));

        Assert.Equal(2, backend.DrawLineCalls);
    }

    [Fact]
    public void Mutate_Propagates_Unsupported_Feature()
    {
        var backend = new FakeBackend("native") { SupportsText = false };
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        FeatureNotSupportedException ex = Assert.Throws<FeatureNotSupportedException>(
            () => image.Mutate(x => x.DrawText("a", PointF.Empty, new TextStyle { Size = 10 })));

        Assert.Equal("DrawText", ex.Feature);
    }

    [Fact]
    public void Clone_With_Action_Keeps_Source_Untouched()
    {
        var backend = new FakeBackend("native");
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        using FacadeImage copy = image.Clone(static x => x.DrawLine(PointF.Empty, new PointF(10, 10), Rgba32.Black));

        Assert.NotSame(image, copy);
        Assert.Equal(Rgba32.White, image.GetPixel(0, 0));   // 原图没被改
        Assert.Equal(Rgba32.Black, copy.GetPixel(0, 0));
    }

    [Fact]
    public void Clone_With_Action_Propagates_And_Leaves_Source_Untouched()
    {
        var backend = new FakeBackend("native") { SupportsText = false };
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(20, 20, Rgba32.White);

        Assert.Throws<FeatureNotSupportedException>(
            () => image.Clone(x => x.DrawText("a", PointF.Empty, new TextStyle { Size = 10 })));

        Assert.Equal(Rgba32.White, image.GetPixel(0, 0));
    }

    [Fact]
    public void Mutate_Rejects_Null_Callback()
    {
        var backend = new FakeBackend("native");
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        Assert.Throws<ArgumentNullException>(() => image.Mutate(null!));
        Assert.Throws<ArgumentNullException>(() => image.Clone(null!));
    }
}
