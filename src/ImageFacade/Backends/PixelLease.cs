namespace ImageFacade.Backends;

/// <summary>
/// 进阶像素租约：指向"规范格式（直通 RGBA32，顶行优先）"的可写像素。
/// 可能零拷贝指向后端内存，也可能是内部副本；<see cref="Dispose"/> 时写回 / 归还。
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> 可重复调用：第二次起是空操作。后端回调里要归还池化缓冲的
/// （例如软后端），必须依赖这一点，否则重复释放会把同一块缓冲还回 <c>ArrayPool</c> 两次，
/// 池子随后可能把它同时交给两个租用者。
/// </remarks>
public ref struct PixelLease
{
    private readonly Span<byte> _pixels;

    private readonly int _stride;

    private readonly Action? _onDispose;

    private bool _disposed;

    public PixelLease(Span<byte> pixels, int stride, Action? onDispose = null)
    {
        _pixels = pixels;
        _stride = stride;
        _onDispose = onDispose;
        _disposed = false;
    }

    public Span<byte> Pixels => _pixels;

    public int Stride => _stride;

    public bool IsValid => _stride > 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _onDispose?.Invoke();
    }
}
