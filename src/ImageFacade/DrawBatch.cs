namespace ImageFacade;

/// <summary>
/// 批量绘制作用域：由 <see cref="FacadeImage.BeginDraw"/> 返回，<c>Dispose</c> 时把批量软帧一次性写回。
/// </summary>
/// <remarks>
/// <para>
/// 作用域内第一次触发回退时，整帧只导出到软后端一次；之后的绘制（包括原生后端本来就能做的）
/// 全部留在那个软帧上按原顺序执行，结束时只写回一次。画 N 个需要回退的元素时，
/// 全图往返从 N 次降到 1 次。
/// </para>
/// <para>
/// 用 <see cref="FacadeImage.BeginDraw"/> 的 <c>flushOnNative</c> 参数可以让每个绘制操作先写回、
/// 再尝试原生，适合"少量中文标签 + 大量原生绘制"的混合场景。
/// </para>
/// </remarks>
public sealed class DrawBatch : IDisposable
{
    private readonly FacadeImage _image;
    private bool _disposed;

    internal DrawBatch(FacadeImage image) => _image = image;

    /// <summary>被批量的图；直接在这上面继续调用 <c>Draw*</c>。</summary>
    public FacadeImage Image => _image;

    /// <summary>
    /// 把批量软帧的内容写回当前后端（一次全图拷贝）。作用域仍然有效，后续绘制可再次触发回退。
    /// </summary>
    public void Flush()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DrawBatch));
        }

        _image.FlushBatchScope();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _image.EndBatchScope();
    }
}
