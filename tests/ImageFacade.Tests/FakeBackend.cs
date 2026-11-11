using ImageFacade.Backends;

namespace ImageFacade.Tests;

internal sealed class FakeFrame : IFrame
{
    internal FakeFrame(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    internal byte[] Pixels { get; }

    internal int Stride => Width * 4;

    internal bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// 测试用后端：像素存规范 RGBA32，能力用开关控制，用来验证路由/回退而不是渲染质量。
/// </summary>
internal class FakeBackend : IImageBackend
{
    private static readonly IReadOnlySet<ImageFormat> NoFormats = new HashSet<ImageFormat>();

    private readonly string _name;

    public FakeBackend(string name = "fake")
    {
        _name = name;
    }

    public bool SupportsLine { get; set; } = true;

    public bool SupportsText { get; set; }

    public bool SupportsResize { get; set; } = true;

    public bool SupportsAlphaBlending { get; set; } = true;

    public int DrawLineCalls { get; private set; }

    public int DrawTextCalls { get; private set; }

    public int MeasureTextCalls { get; private set; }

    public int ResizeCalls { get; private set; }

    /// <summary>整帧分配次数（用来断言批量回退只建一个软帧）。</summary>
    public int CreateCalls { get; private set; }

    /// <summary>整帧导入次数（用来断言批量回退只往返一次）。</summary>
    public int ImportCalls { get; private set; }

    public string Name => _name;

    public Version Version => new(0, 1, 0);

    public BackendCapabilities Capabilities => BackendCapabilities.None;

    public IReadOnlySet<ImageFormat> SupportedDecodeFormats => NoFormats;

    public virtual IReadOnlySet<ImageFormat> SupportedEncodeFormats => NoFormats;

    public virtual IFrame Create(int width, int height, Rgba32? background)
    {
        CreateCalls++;
        var pixels = new byte[width * height * 4];
        if (background is { } color)
        {
            FillRect(pixels, width, height, 0, 0, width, height, color);
        }

        return new FakeFrame(width, height, pixels);
    }

    public bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame)
    {
        frame = null!;
        return false;
    }

    public virtual IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata)
        => throw new NotSupportedException("FakeBackend 不解码。");

    public IFrame Clone(IFrame frame)
    {
        var source = (FakeFrame)frame;
        return new FakeFrame(source.Width, source.Height, (byte[])source.Pixels.Clone());
    }

    public virtual void Encode(IFrame frame, Stream destination, in EncodeRequest request)
        => throw new NotSupportedException("FakeBackend 不编码。");

    public bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result)
    {
        if (!SupportsResize)
        {
            result = null!;
            return false;
        }

        ResizeCalls++;
        var source = (FakeFrame)frame;
        var target = new FakeFrame(request.Width, request.Height, new byte[request.Width * request.Height * 4]);
        for (int y = 0; y < request.Height; y++)
        {
            int sourceY = Math.Min(source.Height - 1, y * source.Height / request.Height);
            for (int x = 0; x < request.Width; x++)
            {
                int sourceX = Math.Min(source.Width - 1, x * source.Width / request.Width);
                int s = (sourceY * source.Stride) + (sourceX * 4);
                int d = (y * target.Stride) + (x * 4);
                target.Pixels[d] = source.Pixels[s];
                target.Pixels[d + 1] = source.Pixels[s + 1];
                target.Pixels[d + 2] = source.Pixels[s + 2];
                target.Pixels[d + 3] = source.Pixels[s + 3];
            }
        }

        result = target;
        return true;
    }

    public bool TryDrawFrame(IFrame destination, IFrame source, PointF offset) => false;

    public bool TryDrawPoint(IFrame frame, in PointSpec spec) => false;

    public bool TryDrawLine(IFrame frame, in LineSpec spec)
    {
        if (!SupportsLine || (!SupportsAlphaBlending && spec.Color.A < 255))
        {
            return false;
        }

        DrawLineCalls++;
        var target = (FakeFrame)frame;
        int x0 = (int)MathF.Round(spec.From.X);
        int y0 = (int)MathF.Round(spec.From.Y);
        int x1 = (int)MathF.Round(spec.To.X);
        int y1 = (int)MathF.Round(spec.To.Y);
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;

        while (true)
        {
            SetPixel(target, x0, y0, spec.Color);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int doubled = 2 * error;
            if (doubled >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (doubled <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }

        return true;
    }

    public bool TryDrawPolyline(IFrame frame, in PolylineSpec spec) => false;

    public bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec) => false;

    public bool TryFillRectangle(IFrame frame, in FilledRectSpec spec)
    {
        if (!SupportsAlphaBlending && spec.Color.A < 255)
        {
            return false;
        }

        var target = (FakeFrame)frame;
        FillRect(
            target.Pixels,
            target.Width,
            target.Height,
            (int)MathF.Round(spec.Rect.Left),
            (int)MathF.Round(spec.Rect.Top),
            (int)MathF.Round(spec.Rect.Right),
            (int)MathF.Round(spec.Rect.Bottom),
            spec.Color);
        return true;
    }

    public bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec) => false;

    public bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec) => false;

    public bool TryFillPolygon(IFrame frame, in PolygonSpec spec) => false;

    public bool TryDrawText(IFrame frame, in TextSpec spec)
    {
        if (!SupportsText || (!SupportsAlphaBlending && spec.Style.Color.A < 255))
        {
            return false;
        }

        DrawTextCalls++;
        TextMetrics metrics = Measure(spec.Text, spec.Style);
        var target = (FakeFrame)frame;

        float top = spec.Style.Anchor == TextAnchor.Baseline ? spec.Origin.Y - metrics.Ascent : spec.Origin.Y;
        FillRect(
            target.Pixels,
            target.Width,
            target.Height,
            (int)MathF.Round(spec.Origin.X),
            (int)MathF.Round(top),
            (int)MathF.Round(spec.Origin.X + metrics.Width),
            (int)MathF.Round(top + metrics.LineHeight),
            spec.Style.Color);
        return true;
    }

    public bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics)
    {
        if (!SupportsText)
        {
            metrics = default;
            return false;
        }

        MeasureTextCalls++;
        metrics = Measure(spec.Text, spec.Style);
        return true;
    }

    /// <summary>假字体：每个字符宽 0.5em，ascent 0.8em，descent 0.2em。</summary>
    private static TextMetrics Measure(string text, TextStyle style)
        => new(text.Length * style.Size * 0.5f, style.Size * 0.8f, style.Size * 0.2f);

    public void Clear(IFrame frame, Rgba32 color)
    {
        var target = (FakeFrame)frame;
        FillRect(target.Pixels, target.Width, target.Height, 0, 0, target.Width, target.Height, color);
    }

    public bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color)
    {
        var target = (FakeFrame)frame;
        int offset = (y * target.Stride) + (x * 4);
        color = new Rgba32(target.Pixels[offset], target.Pixels[offset + 1], target.Pixels[offset + 2], target.Pixels[offset + 3]);
        return true;
    }

    public bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride)
    {
        var source = (FakeFrame)frame;
        for (int y = 0; y < source.Height; y++)
        {
            source.Pixels.AsSpan(y * source.Stride, source.Stride).CopyTo(destination.Slice(y * destinationStride, source.Stride));
        }

        return true;
    }

    public void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride)
    {
        ImportCalls++;
        var target = (FakeFrame)frame;
        for (int y = 0; y < target.Height; y++)
        {
            source.Slice(y * sourceStride, target.Stride).CopyTo(target.Pixels.AsSpan(y * target.Stride, target.Stride));
        }
    }

    public bool TryAcquirePixels(IFrame frame, out PixelLease lease)
    {
        var target = (FakeFrame)frame;
        lease = new PixelLease(target.Pixels, target.Stride);
        return true;
    }

    public void Dispose()
    {
    }

    private static void SetPixel(FakeFrame frame, int x, int y, Rgba32 color)
    {
        if ((uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height)
        {
            return;
        }

        int offset = (y * frame.Stride) + (x * 4);
        frame.Pixels[offset] = color.R;
        frame.Pixels[offset + 1] = color.G;
        frame.Pixels[offset + 2] = color.B;
        frame.Pixels[offset + 3] = color.A;
    }

    private static void FillRect(byte[] pixels, int width, int height, int left, int top, int right, int bottom, Rgba32 color)
    {
        int x0 = Math.Max(0, left);
        int y0 = Math.Max(0, top);
        int x1 = Math.Min(width, right);
        int y1 = Math.Min(height, bottom);

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int offset = ((y * width) + x) * 4;
                pixels[offset] = color.R;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.B;
                pixels[offset + 3] = color.A;
            }
        }
    }
}
