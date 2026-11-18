using PointF = ImageFacade.PointF;
using RectF = ImageFacade.RectF;

namespace ImageFacade.AvaloniaDemo;

/// <summary>
/// 一帧的合成：直通 RGBA32 进 → ImageFacade 画 HUD → 直通 Bgra32 写进目标缓冲。
/// 画布只在尺寸变化时重建，之后每帧用 <see cref="FacadeImage.ImportPixels"/> 覆盖像素。
/// 目标可以是 Avalonia 的 <c>ILockedFramebuffer.Address</c>，也可以是临时内存（自检用）。
/// </summary>
internal sealed class FrameComposer : IDisposable
{
    private readonly ImageEngine _engine = new();
    private readonly TextStyle _style = new()
    {
        Font = FontRef.BuiltIn,
        Size = 22,
        Color = Rgba32.White,
    };

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
        canvas.CopyPixelsTo(destination, destinationStride, PixelFormat.Bgra32);
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
