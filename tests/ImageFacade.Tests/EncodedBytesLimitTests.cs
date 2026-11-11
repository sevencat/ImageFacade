using ImageFacade.SkiaSharp;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// 编码字节数上限（planv2 §10.1）：<see cref="LoadOptions.MaxEncodedBytes"/> 约束"把字节读进来"的过程——
/// 流是累计读、超了就**停止读取**，文件路径是内存映射、按文件长度**先拒后读**；
/// 两条路都抛 <see cref="ImageSizeLimitExceededException"/>，默认 0 表示不限制。
/// </summary>
public sealed class EncodedBytesLimitTests
{
    // ---- 默认行为：不限制（向后兼容） ----

    [Fact]
    public async Task Default_MaxEncodedBytes_Is_Unlimited()
    {
        using var engine = new ImageEngine(new FakeBackend());

        // 256 KB 垃圾数据：默认选项必须一路读到后端，由后端报"不解码"，而不是被字节上限拦下
        byte[] junk = new byte[256 * 1024];
        Assert.Throws<FeatureNotSupportedException>(() => engine.Load(new ForwardOnlyStream(junk)));
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => engine.LoadAsync(new ForwardOnlyStream(junk)));
    }

    // ---- 内存流快路径 ----

    [Fact]
    public void Load_MemoryStream_Rejects_Data_Over_Limit()
    {
        using var engine = new ImageEngine(new FakeBackend());
        byte[] data = new byte[2048];

        var error = Assert.Throws<ImageSizeLimitExceededException>(
            () => engine.Load(new MemoryStream(data), new LoadOptions { MaxEncodedBytes = 1024 }));

        Assert.Equal(1024L, error.MaxEncodedBytes);
        Assert.Equal(2048L, error.EncodedBytes);
        // 字节维度没有尺寸信息，这三个字段保持 0（不要拿"0×0"去猜像素）
        Assert.Equal(0, error.Width);
        Assert.Equal(0, error.Height);
        Assert.Equal(0L, error.MaxPixels);
    }

    [Fact]
    public void Load_MemoryStream_Accepts_Data_At_Exact_Limit()
    {
        using var engine = new ImageEngine(new FakeBackend());
        byte[] data = new byte[1024];

        // 恰好等于上限不算超限：能走到解码说明字节闸放行了
        Assert.Throws<FeatureNotSupportedException>(
            () => engine.Load(new MemoryStream(data), new LoadOptions { MaxEncodedBytes = 1024 }));
    }

    // ---- 通用流：累计校验 ----

    [Fact]
    public void Load_Stream_Rejects_Data_Over_Limit()
    {
        using var engine = new ImageEngine(new FakeBackend());
        byte[] data = new byte[4096];

        var error = Assert.Throws<ImageSizeLimitExceededException>(
            () => engine.Load(new ForwardOnlyStream(data), new LoadOptions { MaxEncodedBytes = 1000 }));

        Assert.Equal(1000L, error.MaxEncodedBytes);
        Assert.True(error.EncodedBytes > 1000, "报出的字节数必须真的超过上限");
    }

    [Fact]
    public void Load_Stream_Stops_Reading_Once_Over_Limit()
    {
        using var engine = new ImageEngine(new FakeBackend());
        var stream = new ForwardOnlyStream(new byte[1024 * 1024]);

        Assert.Throws<ImageSizeLimitExceededException>(
            () => engine.Load(stream, new LoadOptions { MaxEncodedBytes = 1000 }));

        // 缓冲上限是"上限 + 1"，而池子会按桶大小多给一点；关键是别把 1 MB 全读完
        Assert.InRange(stream.BytesRead, 1001, 8 * 1024);
    }

    [Fact]
    public void Load_Stream_Rejects_By_Seekable_Length_Without_Reading()
    {
        using var engine = new ImageEngine(new FakeBackend());
        var stream = new OverlongSeekableStream(actualLength: 5000);

        var error = Assert.Throws<ImageSizeLimitExceededException>(
            () => engine.Load(stream, new LoadOptions { MaxEncodedBytes = 100 }));

        Assert.Equal(0, stream.ReadCalls);   // 明知超限就不该去读第一块
        Assert.Equal(5000L, error.EncodedBytes);
    }

    [Fact]
    public async Task LoadAsync_Stream_Rejects_Data_Over_Limit()
    {
        using var engine = new ImageEngine(new FakeBackend());
        var stream = new ForwardOnlyStream(new byte[8192]);

        var error = await Assert.ThrowsAsync<ImageSizeLimitExceededException>(
            () => engine.LoadAsync(stream, new LoadOptions { MaxEncodedBytes = 512 }));

        Assert.Equal(512L, error.MaxEncodedBytes);
        Assert.InRange(stream.BytesRead, 513, 4 * 1024);
    }

    // ---- 文件路径：只读内存映射，长度先查、内容后读 ----

    [Fact]
    public void Load_FilePath_Rejects_By_Length_Before_Decoding()
    {
        using var engine = new ImageEngine(new FakeBackend());
        string path = WriteTempFile(new byte[5000]);

        try
        {
            var error = Assert.Throws<ImageSizeLimitExceededException>(
                () => engine.Load(path, new LoadOptions { MaxEncodedBytes = 100 }));

            // 报的必须是文件的真实长度（而不是"读着读着停手"时读到的字节数）
            Assert.Equal(100L, error.MaxEncodedBytes);
            Assert.Equal(5000L, error.EncodedBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_FilePath_Rejects_By_Length_Before_Decoding()
    {
        using var engine = new ImageEngine(new FakeBackend());
        string path = WriteTempFile(new byte[8192]);

        try
        {
            var error = await Assert.ThrowsAsync<ImageSizeLimitExceededException>(
                () => engine.LoadAsync(path, new LoadOptions { MaxEncodedBytes = 512 }));

            Assert.Equal(512L, error.MaxEncodedBytes);
            Assert.Equal(8192L, error.EncodedBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Load_FilePath_Rejects_Empty_File()
    {
        using var engine = new ImageEngine(new FakeBackend());
        string path = WriteTempFile([]);

        try
        {
            var error = Assert.Throws<ArgumentException>(() => engine.Load(path));
            Assert.Equal("path", error.ParamName);
            await Assert.ThrowsAsync<ArgumentException>(() => engine.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_FilePath_Within_Limit_Reaches_The_Backend()
    {
        using var engine = new ImageEngine(new FakeBackend());
        string path = WriteTempFile(new byte[1024]);

        try
        {
            // 恰好等于上限不算超限：能走到解码（由替身报"不解码"）说明长度闸放行了
            Assert.Throws<FeatureNotSupportedException>(
                () => engine.Load(path, new LoadOptions { MaxEncodedBytes = 1024 }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"imagefacade-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    // ---- 端到端：限内数据能正常解码（读到的长度必须精确，不能带上池子的多余字节） ----

    [Fact]
    public void Load_Stream_Decodes_Real_Image_Within_Limit()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        byte[] png = EncodeSamplePng(engine);

        using FacadeImage loaded = engine.Load(
            new ForwardOnlyStream(png),
            new LoadOptions { MaxEncodedBytes = png.Length });

        Assert.Equal(37, loaded.Width);
        Assert.Equal(23, loaded.Height);
    }

    [Fact]
    public async Task LoadAsync_Stream_Decodes_Real_Image_Within_Limit()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        byte[] png = EncodeSamplePng(engine);

        using FacadeImage loaded = await engine.LoadAsync(
            new ForwardOnlyStream(png),
            new LoadOptions { MaxEncodedBytes = png.Length });

        Assert.Equal(37, loaded.Width);
        Assert.Equal(23, loaded.Height);
    }

    /// <summary>文件路径端到端：映射出来的内存能直接解码，且解码结束后文件句柄已经放掉。</summary>
    [Fact]
    public void Load_FilePath_Decodes_Real_Image_And_Releases_The_Handle()
    {
        using var engine = new ImageEngine(new SkiaBackend());
        string path = WriteTempFile(EncodeSamplePng(engine));

        try
        {
            using (FacadeImage loaded = engine.Load(path))
            {
                Assert.Equal(37, loaded.Width);
                Assert.Equal(23, loaded.Height);
            }

            // 映射在 Load 返回时就释放了：Windows 上还占着句柄的话这一句会抛 IOException
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static byte[] EncodeSamplePng(ImageEngine engine)
    {
        using FacadeImage image = engine.Create(37, 23, Rgba32.White);
        return image.Encode(new SaveOptions { Format = ImageFormat.Png });
    }

    // ---- 测试替身 ----

    /// <summary>不可寻址、只进不退的流：强制走"按块读入 + 累计校验"那条路径。</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public ForwardOnlyStream(byte[] data) => _inner = new MemoryStream(data, writable: false);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>可寻址且 Length 已知超限，Read 被调用就说明"先看 Length"这条快捷路径没生效。</summary>
    private sealed class OverlongSeekableStream : Stream
    {
        private readonly long _length;

        public OverlongSeekableStream(long actualLength) => _length = actualLength;

        public int ReadCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw new InvalidOperationException("不应该读：Length 已经说明超限。");
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
