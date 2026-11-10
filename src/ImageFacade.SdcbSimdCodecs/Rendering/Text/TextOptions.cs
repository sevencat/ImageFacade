namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 文字绘制选项。与 ImageSharp.Drawing 的 <c>TextOptions</c> 概念一致，
/// 但 v1 只保留本库确实支持的部分（无字体族回退、无 hinting 开关）。
/// </summary>
internal sealed class TextOptions
{
    /// <summary>字体，默认内置文泉驿微米黑（首次访问解压 2 MB 资源）。</summary>
    public Font Font { get; init; } = Font.BuiltIn;

    /// <summary>字号（em 高度，像素），默认 16。</summary>
    public float Size { get; init; } = 16f;

    /// <summary>纯色填充色；<see cref="Brush"/> 非空时被忽略。</summary>
    public Color Color { get; init; } = Color.Black;

    /// <summary>笔刷（渐变等）；为 <see langword="null"/> 时用 <see cref="Color"/> 的纯色笔刷。</summary>
    public IBrush? Brush { get; init; }

    /// <summary>最大行宽（像素），0 表示只按 <c>\n</c> 断行。</summary>
    public float MaxWidth { get; init; }

    /// <summary>行高（像素），0 表示用字体默认行高。</summary>
    public float LineHeight { get; init; }

    /// <summary>行内对齐方式。</summary>
    public TextAlign Align { get; init; }
}
