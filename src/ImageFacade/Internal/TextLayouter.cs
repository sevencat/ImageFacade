using System.Buffers;
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
    /// <summary>
    /// 二分搜索最多记多少条探针。段落长度是 <see cref="int"/>，最多折叠 32 次，
    /// 所以 32 个槽位不可能写满（用来检查"宽度随长度单调不减"是否被实测数据推翻）。
    /// </summary>
    private const int MaxProbes = 32;

    public static TextLayoutResult Layout(FacadeImage image, string text, TextStyle style)
    {
        // 行文本 + 已经量出来的宽度一一对应：折行时每个候选前缀只量一次，度量直接带出来，
        // 不再靠 Dictionary<string, TextMetrics> 缓存——那个缓存在折行路径上全是"只写不读"的条目，
        // 每个候选前缀都要为它付一次 O(长度) 的哈希 / 比较，还要把每个前缀字符串一直钉在字典里
        // （长段落等于按前缀长度累加一遍，O(n²) 的字符串常驻）。
        var rawLines = new List<string>();
        var rawWidths = new List<float>();

        // 第一行的完整度量顺手带出来：行高（style.LineHeight 未指定时）不必再单独量一遍
        TextMetrics? firstLineMetrics = null;

        foreach (string rawParagraph in text.Split('\n'))
        {
            string paragraph = rawParagraph.TrimEnd('\r');
            if (style.MaxWidth <= 0f)
            {
                TextMetrics metrics = image.MeasureSingleLine(paragraph, style);
                if (rawLines.Count == 0)
                {
                    firstLineMetrics = metrics;
                }

                rawLines.Add(paragraph);
                rawWidths.Add(metrics.Width);
                continue;
            }

            // 两条路径只差"断点怎么找"：Exact 逐长度线性扫描（默认，与旧版本一致），
            // Monotonic 用二分把测量次数从 O(n²) 降到 O(n log n)。行生成 / 行宽 / 行高共用 WrapCore。
            if (style.WrapMode == TextWrapMode.Monotonic)
            {
                WrapMonotonic(image, paragraph, style, rawLines, rawWidths, ref firstLineMetrics);
            }
            else
            {
                Wrap(image, paragraph, style, rawLines, rawWidths, ref firstLineMetrics);
            }
        }

        if (rawLines.Count == 0)
        {
            rawLines.Add(string.Empty);
            rawWidths.Add(0f);
        }

        float lineHeight = style.LineHeight > 0f
            ? style.LineHeight
            : (firstLineMetrics ?? image.MeasureSingleLine(rawLines[0], style)).LineHeight;
        if (lineHeight <= 0f)
        {
            lineHeight = style.Size * 1.2f;
        }

        float maxLineWidth = 0f;
        for (int i = 0; i < rawWidths.Count; i++)
        {
            maxLineWidth = MathF.Max(maxLineWidth, rawWidths[i]);
        }

        float blockWidth = style.MaxWidth > 0f ? style.MaxWidth : maxLineWidth;

        var lines = new List<TextLine>(rawLines.Count);
        for (int i = 0; i < rawLines.Count; i++)
        {
            float offsetX = style.Align switch
            {
                TextAlign.Center => (blockWidth - rawWidths[i]) / 2f,
                TextAlign.Right => blockWidth - rawWidths[i],
                _ => 0f,
            };

            lines.Add(new TextLine(rawLines[i], rawWidths[i], i * lineHeight, offsetX));
        }

        return new TextLayoutResult(lines, new SizeF(blockWidth, rawLines.Count * lineHeight));
    }

    /// <summary>
    /// 折行（<see cref="TextWrapMode.Exact"/>，默认路径）。**逐字符结果必须与引入宽度缓存之前完全一致**：
    /// 查找"第一个超宽前缀"仍然是按长度递增线性扫描，只去掉两处原来的无用功——
    /// 一是长度 1 的候选也会量一遍，而溢出判定要求至少 2 个字符，那次结果从头到尾没用过；
    /// 二是每个候选前缀都往 Dictionary 里塞一份（哈希 + 比较 + 把前缀字符串长期留在字典里），
    /// 现在度量只写进一个按终点下标索引的池化数组，行文本本身仍只在真正产出一行时才分配。
    /// </summary>
    private static void Wrap(
        FacadeImage image,
        string paragraph,
        TextStyle style,
        List<string> output,
        List<float> widths,
        ref TextMetrics? firstMetrics)
        => WrapCore(image, paragraph, style, output, widths, ref firstMetrics, monotonic: false);

    /// <summary>
    /// 折行（<see cref="TextWrapMode.Monotonic"/>）：断点改由二分搜索
    /// （<see cref="FindLineEndMonotonic"/>）给出，其余规则——空白裁剪、<c>\n</c> 硬断行、
    /// 超长单词、行宽、行高、末尾空行——与 <see cref="Wrap"/> 走同一个 <see cref="WrapCore"/>，
    /// 所以两条路径只可能在"折行位置"上不同。
    /// </summary>
    private static void WrapMonotonic(
        FacadeImage image,
        string paragraph,
        TextStyle style,
        List<string> output,
        List<float> widths,
        ref TextMetrics? firstMetrics)
        => WrapCore(image, paragraph, style, output, widths, ref firstMetrics, monotonic: true);

    /// <summary>
    /// 两条折行路径共用的行生成：断点由 <paramref name="monotonic"/> 选择的搜索器给出，
    /// 之后的行文本切片、<see cref="string.TrimEnd(char)"/>、行首空格跳过、行度量复用与
    /// "首行度量带出行高"全部只在这里实现一次，所以规则天然一致。
    /// </summary>
    private static void WrapCore(
        FacadeImage image,
        string paragraph,
        TextStyle style,
        List<string> output,
        List<float> widths,
        ref TextMetrics? firstMetrics,
        bool monotonic)
    {
        if (paragraph.Length == 0)
        {
            output.Add(string.Empty);
            widths.Add(0f);
            return;
        }

        float maxWidth = style.MaxWidth;

        // prefixMetrics[end] = 当前行 [start, end) 的度量；只覆盖本次实际量过的终点。
        TextMetrics[] prefixMetrics = ArrayPool<TextMetrics>.Shared.Rent(paragraph.Length + 1);
        try
        {
            int start = 0;
            while (start < paragraph.Length)
            {
                bool chosenWasMeasured;
                int chosen = monotonic
                    ? FindLineEndMonotonic(image, paragraph, start, maxWidth, style, prefixMetrics, out chosenWasMeasured)
                    : FindLineEndLinear(image, paragraph, start, maxWidth, style, prefixMetrics, out chosenWasMeasured);

                string line = paragraph[start..chosen];
                string trimmed = line.TrimEnd(' ');

                // 没有空格被裁掉、且这个前缀确实已经量过时，行的度量可以直接复用，不必再量一遍
                TextMetrics lineMetrics = chosenWasMeasured && trimmed.Length == line.Length && chosen - start > 1
                    ? prefixMetrics[chosen]
                    : image.MeasureSingleLine(trimmed, style);

                if (output.Count == 0)
                {
                    firstMetrics = lineMetrics;
                }

                output.Add(trimmed);
                widths.Add(lineMetrics.Width);
                start = chosen;

                // 行首的空格不再参与排版
                while (start < paragraph.Length && paragraph[start] == ' ')
                {
                    start++;
                }
            }
        }
        finally
        {
            ArrayPool<TextMetrics>.Shared.Return(prefixMetrics);
        }
    }

    /// <summary>
    /// <see cref="TextWrapMode.Exact"/> 的断点搜索：按长度递增扫描到"第一个超宽前缀"，
    /// 断点回退规则与引入池化数组之前逐字符一致。返回的终点是否已经量过由
    /// <paramref name="chosenWasMeasured"/> 带出（供 <see cref="WrapCore"/> 决定能否复用度量）。
    /// </summary>
    private static int FindLineEndLinear(
        FacadeImage image,
        string paragraph,
        int start,
        float maxWidth,
        TextStyle style,
        TextMetrics[] prefixMetrics,
        out bool chosenWasMeasured)
    {
        int end = start + 1;
        int lastBreak = -1;
        int chosen = -1;

        while (end <= paragraph.Length)
        {
            // end == start + 1 也要判一次断点机会：下面 lastBreak == start 的分支依赖它。
            if (IsBreakOpportunity(paragraph, end))
            {
                lastBreak = end;
            }

            if (end - start > 1)
            {
                TextMetrics metrics = MeasurePrefix(image, paragraph, start, end, style);
                prefixMetrics[end] = metrics;
                if (metrics.Width > maxWidth)
                {
                    chosen = lastBreak > start
                        ? lastBreak
                        : (lastBreak == start ? start + 1 : end - 1);
                    break;
                }
            }

            end++;
        }

        if (chosen < 0)
        {
            chosen = paragraph.Length;
        }

        // 线性路径里长度 > 1 的终点一定在上面的循环里量过（长度 1 从不测量）
        chosenWasMeasured = chosen - start > 1;
        return chosen;
    }

    /// <summary>
    /// <see cref="TextWrapMode.Monotonic"/> 的断点搜索：**假设同一行内前缀宽度随长度单调不减**，
    /// 在 [2, 剩余长度] 上二分"第一个超宽前缀"，再往回找最靠后的断点机会；断点回退判定式与
    /// <see cref="FindLineEndLinear"/> 完全一致。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 假设不成立时（混合字体回退、连字、kerning 密集的文本），只要探针之间出现"更长的前缀反而更窄"，
    /// 就把**整行**退回 <see cref="FindLineEndLinear"/> 重扫，结果与 <see cref="TextWrapMode.Exact"/>
    /// 逐字符相同；不抛异常，也不会交出半个结果。代价是最坏情况比线性扫描多 O(log n) 次测量。
    /// </para>
    /// <para>
    /// 探针没覆盖到的位置无法判断宽度是否单调，这正是"可选路径"的边界：需要与旧版本严格一致时，
    /// 请用默认的 <see cref="TextWrapMode.Exact"/>。
    /// </para>
    /// </remarks>
    private static int FindLineEndMonotonic(
        FacadeImage image,
        string paragraph,
        int start,
        float maxWidth,
        TextStyle style,
        TextMetrics[] prefixMetrics,
        out bool chosenWasMeasured)
    {
        int length = paragraph.Length - start;
        if (length <= 1)
        {
            chosenWasMeasured = false;
            return paragraph.Length;
        }

        // 探针长度 / 宽度各 32 个槽位：int 长度的二分最多 32 次折叠，不可能写满
        Span<int> probeLengths = stackalloc int[MaxProbes];
        Span<float> probeWidths = stackalloc float[MaxProbes];
        int probeCount = 0;
        int firstOver = -1;

        int lo = 2;
        int hi = length;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            TextMetrics metrics = MeasurePrefix(image, paragraph, start, start + mid, style);
            prefixMetrics[start + mid] = metrics;

            if (!RecordProbe(probeLengths, probeWidths, ref probeCount, mid, metrics.Width))
            {
                // 实测已经推翻单调假设：整行退回线性扫描，结果与旧 Wrap 逐字符一致
                return FindLineEndLinear(image, paragraph, start, maxWidth, style, prefixMetrics, out chosenWasMeasured);
            }

            if (metrics.Width > maxWidth)
            {
                firstOver = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        if (firstOver < 0)
        {
            // 二分的最后一次探针一定是 length 本身 → 整段都装得下，度量可以直接复用
            chosenWasMeasured = true;
            return paragraph.Length;
        }

        // 与线性扫描同一个断点回退规则：取 [start+1, start+firstOver] 里最靠后的断点机会
        int lastBreak = -1;
        for (int index = start + firstOver; index > start; index--)
        {
            if (IsBreakOpportunity(paragraph, index))
            {
                lastBreak = index;
                break;
            }
        }

        int chosen = lastBreak > start
            ? lastBreak
            : (lastBreak == start ? start + 1 : start + firstOver - 1);

        chosenWasMeasured = chosen == start + firstOver;
        return chosen;
    }

    /// <summary>
    /// 记下一个探针，并检查它是否与已有探针矛盾（更长的前缀反而更窄，或更短的前缀反而更宽）。
    /// 返回 false 表示"宽度随长度单调不减"的假设已经被实测数据推翻。
    /// </summary>
    private static bool RecordProbe(Span<int> lengths, Span<float> widths, ref int count, int length, float width)
    {
        for (int i = 0; i < count; i++)
        {
            bool contradicts = length > lengths[i] ? width < widths[i] : width > widths[i];
            if (contradicts)
            {
                return false;
            }
        }

        if (count < lengths.Length)
        {
            lengths[count] = length;
            widths[count] = width;
            count++;
        }

        return true;
    }

    /// <summary>量 [start, end) 这一段的度量。测量 API 只吃 string，切片不可避免地要分配一份（SPI 不动）。</summary>
    private static TextMetrics MeasurePrefix(FacadeImage image, string paragraph, int start, int end, TextStyle style)
        => image.MeasureSingleLine(paragraph[start..end], style);

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