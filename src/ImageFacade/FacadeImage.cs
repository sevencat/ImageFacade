using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ImageFacade.Backends;
using ImageFacade.Internal;

namespace ImageFacade;

/// <summary>
/// 一张图的句柄：包装后端私有的帧，所有绘制原地修改并返回 <c>this</c>。
/// 一帧一实例、不跨线程；用完立即 <see cref="Dispose"/>。
/// </summary>
/// <remarks>
/// 路由规则（plan §2.2）：先问当前后端 <c>Try*</c>；返回 false 则按
/// <see cref="ImageEngineOptions.Fallback"/> 决定是"临时迁移一次"还是"整帧切到软后端"。
/// </remarks>
public sealed class FacadeImage : IDisposable
{
    private readonly ImageEngine _engine;
    private IImageBackend _backend;
    private IFrame? _frame;
    private ImageMetadata _metadata;
    private int _fallbackCount;

    // ---- 批量作用域状态（BeginDraw / Mutate）----
    private int _batchDepth;
    private bool _batchFlushOnNative;
    private IFrame? _batchSoftFrame;
    private IImageBackend? _batchSoftBackend;
    private string? _batchFeature;
    private long _batchStartedTimestamp;

    internal FacadeImage(ImageEngine engine, IImageBackend backend, IFrame frame, ImageMetadata metadata)
    {
        _engine = engine;
        _backend = backend;
        _frame = frame;
        _metadata = metadata;
    }

    internal delegate bool SpecOperation<TSpec>(IImageBackend backend, IFrame frame, in TSpec spec);

    public int Width => Frame.Width;

    public int Height => Frame.Height;

    public ImageFormat SourceFormat => _metadata.Format;

    public string BackendName => _backend.Name;

    public ImageMetadata Metadata => _metadata;

    /// <summary>当前帧是否由软后端持有。</summary>
    public bool IsSoftwareSurface => string.Equals(_backend.Name, "software", StringComparison.OrdinalIgnoreCase);

    /// <summary>true 表示帧内存直接来自 <c>FromPixels(..., wrap: true)</c>。</summary>
    public bool IsZeroCopy { get; internal set; }

    /// <summary>本实例累计回退次数（成功不归零；每帧新实例意味着天然按帧复位）。</summary>
    public int FallbackCount => _fallbackCount;

    internal IFrame Frame => _frame ?? throw new ObjectDisposedException(nameof(FacadeImage));

    internal IImageBackend Backend => _backend;

    // ---- 尺寸 ----

    /// <summary>纯拉伸（等价于 <see cref="ResizeMode.Stretch"/>）。</summary>
    public FacadeImage Resize(int width, int height)
    {
        ThrowIfDisposed();
        ImageEngine.ValidateSize(width, height);
        FlushBatch();
        ResizeCore(width, height, Sampler.Bicubic);
        return this;
    }

    /// <summary>六种模式；裁剪与补边由门面用像素搬运完成，保证各后端几何一致（plan §3.3）。</summary>
    public FacadeImage Resize(ResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ImageEngine.ValidateSize(options.Width, options.Height);
        FlushBatch();

        ResizePlan plan = ResizePlanner.Plan(Width, Height, options.Width, options.Height, options.Mode);
        ResizeCore(plan.ScaledWidth, plan.ScaledHeight, options.Sampler);

        if (!plan.NeedsComposite)
        {
            return this;
        }

        byte[] scaled = CopyCanonicalPixels(out int scaledStride);
        int outputStride = plan.OutputWidth * 4;
        var output = new byte[checked(outputStride * plan.OutputHeight)];
        Fill(output, options.PadColor);
        Blit(
            scaled,
            scaledStride,
            plan.ScaledWidth,
            plan.ScaledHeight,
            output,
            outputStride,
            plan.OutputWidth,
            plan.OutputHeight,
            plan.PlaceX,
            plan.PlaceY);

        IFrame frame = _backend.Create(plan.OutputWidth, plan.OutputHeight, background: null);
        try
        {
            _backend.ImportPixels(frame, output, outputStride);
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        ReplaceFrame(frame, plan.OutputWidth, plan.OutputHeight);
        return this;
    }

    public FacadeImage Clear(Rgba32 color)
    {
        ThrowIfDisposed();
        if (_batchSoftFrame is { } batchFrame)
        {
            if (!ShouldFlushOnNative)
            {
                _batchSoftBackend!.Clear(batchFrame, color);
                return this;
            }

            FlushBatch();
        }

        _backend.Clear(Frame, color);
        return this;
    }

    // ---- 绘制 ----

    public FacadeImage DrawPoint(PointF point, Rgba32 color, float radius = 1.5f)
    {
        ThrowIfDisposed();
        var spec = new PointSpec(point, color, radius);
        return Execute(nameof(DrawPoint), spec, static (IImageBackend b, IFrame f, in PointSpec s) => b.TryDrawPoint(f, in s));
    }

    public FacadeImage DrawLine(PointF from, PointF to, Rgba32 color, float width = 1f, StrokeStyle? style = null)
    {
        ThrowIfDisposed();
        var spec = new LineSpec(from, to, color, width, style);
        return Execute(nameof(DrawLine), spec, static (IImageBackend b, IFrame f, in LineSpec s) => b.TryDrawLine(f, in s));
    }

    public FacadeImage DrawPolyline(ReadOnlySpan<PointF> points, Rgba32 color, float width = 1f, bool closed = false, StrokeStyle? style = null)
    {
        ThrowIfDisposed();
        return ExecutePoints(nameof(DrawPolyline), points, color, width, closed, style, filled: false);
    }

    public FacadeImage DrawRectangle(RectF rect, Rgba32 color, float width = 1f, StrokeStyle? style = null)
    {
        ThrowIfDisposed();
        var spec = new StrokedRectSpec(rect, color, width, style);
        return Execute(nameof(DrawRectangle), spec, static (IImageBackend b, IFrame f, in StrokedRectSpec s) => b.TryDrawRectangle(f, in s));
    }

    public FacadeImage FillRectangle(RectF rect, Rgba32 color)
    {
        ThrowIfDisposed();
        var spec = new FilledRectSpec(rect, color);
        return Execute(nameof(FillRectangle), spec, static (IImageBackend b, IFrame f, in FilledRectSpec s) => b.TryFillRectangle(f, in s));
    }

    public FacadeImage DrawEllipse(RectF bounds, Rgba32 color, float width = 1f, StrokeStyle? style = null)
    {
        ThrowIfDisposed();
        var spec = new StrokedEllipseSpec(bounds, color, width, style);
        return Execute(nameof(DrawEllipse), spec, static (IImageBackend b, IFrame f, in StrokedEllipseSpec s) => b.TryDrawEllipse(f, in s));
    }

    public FacadeImage FillEllipse(RectF bounds, Rgba32 color)
    {
        ThrowIfDisposed();
        var spec = new FilledEllipseSpec(bounds, color);
        return Execute(nameof(FillEllipse), spec, static (IImageBackend b, IFrame f, in FilledEllipseSpec s) => b.TryFillEllipse(f, in s));
    }

    public FacadeImage FillPolygon(ReadOnlySpan<PointF> points, Rgba32 color)
    {
        ThrowIfDisposed();
        return ExecutePoints(nameof(FillPolygon), points, color, 1f, closed: true, style: null, filled: true);
    }

    public FacadeImage DrawText(string text, PointF at, TextStyle style)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();

        if (text.Length == 0)
        {
            return this;
        }

        // 单行 + 左对齐 + 不折行：不需要测量就能画（后端即使不会测量也能工作）
        if (style.MaxWidth <= 0f && style.Align == TextAlign.Left && text.IndexOf('\n') < 0)
        {
            var single = new TextSpec(text, at, style);
            return Execute(nameof(DrawText), single, static (IImageBackend b, IFrame f, in TextSpec s) => b.TryDrawText(f, in s));
        }

        TextLayoutResult layout;
        try
        {
            layout = TextLayouter.Layout(this, text, style);
        }
        catch (FeatureNotSupportedException)
        {
            // 排版需要测量，但用户调用的是 DrawText：错误信息应该指向 DrawText
            throw new FeatureNotSupportedException(nameof(DrawText), _backend.Name, _engine.Options.Fallback);
        }

        foreach (TextLine line in layout.Lines)
        {
            if (line.Text.Length == 0)
            {
                continue;
            }

            var origin = new PointF(at.X + line.OffsetX, at.Y + line.TopY);
            var spec = new TextSpec(line.Text, origin, style);
            Execute(nameof(DrawText), spec, static (IImageBackend b, IFrame f, in TextSpec s) => b.TryDrawText(f, in s));
        }

        return this;
    }

    public SizeF MeasureText(string text, TextStyle style)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();

        if (text.Length == 0)
        {
            return SizeF.Empty;
        }

        return TextLayouter.Layout(this, text, style).Size;
    }

    // ---- 生命周期 / 后端切换 ----

    /// <summary>
    /// 批量绘制作用域（对应 ImageSharp 的 <c>Mutate</c>）：作用域内第一次触发回退时把整帧导出到软帧一次，
    /// 之后所有操作都在软帧上按原顺序执行，结束时只写回一次。
    /// </summary>
    /// <param name="draw">在这张图上连续绘制。</param>
    /// <param name="flushOnNative">true = 每个操作开始前先把软帧写回，让"原生能做的操作"切回原生（不粘滞）。</param>
    public FacadeImage Mutate(Action<FacadeImage> draw, bool flushOnNative = false)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ThrowIfDisposed();

        BeginBatchScope(flushOnNative);
        try
        {
            draw(this);
        }
        catch
        {
            AbortBatchScope();
            throw;
        }

        EndBatchScope();
        return this;
    }

    /// <summary>打开批量绘制作用域；<c>Dispose</c> 时统一 flush。</summary>
    public DrawBatch BeginDraw(bool flushOnNative = false)
    {
        ThrowIfDisposed();
        BeginBatchScope(flushOnNative);
        return new DrawBatch(this);
    }

    public FacadeImage Clone()
    {
        ThrowIfDisposed();
        FlushBatch();
        IFrame copy = _backend.Clone(Frame);
        return new FacadeImage(_engine, _backend, copy, _metadata);
    }

    /// <summary>立即把当前帧迁移到软后端（不受 <see cref="FallbackMode"/> 影响）。</summary>
    public FacadeImage UseSoftware()
    {
        ThrowIfDisposed();
        FlushBatch();
        MigrateTo(RequireFallback(nameof(UseSoftware)));
        return this;
    }

    /// <summary>迁回引擎的主后端。</summary>
    public FacadeImage UseNative()
    {
        ThrowIfDisposed();
        FlushBatch();
        MigrateTo(_engine.Backend);
        return this;
    }

    // ---- 输出 ----

    public void Save(string path, SaveOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        SaveOptions save = options ?? new SaveOptions();
        ImageFormat format = save.Format ?? ImageFormats.FromExtension(path);
        if (format == ImageFormat.Unknown)
        {
            throw new ArgumentException($"无法从扩展名推断保存格式：'{path}'。请显式设置 SaveOptions.Format。", nameof(path));
        }

        using FileStream stream = File.Create(path);
        SaveCore(stream, save, format);
    }

    public void Save(Stream stream, SaveOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);

        SaveOptions save = options ?? new SaveOptions();
        SaveCore(stream, save, save.Format ?? ImageFormat.Png);
    }

    public byte[] Encode(SaveOptions? options = null)
    {
        ThrowIfDisposed();
        SaveOptions save = options ?? new SaveOptions();
        using var buffer = new MemoryStream();
        SaveCore(buffer, save, save.Format ?? ImageFormat.Png);
        return buffer.ToArray();
    }

    // ---- 异步输出（I/O 真异步；编码走后端异步接口，否则 Task.Run） ----

    public async Task SaveAsync(string path, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        SaveOptions save = options ?? new SaveOptions();
        ImageFormat format = save.Format ?? ImageFormats.FromExtension(path);
        if (format == ImageFormat.Unknown)
        {
            throw new ArgumentException($"无法从扩展名推断保存格式：'{path}'。请显式设置 SaveOptions.Format。", nameof(path));
        }

        byte[] encoded = await EncodeToArrayAsync(save, format, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, encoded, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);

        SaveOptions save = options ?? new SaveOptions();
        ImageFormat format = save.Format ?? ImageFormat.Png;
        ValidateSaveOptions(save);

        if (_backend is IAsyncImageBackend asyncBackend)
        {
            var request = new EncodeRequest(format, save.Quality, save.CompressionLevel, save.PreserveMetadata);
            EncodableFrame encodable = PrepareForEncode(save, format);
            try
            {
                await asyncBackend.EncodeAsync(encodable.Frame, stream, request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                encodable.Temp?.Dispose();
            }

            return;
        }

        await AsyncBridge.Run(() => SaveCore(stream, save, format), cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> EncodeAsync(SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SaveOptions save = options ?? new SaveOptions();
        return await EncodeToArrayAsync(save, save.Format ?? ImageFormat.Png, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> EncodeToArrayAsync(SaveOptions save, ImageFormat format, CancellationToken cancellationToken)
    {
        ValidateSaveOptions(save);

        if (_backend is IAsyncImageBackend asyncBackend)
        {
            using var buffer = new MemoryStream();
            var request = new EncodeRequest(format, save.Quality, save.CompressionLevel, save.PreserveMetadata);
            EncodableFrame encodable = PrepareForEncode(save, format);
            try
            {
                await asyncBackend.EncodeAsync(encodable.Frame, buffer, request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                encodable.Temp?.Dispose();
            }

            return buffer.ToArray();
        }

        return await AsyncBridge.Run(
            () =>
            {
                using var buffer = new MemoryStream();
                SaveCore(buffer, save, format);
                return buffer.ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSaveOptions(SaveOptions save)
    {
        if (save.Quality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(SaveOptions.Quality), save.Quality, "Quality 必须在 1..100。");
        }

        if (save.CompressionLevel is < -1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(SaveOptions.CompressionLevel), save.CompressionLevel, "CompressionLevel 必须在 -1..9。");
        }
    }

    // ---- 像素互操作 ----

    public Rgba32 GetPixel(int x, int y)
    {
        ThrowIfDisposed();
        FlushBatch();
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, $"x 必须在 0..{Width - 1}。");
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, $"y 必须在 0..{Height - 1}。");
        }

        return _backend.TryGetPixel(Frame, x, y, out Rgba32 color)
            ? color
            : throw new FeatureNotSupportedException(nameof(GetPixel), _backend.Name, _engine.Options.Fallback);
    }

    public void CopyPixelsTo(Span<byte> destination, int destinationStride, PixelFormat format = PixelFormat.Rgba32)
    {
        ThrowIfDisposed();
        FlushBatch();

        int bpp = format.GetBytesPerPixel();
        int rowBytes = Width * bpp;
        if (destinationStride < rowBytes)
        {
            throw new ArgumentException($"destinationStride 太小（{destinationStride} < {rowBytes}）。", nameof(destinationStride));
        }

        long required = (((long)Height - 1) * destinationStride) + rowBytes;
        if (destination.Length < required)
        {
            throw new ArgumentException($"destination 缓冲区太小（需要 {required} 字节，实际 {destination.Length}）。", nameof(destination));
        }

        if (format == PixelFormat.Rgba32)
        {
            if (!_backend.TryCopyPixels(Frame, destination, destinationStride))
            {
                throw new FeatureNotSupportedException(nameof(CopyPixelsTo), _backend.Name, _engine.Options.Fallback);
            }

            return;
        }

        int canonicalStride = Width * 4;
        byte[] rented = ArrayPool<byte>.Shared.Rent(checked(canonicalStride * Height));
        try
        {
            Span<byte> canonical = rented.AsSpan(0, canonicalStride * Height);
            if (!_backend.TryCopyPixels(Frame, canonical, canonicalStride))
            {
                throw new FeatureNotSupportedException(nameof(CopyPixelsTo), _backend.Name, _engine.Options.Fallback);
            }

            PixelConverter.Convert(canonical, canonicalStride, PixelFormat.Rgba32, destination, destinationStride, format, Width, Height);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void CopyPixelsTo(IntPtr destination, int destinationStride, PixelFormat format = PixelFormat.Rgba32)
    {
        ThrowIfDisposed();
        if (destination == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        int length = checked(destinationStride * Height);
        unsafe
        {
            CopyPixelsTo(new Span<byte>((void*)destination, length), destinationStride, format);
        }
    }

    public byte[] CopyPixels(PixelFormat format = PixelFormat.Rgba32)
    {
        ThrowIfDisposed();
        int stride = checked(Width * format.GetBytesPerPixel());
        var buffer = new byte[checked(stride * Height)];
        CopyPixelsTo(buffer, stride, format);
        return buffer;
    }

    public PixelLease AcquirePixels()
    {
        ThrowIfDisposed();
        FlushBatch();
        return _backend.TryAcquirePixels(Frame, out PixelLease lease)
            ? lease
            : throw new FeatureNotSupportedException(nameof(AcquirePixels), _backend.Name, _engine.Options.Fallback);
    }

    // ---- 路由 ----

    private FacadeImage Execute<TSpec>(string feature, in TSpec spec, SpecOperation<TSpec> operation)
    {
        ThrowIfDisposed();

        // 批量作用域内已经切到软帧：后续操作按原顺序全部在软帧上执行（一次往返）
        if (_batchSoftFrame is not null)
        {
            if (!ShouldFlushOnNative)
            {
                ExecuteOnBatch(feature, in spec, operation);
                return this;
            }

            FlushBatch();
        }

        if (!_engine.Options.PreferSoftwareRendering && operation(_backend, Frame, in spec))
        {
            return this;
        }

        return _batchDepth > 0
            ? ExecuteBatch(feature, in spec, operation)
            : ExecuteFallback(feature, in spec, operation);
    }

    private FacadeImage ExecuteFallback<TSpec>(string feature, in TSpec spec, SpecOperation<TSpec> operation)
    {
        if (_engine.Options.Strict)
        {
            throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
        }

        IImageBackend fallback = RequireFallback(feature);
        if (ReferenceEquals(fallback, _backend))
        {
            if (!operation(_backend, Frame, in spec))
            {
                throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
            }

            return this;
        }

        string sourceBackend = _backend.Name;
        _fallbackCount++;
        long startTimestamp = Stopwatch.GetTimestamp();

        if (ShouldSwitch())
        {
            MigrateTo(fallback);
            if (!operation(fallback, Frame, in spec))
            {
                throw new FeatureNotSupportedException(feature, fallback.Name, _engine.Options.Fallback);
            }
        }
        else
        {
            RunTemporary(fallback, feature, spec, operation);
        }

        RaiseFallback(sourceBackend, fallback.Name, feature, startTimestamp);
        return this;
    }

    /// <summary>执行一批"后端不支持"的操作：第一次回退时把整帧导出到软帧，之后复用同一个软帧。</summary>
    private FacadeImage ExecuteBatch<TSpec>(string feature, in TSpec spec, SpecOperation<TSpec> operation)
    {
        if (_engine.Options.Strict)
        {
            throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
        }

        IImageBackend fallback = RequireFallback(feature);
        if (ReferenceEquals(fallback, _backend))
        {
            // 已经在回退目标上却仍不支持：真不支持，不再尝试
            if (!operation(_backend, Frame, in spec))
            {
                throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
            }

            return this;
        }

        EnsureBatchSoft(fallback, feature);
        ExecuteOnBatch(feature, in spec, operation);
        return this;
    }

    private void ExecuteOnBatch<TSpec>(string feature, in TSpec spec, SpecOperation<TSpec> operation)
    {
        IImageBackend soft = _batchSoftBackend!;
        if (!operation(soft, _batchSoftFrame!, in spec))
        {
            throw new FeatureNotSupportedException(feature, soft.Name, _engine.Options.Fallback);
        }
    }

    /// <summary>把当前帧导出到批量软帧（一次全图拷贝 + 一次软帧分配）。</summary>
    private void EnsureBatchSoft(IImageBackend soft, string feature)
    {
        int width = Width;
        int height = Height;
        int stride = width * 4;
        int length = checked(stride * height);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        IFrame softFrame = soft.Create(width, height, background: null);
        try
        {
            Span<byte> canonical = rented.AsSpan(0, length);
            if (!_backend.TryCopyPixels(Frame, canonical, stride))
            {
                throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
            }

            soft.ImportPixels(softFrame, canonical, stride);
        }
        catch
        {
            softFrame.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        _batchSoftFrame = softFrame;
        _batchSoftBackend = soft;
        _batchFeature = feature;
        _batchStartedTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// 把批量软帧写回当前后端（一次全图拷贝）。<see cref="FallbackMode.SwitchAndStay"/> 与累计达到阈值的
    /// <see cref="FallbackMode.Adaptive"/> 直接接管软帧，省掉这次拷贝。
    /// </summary>
    private void FlushBatch()
    {
        IFrame? softFrame = _batchSoftFrame;
        IImageBackend? soft = _batchSoftBackend;
        string feature = _batchFeature ?? "Draw";
        long startTimestamp = _batchStartedTimestamp;
        _batchSoftFrame = null;
        _batchSoftBackend = null;
        _batchFeature = null;

        if (softFrame is null || soft is null)
        {
            return;
        }

        string sourceBackend = _backend.Name;
        _fallbackCount++;

        if (ShouldSwitch())
        {
            IFrame previous = Frame;
            _frame = softFrame;
            _backend = soft;
            IsZeroCopy = false;
            previous.Dispose();
        }
        else
        {
            int stride = Width * 4;
            int length = checked(stride * Height);
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                Span<byte> canonical = rented.AsSpan(0, length);
                if (!soft.TryCopyPixels(softFrame, canonical, stride))
                {
                    throw new FeatureNotSupportedException(feature, soft.Name, _engine.Options.Fallback);
                }

                _backend.ImportPixels(Frame, canonical, stride);
            }
            finally
            {
                softFrame.Dispose();
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        RaiseFallback(sourceBackend, soft.Name, feature, startTimestamp);
    }

    internal void FlushBatchScope() => FlushBatch();

    internal void BeginBatchScope(bool flushOnNative)
    {
        if (_batchDepth++ == 0)
        {
            _batchFlushOnNative = flushOnNative;
        }
    }

    /// <summary>
    /// 是否在每次操作前先写回软帧。<see cref="ImageEngineOptions.PreferSoftwareRendering"/> 下没有"原生"可回，
    /// 所以一律粘滞（否则每个 op 都会写回再重建软帧）。
    /// </summary>
    private bool ShouldFlushOnNative => _batchFlushOnNative && !_engine.Options.PreferSoftwareRendering;

    internal void EndBatchScope()
    {
        if (_batchDepth == 0)
        {
            return;
        }

        if (--_batchDepth == 0)
        {
            try
            {
                FlushBatch();
            }
            finally
            {
                _batchFlushOnNative = false;
            }
        }
    }

    /// <summary>作用域内抛出异常时调用：丢弃软帧，不把半成品写回（已有操作留在软帧里一起丢弃）。</summary>
    internal void AbortBatchScope()
    {
        _batchDepth = 0;
        _batchFlushOnNative = false;
        _batchFeature = null;
        _batchSoftBackend = null;
        IFrame? softFrame = _batchSoftFrame;
        _batchSoftFrame = null;
        softFrame?.Dispose();
    }

    private void RunTemporary<TSpec>(IImageBackend fallback, string feature, TSpec spec, SpecOperation<TSpec> operation)
    {
        int stride = Width * 4;
        byte[] rented = ArrayPool<byte>.Shared.Rent(checked(stride * Height));
        IFrame temp = fallback.Create(Width, Height, background: null);
        try
        {
            Span<byte> canonical = rented.AsSpan(0, stride * Height);
            if (!_backend.TryCopyPixels(Frame, canonical, stride))
            {
                throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
            }

            fallback.ImportPixels(temp, canonical, stride);

            if (!operation(fallback, temp, in spec))
            {
                throw new FeatureNotSupportedException(feature, fallback.Name, _engine.Options.Fallback);
            }

            if (!fallback.TryCopyPixels(temp, canonical, stride))
            {
                throw new FeatureNotSupportedException(feature, fallback.Name, _engine.Options.Fallback);
            }

            _backend.ImportPixels(Frame, canonical, stride);
        }
        finally
        {
            temp.Dispose();
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>折线 / 多边形用：<see cref="PolylineSpec"/> 是 ref struct，不能当泛型实参，所以单独走一条路径。</summary>
    private FacadeImage ExecutePoints(string feature, ReadOnlySpan<PointF> points, Rgba32 color, float width, bool closed, StrokeStyle? style, bool filled)
    {
        if (_batchSoftFrame is not null)
        {
            if (!ShouldFlushOnNative)
            {
                ExecutePointsOnBatch(feature, points, color, width, closed, style, filled);
                return this;
            }

            FlushBatch();
        }

        if (!_engine.Options.PreferSoftwareRendering && TryPointOperation(_backend, Frame, points, color, width, closed, style, filled))
        {
            return this;
        }

        if (_engine.Options.Strict)
        {
            throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
        }

        IImageBackend fallback = RequireFallback(feature);
        if (ReferenceEquals(fallback, _backend))
        {
            if (!TryPointOperation(_backend, Frame, points, color, width, closed, style, filled))
            {
                throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
            }

            return this;
        }

        if (_batchDepth > 0)
        {
            EnsureBatchSoft(fallback, feature);
            ExecutePointsOnBatch(feature, points, color, width, closed, style, filled);
            return this;
        }

        string sourceBackend = _backend.Name;
        _fallbackCount++;
        long startTimestamp = Stopwatch.GetTimestamp();

        if (ShouldSwitch())
        {
            MigrateTo(fallback);
            if (!TryPointOperation(fallback, Frame, points, color, width, closed, style, filled))
            {
                throw new FeatureNotSupportedException(feature, fallback.Name, _engine.Options.Fallback);
            }
        }
        else
        {
            int stride = Width * 4;
            byte[] rented = ArrayPool<byte>.Shared.Rent(checked(stride * Height));
            IFrame temp = fallback.Create(Width, Height, background: null);
            try
            {
                Span<byte> canonical = rented.AsSpan(0, stride * Height);
                if (!_backend.TryCopyPixels(Frame, canonical, stride))
                {
                    throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
                }

                fallback.ImportPixels(temp, canonical, stride);

                if (!TryPointOperation(fallback, temp, points, color, width, closed, style, filled))
                {
                    throw new FeatureNotSupportedException(feature, fallback.Name, _engine.Options.Fallback);
                }

                if (!fallback.TryCopyPixels(temp, canonical, stride))
                {
                    throw new FeatureNotSupportedException(feature, fallback.Name, _engine.Options.Fallback);
                }

                _backend.ImportPixels(Frame, canonical, stride);
            }
            finally
            {
                temp.Dispose();
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        RaiseFallback(sourceBackend, fallback.Name, feature, startTimestamp);
        return this;
    }

    private void ExecutePointsOnBatch(string feature, ReadOnlySpan<PointF> points, Rgba32 color, float width, bool closed, StrokeStyle? style, bool filled)
    {
        IImageBackend soft = _batchSoftBackend!;
        if (!TryPointOperation(soft, _batchSoftFrame!, points, color, width, closed, style, filled))
        {
            throw new FeatureNotSupportedException(feature, soft.Name, _engine.Options.Fallback);
        }
    }

    private static bool TryPointOperation(
        IImageBackend backend,
        IFrame frame,
        ReadOnlySpan<PointF> points,
        Rgba32 color,
        float width,
        bool closed,
        StrokeStyle? style,
        bool filled)
    {
        if (filled)
        {
            var polygon = new PolygonSpec(points, color);
            return backend.TryFillPolygon(frame, in polygon);
        }

        var polyline = new PolylineSpec(points, color, width, closed, style);
        return backend.TryDrawPolyline(frame, in polyline);
    }

    private bool ShouldSwitch() => _engine.Options.Fallback switch
    {
        FallbackMode.SwitchAndStay => true,
        FallbackMode.Adaptive => _fallbackCount >= _engine.Options.AdaptiveThreshold,
        _ => false,
    };

    private void RaiseFallback(string sourceBackend, string targetBackend, string feature, long startTimestamp)
    {
        Action<FallbackEvent>? handler = _engine.Options.OnFallback;
        if (handler is null)
        {
            return;
        }

        double elapsed = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        handler(new FallbackEvent(sourceBackend, targetBackend, feature, elapsed));
    }

    private IImageBackend RequireFallback(string feature)
    {
        if (_engine.Options.Fallback == FallbackMode.Disabled)
        {
            throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
        }

        return _engine.GetFallback()
            ?? throw new FeatureNotSupportedException(feature, _backend.Name, _engine.Options.Fallback);
    }

    private void MigrateTo(IImageBackend target)
    {
        if (ReferenceEquals(target, _backend))
        {
            return;
        }

        int stride = Width * 4;
        int length = checked(stride * Height);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        IFrame frame = target.Create(Width, Height, background: null);
        try
        {
            Span<byte> canonical = rented.AsSpan(0, length);
            if (!_backend.TryCopyPixels(Frame, canonical, stride))
            {
                throw new FeatureNotSupportedException(nameof(CopyPixels), _backend.Name, _engine.Options.Fallback);
            }

            target.ImportPixels(frame, canonical, stride);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        IFrame previous = Frame;
        _frame = frame;
        _backend = target;
        IsZeroCopy = false;
        previous.Dispose();
    }

    private void ResizeCore(int width, int height, Sampler sampler)
    {
        if (width == Width && height == Height)
        {
            return;
        }

        var request = new ResizeRequest(width, height, sampler);

        if (!_engine.Options.PreferSoftwareRendering && _backend.TryResize(Frame, in request, out IFrame resized))
        {
            ReplaceFrame(resized, width, height);
            return;
        }

        // 回退：让软后端算一次，再把结果导回当前后端的新帧（不迁移整张图）
        if (_engine.Options.Strict)
        {
            throw new FeatureNotSupportedException(nameof(Resize), _backend.Name, _engine.Options.Fallback);
        }

        string sourceBackend = _backend.Name;
        long startTimestamp = Stopwatch.GetTimestamp();

        IImageBackend fallback = RequireFallback(nameof(Resize));
        if (ReferenceEquals(fallback, _backend))
        {
            throw new FeatureNotSupportedException(nameof(Resize), _backend.Name, _engine.Options.Fallback);
        }

        byte[] sourcePixels = CopyCanonicalPixels(out int sourceStride);
        IFrame source = fallback.Create(Width, Height, background: null);
        try
        {
            fallback.ImportPixels(source, sourcePixels, sourceStride);
            if (!fallback.TryResize(source, in request, out IFrame scaled))
            {
                throw new FeatureNotSupportedException(nameof(Resize), fallback.Name, _engine.Options.Fallback);
            }

            try
            {
                int stride = width * 4;
                var buffer = new byte[checked(stride * height)];
                if (!fallback.TryCopyPixels(scaled, buffer, stride))
                {
                    throw new FeatureNotSupportedException(nameof(Resize), fallback.Name, _engine.Options.Fallback);
                }

                IFrame target = _backend.Create(width, height, background: null);
                try
                {
                    _backend.ImportPixels(target, buffer, stride);
                }
                catch
                {
                    target.Dispose();
                    throw;
                }

                ReplaceFrame(target, width, height);
                _fallbackCount++;
                RaiseFallback(sourceBackend, fallback.Name, nameof(Resize), startTimestamp);
            }
            finally
            {
                scaled.Dispose();
            }
        }
        finally
        {
            source.Dispose();
        }
    }

    internal TextMetrics MeasureSingleLine(string text, TextStyle style)
    {
        var spec = new TextSpec(text, PointF.Empty, style);

        if (!_engine.Options.PreferSoftwareRendering && _backend.TryMeasureText(Frame, in spec, out TextMetrics metrics))
        {
            return metrics;
        }

        if (_engine.Options.Strict)
        {
            throw new FeatureNotSupportedException(nameof(MeasureText), _backend.Name, _engine.Options.Fallback);
        }

        IImageBackend fallback = RequireFallback(nameof(MeasureText));
        if (ReferenceEquals(fallback, _backend))
        {
            throw new FeatureNotSupportedException(nameof(MeasureText), _backend.Name, _engine.Options.Fallback);
        }

        IFrame probe = fallback.Create(1, 1, background: null);
        try
        {
            if (fallback.TryMeasureText(probe, in spec, out TextMetrics fallbackMetrics))
            {
                return fallbackMetrics;
            }
        }
        finally
        {
            probe.Dispose();
        }

        throw new FeatureNotSupportedException(nameof(MeasureText), fallback.Name, _engine.Options.Fallback);
    }

    private void SaveCore(Stream stream, SaveOptions save, ImageFormat format)
    {
        ValidateSaveOptions(save);

        var request = new EncodeRequest(format, save.Quality, save.CompressionLevel, save.PreserveMetadata);
        EncodableFrame encodable = PrepareForEncode(save, format);
        try
        {
            IFrame source = encodable.Frame;

            if (_backend.SupportedEncodeFormats.Contains(format))
            {
                try
                {
                    _backend.Encode(source, stream, in request);
                    return;
                }
                catch (NotSupportedException)
                {
                    // 落到回退路径
                }
                catch (Exception ex) when (ex is not ImageBackendException)
                {
                    throw new ImageBackendException(_backend.Name, $"编码为 {format} 失败。", ex);
                }
            }

            if (_engine.Options.Strict)
            {
                throw new FeatureNotSupportedException("Encode", _backend.Name, _engine.Options.Fallback);
            }

            string sourceBackend = _backend.Name;
            long startTimestamp = Stopwatch.GetTimestamp();

            IImageBackend fallback = RequireFallback("Encode");
            if (ReferenceEquals(fallback, _backend))
            {
                throw new FeatureNotSupportedException("Encode", _backend.Name, _engine.Options.Fallback);
            }

            int stride = Width * 4;
            int length = checked(stride * Height);
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            IFrame temp = fallback.Create(Width, Height, background: null);
            try
            {
                Span<byte> canonical = rented.AsSpan(0, length);
                if (!_backend.TryCopyPixels(source, canonical, stride))
                {
                    throw new FeatureNotSupportedException("Encode", _backend.Name, _engine.Options.Fallback);
                }

                fallback.ImportPixels(temp, canonical, stride);
                fallback.Encode(temp, stream, in request);
                _fallbackCount++;
                RaiseFallback(sourceBackend, fallback.Name, "Encode", startTimestamp);
            }
            finally
            {
                temp.Dispose();
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        finally
        {
            encodable.Temp?.Dispose();
        }
    }

    /// <summary>
    /// 编码前的准备：目标格式没有 alpha（JPEG / BMP）时，先按 <see cref="SaveOptions.BackgroundColor"/>
    /// 把透明像素合成到不透明的临时帧上；已经全不透明则直接用原帧（plan §3.2 / M8）。
    /// </summary>
    private EncodableFrame PrepareForEncode(SaveOptions save, ImageFormat format)
    {
        FlushBatch();

        if (format.SupportsAlpha())
        {
            return new EncodableFrame(Frame, null);
        }

        int stride = Width * 4;
        int length = checked(stride * Height);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        IFrame? frame = null;
        try
        {
            Span<byte> canonical = rented.AsSpan(0, length);
            if (!_backend.TryCopyPixels(Frame, canonical, stride))
            {
                throw new FeatureNotSupportedException(nameof(CopyPixels), _backend.Name, _engine.Options.Fallback);
            }

            if (!CompositeOverBackground(rented, stride, save.BackgroundColor))
            {
                return new EncodableFrame(Frame, null);
            }

            frame = _backend.Create(Width, Height, background: null);
            _backend.ImportPixels(frame, canonical, stride);
            return new EncodableFrame(frame, frame);
        }
        catch
        {
            frame?.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>把直通 RGBA 就地合成到 <paramref name="background"/> 上；返回 false 表示本来就全不透明。</summary>
    private bool CompositeOverBackground(byte[] pixels, int stride, Rgba32 background)
    {
        int width = Width;
        int height = Height;
        bool hasTransparency = false;

        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, stride * height));
        foreach (uint word in words)
        {
            if ((word & 0xFF000000u) != 0xFF000000u)
            {
                hasTransparency = true;
                break;
            }
        }

        if (!hasTransparency)
        {
            return false;
        }

        byte backgroundRed = background.R;
        byte backgroundGreen = background.G;
        byte backgroundBlue = background.B;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int offset = row + (x * 4);
                int alpha = pixels[offset + 3];
                if (alpha == 255)
                {
                    continue;
                }

                int inverse = 255 - alpha;
                pixels[offset] = (byte)(((pixels[offset] * alpha) + (backgroundRed * inverse) + 127) / 255);
                pixels[offset + 1] = (byte)(((pixels[offset + 1] * alpha) + (backgroundGreen * inverse) + 127) / 255);
                pixels[offset + 2] = (byte)(((pixels[offset + 2] * alpha) + (backgroundBlue * inverse) + 127) / 255);
                pixels[offset + 3] = 255;
            }
        }

        return true;
    }

    /// <summary>编码用的一帧：可能是指向临时不透明副本的所有权。</summary>
    private readonly struct EncodableFrame
    {
        internal EncodableFrame(IFrame frame, IFrame? temp)
        {
            Frame = frame;
            Temp = temp;
        }

        internal IFrame Frame { get; }

        internal IFrame? Temp { get; }
    }

    // ---- 辅助 ----

    private byte[] CopyCanonicalPixels(out int stride) => CopyCanonicalPixels(Frame, out stride);

    private byte[] CopyCanonicalPixels(IFrame frame, out int stride)
    {
        stride = Width * 4;
        var buffer = new byte[checked(stride * Height)];
        if (!_backend.TryCopyPixels(frame, buffer, stride))
        {
            throw new FeatureNotSupportedException(nameof(CopyPixels), _backend.Name, _engine.Options.Fallback);
        }

        return buffer;
    }

    private void ReplaceFrame(IFrame frame, int width, int height)
    {
        IFrame previous = Frame;
        _frame = frame;
        previous.Dispose();
        IsZeroCopy = false;
        _metadata = new ImageMetadata(_metadata.Format, width, height, _metadata.Orientation, _metadata.HasAlpha, _metadata.IccProfile);
    }

    private static void Fill(byte[] buffer, Rgba32 color)
    {
        uint packed = (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24));
        MemoryMarshal.Cast<byte, uint>(buffer.AsSpan()).Fill(packed);
    }

    private static void Blit(
        byte[] source,
        int sourceStride,
        int sourceWidth,
        int sourceHeight,
        byte[] destination,
        int destinationStride,
        int destinationWidth,
        int destinationHeight,
        int placeX,
        int placeY)
    {
        int startX = Math.Max(0, placeX);
        int startY = Math.Max(0, placeY);
        int endX = Math.Min(destinationWidth, placeX + sourceWidth);
        int endY = Math.Min(destinationHeight, placeY + sourceHeight);

        for (int y = startY; y < endY; y++)
        {
            int sourceOffset = ((y - placeY) * sourceStride) + ((startX - placeX) * 4);
            int destinationOffset = (y * destinationStride) + (startX * 4);
            Buffer.BlockCopy(source, sourceOffset, destination, destinationOffset, (endX - startX) * 4);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_frame is null)
        {
            throw new ObjectDisposedException(nameof(FacadeImage));
        }
    }

    public void Dispose()
    {
        IFrame? batchSoft = _batchSoftFrame;
        _batchSoftFrame = null;
        _batchSoftBackend = null;
        _batchFeature = null;
        _batchDepth = 0;
        batchSoft?.Dispose();

        IFrame? frame = _frame;
        _frame = null;
        frame?.Dispose();
    }
}
