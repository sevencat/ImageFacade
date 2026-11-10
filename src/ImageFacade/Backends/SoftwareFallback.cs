namespace ImageFacade.Backends;

/// <summary>
/// 软后端的注册点。<c>ImageFacade.Software</c> 通过 <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/>
/// 在程序集加载时注册工厂；**没有引用它就没有回退**（plan §5.5）。
/// </summary>
public static class SoftwareFallback
{
    private static Func<IImageBackend>? s_factory;

    public static bool IsRegistered => Volatile.Read(ref s_factory) is not null;

    /// <summary>注册软后端工厂（后注册的覆盖先注册的）。</summary>
    public static void Register(Func<IImageBackend> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Volatile.Write(ref s_factory, factory);
    }

    public static void Unregister() => Volatile.Write(ref s_factory, null);

    internal static IImageBackend? TryCreate() => Volatile.Read(ref s_factory)?.Invoke();
}
