using ImageFacade.Backends;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// 换帧后 <see cref="FacadeImage.Metadata"/> 必须反映新帧的真实状态（planv2 第 9 条 / §10.2）。
/// 判断 alpha 不扫全图：按"这次几何变换做了什么"静态推导——重采样不会创造或消灭 alpha，
/// 补透明边一定会引入透明像素，拉伸 / 裁剪 / 补不透明边原样继承，且这些操作都不改 EXIF 方向。
/// </summary>
public sealed class MetadataTests
{
    [Fact]
    public void Resize_Refreshes_Width_Height_Alpha_And_Orientation()
    {
        byte[] icc = [1, 2, 3, 4];
        var backend = new FixedMetadataBackend(
            8,
            6,
            new ImageMetadata(ImageFormat.Png, 8, 6, orientation: 6, hasAlpha: true, iccProfile: icc));
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Load(new byte[] { 0xAA });

        image.Resize(4, 3);

        Assert.Equal(4, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(4, image.Metadata.Width);
        Assert.Equal(3, image.Metadata.Height);
        Assert.True(image.Metadata.HasAlpha);
        Assert.Equal(6, image.Metadata.Orientation);
        Assert.Equal(ImageFormat.Png, image.Metadata.Format);
        Assert.False(image.Metadata.IccProfile.IsEmpty);
        Assert.Equal(4, image.Metadata.IccProfile.Length);
    }

    [Fact]
    public void Resize_Pad_With_Transparent_Color_Sets_HasAlpha()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(40, 20, Rgba32.White);
        Assert.False(image.Metadata.HasAlpha);

        image.Resize(new ResizeOptions
        {
            Width = 40,
            Height = 40,
            Mode = ResizeMode.Pad,
            PadColor = Rgba32.Transparent,
        });

        Assert.Equal(40, image.Metadata.Width);
        Assert.Equal(40, image.Metadata.Height);
        Assert.True(image.Metadata.HasAlpha, "补了透明边之后图上确实有透明像素");
        Assert.Equal(0, image.Metadata.Orientation);
    }

    [Fact]
    public void Resize_Pad_With_Opaque_Color_Keeps_HasAlpha_False()
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

        Assert.False(image.Metadata.HasAlpha);
    }

    [Fact]
    public void Resize_Crop_Keeps_HasAlpha()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(40, 40, background: null);
        Assert.True(image.Metadata.HasAlpha);

        image.Resize(new ResizeOptions { Width = 20, Height = 20, Mode = ResizeMode.Crop });

        Assert.Equal(20, image.Metadata.Width);
        Assert.Equal(20, image.Metadata.Height);
        Assert.True(image.Metadata.HasAlpha);
    }

    [Fact]
    public void Resize_Stretch_Keeps_Opaque()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.Resize(16, 8);

        Assert.Equal(16, image.Metadata.Width);
        Assert.Equal(8, image.Metadata.Height);
        Assert.False(image.Metadata.HasAlpha);
    }

    /// <summary>只解码、不做任何渲染的替身：用固定 metadata + 固定尺寸的帧。</summary>
    private sealed class FixedMetadataBackend : FakeBackend
    {
        private readonly int _width;
        private readonly int _height;
        private readonly ImageMetadata _metadata;

        public FixedMetadataBackend(int width, int height, ImageMetadata metadata)
            : base("fixed-metadata")
        {
            _width = width;
            _height = height;
            _metadata = metadata;
        }

        public override IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
        {
            metadata = _metadata;
            return new FakeFrame(_width, _height, new byte[_width * _height * 4]);
        }
    }
}
