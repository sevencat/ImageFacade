namespace ImageFacade;

/// <summary>第三个后端抛出的异常统一包成它（保留 <see cref="Exception.InnerException"/>）。</summary>
public class ImageBackendException : Exception
{
    public ImageBackendException(string backendName, string message, Exception? innerException = null)
        : base($"[{backendName}] {message}", innerException)
    {
        BackendName = backendName;
    }

    public string BackendName { get; }
}

/// <summary>
/// 后端不具备该能力，且没有可用的回退后端（或 <see cref="FallbackMode.Disabled"/> / Strict 模式）。
/// </summary>
public sealed class FeatureNotSupportedException : NotSupportedException
{
    public FeatureNotSupportedException(string feature, string backendName, FallbackMode fallback)
        : base(BuildMessage(feature, backendName, fallback))
    {
        Feature = feature;
        BackendName = backendName;
        Fallback = fallback;
    }

    public string Feature { get; }

    public string BackendName { get; }

    public FallbackMode Fallback { get; }

    private static string BuildMessage(string feature, string backendName, FallbackMode fallback)
    {
        string hint = fallback switch
        {
            FallbackMode.Disabled => "当前 Fallback = Disabled（严格模式），不会自动回退。",
            _ => "没有可用的回退后端：引用 ImageFacade.Software，或在 ImageEngineOptions.FallbackBackend 里显式指定一个。",
        };

        return $"后端 '{backendName}' 不支持能力 '{feature}'。{hint}";
    }
}
