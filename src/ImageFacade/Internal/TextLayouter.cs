using ImageFacade.Backends;

namespace ImageFacade.Internal;

internal readonly struct TextLine
{
    public TextLine(string text, float width, float topY, float offsetX)
    {
        Text = text;
        Width = width;
        TopY = topY;
        OffsetX = offsetX;
    }

    public string Text { get; }

    public float Width { get; }

    /// <summary>相对块左上角的纵向偏移（第 n 行 = n × 行高）。</summary>
    public float TopY { get; }

    /// <summary>对齐产生的横向偏移。</summary>
    public float OffsetX { get; }
}

internal sealed class TextLayoutResult
{
    public TextLayoutResult(List<TextLine> lines, SizeF size)
    {
        Lines = lines;
        Size = size;
    }

    public IReadOnlyList<TextLine> Lines { get; }

    /// <summary>排版框尺寸：宽 = MaxWidth（为 0 时用最宽行），高 = 行数 × 行高。</summary>
    public SizeF Size { get; }
}

/// <summary>
/// 折行 / 对齐 / 行距在门面层算（plan §3.4 / misc §J.4）：后端只负责"画一行"，
/// 所以 <see cref="FacadeImage.MeasureText"/> 与 <see cref="FacadeImage.DrawText"/> 用同一套排版结果。
/// </summary>
internal static class TextLayouter
{
    public static TextLayoutResult Layout(FacadeImage image, string text, TextStyle style)
    {
        var cache = new Dictionary<string, TextMetrics>(StringComparer.Ordinal);

        TextMetrics Measure(string value)
        {
            if (cache.TryGetValue(value, out TextMetrics cached))
            {
                return cached;
            }

            TextMetrics metrics = image.MeasureSingleLine(value, style);
            cache[value] = metrics;
            return metrics;
        }

        var rawLines = new List<string>();
        foreach (string rawParagraph in text.Split('\n'))
        {
            string paragraph = rawParagraph.TrimEnd('\r');
            if (style.MaxWidth <= 0f)
            {
                rawLines.Add(paragraph);
                continue;
            }

            Wrap(paragraph, style.MaxWidth, Measure, rawLines);
        }

        if (rawLines.Count == 0)
        {
            rawLines.Add(string.Empty);
        }

        float lineHeight = style.LineHeight > 0f ? style.LineHeight : Measure(rawLines[0]).LineHeight;
        if (lineHeight <= 0f)
        {
            lineHeight = style.Size * 1.2f;
        }

        var widths = new float[rawLines.Count];
        float maxLineWidth = 0f;
        for (int i = 0; i < rawLines.Count; i++)
        {
            widths[i] = Measure(rawLines[i]).Width;
            maxLineWidth = MathF.Max(maxLineWidth, widths[i]);
        }

        float blockWidth = style.MaxWidth > 0f ? style.MaxWidth : maxLineWidth;

        var lines = new List<TextLine>(rawLines.Count);
        for (int i = 0; i < rawLines.Count; i++)
        {
            float offsetX = style.Align switch
            {
                TextAlign.Center => (blockWidth - widths[i]) / 2f,
                TextAlign.Right => blockWidth - widths[i],
                _ => 0f,
            };

            lines.Add(new TextLine(rawLines[i], widths[i], i * lineHeight, offsetX));
        }

        return new TextLayoutResult(lines, new SizeF(blockWidth, rawLines.Count * lineHeight));
    }

    private static void Wrap(string paragraph, float maxWidth, Func<string, TextMetrics> measure, List<string> output)
    {
        if (paragraph.Length == 0)
        {
            output.Add(string.Empty);
            return;
        }

        int start = 0;
        while (start < paragraph.Length)
        {
            int end = start + 1;
            int lastBreak = -1;
            int chosen = -1;

            while (end <= paragraph.Length)
            {
                if (end > start && IsBreakOpportunity(paragraph, end))
                {
                    lastBreak = end;
                }

                string candidate = paragraph[start..end];
                if (end - start > 1 && measure(candidate).Width > maxWidth)
                {
                    chosen = lastBreak > start
                        ? lastBreak
                        : (lastBreak == start ? start + 1 : end - 1);
                    break;
                }

                end++;
            }

            if (chosen < 0)
            {
                chosen = paragraph.Length;
            }

            string line = paragraph[start..chosen].TrimEnd(' ');
            output.Add(line);
            start = chosen;

            // 行首的空格不再参与排版
            while (start < paragraph.Length && paragraph[start] == ' ')
            {
                start++;
            }
        }
    }

    /// <summary>能否在 <paramref name="index"/> 处断行（CJK 逐字断、拉丁按空格断）。</summary>
    private static bool IsBreakOpportunity(string text, int index)
    {
        if (index <= 0 || index >= text.Length)
        {
            return true;
        }

        char previous = text[index - 1];
        char current = text[index];
        return previous == ' ' || current == ' ' || IsCjk(previous) || IsCjk(current);
    }

    private static bool IsCjk(char c)
        => c is >= (char)0x2E80 and <= (char)0x9FFF
        || c is >= (char)0xF900 and <= (char)0xFAFF
        || c is >= (char)0xFF00 and <= (char)0xFFEF;
}
