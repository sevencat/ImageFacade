namespace ImageFacade.Backends;

/// <summary>异步解码结果。</summary>
public readonly struct DecodedImage
{
    public DecodedImage(IFrame frame, ImageMetadata metadata)
    {
        Frame = frame;
        Metadata = metadata;
    }

    public IFrame Frame { get; }

    public ImageMetadata Metadata { get; }
}

/// <summary>
/// **可选**的异步 SPI：只有当后端真的能异步（原生库给了异步 API、或内部能并行分块）时才实现它。
/// </summary>
/// <remarks>
/// <para>
/// 没实现这个接口的后端不需要改动：门面会用 <see cref="Task.Run(Action, CancellationToken)"/>
/// 把同步实现包成异步（默认回退策略）。
/// </para>
/// <para>
/// 约定：实现里不要自己再套 <c>Task.Run</c>；取消令牌要尽量往下传，
/// 至少要在开始和结束前检查一次。
/// </para>
/// </remarks>
public interface IAsyncImageBackend : IImageBackend
{
    Task<DecodedImage> DecodeAsync(
        ReadOnlyMemory<byte> data,
        DecodeRequest request,
        CancellationToken cancellationToken);

    Task EncodeAsync(
        IFrame frame,
        Stream destination,
        EncodeRequest request,
        CancellationToken cancellationToken);
}
