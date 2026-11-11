using ImageFacade.Backends;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// planv2 §8 那批"局部易改"项的回归：Save 原子写、异步编码能力检查、ImageEngine 所有权、
/// 内置字体单例、Skia 解码零拷贝。
/// </summary>
public sealed class HardeningTests
{
    // ---- Save(path)：失败即删除 ----

    [Fact]
    public void Save_Deletes_Target_And_Says_So_When_Encoding_Fails()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        string path = Path.Combine(Path.GetTempPath(), $"imagefacade-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [0xDE, 0xAD, 0xBE, 0xEF]);

        try
        {
            var error = Assert.Throws<ImageSaveFailedException>(() => image.Save(path));

            Assert.Equal(path, error.Path);
            Assert.True(error.FileDeleted);
            Assert.Contains("已删除", error.Message);
            Assert.IsType<FeatureNotSupportedException>(error.InnerException);
            Assert.False(File.Exists(path), "失败后不该留下写了一半的文件");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Save_Leaves_No_File_Behind_On_Failure()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        string directory = Path.Combine(Path.GetTempPath(), $"imagefacade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "out.png");

        try
        {
            Assert.Throws<ImageSaveFailedException>(() => image.Save(path));

            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_Succeeds_And_Writes_Exact_Bytes()
    {
        using var engine = new ImageEngine(new PngFakeBackend());
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        string path = Path.Combine(Path.GetTempPath(), $"imagefacade-{Guid.NewGuid():N}.png");
        try
        {
            image.Save(path);

            Assert.True(File.Exists(path));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_Deletes_Target_When_Encoding_Fails()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        string path = Path.Combine(Path.GetTempPath(), $"imagefacade-{Guid.NewGuid():N}.png");

        try
        {
            // 编码阶段就失败：此时还没碰磁盘，异常应保持原样、不创建文件
            await Assert.ThrowsAnyAsync<FeatureNotSupportedException>(() => image.SaveAsync(path));
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

    // ---- 异步编码也要过能力表 ----

    [Fact]
    public async Task EncodeAsync_Checks_Supported_Formats()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => image.EncodeAsync(new SaveOptions { Format = ImageFormat.Png }));
    }

    [Fact]
    public async Task SaveAsync_Stream_Checks_Supported_Formats()
    {
        using var engine = new ImageEngine(new FakeBackend());
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => image.SaveAsync(stream, new SaveOptions { Format = ImageFormat.Png }));
    }

    // ---- ImageEngine 的后端所有权 ----

    [Fact]
    public void Dispose_Releases_Owned_Backend_By_Default()
    {
        var backend = new DisposableBackend();

        using (var engine = new ImageEngine(backend))
        {
        }

        Assert.True(backend.Disposed);
    }

    [Fact]
    public void Dispose_Leaves_Externally_Owned_Backend_Alive()
    {
        var backend = new DisposableBackend();
        var engine = new ImageEngine(backend, ownsBackend: false);

        engine.Dispose();

        Assert.False(backend.Disposed);
        backend.Dispose();
    }

    // ---- ImageEngine.Default 线程安全读写 ----

    [Fact]
    public void Default_Round_Trips()
    {
        ImageEngine? previous = ImageEngine.Default;
        try
        {
            var backend = new DisposableBackend();
            using var engine = new ImageEngine(backend, ownsBackend: false);

            ImageEngine.Default = engine;
            Assert.Same(engine, ImageEngine.Default);

            ImageEngine.Default = null;
            Assert.Null(ImageEngine.Default);
        }
        finally
        {
            ImageEngine.Default = previous;
        }
    }

    // ---- 内置字体单例 ----

    [Fact]
    public void BuiltInFont_Data_Is_Stable_Across_Reads()
    {
        ReadOnlyMemory<byte> first = BuiltInFont.Data;
        ReadOnlyMemory<byte> second = BuiltInFont.Data;

        Assert.False(first.IsEmpty);
        Assert.True(first.Span.SequenceEqual(second.Span));
    }

    // ---- 引擎释放后图片不可再用 ----

    [Fact]
    public void Image_Throws_Clear_Error_After_Engine_Disposed()
    {
        var engine = new ImageEngine(new FakeBackend());
        FacadeImage image = engine.Create(8, 8, Rgba32.White);

        engine.Dispose();

        try
        {
            var error = Assert.Throws<ObjectDisposedException>(
                () => image.DrawLine(new PointF(0, 0), new PointF(7, 7), Rgba32.Black));

            Assert.Contains("ImageEngine", error.ObjectName);
        }
        finally
        {
            image.Dispose();
        }
    }

    private sealed class DisposableBackend : FakeBackend
    {
        public bool Disposed { get; private set; }

        public override void Dispose() => Disposed = true;
    }
}
