using System.Buffers;
using System.Runtime.InteropServices;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 轴对齐矩形的填充快路径（plan §6.4.1）。
/// <para>
/// 与通用扫描线光栅化**逐字节一致**：内部整行直接按覆盖率 255 混合，只有边界行按
/// 「4 条子扫描线命中几条」×「横向覆盖」计算，和 <see cref="ScanlineRasterizer"/> 的
/// <c>(Σ sub-coverage + 2) / 4</c> 完全同构。
/// </para>
/// </summary>
internal static class RectangleFill
{
    private const int SubSamples = 4;

    public static void Fill(Canvas canvas, RectF rect, Color color)
    {
        float left = MathF.Max(rect.Left, 0f);
        float top = MathF.Max(rect.Top, 0f);
        float right = MathF.Min(rect.Right, canvas.Width);
        float bottom = MathF.Min(rect.Bottom, canvas.Height);

        if (right <= left || bottom <= top)
        {
            return;
        }

        int firstColumn = (int)MathF.Floor(left);
        int lastColumn = (int)MathF.Ceiling(right) - 1;
        int firstRow = (int)MathF.Floor(top);
        int lastRow = (int)MathF.Ceiling(bottom) - 1;
        int span = lastColumn - firstColumn + 1;

        byte[] full = ArrayPool<byte>.Shared.Rent(span);
        byte[] partial = ArrayPool<byte>.Shared.Rent(span);

        try
        {
            full.AsSpan(0, span).Fill(255);

            for (int y = firstRow; y <= lastRow; y++)
            {
                int coveredSubSamples = CountCoveredSubSamples(top, bottom, y);
                if (coveredSubSamples == 0)
                {
                    continue;
                }

                Span<byte> destination = canvas.GetRow(y)
                    .Slice(firstColumn * Canvas.BytesPerPixel, span * Canvas.BytesPerPixel);

                // 整行全覆盖：连 coverage 都不用读，直接把 255 行喂给混合内核
                if (coveredSubSamples == SubSamples && left <= firstColumn && right >= lastColumn + 1)
                {
                    // 不透明纯色 + 整行全覆盖：source-over 退化为覆盖写，跳过逐像素混合
                    if (color.A == 255)
                    {
                        MemoryMarshal.Cast<byte, uint>(destination).Fill(Canvas.PackPremultiplied(color));
                        continue;
                    }

                    // 半透明纯色整行：覆盖率是常量 255，走少一半除法的专用内核
                    RowBlender.OverSolidFullRow(destination, color);
                    continue;
                }

                Span<byte> coverage = partial.AsSpan(0, span);
                coverage.Clear();
                for (int x = firstColumn; x <= lastColumn; x++)
                {
                    int horizontal = HorizontalCoverage(left, right, x);
                    if (horizontal == 0)
                    {
                        continue;
                    }

                    coverage[x - firstColumn] = (byte)(((coveredSubSamples * horizontal) + (SubSamples / 2)) / SubSamples);
                }

                RowBlender.OverSolid(destination, color, coverage);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(full);
            ArrayPool<byte>.Shared.Return(partial);
        }
    }

    private static int CountCoveredSubSamples(float top, float bottom, int row)
    {
        int covered = 0;
        for (int i = 0; i < SubSamples; i++)
        {
            float y = row + ((i + 0.5f) / SubSamples);
            if (y >= top && y < bottom)
            {
                covered++;
            }
        }

        return covered;
    }

    private static int HorizontalCoverage(float left, float right, int column)
    {
        float overlapLeft = MathF.Max(left, column);
        float overlapRight = MathF.Min(right, column + 1);
        if (overlapRight <= overlapLeft)
        {
            return 0;
        }

        return (int)(((overlapRight - overlapLeft) * 255f) + 0.5f);
    }
}
