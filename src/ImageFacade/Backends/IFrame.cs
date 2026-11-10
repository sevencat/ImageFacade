namespace ImageFacade.Backends;

/// <summary>
/// 后端私有的图像句柄。门面只透传、绝不看内部结构；一帧一实例，不跨线程。
/// </summary>
public interface IFrame : IDisposable
{
    int Width { get; }

    int Height { get; }
}
