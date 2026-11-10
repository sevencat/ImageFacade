namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 笔刷：把覆盖率采样成颜色并混进画布。
/// <para>
/// <c>region</c> 是 coverage 块在画布上的位置，<c>coverage</c> 的每一行前 <c>region.Width</c> 字节有效，
/// 行间距是 <c>coverageStride</c>。实现只应写 <c>coverage &gt; 0</c> 的像素。
/// </para>
/// </summary>
internal interface IBrush
{
    /// <summary>纯色笔刷返回 <see langword="true"/>，纯色能命中「整行无 coverage 计算」的快路径。</summary>
    bool TryGetSolidColor(out Color color);

    /// <summary>按覆盖率把颜色混进画布（预乘 source-over）。</summary>
    void Blend(Canvas canvas, Rectangle region, ReadOnlySpan<byte> coverage, int coverageStride);
}

/// <summary>实心颜色笔刷。与 ImageSharp.Drawing 的 <c>SolidBrush</c> 对齐。</summary>
internal sealed class SolidBrush : IBrush
{
    public SolidBrush(Color color) => Color = color;

    public Color Color { get; }

    public bool TryGetSolidColor(out Color color)
    {
        color = Color;
        return true;
    }

    public void Blend(Canvas canvas, Rectangle region, ReadOnlySpan<byte> coverage, int coverageStride)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        for (int y = 0; y < region.Height; y++)
        {
            Span<byte> row = canvas.GetRow(region.Y + y).Slice(region.X * Canvas.BytesPerPixel, region.Width * Canvas.BytesPerPixel);
            ReadOnlySpan<byte> coverageRow = coverage.Slice(y * coverageStride, region.Width);
            RowBlender.OverSolid(row, Color, coverageRow);
        }
    }
}
