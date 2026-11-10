using System.Buffers;
using System.Numerics;

namespace ImageFacade.SdcbSimdCodecs.Rendering;


/// <summary>
/// 绘制上下文：把「笔刷 + 轮廓」落到某个 <see cref="Canvas"/> 上。
/// <para>
/// 与 ImageSharp.Drawing 的 <c>DrawingContext</c> 对应；ext 层的
/// <c>IImageProcessingContext</c> 会把它包成 <c>image.Mutate(x =&gt; x.Fill(...))</c> 的形态。
/// </para>
/// <para>
/// 变换是「当前矩阵」而不是栈：<see cref="PushTransform"/> 是左乘（等价于 SVG 的嵌套 <c>transform</c>），
/// <see cref="ResetTransform"/> 回到恒等。所有坐标（路径、笔刷、文字位置）都在用户坐标系里给。
/// </para>
/// <para>
/// 裁剪才是栈：<see cref="PushClip"/> 与已有裁剪求交，<see cref="PopClip"/> 逐层恢复。
/// 每层是一张画布大小的 A8 掩码，因此浅层裁剪很便宜、深层会占内存。
/// </para>
/// </summary>
internal sealed class DrawingContext
{
    private readonly ScanlineRasterizer _rasterizer = new();

    /// <summary>裁剪掩码栈；每层长度 <c>Canvas.Width * Canvas.Height</c>，来自 <see cref="ArrayPool{T}.Shared"/>。</summary>
    private readonly List<byte[]> _clipMasks = new();

    public DrawingContext(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Canvas = canvas;
    }

    public Canvas Canvas { get; }

    /// <summary>当前用户坐标系到画布坐标系的矩阵。默认恒等。</summary>
    public Matrix3x2 Transform { get; private set; } = Matrix3x2.Identity;

    /// <summary>当前是否处于至少一层裁剪之内。</summary>
    public bool HasClip => _clipMasks.Count != 0;

    /// <summary>裁剪层数（<see cref="PushClip"/> 减 <see cref="PopClip"/> 的净次数）。</summary>
    public int ClipDepth => _clipMasks.Count;

    /// <summary>在当前变换之上再叠加一层（先应用 <paramref name="transform"/>，再应用已有变换）。</summary>
    public void PushTransform(Matrix3x2 transform) => Transform = Matrix3x2.Multiply(transform, Transform);

    /// <summary>直接替换当前变换。</summary>
    public void SetTransform(Matrix3x2 transform) => Transform = transform;

    /// <summary>回到恒等变换。</summary>
    public void ResetTransform() => Transform = Matrix3x2.Identity;

    /// <summary>
    /// 把后续绘制的可见区域限制在 <paramref name="path"/> 之内（与已有裁剪取交集）。
    /// <para>
    /// 轮廓按当前 <see cref="Transform"/> 变换后光栅化成 A8 掩码，之后每次填充/描边/文字都会先乘上它，
    /// 因此斜边同样有抗锯齿，且半透明笔刷在边界上不会被叠加两次。
    /// </para>
    /// </summary>
    public void PushClip(IPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int width = Canvas.Width;
        int height = Canvas.Height;
        byte[] mask = ArrayPool<byte>.Shared.Rent(width * height);
        Span<byte> maskSpan = mask.AsSpan(0, width * height);
        maskSpan.Clear();

        IPath shape = Transform.IsIdentity ? path : path.Transform(Transform);
        FlattenedPath flattened = Path.Flatten(shape);
        Rectangle area = Rectangle.Intersect(
            flattened.Bounds.ToBoundingRectangle(),
            new Rectangle(0, 0, width, height));

        if (!area.IsEmpty)
        {
            using var coverage = new CoverageBuffer(area.Width, area.Height);
            _rasterizer.Rasterize(flattened, FillRule.NonZero, area, coverage);

            byte[]? parent = _clipMasks.Count == 0 ? null : _clipMasks[^1];
            for (int y = 0; y < area.Height; y++)
            {
                ReadOnlySpan<byte> source = coverage.GetRow(y);
                Span<byte> destination = maskSpan.Slice(((area.Y + y) * width) + area.X, area.Width);

                if (parent is null)
                {
                    source.CopyTo(destination);
                    continue;
                }

                ReadOnlySpan<byte> parentRow = parent.AsSpan(((area.Y + y) * width) + area.X, area.Width);
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (byte)PixelOps.Mul255(source[i], parentRow[i]);
                }
            }
        }

        // 交不出任何像素时保留一层全零掩码，深度与 PopClip 仍然一一对应
        _clipMasks.Add(mask);
    }

    /// <summary>弹出一层裁剪（与 <see cref="PushClip"/> 配对）。没有裁剪时抛 <see cref="InvalidOperationException"/>。</summary>
    public void PopClip()
    {
        if (_clipMasks.Count == 0)
        {
            throw new InvalidOperationException("没有可弹出的裁剪区域：PopClip 必须与 PushClip 配对。");
        }

        int last = _clipMasks.Count - 1;
        ArrayPool<byte>.Shared.Return(_clipMasks[last]);
        _clipMasks.RemoveAt(last);
    }

    /// <summary>用笔刷填充轮廓。</summary>
    public void Fill(IBrush brush, IPath path, FillRule rule = FillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(path);

        FillCore(
            PrepareBrush(brush),
            Transform.IsIdentity ? path : path.Transform(Transform),
            rule);
    }

    /// <summary>用纯色填充轮廓。</summary>
    public void Fill(Color color, IPath path, FillRule rule = FillRule.NonZero)
        => Fill(new SolidBrush(color), path, rule);

    /// <summary>用画笔描边：线宽与虚线图案都按变换的缩放因子等比放大。</summary>
    public void Draw(IPen pen, IPath path)
    {
        ArgumentNullException.ThrowIfNull(pen);
        ArgumentNullException.ThrowIfNull(path);

        float width = pen.Width;
        if (!(width > 0f) || float.IsInfinity(width))
        {
            throw new ArgumentOutOfRangeException(nameof(pen), width, "线宽必须是有限正数。");
        }

        LineJoin join = pen.LineJoin;
        if (!Enum.IsDefined(join))
        {
            throw new ArgumentOutOfRangeException(nameof(pen), join, "未知的连接方式。");
        }

        LineCap cap = pen.LineCap;
        if (!Enum.IsDefined(cap))
        {
            throw new ArgumentOutOfRangeException(nameof(pen), cap, "未知的端点形状。");
        }

        float miterLimit = pen.MiterLimit;
        if (!(miterLimit >= 1f) || float.IsInfinity(miterLimit))
        {
            throw new ArgumentOutOfRangeException(nameof(pen), miterLimit, "MiterLimit 必须 ≥ 1 且有限。");
        }

        float dashOffset = pen.DashOffset;
        if (!float.IsFinite(dashOffset))
        {
            throw new ArgumentOutOfRangeException(nameof(pen), dashOffset, "虚线偏移必须是有限数。");
        }

        float[]? dashPattern = ValidateDashPattern(pen.DashPattern);

        Matrix3x2 transform = Transform;
        float scale = 1f;

        if (!transform.IsIdentity)
        {
            float determinant = transform.GetDeterminant();
            if (!(MathF.Abs(determinant) > 0f) || float.IsInfinity(determinant))
            {
                return;   // 退化矩阵（含 0 缩放）什么都画不出来
            }

            scale = MathF.Sqrt(MathF.Abs(determinant));
            path = path.Transform(transform);
        }

        FlattenedPath flattened = Path.Flatten(path);
        if (flattened.Points.Length == 0)
        {
            return;
        }

        float actualWidth = width * scale;

        // 图案单位是线宽倍数，因此跟着实际线宽（含变换缩放）一起放大
        StrokeStyle style = StrokeStyle.Create(join, cap, miterLimit, dashPattern, dashOffset, actualWidth);

        // 轮廓已经在画布坐标系里，不能再让 Fill 乘一次变换
        FillCore(new SolidBrush(pen.Color), StrokeOutline.Build(flattened, actualWidth, style), FillRule.NonZero);
    }

    /// <summary>校验自定义 <see cref="IPen"/> 返回的虚线图案；<see cref="Pen"/> 已在构造时校验过。</summary>
    private static float[]? ValidateDashPattern(float[]? pattern)
    {
        if (pattern is null || pattern.Length == 0)
        {
            return null;
        }

        double total = 0d;
        foreach (float length in pattern)
        {
            if (!(length >= 0f) || float.IsInfinity(length))
            {
                throw new ArgumentOutOfRangeException(nameof(pattern), length, "虚线长度必须是非负有限数。");
            }

            total += length;
        }

        if (!(total > 0d))
        {
            throw new ArgumentException("虚线图案不能全为 0。", nameof(pattern));
        }

        return pattern;
    }

    /// <summary>用纯色描边。</summary>
    public void Draw(Color color, float width, IPath path) => Draw(new Pen(color, width), path);

    /// <summary>用纯色清空整个画布（不做混合，也不受变换/裁剪影响）。</summary>
    public void Clear(Color color) => Canvas.Clear(color);

    /// <summary>
    /// 把一块已经算好的 coverage 混到画布上（供 <see cref="TextDrawing"/> 的字形掩码复用）。
    /// 内部会自动乘上当前裁剪掩码。
    /// </summary>
    internal void BlendCoverage(IBrush brush, Rectangle region, CoverageBuffer coverage)
    {
        if (_clipMasks.Count != 0)
        {
            ApplyClip(region, coverage);
        }

        brush.Blend(Canvas, region, coverage.Data, coverage.Stride);
    }

    /// <summary>
    /// 让支持变换的笔刷跟随当前 <see cref="Transform"/>：渐变/图案在**用户坐标系**里定义，
    /// 采样时先用逆矩阵把画布像素映射回去。笔刷本身不可变，这里返回的是共享色表的轻量副本。
    /// </summary>
    private IBrush PrepareBrush(IBrush brush)
    {
        if (Transform.IsIdentity)
        {
            return brush;
        }

        // 退化矩阵下路径本身也退化成零面积，采样结果无人使用
        if (!Matrix3x2.Invert(Transform, out Matrix3x2 inverse))
        {
            return brush;
        }

        return brush switch
        {
            GradientBrush gradient => gradient.WithInverseTransform(inverse),
            PatternBrush pattern => pattern.WithInverseTransform(inverse),
            _ => brush,
        };
    }

    private void FillCore(IBrush brush, IPath path, FillRule rule)
    {
        bool clipped = _clipMasks.Count != 0;

        // 纯色 + 轴对齐矩形：完全绕开光栅化（plan §6.4.1）。有裁剪时矩形会被裁掉一部分，必须走通用路径
        if (!clipped && path is RectangularPolygon && brush.TryGetSolidColor(out Color solid))
        {
            RectangleFill.Fill(Canvas, path.Bounds, solid);
            return;
        }

        FlattenedPath flattened = Path.Flatten(path);
        Rectangle clip = Rectangle.Intersect(
            flattened.Bounds.ToBoundingRectangle(),
            new Rectangle(0, 0, Canvas.Width, Canvas.Height));

        if (clip.IsEmpty)
        {
            return;
        }

        using var coverage = new CoverageBuffer(clip.Width, clip.Height);

        // 整块都落在裁剪掩码之外时省掉扫描线：先看掩码覆盖行，非零才光栅化
        if (clipped && !AnyClipPixelInside(clip))
        {
            return;
        }

        _rasterizer.Rasterize(flattened, rule, clip, coverage);
        BlendCoverage(brush, clip, coverage);
    }

    /// <summary>裁剪区域与 <paramref name="region"/> 是否有交集（整块掩码为 0 时直接跳过光栅化）。</summary>
    private bool AnyClipPixelInside(Rectangle region)
    {
        byte[] mask = _clipMasks[^1];
        for (int y = 0; y < region.Height; y++)
        {
            ReadOnlySpan<byte> row = mask.AsSpan(((region.Y + y) * Canvas.Width) + region.X, region.Width);
            for (int i = 0; i < row.Length; i++)
            {
                if (row[i] != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void ApplyClip(Rectangle region, CoverageBuffer coverage)
    {
        byte[] mask = _clipMasks[^1];
        int width = Canvas.Width;

        for (int y = 0; y < region.Height; y++)
        {
            ReadOnlySpan<byte> maskRow = mask.AsSpan(((region.Y + y) * width) + region.X, region.Width);
            Span<byte> coverageRow = coverage.GetRow(y);

            for (int i = 0; i < coverageRow.Length; i++)
            {
                coverageRow[i] = (byte)PixelOps.Mul255(coverageRow[i], maskRow[i]);
            }
        }
    }
}
