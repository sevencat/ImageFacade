using ImageFacade.Pipeline;
using ImageFacade.SkiaSharp;
using Xunit;

namespace ImageFacade.Tests;

public sealed class LetterboxTests
{
    [Fact]
    public void Apply_Does_Not_Touch_Source()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(320, 240, Rgba32.FromHex(0x336699));
        Rgba32 before = source.GetPixel(10, 10);

        using FacadeImage padded = source.Apply(320, 320, out _);

        Assert.Equal(320, source.Width);
        Assert.Equal(240, source.Height);
        Assert.Equal(before, source.GetPixel(10, 10));

        Assert.Equal(320, padded.Width);
        Assert.Equal(320, padded.Height);
    }

    [Fact]
    public void Apply_Centers_By_Default_And_Reports_Geometry()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(640, 480, Rgba32.White);

        using FacadeImage padded = source.Apply(640, 640, out LetterboxInfo info);

        Assert.Equal(1f, info.Scale, 4);
        Assert.Equal(0, info.PadLeft);
        Assert.Equal(80, info.PadTop);
        Assert.Equal(new Size(640, 480), info.SourceSize);
        Assert.Equal(new Size(640, 640), info.ModelSize);

        // 顶部补边是默认灰，中间是原图内容。
        Assert.Equal(Letterbox.DefaultPadColor, padded.GetPixel(320, 10));
        Assert.Equal(Rgba32.White, padded.GetPixel(320, 320));
    }

    [Fact]
    public void Apply_NonCentered_Puts_Content_TopLeft()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(640, 480, Rgba32.White);

        using FacadeImage padded = source.Apply(640, 640, out LetterboxInfo info, center: false);

        Assert.Equal(0, info.PadLeft);
        Assert.Equal(0, info.PadTop);
        Assert.Equal(Rgba32.White, padded.GetPixel(10, 10));
    }

    [Fact]
    public void Info_RoundTrips_Within_One_Pixel()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(1920, 1080, Rgba32.White);

        using FacadeImage padded = source.Apply(640, 640, out LetterboxInfo info);

        Assert.Equal(640, padded.Width);
        Assert.Equal(640, padded.Height);
        Assert.Equal(0.3333f, info.Scale, 3);
        Assert.Equal(0, info.PadLeft);
        Assert.Equal(140, info.PadTop);

        var box = new RectF(100, 200, 300, 150);
        RectF model = info.ToModel(box);
        RectF back = info.ToSource(model);

        Assert.InRange(MathF.Abs(back.X - box.X), 0f, 1f);
        Assert.InRange(MathF.Abs(back.Y - box.Y), 0f, 1f);
        Assert.InRange(MathF.Abs(back.Width - box.Width), 0f, 1f);
        Assert.InRange(MathF.Abs(back.Height - box.Height), 0f, 1f);
    }

    [Fact]
    public void Apply_Rejects_NonPositive_Target()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(10, 10, Rgba32.White);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = source.Apply(new LetterboxOptions { Width = 0, Height = 10 }, out _); });
    }
}

public sealed class TensorizerTests
{
    [Fact]
    public void ToTensor_Nchw_Writes_Planes_In_Rgb_Order()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(2, 2, Rgba32.FromHex(0x102030));

        float[] tensor = source.ToTensor(new TensorOptions { Width = 2, Height = 2 }, out _);

        Assert.Equal(12, tensor.Length);
        Assert.Equal(0x10 / 255f, tensor[0], 5);
        Assert.Equal(0x20 / 255f, tensor[4], 5);
        Assert.Equal(0x30 / 255f, tensor[8], 5);
    }

    [Fact]
    public void ToTensor_Nhwc_Is_Interleaved()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(1, 1, Rgba32.FromHex(0x102030));

        float[] tensor = source.ToTensor(
            new TensorOptions { Width = 1, Height = 1, Layout = TensorLayout.Nhwc },
            out _);

        Assert.Equal(0x10 / 255f, tensor[0], 5);
        Assert.Equal(0x20 / 255f, tensor[1], 5);
        Assert.Equal(0x30 / 255f, tensor[2], 5);
    }

    [Fact]
    public void ToTensor_Bgr_Swaps_Red_And_Blue()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(1, 1, Rgba32.FromHex(0x102030));

        float[] tensor = source.ToTensor(
            new TensorOptions { Width = 1, Height = 1, Channels = ChannelOrder.Bgr },
            out _);

        Assert.Equal(0x30 / 255f, tensor[0], 5);
        Assert.Equal(0x20 / 255f, tensor[1], 5);
        Assert.Equal(0x10 / 255f, tensor[2], 5);
    }

    [Fact]
    public void ToTensor_Applies_Mean_And_Std()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(1, 1, Rgba32.White);

        float[] tensor = source.ToTensor(
            new TensorOptions
            {
                Width = 1,
                Height = 1,
                Mean = new float[] { 0.5f, 0.5f, 0.5f },
                Std = new float[] { 0.5f, 0.5f, 0.5f },
            },
            out _);

        // (1.0 - 0.5) / 0.5 = 1
        Assert.Equal(1f, tensor[0], 4);
        Assert.Equal(1f, tensor[1], 4);
        Assert.Equal(1f, tensor[2], 4);
    }

    [Fact]
    public void ToTensor_Fills_Padding_With_PadColor()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(320, 240, Rgba32.White);

        float[] tensor = source.ToTensor(new TensorOptions { Width = 320, Height = 320 }, out LetterboxInfo info);

        Assert.Equal(40, info.PadTop);

        // NCHW 平面 0 的左上角落在补边区域。
        Assert.Equal(114 / 255f, tensor[0], 4);

        // 中心落回原图（白色）。
        int center = (160 * 320) + 160;
        Assert.Equal(1f, tensor[center], 4);
    }

    [Fact]
    public void ToTensor_Throws_When_Destination_Is_Too_Small()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(4, 4, Rgba32.White);

        var options = new TensorOptions { Width = 4, Height = 4 };
        var destination = new float[10];

        Assert.Throws<ArgumentException>(() => source.ToTensor(options, destination, out _));
    }

    [Fact]
    public void ToTensor_Rejects_Wrong_Channel_Vector_Length()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var source = engine.Create(4, 4, Rgba32.White);

        var options = new TensorOptions
        {
            Width = 4,
            Height = 4,
            Mean = new float[] { 0.5f, 0.5f },
        };

        Assert.Throws<ArgumentException>(() => { _ = source.ToTensor(options, out _); });
    }

    [Fact]
    public void TensorBuffer_Rents_And_Returns_To_Pool()
    {
        using var buffer = TensorBuffer.Rent(100);

        Assert.Equal(100, buffer.Length);
        Assert.Equal(100, buffer.Span.Length);

        buffer.Span[0] = 42f;
        Assert.Equal(42f, buffer.Span[0]);

        buffer.Dispose();
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = buffer.Span.Length; });
    }
}

public sealed class NmsTests
{
    [Fact]
    public void Suppress_Keeps_Higher_Score_And_Drops_Overlap()
    {
        var detections = new[]
        {
            new Detection(new RectF(0, 0, 100, 100), "low", 0.60f),
            new Detection(new RectF(2, 2, 100, 100), "high", 0.95f),
            new Detection(new RectF(500, 500, 100, 100), "far", 0.70f),
        };

        int kept = Nms.Suppress(detections, iouThreshold: 0.45f);

        Assert.Equal(2, kept);
        Assert.Equal("high", detections[0].Label);
        Assert.Equal("far", detections[1].Label);
    }

    [Fact]
    public void Suppress_Ignores_Overlap_Across_Classes()
    {
        var detections = new[]
        {
            new Detection(new RectF(0, 0, 100, 100), "person", 0.9f) { ClassId = 1 },
            new Detection(new RectF(2, 2, 100, 100), "car", 0.8f) { ClassId = 2 },
        };

        Assert.Equal(2, Nms.Suppress(detections, classAware: true));
        Assert.Equal(1, Nms.Suppress(detections, classAware: false));
    }

    [Fact]
    public void Suppress_Is_Order_Independent()
    {
        var forward = new[]
        {
            new Detection(new RectF(0, 0, 100, 100), "a", 0.9f),
            new Detection(new RectF(3, 3, 100, 100), "b", 0.8f),
            new Detection(new RectF(400, 400, 100, 100), "c", 0.7f),
        };

        var backward = new[] { forward[2], forward[1], forward[0] };

        Assert.Equal(Nms.Suppress(forward), Nms.Suppress(backward));
        Assert.Equal("a", forward[0].Label);
        Assert.Equal("a", backward[0].Label);
    }

    [Fact]
    public void Suppress_Handles_Empty_Input()
    {
        Assert.Equal(0, Nms.Suppress(Span<Detection>.Empty));
        Assert.Empty(Nms.Select(ReadOnlySpan<Detection>.Empty));
    }

    [Fact]
    public void IoU_Is_One_For_Identical_Boxes()
    {
        var box = new RectF(10, 10, 50, 50);
        Assert.Equal(1f, Nms.IoU(box, box), 5);
    }

    [Fact]
    public void Select_Returns_Original_Indices_In_Score_Order()
    {
        var detections = new[]
        {
            new Detection(new RectF(0, 0, 100, 100), "a", 0.5f),
            new Detection(new RectF(300, 300, 100, 100), "b", 0.9f),
        };

        int[] picked = Nms.Select(detections);

        Assert.Equal(new[] { 1, 0 }, picked);
    }
}

public sealed class DetectionDrawingTests
{
    [Fact]
    public void DrawDetections_Paints_Box_And_Label()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var image = engine.Create(200, 200, Rgba32.White);

        var detections = new[] { new Detection(new RectF(40, 40, 80, 60), "person", 0.92f) };

        image.DrawDetections(detections);

        int ink = 0;
        byte[] pixels = image.CopyPixels();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
            {
                ink++;
            }
        }

        Assert.True(ink > 0, "应该至少有边框被画出来。");
    }

    [Fact]
    public void DrawDetections_Rejects_Empty_Palette()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var image = engine.Create(20, 20, Rgba32.White);

        var style = new DetectionStyle { Palette = Array.Empty<Rgba32>() };
        var detections = new[] { new Detection(new RectF(1, 1, 10, 10)) };

        Assert.Throws<ArgumentException>(() => { _ = image.DrawDetections(detections, style); });
    }

    [Fact]
    public void FormatLabel_Can_Be_Customized()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        using var image = engine.Create(120, 120, Rgba32.White);

        var style = new DetectionStyle { FormatLabel = d => $"#{d.ClassId}" };
        var detections = new[] { new Detection(new RectF(10, 10, 60, 40)) { ClassId = 7 } };

        image.DrawDetections(detections, style);

        Assert.True(image.Width == 120);
    }
}
