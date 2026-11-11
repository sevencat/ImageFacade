using System.Numerics;
using System.Text;
using ImageFacade.Text.Engine;

namespace ImageFacade;

public sealed partial class Font
{
    /// <summary>
    /// 排版文本：按码点取字形、累加前进宽度、按需换行。
    /// 中文、日文、韩文都是单码点单字形，这里不需要 HarfBuzz 级别的整形。
    /// </summary>
    internal TextLayout Layout(string text, float size, TextLayoutOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lineHeight = options.LineHeight > 0f ? options.LineHeight : GetLineHeight(size);
        var ascent = GetAscent(size);
        var maxWidth = options.MaxWidth;

        var glyphs = new List<PositionedGlyph>(text.Length);
        var lineWidths = new List<float>();
        var penX = 0f;
        var lineIndex = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var codePoint = rune.Value;
            if (codePoint == '\n')
            {
                lineWidths.Add(penX);
                penX = 0f;
                lineIndex++;
                continue;
            }

            if (codePoint == '\r')
            {
                continue;
            }

            if (codePoint == '\t')
            {
                var space = GetAdvance(' ', size) * 4f;
                if (maxWidth > 0f && penX > 0f && penX + space > maxWidth)
                {
                    lineWidths.Add(penX);
                    penX = 0f;
                    lineIndex++;
                }

                penX += space;
                continue;
            }

            var mask = GetGlyphMask(codePoint, size);

            if (maxWidth > 0f && penX > 0f && penX + mask.Advance > maxWidth)
            {
                lineWidths.Add(penX);
                penX = 0f;
                lineIndex++;
            }

            if (!mask.IsEmpty)
            {
                var baseline = ascent + (lineIndex * lineHeight);
                glyphs.Add(new PositionedGlyph(
                    mask,
                    (int)MathF.Round(penX) + mask.OffsetX,
                    (int)MathF.Round(baseline) + mask.OffsetY,
                    lineIndex));
            }

            penX += mask.Advance;
        }

        lineWidths.Add(penX);

        var widest = 0f;
        foreach (var lineWidth in lineWidths)
        {
            widest = MathF.Max(widest, lineWidth);
        }

        ApplyAlignment(glyphs, lineWidths, maxWidth, options.Align);

        var lineCount = lineWidths.Count;
        return new TextLayout(glyphs, widest, lineCount * lineHeight, ascent, lineHeight, lineCount, lineWidths);
    }

    /// <summary>测量单行（或多行取最宽行）的像素宽度。</summary>
    public float Measure(string text, float size)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Measure(text.AsSpan(), size);
    }

    /// <summary>
    /// 测量一段字符切片的像素宽度。与 <see cref="Measure(string, float)"/> 逐码点同一条路径
    /// （本仓库的托管字体栈不做字距调整，宽度就是字形 advance 之和），所以折行时量"段落前缀"
    /// 可以直接传切片，不必先切一份 string。
    /// </summary>
    public float Measure(ReadOnlySpan<char> text, float size)
    {
        var width = 0f;
        var widest = 0f;

        // 手写解码循环而不是 foreach：SpanRuneEnumerator 是实现了接口的 ref struct，
        // foreach 模式要用到 C# 13 的 "ref struct 接口" 特性（本仓库锁在 C# 12）。
        for (int i = 0; i < text.Length;)
        {
            int codePoint = text[i];
            int consumed = 1;
            if (char.IsHighSurrogate((char)codePoint)
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32((char)codePoint, text[i + 1]);
                consumed = 2;
            }
            else if (char.IsSurrogate((char)codePoint))
            {
                // 落单的代理项：与 Rune 语义一致地当成替换字符
                codePoint = 0xFFFD;
            }

            i += consumed;

            if (codePoint == '\n')
            {
                widest = MathF.Max(widest, width);
                width = 0f;
                continue;
            }

            if (codePoint == '\r')
            {
                continue;
            }

            if (codePoint == '\t')
            {
                width += GetAdvance(' ', size) * 4f;
                continue;
            }

            width += GetGlyphMask(codePoint, size).Advance;
        }

        return MathF.Max(widest, width);
    }

    /// <summary>
    /// 取整段文字的矢量路径集合：按与 <see cref="Layout"/> 相同的规则（<c>\n</c>、<c>\t</c>、
    /// <c>MaxWidth</c> 折行、<c>Align</c> 对齐）逐字平移后合并成一组路径。
    /// 坐标原点在首行基线左端，y 轴向下。
    /// </summary>
    internal PathCollection GetTextPaths(string text, float size, TextLayoutOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lineHeight = options.LineHeight > 0f ? options.LineHeight : GetLineHeight(size);
        var ascent = GetAscent(size);
        var maxWidth = options.MaxWidth;

        var paths = new List<(VectorPath Path, int Line)>(text.Length * 2);
        var lineWidths = new List<float>();
        var penX = 0f;
        var lineIndex = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var codePoint = rune.Value;
            if (codePoint == '\n')
            {
                lineWidths.Add(penX);
                penX = 0f;
                lineIndex++;
                continue;
            }

            if (codePoint == '\r')
            {
                continue;
            }

            if (codePoint == '\t')
            {
                var space = GetAdvance(' ', size) * 4f;
                if (maxWidth > 0f && penX > 0f && penX + space > maxWidth)
                {
                    lineWidths.Add(penX);
                    penX = 0f;
                    lineIndex++;
                }

                penX += space;
                continue;
            }

            var advance = GetAdvance(codePoint, size);
            if (maxWidth > 0f && penX > 0f && penX + advance > maxWidth)
            {
                lineWidths.Add(penX);
                penX = 0f;
                lineIndex++;
            }

            var glyphPaths = GetGlyphPaths(codePoint, size);
            if (glyphPaths.Count > 0)
            {
                var offset = Matrix3x2.CreateTranslation(penX, ascent + (lineIndex * lineHeight));
                foreach (var path in glyphPaths)
                {
                    paths.Add((path.Transform(offset), lineIndex));
                }
            }

            penX += advance;
        }

        lineWidths.Add(penX);

        var lineOffsets = ComputeLineOffsets(lineWidths, maxWidth, options.Align);
        var shifted = new List<VectorPath>(paths.Count);
        foreach (var (path, line) in paths)
        {
            var shift = (int)MathF.Round(lineOffsets[line]);
            shifted.Add(shift == 0 ? path : path.Transform(Matrix3x2.CreateTranslation(shift, 0f)));
        }

        return shifted.Count == 0 ? PathCollection.Empty : new PathCollection(shifted);
    }

    private static void ApplyAlignment(List<PositionedGlyph> glyphs, List<float> lineWidths, float maxWidth, TextAlign align)
    {
        if (align == TextAlign.Left || glyphs.Count == 0)
        {
            return;
        }

        var lineOffsets = ComputeLineOffsets(lineWidths, maxWidth, align);
        for (var i = 0; i < glyphs.Count; i++)
        {
            var glyph = glyphs[i];
            var shift = (int)MathF.Round(lineOffsets[glyph.Line]);
            if (shift != 0)
            {
                glyphs[i] = glyph with { X = glyph.X + shift };
            }
        }
    }

    /// <summary>
    /// 每行的水平偏移：以 <paramref name="maxWidth"/>（为 0 时用最宽行）为参照，
    /// 按 <paramref name="align"/> 计算左/中/右的位置。
    /// </summary>
    private static float[] ComputeLineOffsets(List<float> lineWidths, float maxWidth, TextAlign align)
    {
        var offsets = new float[lineWidths.Count];
        if (align == TextAlign.Left)
        {
            return offsets;
        }

        var target = maxWidth;
        if (target <= 0f)
        {
            foreach (var lineWidth in lineWidths)
            {
                target = MathF.Max(target, lineWidth);
            }
        }

        for (var i = 0; i < lineWidths.Count; i++)
        {
            var free = target - lineWidths[i];
            if (free <= 0f)
            {
                continue;
            }

            offsets[i] = align == TextAlign.Center ? free * 0.5f : free;
        }

        return offsets;
    }
}
