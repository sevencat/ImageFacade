namespace ImageFacade.Internal;

/// <summary>
/// 同步 → 异步的默认桥接：后端没实现 <c>IAsyncImageBackend</c> 时，用线程池跑同步实现。
/// </summary>
/// <remarks>
/// 取消只在**开始前**生效（CPU 密集的同步解码/编码无法中途打断）。
/// 需要真正的取消语义就实现 <c>IAsyncImageBackend</c>。
/// </remarks>
internal static class AsyncBridge
{
    public static Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        return Task.Run(work, cancellationToken);
    }

    public static Task Run(Action work, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return Task.Run(work, cancellationToken);
    }
}
