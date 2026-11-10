using System.Globalization;

namespace ImageFacade.Pipeline;

/// <summary>把检测结果画到图上：框 + 标签胶囊。</summary>
/// <remarks>
/// 这是**默认实现**，不是唯一实现：需要特殊排版时自己循环
/// <see cref="FacadeImage.DrawRectangle"/> + <see cref="FacadeImage.FillRectangle"/> +
/// <see cref="FacadeImage.DrawText"/> 即可。
/// 标签宽度用 <see cref="FacadeImage.MeasureText"/> 算，因此与当前后端自洽。
/// </remarks>
public static class DetectionDrawing
{
    private static readonly DetectionStyle DefaultStyle = new();

    /// <summary>画框与标签；输入是**源图坐标**（先用 <see cref="LetterboxInfo.ToSource(RectF)"/> 反算）。</summary>
    public static FacadeImage DrawDetections(
        this FacadeImage image,
        ReadOnlySpan<Detection> detections,
        DetectionStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        style ??= DefaultStyle;
        if (style.Palette.Count == 0)
        {
            throw new ArgumentException("DetectionStyle.Palette 不能为空。", nameof(style));
        }

        var textStyle = new TextStyle
        {
            Font = style.Font,
            Size = style.FontSize,
            Color = style.LabelColor,
        };

        Dictionary<string, SizeF>? measured = null;

        foreach (Detection detection in detections)
        {
            image.DrawRectangle(detection.Box, ResolveColor(detection, style), style.StrokeWidth);

            string text = style.FormatLabel?.Invoke(detection) ?? Format(detection, style);
            if (text.Length == 0)
            {
                continue;
            }

            measured ??= new Dictionary<string, SizeF>(StringComparer.Ordinal);
            if (!measured.TryGetValue(text, out SizeF size))
            {
                size = image.MeasureText(text, textStyle);
                measured[text] = size;
            }

            float padding = style.LabelPadding;
            float width = size.Width + (padding * 2f);
            float height = size.Height + (padding * 2f);

            // 标签默认贴在框的上沿外侧；贴到画布外时改放框内。
            float top = detection.Box.Top - height;
            if (top < 0f)
            {
                top = detection.Box.Top;
            }

            var labelRect = new RectF(detection.Box.Left, top, width, height);
            image.FillRectangle(labelRect, style.LabelBackground);
            image.DrawText(text, new PointF(labelRect.X + padding, labelRect.Y + padding), textStyle);
        }

        return image;
    }

    private static Rgba32 ResolveColor(in Detection detection, DetectionStyle style)
    {
        Rgba32 color = detection.Color;
        if (color.A != 0 || color.R != 0 || color.G != 0 || color.B != 0)
        {
            return color;
        }

        int index = detection.ClassId % style.Palette.Count;
        if (index < 0)
        {
            index += style.Palette.Count;
        }

        return style.Palette[index];
    }

    private static string Format(in Detection detection, DetectionStyle style)
    {
        string label = detection.Label ?? string.Empty;
        if (!style.ShowScore || detection.Score <= 0f)
        {
            return label;
        }

        string score = detection.Score.ToString("0.00", CultureInfo.InvariantCulture);
        return label.Length == 0 ? score : string.Concat(label, " ", score);
    }
}
