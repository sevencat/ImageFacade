namespace ImageFacade.Backends;

/// <summary>
/// 进阶像素租约：指向"规范格式（直通 RGBA32，顶行优先）"的可写像素。
/// 可能零拷贝指向后端内存，也可能是内部副本；<see cref="Dispose"/> 时写回 / 归还。
/// </summary>
public ref struct PixelLease
{
    private readonly Span<byte> _pixels;

    private readonly int _stride;

    private readonly Action? _onDispose;

    public PixelLease(Span<byte> pixels, int stride, Action? onDispose = null)
    {
        _pixels = pixels;
        _stride = stride;
        _onDispose = onDispose;
    }

    public Span<byte> Pixels => _pixels;

    public int Stride => _stride;

    public bool IsValid => _stride > 0;

    public void Dispose() => _onDispose?.Invoke();
}
