using ImageFacade.SdcbSimdCodecs;
using PointF = ImageFacade.PointF;
using RectF = ImageFacade.RectF;

namespace ImageFacade.AvaloniaDemo;

/// <summary>
/// 一帧的合成：视频帧本身就是画布，HUD 直接画上去，再整块写进目标缓冲。
/// 目标可以是 Avalonia 的 <c>ILockedFramebuffer.Address</c>，也可以是临时内存（自检用）。
/// </summary>
internal sealed unsafe class FrameComposer : IDisposable
{
    // 只在保存 PNG 时用到：编解码挂在这个软后端上，导回来的画布能直接 SavePng。
    // 逐帧渲染不经过它——帧属于 VideoPlayer 自己的核心光栅引擎。
    private readonly ImageEngine _engine = new(new SoftwareBackend());
    private readonly TextStyle _style = new()
    {
        Font = FontRef.BuiltIn,
        Size = 22,
        Color = Rgba32.White,
    };

    private FacadeImage? _canvas;

    /// <summary>在视频帧上叠 HUD，然后把结果整块写进目标缓冲（直通 RGBA32）。</summary>
    public void Compose(
        FacadeImage frame,
        IntPtr destination,
        int destinationStride,
        TimeSpan position,
        double fps)
    {
        ArgumentNullException.ThrowIfNull(frame);

        frame.FillRectangle(new RectF(12, 12, 214, 64), new Rgba32(0, 0, 0, 150));
        frame.DrawText("时间 " + position.ToString(@"mm\:ss\.fff"), new PointF(20, 18), _style);
        frame.DrawText($"fps {fps:0.0}", new PointF(20, 44), _style);
        frame.CopyPixelsTo(destination, destinationStride, PixelFormat.Rgba32);
    }

    /// <summary>
    /// 把已经渲染好的画面（Avalonia framebuffer / 临时内存）导回一张软后端画布再编码成 PNG。
    /// 帧归还池之后随时会被解码线程改写，所以保存必须基于目标缓冲，而不是视频帧。
    /// </summary>
    public void SavePng(IntPtr rgba, int stride, int width, int height, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (rgba == IntPtr.Zero)
        {
            throw new ArgumentException("目标缓冲为空。", nameof(rgba));
        }

        FacadeImage canvas = EnsureCanvas(width, height);
        canvas.ImportPixels(new ReadOnlySpan<byte>((void*)rgba, checked(stride * height)), stride);
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
