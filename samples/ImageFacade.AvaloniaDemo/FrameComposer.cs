using ImageFacade.SdcbSimdCodecs;
using PointF = ImageFacade.PointF;
using RectF = ImageFacade.RectF;

namespace ImageFacade.AvaloniaDemo;

/// <summary>
/// 一帧的合成：直通 RGBA32 进 → ImageFacade 画 HUD → 直通 RGBA32 写进目标缓冲。
/// 画布只在尺寸变化时重建，之后每帧用 <see cref="FacadeImage.ImportPixels"/> 覆盖像素。
/// 目标可以是 Avalonia 的 <c>ILockedFramebuffer.Address</c>，也可以是临时内存（自检用）。
/// </summary>
internal sealed class FrameComposer : IDisposable
{
    // 渲染和保存共用一个软后端：绘制 / 像素读写由 ImageBackendBase 直接转给核心光栅
    // （与 new ImageEngine() 是同一份实现），编解码则挂在这个后端上——
    // 所以画布本身就能直接 SavePng，不需要中转一份像素。
    private readonly ImageEngine _engine = new(new SoftwareBackend());
    private readonly TextStyle _style = new()
    {
        Font = FontRef.BuiltIn,
        Size = 22,
        Color = Rgba32.White,
    };

    //这个就是用来做处理的。尺寸大小变化 时重建
    private FacadeImage? _canvas;

    public void Compose(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        IntPtr destination,
        int destinationStride,
        TimeSpan position,
        double fps)
    {
        FacadeImage canvas = EnsureCanvas(width, height);
        canvas.ImportPixels(rgba, width * 4);

        canvas.FillRectangle(new RectF(12, 12, 214, 64), new Rgba32(0, 0, 0, 150));
        canvas.DrawText("时间 "+position.ToString(@"mm\:ss\.fff"), new PointF(20, 18), _style);
        canvas.DrawText($"fps {fps:0.0}", new PointF(20, 44), _style);
        canvas.CopyPixelsTo(destination, destinationStride, PixelFormat.Rgba32);
    }

    /// <summary>
    /// 把最近一帧（含 HUD）直接交给 Sdcb 软后端编码成 PNG。
    /// 画布本身就在软后端上，编码器直接从它的像素读，没有中转缓冲。
    /// </summary>
    public void SavePng(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FacadeImage canvas = _canvas ?? throw new InvalidOperationException("还没有可保存的帧。");
        canvas.SavePng(path);
    }

    private FacadeImage EnsureCanvas(int width, int height)
    {
        if (_canvas is { } existing && existing.Width == width && existing.Height == height)
        {
            return existing;
        }

        _canvas?.Dispose();
        _canvas = _engine.Create(width, height, Rgba32.Black);
        return _canvas;
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        _engine.Dispose();
    }
}
