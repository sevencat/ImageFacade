using ImageFacade.Backends;

namespace ImageFacade;

/// <summary>回退策略（plan §2.3）。</summary>
public enum FallbackMode
{
    /// <summary>不回退，缺能力直接抛 <see cref="FeatureNotSupportedException"/>。</summary>
    Disabled = 0,

    /// <summary>每次回退做一次"导出 → 软绘制 → 导入"，图始终留在原后端（默认）。</summary>
    PerOperation = 1,

    /// <summary>先按 PerOperation，累计回退达到阈值后整帧切到软后端并留下（视频流水线推荐）。</summary>
    Adaptive = 2,

    /// <summary>第一次回退就永久迁移到软后端。</summary>
    SwitchAndStay = 3,
}

/// <summary>一次回退事件的诊断信息（<see cref="ImageEngineOptions.OnFallback"/>）。</summary>
public readonly struct FallbackEvent
{
    public FallbackEvent(string fromBackend, string toBackend, string feature, double elapsedMilliseconds)
    {
        FromBackend = fromBackend;
        ToBackend = toBackend;
        Feature = feature;
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    public string FromBackend { get; }

    public string ToBackend { get; }

    public string Feature { get; }

    public double ElapsedMilliseconds { get; }
}

public sealed class ImageEngineOptions
{
    /// <summary>显式回退后端；null = 用已注册的软件后端工厂，不归引擎释放。</summary>
    public IImageBackend? FallbackBackend { get; init; }

    public FallbackMode Fallback { get; init; } = FallbackMode.PerOperation;

    /// <summary>Adaptive 模式下累计多少次回退后整帧切换到软后端。</summary>
    public int AdaptiveThreshold { get; init; } = 3;

    /// <summary>所有绘制都走软后端，保证各后端输出一致（编解码仍由原后端负责）。</summary>
    public bool PreferSoftwareRendering { get; init; }

    /// <summary>任何回退都视为错误（CI / 性能敏感服务）。</summary>
    public bool Strict { get; init; }

    public Action<FallbackEvent>? OnFallback { get; init; }

    internal ImageEngineOptions Validate()
    {
        if (AdaptiveThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(AdaptiveThreshold), AdaptiveThreshold, "AdaptiveThreshold 至少为 1。");
        }

        return this;
    }
}
