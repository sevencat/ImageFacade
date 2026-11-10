namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 重采样核。与 ImageSharp 的 <c>IResampler</c> 同形：给出支撑半径与核函数值，
/// 归一化、边界处理由 <see cref="Resizer"/> 统一负责，自定义核只关心形状。
/// </summary>
internal interface IResampler
{
    /// <summary>核的支撑半径（以源像素为单位）。窗口宽度约为 <c>2 * Radius</c>。</summary>
    float Radius { get; }

    /// <summary>在 <paramref name="x"/> 处的核值；<paramref name="x"/> 已按缩放比例归一化。</summary>
    float GetValue(float x);
}

/// <summary>
/// 内置重采样核，命名与 ImageSharp 的 <c>KnownResamplers</c> 对齐。
/// 缩小图像时 <see cref="Resizer"/> 会按比例拉宽核，等效于先低通再采样。
/// </summary>
internal static class KnownResamplers
{
    /// <summary>最近邻：最快、有锯齿，适合像素画放大。</summary>
    public static IResampler NearestNeighbour { get; } = new NearestNeighbourResampler();

    /// <summary>盒式：与最近邻接近，缩小时表现为区域平均。</summary>
    public static IResampler Box { get; } = new BoxResampler();

    /// <summary>双线性（三角核）。</summary>
    public static IResampler Bilinear { get; } = new TriangleResampler();

    /// <summary>双三次（Catmull-Rom，a = -0.5），默认核。</summary>
    public static IResampler Bicubic { get; } = new CubicResampler(0f, 0.5f);

    /// <summary>Catmull-Rom，与 <see cref="Bicubic"/> 相同（保留别名以对齐 ImageSharp）。</summary>
    public static IResampler CatmullRom { get; } = Bicubic;

    /// <summary>Mitchell-Netravali（B = C = 1/3），比 Catmull-Rom 更柔和。</summary>
    public static IResampler MitchellNetravali { get; } = new CubicResampler(1f / 3f, 1f / 3f);

    /// <summary>Lanczos2：锐度与 ringing 的折中。</summary>
    public static IResampler Lanczos2 { get; } = new LanczosResampler(2);

    /// <summary>Lanczos3：缩小时最锐利，代价是有轻微 ringing。</summary>
    public static IResampler Lanczos3 { get; } = new LanczosResampler(3);
}

internal sealed class NearestNeighbourResampler : IResampler
{
    public float Radius => 0.5f;

    public float GetValue(float x) => MathF.Abs(x) <= 0.5f ? 1f : 0f;
}

internal sealed class BoxResampler : IResampler
{
    public float Radius => 0.5f;

    public float GetValue(float x) => MathF.Abs(x) <= 0.5f ? 1f : 0f;
}

internal sealed class TriangleResampler : IResampler
{
    public float Radius => 1f;

    public float GetValue(float x)
    {
        float ax = MathF.Abs(x);
        return ax < 1f ? 1f - ax : 0f;
    }
}

internal sealed class CubicResampler : IResampler
{
    private readonly float _b;
    private readonly float _c;

    internal CubicResampler(float b, float c)
    {
        _b = b;
        _c = c;
    }

    public float Radius => 2f;

    public float GetValue(float x)
    {
        float ax = MathF.Abs(x);
        float ax2 = ax * ax;
        float ax3 = ax2 * ax;

        if (ax < 1f)
        {
            return ((12f - (9f * _b) - (6f * _c)) * ax3
                    + ((-18f + (12f * _b) + (6f * _c)) * ax2)
                    + (6f - (2f * _b))) / 6f;
        }

        if (ax < 2f)
        {
            return ((-_b - (6f * _c)) * ax3
                    + ((6f * _b) + (30f * _c)) * ax2
                    + ((-12f * _b) - (48f * _c)) * ax
                    + (8f * _b) + (24f * _c)) / 6f;
        }

        return 0f;
    }
}

internal sealed class LanczosResampler : IResampler
{
    private readonly float _support;

    internal LanczosResampler(float support) => _support = support;

    public float Radius => _support;

    public float GetValue(float x)
    {
        float ax = MathF.Abs(x);
        if (ax == 0f)
        {
            return 1f;
        }

        if (ax >= _support)
        {
            return 0f;
        }

        return _support * MathF.Sin(MathF.PI * ax) * MathF.Sin(MathF.PI * ax / _support)
               / (MathF.PI * MathF.PI * ax * ax);
    }
}
