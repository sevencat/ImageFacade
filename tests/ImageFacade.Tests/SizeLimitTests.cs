using System.Buffers.Binary;
using ImageFacade.Backends;
using ImageFacade.Internal;
using ImageFacade.SkiaSharp;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// 解码尺寸上限（planv2 §1）：头部声明超限的输入必须在**分配像素之前**被拒绝，
/// 嗅探不到的格式退化为解码后兜底，且超限的帧要被释放。
/// </summary>
public sealed class SizeLimitTests
{
    private static ImageEngine CreateSkiaEngine() => new(new SkiaBackend());

    /// <summary>用 ImageSharp 编 BMP / GIF / TIFF（Skia 不支持这三个编码）。</summary>
    private static ImageEngine CreateImageSharpEngine() => new(new ImageFacade.ImageSharp.ImageSharpBackend());

    /// <summary>造一个只有签名与 IHDR 的 PNG：声明尺寸由参数决定，不含任何像素数据。</summary>
    private static byte[] FakePngHeader(int width, int height)
    {
        var data = new byte[33];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 13);   // IHDR 长度
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)height);
        data[24] = 8;    // 位深
        data[25] = 6;    // RGBA
        return data;
    }

    // ---- 第一道闸：解码前 ----

    [Fact]
    public void Load_Rejects_Huge_Header_Before_Decoding()
    {
        // 后端从不解码：能抛 ImageSizeLimitExceededException 就说明是解码前拦下的
        using var engine = new ImageEngine(new NeverDecodesBackend());
        byte[] bomb = FakePngHeader(30000, 30000);

        var error = Assert.Throws<ImageSizeLimitExceededException>(() => engine.Load(bomb));

        Assert.Equal(30000, error.Width);
        Assert.Equal(30000, error.Height);
        Assert.Equal(100_000_000, error.MaxPixels);
    }

    [Fact]
    public void Load_Honors_Custom_MaxPixels()
    {
        using var engine = new ImageEngine(new NeverDecodesBackend());
        byte[] data = FakePngHeader(2000, 2000);

        var error = Assert.Throws<ImageSizeLimitExceededException>(
            () => engine.Load(data, new LoadOptions { MaxPixels = 1_000_000 }));

        Assert.Equal(4_000_000L, (long)error.Width * error.Height);
        Assert.Equal(1_000_000L, error.MaxPixels);
    }

    [Fact]
    public void Load_Allows_When_MaxPixels_Is_Zero()
    {
        using var engine = new ImageEngine(new NeverDecodesBackend());

        // 0 = 不限制：第一道闸放过，接着走到后端解码（用的是"从不解码"的后端，所以看到的是它的异常）
        Assert.Throws<FeatureNotSupportedException>(
            () => engine.Load(FakePngHeader(30000, 30000), new LoadOptions { MaxPixels = 0 }));
    }

    [Fact]
    public async Task LoadAsync_Also_Rejects_Huge_Header()
    {
        using var engine = new ImageEngine(new NeverDecodesBackend());
        byte[] bomb = FakePngHeader(30000, 30000);

        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => engine.LoadAsync(bomb));
    }

    // ---- 第二道闸：解码后兜底（嗅探不认识、或头部与实际不符） ----

    [Fact]
    public void Load_Disposes_Frame_That_Exceeds_Limit_After_Decode()
    {
        var backend = new ReturnsFixedFrameBackend(4, 4);
        using var engine = new ImageEngine(backend);

        // 数据不是任何已知格式，嗅探失败；解码后 4x4 = 16 像素 > 8
        var error = Assert.Throws<ImageSizeLimitExceededException>(
            () => engine.Load(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, new LoadOptions { MaxPixels = 8 }));

        Assert.Equal(4, error.Width);
        Assert.Equal(4, error.Height);
        Assert.True(backend.LastFrame!.Disposed, "超限的帧必须被释放");
    }

    // ---- 头部嗅探覆盖 ----

    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.WebP)]
    public void Reads_Header_Of_Skia_Encoded_Image(ImageFormat format)
    {
        using var engine = CreateSkiaEngine();
        using FacadeImage image = engine.Create(37, 23, Rgba32.White);
        byte[] encoded = image.Encode(new SaveOptions { Format = format });

        Assert.True(ImageHeaderReader.TryReadDimensions(encoded, out int width, out int height));
        Assert.Equal(37, width);
        Assert.Equal(23, height);
    }

    [Theory]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Gif)]
    [InlineData(ImageFormat.Tiff)]
    public void Reads_Header_Of_ImageSharp_Encoded_Image(ImageFormat format)
    {
        using var engine = CreateImageSharpEngine();
        using FacadeImage image = engine.Create(37, 23, Rgba32.White);
        byte[] encoded = image.Encode(new SaveOptions { Format = format });

        Assert.True(ImageHeaderReader.TryReadDimensions(encoded, out int width, out int height));
        Assert.Equal(37, width);
        Assert.Equal(23, height);
    }

    [Fact]
    public void Reads_Bmp_Header_With_Negative_Height()
    {
        // 自底向上的 BMP 把高度写成负数
        byte[] data = new byte[54];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), 17);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), -9);

        Assert.True(ImageHeaderReader.TryReadDimensions(data, out int width, out int height));
        Assert.Equal(17, width);
        Assert.Equal(9, height);
    }

    [Fact]
    public void Returns_False_For_Unknown_Data()
    {
        Assert.False(ImageHeaderReader.TryReadDimensions(new byte[] { 1, 2, 3 }, out _, out _));
        Assert.False(ImageHeaderReader.TryReadDimensions("这不是图像"u8, out _, out _));
    }

    // ---- 端到端：默认上限下正常图片照常工作 ----

    [Fact]
    public void Default_Limit_Does_Not_Affect_Normal_Images()
    {
        using var engine = CreateSkiaEngine();
        using FacadeImage image = engine.Create(320, 240, Rgba32.White);
        byte[] png = image.Encode(new SaveOptions { Format = ImageFormat.Png });

        using FacadeImage loaded = engine.Load(png);

        Assert.Equal(320, loaded.Width);
        Assert.Equal(240, loaded.Height);
    }

    [Fact]
    public void Load_Rejects_Real_Image_Over_Custom_Limit()
    {
        using var engine = CreateSkiaEngine();
        using FacadeImage image = engine.Create(200, 200, Rgba32.White);
        byte[] png = image.Encode(new SaveOptions { Format = ImageFormat.Png });

        Assert.Throws<ImageSizeLimitExceededException>(
            () => engine.Load(png, new LoadOptions { MaxPixels = 10_000 }));
    }

    // ---- 测试替身 ----

    private sealed class NeverDecodesBackend : FakeBackend
    {
    }

    private sealed class ReturnsFixedFrameBackend : FakeBackend
    {
        private readonly int _width;
        private readonly int _height;

        public ReturnsFixedFrameBackend(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public FakeFrame? LastFrame { get; private set; }

        public override IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
        {
            metadata = new ImageMetadata(ImageFormat.Unknown, _width, _height);
            LastFrame = new FakeFrame(_width, _height, new byte[_width * _height * 4]);
            return LastFrame;
        }
    }
}
