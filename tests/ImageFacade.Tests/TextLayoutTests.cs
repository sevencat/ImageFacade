using ImageFacade.Backends;
using ImageFacade.Internal;
using Xunit;

namespace ImageFacade.Tests;

public sealed class TextLayoutTests
{
    [Fact]
    public void Wraps_At_MaxWidth()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native);
        using FacadeImage image = engine.Create(100, 100, Rgba32.White);

        var style = new TextStyle { Size = 10, MaxWidth = 25 };
        SizeF size = image.MeasureText("abcdefghij", style);

        Assert.Equal(25f, size.Width);
        Assert.Equal(20f, size.Height);   // 2 行 × 行高 10
    }

    [Fact]
    public void Wraps_Chinese_Per_Character()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native);
        using FacadeImage image = engine.Create(100, 100, Rgba32.White);

        var style = new TextStyle { Size = 10, MaxWidth = 25 };
        SizeF size = image.MeasureText("一二三四五六七八九十", style);

        Assert.Equal(25f, size.Width);
        Assert.Equal(20f, size.Height);
    }

    [Fact]
    public void Hard_Newlines_Are_Preserved()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native);
        using FacadeImage image = engine.Create(100, 100, Rgba32.White);

        SizeF size = image.MeasureText("a\nbb", new TextStyle { Size = 10 });

        Assert.Equal(10f, size.Width);
        Assert.Equal(20f, size.Height);
    }

    [Fact]
    public void DrawText_Center_Align_Offsets_By_Half()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native);
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.DrawText("ab", PointF.Empty, new TextStyle
        {
            Size = 10,
            MaxWidth = 20,
            Align = TextAlign.Center,
        });

        Assert.Equal(Rgba32.White, image.GetPixel(1, 3));
        Assert.Equal(Rgba32.Black, image.GetPixel(6, 3));
        Assert.Equal(Rgba32.White, image.GetPixel(16, 3));
    }

    [Fact]
    public void DrawText_Baseline_Anchor_Subtracts_Ascent()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native);
        using FacadeImage image = engine.Create(40, 40, Rgba32.White);

        image.DrawText("ab", new PointF(0, 20), new TextStyle
        {
            Size = 10,
            Anchor = TextAnchor.Baseline,
        });

        // ascent = 0.8 × 字号 = 8 → 墨迹落在 y = 12..22
        Assert.Equal(Rgba32.White, image.GetPixel(0, 11));
        Assert.Equal(Rgba32.Black, image.GetPixel(0, 13));
    }

    [Fact]
    public void MultiLine_Uses_Explicit_LineHeight()
    {
        var native = new FakeBackend { SupportsText = true };
        using var engine = new ImageEngine(native);
        using FacadeImage image = engine.Create(40, 80, Rgba32.White);

        image.DrawText("a\nb", PointF.Empty, new TextStyle { Size = 10, LineHeight = 20 });

        Assert.Equal(Rgba32.Black, image.GetPixel(0, 3));
        Assert.Equal(Rgba32.White, image.GetPixel(0, 13));
        Assert.Equal(Rgba32.Black, image.GetPixel(0, 23));
    }

    // ---- 折行结果必须与旧实现逐字符一致（planv2 第 18 条 / §10.3） ----

    /// <summary>
    /// 差分测试：把这套实现和一比一照抄的旧算法放在**同一套测量函数**下跑同一个语料。
    /// 替身的宽度对前缀长度故意**不单调**（后面的字符可能更"窄"），所以"第一个超宽前缀"
    /// 这类需要逐长度推进的判定一旦被换成二分 / 跳跃扫描，这里必然炸。
    /// </summary>
    [Fact]
    public void Wrap_Result_Matches_Reference_Implementation()
    {
        using var engine = new ImageEngine(new NonMonotoneMeasureBackend());
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        const string pool = "ab c一二三 四五六\r\n.; ";
        string[] samples =
        [
            "",
            "a",
            "ab",
            "abcdefghij",
            "hello world, this is a fairly long latin sentence",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "一二三四五六七八九十",
            "中英混排 mixed 文本 with  多个空格 ",
            "   leading and trailing   ",
            "a  b   c    d",
            "line1\nline2\n\nline4",
            "crlf\r\nsecond\r\n",
            " \n \r\n",
            "supercalifragilisticexpialidocious",
        ];

        float[] limits = [0f, 1f, 5f, 10f, 25f, 37f, 100f, 1000f];

        var cases = new List<(string Text, float MaxWidth)>();
        foreach (string sample in samples)
        {
            foreach (float limit in limits)
            {
                cases.Add((sample, limit));
            }
        }

        var random = new Random(20261111);
        for (int i = 0; i < 400; i++)
        {
            int length = random.Next(0, 41);
            var buffer = new char[length];
            for (int j = 0; j < length; j++)
            {
                buffer[j] = pool[random.Next(pool.Length)];
            }

            cases.Add((new string(buffer), limits[random.Next(limits.Length)]));
        }

        for (int index = 0; index < cases.Count; index++)
        {
            (string text, float maxWidth) = cases[index];

            // 显式行高与"用字体度量"（LineHeight = 0）两条路径都要走一遍
            foreach (float lineHeight in new[] { 12f, 0f })
            {
                var style = new TextStyle { Size = 10, MaxWidth = maxWidth, LineHeight = lineHeight };

                TextLayoutResult actual = TextLayouter.Layout(image, text, style);
                (List<string> expectedLines, List<float> expectedWidths, SizeF expectedSize)
                    = ReferenceLayout(text, style, NonMonotoneMeasureBackend.WidthOf);

                string label = $"case #{index}: maxWidth={maxWidth} lineHeight={lineHeight} text='{Escape(text)}'";
                Assert.True(expectedLines.Count == actual.Lines.Count, $"{label}：行数不同");

                for (int i = 0; i < expectedLines.Count; i++)
                {
                    Assert.True(expectedLines[i] == actual.Lines[i].Text, $"{label}：第 {i} 行文本不同");
                    Assert.True(expectedWidths[i] == actual.Lines[i].Width, $"{label}：第 {i} 行宽度不同");
                }

                Assert.True(expectedSize.Width == actual.Size.Width, $"{label}：排版宽度不同");
                Assert.True(expectedSize.Height == actual.Size.Height, $"{label}：排版高度不同");
            }
        }
    }

    /// <summary>长度 1 的候选从前也要量一次，但溢出判定要求至少 2 个字符——那次测量从头到尾没用过。</summary>
    [Fact]
    public void Wrap_Does_Not_Measure_One_Character_Candidates()
    {
        var backend = new NonMonotoneMeasureBackend();
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        // 显式 LineHeight 让布局不再为行高单独量一次；"abcdefghij" 一行放得下，
        // 于是只有长度 2..10 这 9 个前缀需要测量（旧实现是 1..10 共 10 次）。
        // 这个替身**只重写了 string 版**，量的是接口默认实现把切片复制成 string 之后的结果。
        image.MeasureText("abcdefghij", new TextStyle { Size = 10, MaxWidth = 1000, LineHeight = 12 });

        Assert.Equal(9, backend.TextMeasureCalls);
    }

    /// <summary>折行候选走的是切片版测量：数量与走 string 的路径一致，且一次 string 版都不调用。</summary>
    [Fact]
    public void Wrap_Measures_Prefixes_Through_Slice_Overload()
    {
        var backend = new SliceMeasureBackend();
        using var engine = new ImageEngine(backend);
        using FacadeImage image = engine.Create(4, 4, Rgba32.White);

        image.MeasureText("abcdefghij", new TextStyle { Size = 10, MaxWidth = 1000, LineHeight = 12 });

        Assert.Equal(9, backend.SliceMeasureCalls);
        Assert.Equal(0, backend.StringMeasureCalls);
    }

    /// <summary>重写切片版与没重写（走默认实现的 string 回退）两条路，折行结果逐行相同。</summary>
    [Fact]
    public void Wrap_Result_Is_Identical_With_And_Without_Slice_Overload()
    {
        var slice = new SliceMeasureBackend();
        using var sliceEngine = new ImageEngine(slice);
        using FacadeImage sliceImage = sliceEngine.Create(4, 4, Rgba32.White);

        var fallback = new NonMonotoneMeasureBackend();
        using var fallbackEngine = new ImageEngine(fallback);
        using FacadeImage fallbackImage = fallbackEngine.Create(4, 4, Rgba32.White);

        string[] texts =
        [
            "abcdefghij",
            "abc def ghi jkl",
            "一二三四五六七八九十",
            "中文 mixed English 混排",
            "a b  c   d",
        ];

        foreach (string text in texts)
        {
            foreach (float maxWidth in new[] { 8f, 17f, 25f, 60f })
            {
                var style = new TextStyle { Size = 10, MaxWidth = maxWidth };

                TextLayoutResult expected = TextLayouter.Layout(fallbackImage, text, style);
                TextLayoutResult actual = TextLayouter.Layout(sliceImage, text, style);

                string label = $"text='{text}' maxWidth={maxWidth}";
                Assert.True(expected.Lines.Count == actual.Lines.Count, $"{label}：行数不同");

                for (int i = 0; i < expected.Lines.Count; i++)
                {
                    Assert.True(expected.Lines[i].Text == actual.Lines[i].Text, $"{label}：第 {i} 行文本不同");
                    Assert.True(expected.Lines[i].Width == actual.Lines[i].Width, $"{label}：第 {i} 行宽度不同");
                }

                Assert.True(expected.Size.Width == actual.Size.Width, $"{label}：排版宽度不同");
                Assert.True(expected.Size.Height == actual.Size.Height, $"{label}：排版高度不同");
            }
        }
    }

    private static string Escape(string text)
        => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>旧实现的逐行结果（照抄 planv2 §10.3 改动前的算法，只保留产出行文本的部分）。</summary>
    private static (List<string> Lines, List<float> Widths, SizeF Size) ReferenceLayout(
        string text,
        TextStyle style,
        Func<string, float, float> measure)
    {
        var lines = new List<string>();
        foreach (string rawParagraph in text.Split('\n'))
        {
            string paragraph = rawParagraph.TrimEnd('\r');
            if (style.MaxWidth <= 0f)
            {
                lines.Add(paragraph);
                continue;
            }

            ReferenceWrap(paragraph, style.MaxWidth, style.Size, measure, lines);
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        var widths = new List<float>(lines.Count);
        float maxWidth = 0f;
        foreach (string line in lines)
        {
            float width = measure(line, style.Size);
            widths.Add(width);
            maxWidth = MathF.Max(maxWidth, width);
        }

        // 替身的行高 = ascent(0.8×size) + descent(0.2×size) = size（旧实现同样是量首行取 LineHeight）
        float lineHeight = style.LineHeight > 0f ? style.LineHeight : style.Size;
        float blockWidth = style.MaxWidth > 0f ? style.MaxWidth : maxWidth;
        return (lines, widths, new SizeF(blockWidth, lines.Count * lineHeight));
    }

    private static void ReferenceWrap(
        string paragraph,
        float maxWidth,
        float size,
        Func<string, float, float> measure,
        List<string> output)
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
                if (end - start > 1 && measure(candidate, size) > maxWidth)
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

            output.Add(paragraph[start..chosen].TrimEnd(' '));
            start = chosen;

            while (start < paragraph.Length && paragraph[start] == ' ')
            {
                start++;
            }
        }
    }

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

    /// <summary>宽度对前缀长度**不单调**的替身：同一行里更长的前缀可能更窄。</summary>
    private sealed class NonMonotoneMeasureBackend : FakeBackend
    {
        public NonMonotoneMeasureBackend()
            : base("fake-nonmonotone")
        {
            SupportsText = true;
        }

        public int TextMeasureCalls { get; private set; }

        public static float WidthOf(string text, float size)
        {
            float width = 0f;
            foreach (char c in text)
            {
                width += (((c * 37) % 11) - 3) * 0.5f * size;
            }

            return width;
        }

        public static float WidthOfSlice(ReadOnlySpan<char> text, float size)
        {
            float width = 0f;
            foreach (char c in text)
            {
                width += (((c * 37) % 11) - 3) * 0.5f * size;
            }

            return width;
        }

        public override bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
        {
            TextMeasureCalls++;
            float size = spec.Style.Size;
            metrics = new TextMetrics(WidthOf(spec.Text, size), size * 0.8f, size * 0.2f);
            return true;
        }
    }

    /// <summary>
    /// 实现了切片版测量的替身：宽度算法与 <see cref="NonMonotoneMeasureBackend"/> 完全相同。
    /// <para>
    /// 这里必须**重新列出** <see cref="IImageBackend"/>：<see cref="FakeBackend"/> 已经实现了该接口，
    /// 派生类里新加的成员不会自动进接口映射表（默认接口方法的槽位会一直留在基类的映射里），
    /// 只有再次声明"我实现这个接口"才会把切片版接上。
    /// </para>
    /// </summary>
    private sealed class SliceMeasureBackend : FakeBackend, IImageBackend
    {
        public SliceMeasureBackend()
            : base("fake-slice")
        {
            SupportsText = true;
        }

        public int SliceMeasureCalls { get; private set; }

        public int StringMeasureCalls { get; private set; }

        public override bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
        {
            StringMeasureCalls++;
            float size = spec.Style.Size;
            metrics = new TextMetrics(NonMonotoneMeasureBackend.WidthOf(spec.Text, size), size * 0.8f, size * 0.2f);
            return true;
        }

        /// <summary>
        /// <see cref="IImageBackend"/> 的切片版是默认接口方法，类里**不能** override，只能实现它
        /// （实现了就顶替默认实现，不再走 <c>new string(...)</c>）。
        /// </summary>
        public bool TryMeasureText(IFrame frame, ReadOnlySpan<char> text, in TextStyle style, out TextMetrics metrics)
        {
            SliceMeasureCalls++;
            float size = style.Size;
            metrics = new TextMetrics(NonMonotoneMeasureBackend.WidthOfSlice(text, size), size * 0.8f, size * 0.2f);
            return true;
        }
    }
}
