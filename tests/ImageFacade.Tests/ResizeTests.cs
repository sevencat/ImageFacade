using ImageFacade.Internal;
using Xunit;

namespace ImageFacade.Tests;

public sealed class ResizePlannerTests
{
    [Fact]
    public void Stretch_Uses_Target_Directly()
    {
        ResizePlan plan = ResizePlanner.Plan(640, 480, 320, 320, ResizeMode.Stretch);

        Assert.Equal(320, plan.ScaledWidth);
        Assert.Equal(320, plan.ScaledHeight);
        Assert.Equal(0, plan.PlaceX);
        Assert.Equal(0, plan.PlaceY);
        Assert.False(plan.NeedsComposite);
    }

    [Fact]
    public void Pad_Keeps_Aspect_And_Centers()
    {
        ResizePlan plan = ResizePlanner.Plan(640, 480, 320, 320, ResizeMode.Pad);
        Assert.Equal(320, plan.ScaledWidth);
        Assert.Equal(240, plan.ScaledHeight);
        Assert.Equal(320, plan.OutputWidth);
        Assert.Equal(320, plan.OutputHeight);
        Assert.Equal(0, plan.PlaceX);
        Assert.Equal(40, plan.PlaceY);
        Assert.True(plan.NeedsComposite);
    }

    [Fact]
    public void Crop_Overflows_And_Centers()
    {
        ResizePlan plan = ResizePlanner.Plan(640, 480, 320, 320, ResizeMode.Crop);

        Assert.Equal(427, plan.ScaledWidth);
        Assert.Equal(320, plan.ScaledHeight);
        Assert.Equal(-53, plan.PlaceX);
        Assert.Equal(0, plan.PlaceY);
        Assert.True(plan.NeedsComposite);
    }

    [Theory]
    [InlineData(1280, 720, 640, 640, 640, 360)]   // Max：缩到完全放进 640×640
    [InlineData(100, 100, 640, 640, 640, 640)]    // 放大
    [InlineData(640, 480, 320, 320, 320, 240)]
    public void Max_Keeps_Aspect_And_Does_Not_Pad(int sw, int sh, int tw, int th, int expectedWidth, int expectedHeight)
    {
        ResizePlan plan = ResizePlanner.Plan(sw, sh, tw, th, ResizeMode.Max);

        Assert.Equal(expectedWidth, plan.OutputWidth);
        Assert.Equal(expectedHeight, plan.OutputHeight);
        Assert.False(plan.NeedsComposite);
    }

    [Fact]
    public void Min_Covers_Target()
    {
        ResizePlan plan = ResizePlanner.Plan(1280, 720, 640, 640, ResizeMode.Min);

        Assert.Equal(1138, plan.OutputWidth);
        Assert.Equal(640, plan.OutputHeight);
        Assert.False(plan.NeedsComposite);
    }

    [Fact]
    public void BoxPad_Never_Exceeds_Target()
    {
        ResizePlan plan = ResizePlanner.Plan(641, 481, 320, 320, ResizeMode.BoxPad);

        Assert.True(plan.ScaledWidth <= 320);
        Assert.True(plan.ScaledHeight <= 320);
        Assert.Equal(320, plan.OutputWidth);
    }
}

public sealed class FacadeResizeTests
{
    [Fact]
    public void Pad_Fills_Border_With_PadColor()
    {
        using var engine = new ImageEngine(new FakeBackend(), new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(100, 50, Rgba32.White);

        image.Resize(new ResizeOptions
        {
            Width = 60,
            Height = 60,
            Mode = ResizeMode.Pad,
            PadColor = Rgba32.Red,
        });

        Assert.Equal(60, image.Width);
        Assert.Equal(60, image.Height);
        Assert.Equal(Rgba32.Red, image.GetPixel(0, 0));
        Assert.Equal(Rgba32.Red, image.GetPixel(59, 59));
        Assert.Equal(Rgba32.White, image.GetPixel(30, 30));
    }

    [Fact]
    public void Crop_Outputs_Exact_Target_Size()
    {
        using var engine = new ImageEngine(new FakeBackend(), new ImageEngineOptions { Fallback = FallbackMode.Disabled });
        using FacadeImage image = engine.Create(100, 50, Rgba32.White);

        image.Resize(new ResizeOptions { Width = 60, Height = 60, Mode = ResizeMode.Crop, PadColor = Rgba32.Red });

        Assert.Equal(60, image.Width);
        Assert.Equal(60, image.Height);
        Assert.Equal(Rgba32.White, image.GetPixel(0, 0));
        Assert.Equal(Rgba32.White, image.GetPixel(59, 59));
    }

    [Fact]
    public void Resize_Falls_Back_When_Backend_Cannot_Resize()
    {
        var native = new FakeBackend("native") { SupportsResize = false };
        var software = new FakeBackend("software");

        using var engine = new ImageEngine(native, new ImageEngineOptions
        {
            FallbackBackend = software,
            Fallback = FallbackMode.PerOperation,
        });

        using FacadeImage image = engine.Create(40, 40, Rgba32.White);
        image.Resize(20, 20);

        Assert.Equal(20, image.Width);
        Assert.Equal("native", image.BackendName);      // 不迁移整张图
        Assert.Equal(1, software.ResizeCalls);
        Assert.Equal(1, image.FallbackCount);
    }
}
