namespace ImageFacade.Pipeline;

/// <summary>检测框与标签的绘制样式。</summary>
public sealed class DetectionStyle
{
    /// <summary>框线宽（像素）。</summary>
    public float StrokeWidth { get; init; } = 2f;

    /// <summary>标签字号。</summary>
    public float FontSize { get; init; } = 14f;

    /// <summary>标签字体；默认内置字体（无系统字体也能画中文）。</summary>
    public FontRef Font { get; init; } = FontRef.BuiltIn;

    /// <summary>标签文字颜色。</summary>
    public Rgba32 LabelColor { get; init; } = Rgba32.White;

    /// <summary>标签底色；默认半透明黑。后端不支持 alpha 混合时需改成不透明（例如 OpenCV）。</summary>
    public Rgba32 LabelBackground { get; init; } = new(0, 0, 0, 160);

    /// <summary>标签是否带置信度（形如 <c>person 0.92</c>）。</summary>
    public bool ShowScore { get; init; } = true;

    /// <summary>标签文字与底色边缘的间距。</summary>
    public float LabelPadding { get; init; } = 3f;

    /// <summary>按 <see cref="Detection.ClassId"/> 取色的调色板。</summary>
    public IReadOnlyList<Rgba32> Palette { get; init; } = PaletteColors;

    /// <summary>自定义标签文本；为空时用 <c>"label score"</c>。</summary>
    public Func<Detection, string>? FormatLabel { get; init; }

    /// <summary>默认调色板（高对比度循环色）。</summary>
    public static IReadOnlyList<Rgba32> DefaultPalette => PaletteColors;

    private static readonly Rgba32[] PaletteColors =
    [
        Rgba32.FromHex(0xFF3838),
        Rgba32.FromHex(0xFF9D97),
        Rgba32.FromHex(0xFF701F),
        Rgba32.FromHex(0xFFB21D),
        Rgba32.FromHex(0xCFD231),
        Rgba32.FromHex(0x48F90A),
        Rgba32.FromHex(0x92CC17),
        Rgba32.FromHex(0x3DDB86),
        Rgba32.FromHex(0x1A9334),
        Rgba32.FromHex(0x00D4BB),
        Rgba32.FromHex(0x2C99A8),
        Rgba32.FromHex(0x00C2FF),
        Rgba32.FromHex(0x344593),
        Rgba32.FromHex(0x6473FF),
        Rgba32.FromHex(0x0018EC),
        Rgba32.FromHex(0x8438FF),
        Rgba32.FromHex(0x520085),
        Rgba32.FromHex(0xCB38FF),
        Rgba32.FromHex(0xFF95C8),
        Rgba32.FromHex(0xFF37C7),
    ];
}
