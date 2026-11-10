using System.Numerics;
using ImageFacade.Text.Engine;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 文字绘制与测量。两条路径：
/// <list type="bullet">
/// <item>快速路径 <see cref="DrawText"/>：字形掩码（A8）直接当 coverage，与形状共用同一套混合内核。</item>
/// <item>慢路径 <see cref="FillText"/>：字形轮廓转成核心 <see cref="Path"/> 后走通用光栅化，便于后续做变换/描边。</item>
/// </list>
/// </summary>
internal static class TextDrawing
{
    /// <summary>在 <paramref name="location"/>（首行基线左端）处绘制文本。</summary>
    public static void DrawText(this DrawingContext context, string text, PointF location, TextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(text);
        options ??= new TextOptions();

        if (!context.Transform.IsIdentity)
        {
            // 字形掩码是「轴对齐位图」，旋转/缩放/斜切下不能直接用；转成轮廓后会跟着变换走
            FillText(context, options.Brush ?? new SolidBrush(options.Color), text, location, options);
            return;
        }

        TextLayout layout = options.Font.Layout(text, options.Size, CreateLayoutOptions(options));
        if (layout.Glyphs.Count == 0)
        {
            return;
        }

        int originX = (int)MathF.Round(location.X);
        int originY = (int)MathF.Round(location.Y);

        if (!TryGetRegion(layout, originX, originY, context.Canvas, out Rectangle region))
        {
            return;
        }

        using var coverage = new CoverageBuffer(region.Width, region.Height);
        BlendGlyphs(layout, originX, originY, region, coverage);

        IBrush brush = options.Brush ?? new SolidBrush(options.Color);
        context.BlendCoverage(brush, region, coverage);
    }

    /// <summary>
    /// 用矢量轮廓绘制文本（慢路径）：每个字形转成 <see cref="Path"/> 后走通用填充。
    /// <para>
    /// <see cref="DrawText"/> 在 <see cref="DrawingContext.Transform"/> 非恒等时会自动改走这里；
    /// 想给文字描边（<c>Draw(pen, GetTextPath(...))</c>）也走这条路径。
    /// </para>
    /// </summary>
    public static void FillText(this DrawingContext context, IBrush brush, string text, PointF location, TextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(text);
        options ??= new TextOptions();

        PathCollection collection = options.Font.GetTextPaths(text, options.Size, CreateLayoutOptions(options));
        if (collection.Count == 0)
        {
            return;
        }

        Path path = GlyphPathBridge.ToPath(collection);
        context.Fill(brush, path.Transform(Matrix3x2.CreateTranslation(location.X, location.Y)));
    }

    /// <summary>
    /// 把文本排成一条轮廓路径（<paramref name="location"/> 是首行基线左端）。
    /// 拿到路径后可以 <c>Fill</c>、<c>Draw</c> 描边，或自己 <c>Transform</c>。
    /// </summary>
    public static Path GetTextPath(string text, PointF location, TextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new TextOptions();

        PathCollection collection = options.Font.GetTextPaths(text, options.Size, CreateLayoutOptions(options));
        Path path = GlyphPathBridge.ToPath(collection);
        return (Path)path.Transform(Matrix3x2.CreateTranslation(location.X, location.Y));
    }

    /// <summary>测量文本：宽度是最宽行的前进宽度，高度是行数 × 行高。</summary>
    public static SizeF MeasureText(this Font font, string text, float size, float maxWidth = 0f)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        TextLayout layout = font.Layout(text, size, new TextLayoutOptions { MaxWidth = maxWidth });
        return new SizeF(layout.Width, layout.Height);
    }

    private static TextLayoutOptions CreateLayoutOptions(TextOptions options) => new()
    {
        MaxWidth = options.MaxWidth,
        LineHeight = options.LineHeight,
        Align = options.Align,
    };

    private static bool TryGetRegion(TextLayout layout, int originX, int originY, Canvas canvas, out Rectangle region)
    {
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;

        foreach (PositionedGlyph glyph in layout.Glyphs)
        {
            int left = originX + glyph.X;
            int top = originY + glyph.Y;
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, left + glyph.Mask.Width);
            maxY = Math.Max(maxY, top + glyph.Mask.Height);
        }

        if (maxX <= minX || maxY <= minY)
        {
            region = Rectangle.Empty;
            return false;
        }

        region = Rectangle.Intersect(
            new Rectangle(minX, minY, maxX - minX, maxY - minY),
            new Rectangle(0, 0, canvas.Width, canvas.Height));

        return !region.IsEmpty;
    }

    private static void BlendGlyphs(TextLayout layout, int originX, int originY, Rectangle region, CoverageBuffer coverage)
    {
        byte[] buffer = coverage.Data;
        int stride = coverage.Stride;

        foreach (PositionedGlyph glyph in layout.Glyphs)
        {
            GlyphMask mask = glyph.Mask;
            int left = originX + glyph.X;
            int top = originY + glyph.Y;

            int minCol = Math.Max(0, region.X - left);
            int maxCol = Math.Min(mask.Width, region.Right - left);
            int minRow = Math.Max(0, region.Y - top);
            int maxRow = Math.Min(mask.Height, region.Bottom - top);
            if (maxCol <= minCol || maxRow <= minRow)
            {
                continue;
            }

            for (int row = minRow; row < maxRow; row++)
            {
                ReadOnlySpan<byte> source = mask.Coverage.AsSpan((row * mask.Width) + minCol, maxCol - minCol);
                Span<byte> destination = buffer.AsSpan(
                    ((top + row - region.Y) * stride) + (left + minCol - region.X),
                    maxCol - minCol);

                // 字形重叠时取最大值：同一墨水不被叠加两次，避免描边发黑
                for (int i = 0; i < source.Length; i++)
                {
                    if (source[i] > destination[i])
                    {
                        destination[i] = source[i];
                    }
                }
            }
        }
    }
}
