using ImageFacade.Backends;
using ImageFacade.Internal;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// "图里到底有没有半透明像素"的检查：加载时查一次（<see cref="LoadOptions.DetectTransparency"/>），
/// 之后由 <see cref="FacadeImage.DetectTransparency(bool)"/> 按需重算。
/// </summary>
public sealed class TransparencyTests
{
    [Fact]
    public void Load_Detects_Transparency_Once_And_Caches_It()
    {
        var backend = new DecodingBackend(
            new ImageMetadata(ImageFormat.Png, 4, 2, hasAlpha: true),
            Pixels(4, 2, firstAlpha: 0));
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Load(new byte[] { 0xAA });

        Assert.True(image.Metadata.HasTransparency);
        Assert.Equal(1, backend.TransparencyQueries);
        Assert.Equal(1, backend.AcquireCalls);   // 后端答不上来，门面扫了一遍

        // 结论已经在了：再问一次既不再问后端，也不扫像素
        Assert.True(image.DetectTransparency());
        Assert.Equal(1, backend.TransparencyQueries);
        Assert.Equal(1, backend.AcquireCalls);
    }

    [Fact]
    public void Load_Without_Alpha_Channel_Answers_False_Without_Asking_Backend()
    {
        // JPEG-ish：解码器说没有 alpha 通道，半透明像素不可能存在
        var backend = new DecodingBackend(
            new ImageMetadata(ImageFormat.Jpeg, 4, 2, hasAlpha: false),
            Pixels(4, 2));
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Load(new byte[] { 0xAA });

        Assert.False(image.Metadata.HasTransparency);
        Assert.Equal(0, backend.TransparencyQueries);
        Assert.Equal(0, backend.AcquireCalls);
    }

    [Fact]
    public void Load_With_Detection_Disabled_Leaves_Unknown()
    {
        var backend = new DecodingBackend(
            new ImageMetadata(ImageFormat.Png, 4, 2, hasAlpha: true),
            Pixels(4, 2, firstAlpha: 0));
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Load([0xAA], new LoadOptions { DetectTransparency = false });

        Assert.Null(image.Metadata.HasTransparency);
        Assert.Equal(0, backend.TransparencyQueries);
        Assert.Equal(0, backend.AcquireCalls);

        // 用户自己需要时再查：这时才真的去看像素
        Assert.True(image.DetectTransparency());
        Assert.Equal(1, backend.AcquireCalls);
        Assert.True(image.Metadata.HasTransparency);
    }

    [Fact]
    public void Load_Uses_Backend_Answer_Without_Scanning_Pixels()
    {
        var backend = new DecodingBackend(
            new ImageMetadata(ImageFormat.Png, 4, 2, hasAlpha: true),
            Pixels(4, 2, firstAlpha: 0))
        {
            TransparencyAnswer = false,
        };
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Load(new byte[] { 0xAA });

        Assert.False(image.Metadata.HasTransparency);
        Assert.Equal(1, backend.TransparencyQueries);
        Assert.Equal(0, backend.AcquireCalls);

        // force 无视缓存重问后端
        backend.TransparencyAnswer = true;
        Assert.True(image.DetectTransparency(force: true));
        Assert.True(image.Metadata.HasTransparency);
        Assert.Equal(2, backend.TransparencyQueries);
    }

    [Fact]
    public void Load_Leaves_Unknown_When_Backend_Can_Neither_Answer_Nor_Export_Pixels()
    {
        var backend = new DecodingBackend(
            new ImageMetadata(ImageFormat.Png, 4, 2, hasAlpha: true),
            Pixels(4, 2, firstAlpha: 0))
        {
            CanAcquirePixels = false,
        };
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Load(new byte[] { 0xAA });

        Assert.Null(image.Metadata.HasTransparency);
        Assert.Equal(1, backend.AcquireCalls);

        // 查不出来不是异常：还是 null
        Assert.Null(image.DetectTransparency());
        Assert.Null(image.Metadata.HasTransparency);
    }

    [Fact]
    public void Drawing_Invalidates_The_Cached_Answer()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(8, 8, Rgba32.White);
        Assert.False(image.Metadata.HasTransparency);

        image.DrawLine(new PointF(0, 0), new PointF(7, 7), new Rgba32(255, 0, 0, 128));

        Assert.Null(image.Metadata.HasTransparency);
        Assert.True(image.DetectTransparency());
        Assert.True(image.Metadata.HasTransparency);

        // 不透明画笔同样让结论过期，但重算出来还是 false
        image.DrawLine(new PointF(0, 0), new PointF(7, 0), Rgba32.Black);
        Assert.Null(image.Metadata.HasTransparency);
        Assert.True(image.DetectTransparency());
    }

    [Fact]
    public void Clear_Decides_The_Answer_From_The_Color()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(8, 8, Rgba32.White);

        image.Clear(new Rgba32(0, 0, 0, 40));
        Assert.True(image.Metadata.HasTransparency);
        Assert.True(image.DetectTransparency());

        image.Clear(Rgba32.Black);
        Assert.False(image.Metadata.HasTransparency);
        Assert.False(image.DetectTransparency());
    }

    [Fact]
    public void Resize_Pad_Derives_Answer_From_Pad_Color()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(40, 20, Rgba32.White);

        image.Resize(new ResizeOptions
        {
            Width = 40,
            Height = 40,
            Mode = ResizeMode.Pad,
            PadColor = new Rgba32(0, 0, 0, 128),
        });

        Assert.True(image.Metadata.HasTransparency);
        Assert.True(image.DetectTransparency());
    }

    [Fact]
    public void Resize_Pad_With_Opaque_Color_Keeps_Answer_False()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(40, 20, Rgba32.White);

        image.Resize(new ResizeOptions
        {
            Width = 40,
            Height = 40,
            Mode = ResizeMode.Pad,
            PadColor = Rgba32.Black,
        });

        Assert.False(image.Metadata.HasTransparency);
    }

    [Fact]
    public void Encode_Passes_AssumeOpaque_Only_When_Known_Opaque()
    {
        var backend = new EncodeRecordingBackend();
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(8, 8, Rgba32.White);

        int createsBefore = backend.CreateCalls;
        image.Encode(new SaveOptions { Format = ImageFormat.Jpeg });

        Assert.True(backend.LastAssumeOpaque, "已知全不透明：后端可跳过 alpha 探测");
        Assert.Equal(createsBefore, backend.CreateCalls);   // 也不该为了合背景色建临时帧

        image.DrawLine(new PointF(0, 0), new PointF(7, 7), new Rgba32(255, 0, 0, 100));
        image.Encode(new SaveOptions { Format = ImageFormat.Jpeg });

        Assert.Null(backend.LastAssumeOpaque);   // 画过东西：交回后端自己判断
    }

    [Fact]
    public void Scan_Respects_Row_Padding()
    {
        // 3x2，行间 stride 比 12 字节宽：填充区里的垃圾字节不能被当成像素
        var pixels = new byte[16 * 2];
        pixels.AsSpan().Fill(0x00);   // 连 alpha 都是 0，但只在有效像素上检查
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                pixels[(y * 16) + (x * 4) + 3] = 255;
            }
        }

        Assert.False(AlphaProbe.Scan(pixels, 16, 3, 2));

        pixels[(16 + (2 * 4)) + 3] = 254;
        Assert.True(AlphaProbe.Scan(pixels, 16, 3, 2));
    }

    private static byte[] Pixels(int width, int height, byte firstAlpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        pixels[3] = firstAlpha;
        return pixels;
    }

    /// <summary>解码出固定 metadata + 固定像素的替身，用 <see cref="FakeBackend"/> 的开关控制回答方式。</summary>
    private sealed class DecodingBackend : FakeBackend
    {
        private readonly ImageMetadata _metadata;
        private readonly byte[] _pixels;

        public DecodingBackend(ImageMetadata metadata, byte[] pixels)
            : base("decoding")
        {
            _metadata = metadata;
            _pixels = pixels;
        }

        public override IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
        {
            metadata = _metadata;
            return new FakeFrame(_metadata.Width, _metadata.Height, _pixels);
        }
    }

    /// <summary>只会"编码"的后端：把门面给的 <see cref="EncodeRequest.AssumeOpaque"/> 记下来。</summary>
    private sealed class EncodeRecordingBackend : FakeBackend
    {
        private static readonly IReadOnlySet<ImageFormat> JpegOnly = new HashSet<ImageFormat> { ImageFormat.Jpeg };

        public EncodeRecordingBackend()
            : base("encode-recording")
        {
        }

        public bool? LastAssumeOpaque { get; private set; }

        public override IReadOnlySet<ImageFormat> SupportedEncodeFormats => JpegOnly;

        public override void Encode(IFrame frame, Stream destination, in EncodeRequest request)
        {
            LastAssumeOpaque = request.AssumeOpaque;
            destination.Write([1, 2, 3, 4]);
        }
    }
}
