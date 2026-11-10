namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 分离式（先水平后垂直）重采样实现。只在预乘空间做插值——直通空间对半透明像素插值会产生色边，
/// 这是内部统一用预乘的主要收益之一（misc §D）。
/// </summary>
internal static class Resizer
{
    internal static Canvas Resize(Canvas source, ResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Sampler is null)
        {
            throw new ArgumentException("ResizeOptions.Sampler 不能为 null。", nameof(options));
        }

        if (options.Compand)
        {
            throw new NotSupportedException("v1 不支持在线性光空间重采样（ResizeOptions.Compand = true）。");
        }

        if (options.Size.Width <= 0 || options.Size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Size, "目标尺寸必须为正。");
        }

        Layout layout = ResolveLayout(source, options);

        if (layout.Image.Width == source.Width && layout.Image.Height == source.Height)
        {
            // 尺寸没变：不重采样，避免白丢一次精度
            return Place(source.Clone(), layout);
        }

        Canvas scaled = ScaleCore(source, layout.Image.Width, layout.Image.Height, options.Sampler);
        return Place(scaled, layout);
    }

    private static Canvas Place(Canvas scaled, Layout layout)
    {
        if (layout.Canvas.Width == scaled.Width && layout.Canvas.Height == scaled.Height)
        {
            return scaled;
        }

        Canvas target = Canvas.Create(layout.Canvas.Width, layout.Canvas.Height, layout.PadColor);
        Blit(scaled, target, layout.OffsetX, layout.OffsetY);
        scaled.Dispose();
        return target;
    }

    private static void Blit(Canvas source, Canvas target, int x, int y)
    {
        int sx0 = Math.Max(0, -x);
        int sy0 = Math.Max(0, -y);
        int sx1 = Math.Min(source.Width, target.Width - x);
        int sy1 = Math.Min(source.Height, target.Height - y);
        int count = sx1 - sx0;
        if (count <= 0 || sy1 <= sy0)
        {
            return;
        }

        int bytes = count * Canvas.BytesPerPixel;
        for (int sy = sy0; sy < sy1; sy++)
        {
            source.GetRow(sy).Slice(sx0 * Canvas.BytesPerPixel, bytes)
                  .CopyTo(target.GetRow(y + sy).Slice((x + sx0) * Canvas.BytesPerPixel, bytes));
        }
    }

    private readonly struct Layout
    {
        public Layout(Size canvas, Size image, int offsetX, int offsetY, Color padColor)
        {
            Canvas = canvas;
            Image = image;
            OffsetX = offsetX;
            OffsetY = offsetY;
            PadColor = padColor;
        }

        /// <summary>最终画布尺寸。</summary>
        public Size Canvas { get; }

        /// <summary>重采样后的图像尺寸（可能大于或小于画布）。</summary>
        public Size Image { get; }

        public int OffsetX { get; }

        public int OffsetY { get; }

        public Color PadColor { get; }
    }

    private static Layout ResolveLayout(Canvas source, ResizeOptions options)
    {
        Size box = options.Size;
        float scaleX = (float)box.Width / source.Width;
        float scaleY = (float)box.Height / source.Height;

        switch (options.Mode)
        {
            case ResizeMode.Stretch:
                return new Layout(box, box, 0, 0, options.PadColor);

            case ResizeMode.Max:
            case ResizeMode.Min:
            {
                float scale = options.Mode == ResizeMode.Max
                    ? MathF.Min(scaleX, scaleY)
                    : MathF.Max(scaleX, scaleY);
                Size image = ScaleSize(source, scale);
                return new Layout(image, image, 0, 0, options.PadColor);
            }

            case ResizeMode.Crop:
            {
                float scale = MathF.Max(scaleX, scaleY);
                Size image = ScaleSize(source, scale);
                return new Layout(box, image, (box.Width - image.Width) / 2, (box.Height - image.Height) / 2, options.PadColor);
            }

            case ResizeMode.Pad:
            case ResizeMode.BoxPad:
            {
                float scale = MathF.Min(scaleX, scaleY);
                if (options.Mode == ResizeMode.BoxPad)
                {
                    scale = MathF.Min(scale, 1f);
                }

                Size image = ScaleSize(source, scale);
                return new Layout(box, image, (box.Width - image.Width) / 2, (box.Height - image.Height) / 2, options.PadColor);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "未知的 ResizeMode。");
        }
    }

    private static Size ScaleSize(Canvas source, float scale)
    {
        return new Size(
            Math.Max(1, (int)MathF.Round(source.Width * scale)),
            Math.Max(1, (int)MathF.Round(source.Height * scale)));
    }

    /// <summary>中间缓冲的目标预算；超出就按目标行分带，避免 4K 缩放一次性占几十 MB。</summary>
    private const int IntermediateBudgetBytes = 8 * 1024 * 1024;

    private static Canvas ScaleCore(Canvas source, int width, int height, IResampler sampler)
        => ScaleCore(source, width, height, sampler, IntermediateBudgetBytes);

    internal static Canvas ScaleCore(Canvas source, int width, int height, IResampler sampler, int budgetBytes)
    {
        if (sampler is NearestNeighbourResampler)
        {
            return ScaleNearest(source, width, height);
        }

        KernelTable horizontal = KernelTable.Build(source.Width, width, sampler);
        KernelTable vertical = KernelTable.Build(source.Height, height, sampler);

        int sourceHeight = source.Height;
        int rowFloats = width * Canvas.BytesPerPixel;

        // 一条目标行最多要看多少源行：分带至少要装得下它，否则会跨带反复重算
        int maxWindow = 1;
        for (int y = 0; y < height; y++)
        {
            maxWindow = Math.Max(maxWindow, MaxSourceRow(vertical, y) - MinSourceRow(vertical, y) + 1);
        }

        int bandRows = Math.Max(1, Math.Min(sourceHeight, budgetBytes / (rowFloats * sizeof(float))));
        bandRows = Math.Max(bandRows, Math.Min(maxWindow, sourceHeight));

        long intermediateLength = (long)bandRows * rowFloats;
        if (intermediateLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"中间缓冲需要 {intermediateLength * sizeof(float) / (1024 * 1024)} MB，超出单数组上限；请分块缩小。");
        }

        var intermediate = new float[(int)intermediateLength];
        Canvas result = Canvas.Create(width, height);

        int destinationY = 0;
        while (destinationY < height)
        {
            int bandStart = MinSourceRow(vertical, destinationY);
            int bandEnd = Math.Min(
                sourceHeight,
                Math.Max(bandStart + bandRows, MaxSourceRow(vertical, destinationY) + 1));

            // 水平趟：只算本带，顺序访问，缓存友好
            for (int y = bandStart; y < bandEnd; y++)
            {
                ReadOnlySpan<byte> row = source.GetRow(y);
                int rowBase = (y - bandStart) * rowFloats;
                for (int x = 0; x < width; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    int start = horizontal.Starts[x];
                    int end = start + horizontal.Counts[x];
                    for (int k = start; k < end; k++)
                    {
                        int offset = horizontal.Indices[k] * Canvas.BytesPerPixel;
                        float weight = horizontal.Weights[k];
                        r += row[offset] * weight;
                        g += row[offset + 1] * weight;
                        b += row[offset + 2] * weight;
                        a += row[offset + 3] * weight;
                    }

                    int destination = rowBase + (x * Canvas.BytesPerPixel);
                    intermediate[destination] = r;
                    intermediate[destination + 1] = g;
                    intermediate[destination + 2] = b;
                    intermediate[destination + 3] = a;
                }
            }

            // 垂直趟：只处理窗口完全落在本带的目标行，累加顺序与整图缓冲时一致
            while (destinationY < height && MaxSourceRow(vertical, destinationY) < bandEnd)
            {
                Span<byte> destination = result.GetRow(destinationY);
                int start = vertical.Starts[destinationY];
                int end = start + vertical.Counts[destinationY];

                for (int x = 0; x < width; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int k = start; k < end; k++)
                    {
                        int offset = (((vertical.Indices[k] - bandStart) * rowFloats) + (x * Canvas.BytesPerPixel));
                        float weight = vertical.Weights[k];
                        r += intermediate[offset] * weight;
                        g += intermediate[offset + 1] * weight;
                        b += intermediate[offset + 2] * weight;
                        a += intermediate[offset + 3] * weight;
                    }

                    byte alpha = ToByte(a);
                    int o = x * Canvas.BytesPerPixel;
                    bool clampToAlpha = alpha != 255;

                    // 负权重核（Lanczos）可能让通道越界；预乘像素必须满足 rgb <= a
                    destination[o] = ToByte(r, clampToAlpha ? alpha : 255);
                    destination[o + 1] = ToByte(g, clampToAlpha ? alpha : 255);
                    destination[o + 2] = ToByte(b, clampToAlpha ? alpha : 255);
                    destination[o + 3] = alpha;
                }

                destinationY++;
            }
        }

        return result;
    }

    private static int MinSourceRow(KernelTable table, int index) => table.Indices[table.Starts[index]];

    private static int MaxSourceRow(KernelTable table, int index)
        => table.Indices[table.Starts[index] + table.Counts[index] - 1];

    private static Canvas ScaleNearest(Canvas source, int width, int height)
    {
        Canvas result = Canvas.Create(width, height);
        int[] sourceX = BuildNearestMap(source.Width, width);
        for (int y = 0; y < height; y++)
        {
            int sy = NearestIndex(source.Height, height, y);
            ReadOnlySpan<byte> row = source.GetRow(sy);
            Span<byte> destination = result.GetRow(y);
            for (int x = 0; x < width; x++)
            {
                row.Slice(sourceX[x] * Canvas.BytesPerPixel, Canvas.BytesPerPixel)
                   .CopyTo(destination.Slice(x * Canvas.BytesPerPixel, Canvas.BytesPerPixel));
            }
        }

        return result;
    }

    private static int[] BuildNearestMap(int sourceLength, int destinationLength)
    {
        var map = new int[destinationLength];
        for (int i = 0; i < destinationLength; i++)
        {
            map[i] = NearestIndex(sourceLength, destinationLength, i);
        }

        return map;
    }

    private static int NearestIndex(int sourceLength, int destinationLength, int index)
    {
        double center = ((index + 0.5) * sourceLength / destinationLength) - 0.5;
        int source = (int)Math.Floor(center + 0.5);
        return Math.Clamp(source, 0, sourceLength - 1);
    }

    private static byte ToByte(float value, int max = 255)
    {
        float v = value + 0.5f;
        if (v <= 0)
        {
            return 0;
        }

        if (v >= max)
        {
            return (byte)max;
        }

        return (byte)v;
    }

    /// <summary>每个目标像素对应的源像素索引与权重（窗口内已归一化、索引已 clamp）。</summary>
    private sealed class KernelTable
    {
        private KernelTable(int[] starts, int[] counts, int[] indices, float[] weights)
        {
            Starts = starts;
            Counts = counts;
            Indices = indices;
            Weights = weights;
        }

        public int[] Starts { get; }

        public int[] Counts { get; }

        public int[] Indices { get; }

        public float[] Weights { get; }

        internal static KernelTable Build(int sourceLength, int destinationLength, IResampler sampler)
        {
            var starts = new int[destinationLength];
            var counts = new int[destinationLength];
            var indices = new List<int>(destinationLength * 4);
            var weights = new List<float>(destinationLength * 4);

            double scale = (double)destinationLength / sourceLength;
            double invScale = 1.0 / scale;
            double sampleScale = Math.Min(scale, 1.0);
            double support = sampler.Radius * Math.Max(invScale, 1.0);

            for (int i = 0; i < destinationLength; i++)
            {
                double center = ((i + 0.5) * invScale) - 0.5;
                int left = Math.Max(0, (int)Math.Floor(center - support));
                int right = Math.Min(sourceLength - 1, (int)Math.Ceiling(center + support));

                starts[i] = indices.Count;
                double sum = 0;
                int added = 0;
                for (int j = left; j <= right; j++)
                {
                    double weight = sampler.GetValue((float)((j - center) * sampleScale));
                    if (weight == 0)
                    {
                        continue;
                    }

                    indices.Add(j);
                    weights.Add((float)weight);
                    sum += weight;
                    added++;
                }

                if (added == 0 || sum == 0)
                {
                    // 极端缩小或核有零点：退化为最近像素
                    int nearest = Math.Clamp((int)Math.Round(center), 0, sourceLength - 1);
                    indices.Add(nearest);
                    weights.Add(1f);
                    added = 1;
                    sum = 1;
                }

                float inverse = (float)(1.0 / sum);
                for (int k = starts[i]; k < starts[i] + added; k++)
                {
                    weights[k] *= inverse;
                }

                counts[i] = added;
            }

            return new KernelTable(starts, counts, indices.ToArray(), weights.ToArray());
        }
    }
}
