namespace ImageFacade.Pipeline;

/// <summary>一个检测结果：框（源图坐标）+ 类别 + 分数 + 可选颜色/标签。</summary>
public readonly struct Detection
{
    public Detection(RectF box, string? label = null, float score = 0f, Rgba32 color = default)
    {
        Box = box;
        Label = label;
        Score = score;
        Color = color;
        ClassId = 0;
    }

    /// <summary>源图坐标下的框。</summary>
    public RectF Box { get; }

    /// <summary>类别编号，仅供 NMS 分组与调色板取色；默认 0。</summary>
    public int ClassId { get; init; }

    /// <summary>类别名，可为空。</summary>
    public string? Label { get; }

    /// <summary>置信度，0..1。</summary>
    public float Score { get; }

    /// <summary>
    /// 显式颜色；<c>default</c>（全 0）表示交给 <see cref="DetectionStyle.Palette"/> 按
    /// <see cref="ClassId"/> 轮转。
    /// </summary>
    public Rgba32 Color { get; }

    public override string ToString()
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Label ?? "?"} {Score:0.00} {Box} class={ClassId}");
}
