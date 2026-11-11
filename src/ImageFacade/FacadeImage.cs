using System.Buffers;
using System.Runtime.InteropServices;
using ImageFacade.Backends;
using ImageFacade.Internal;

namespace ImageFacade;

/// <summary>
/// 一张图的句柄：包装后端私有的帧，所有绘制原地修改并返回 <c>this</c>。
/// 一帧一实例、不跨线程；用完立即 <see cref="Dispose"/>。
/// </summary>
/// <remarks>
/// 后端不支持的能力直接抛 <see cref="FeatureNotSupportedException"/>（含能力名与后端名），
/// 不做跨后端回退；需要另一种渲染时显式换后端。
/// </remarks>
public sealed class FacadeImage : IDisposable
{
    private readonly ImageEngine _engine;
    private IImageBackend _backend;
    private IFrame? _frame;
    private ImageMetadata _metadata;

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

    /// <summary>true 表示帧内存直接来自 <c>FromPixels(..., wrap: true)</c>。</summary>
    public bool IsZeroCopy { get; internal set; }

    internal IFrame Frame => _frame ?? throw new ObjectDisposedException(nameof(FacadeImage));

    internal IImageBackend Backend => _backend;

    /// <summary>Pipeline（letterbox / 张量化）需要借同一个引擎造临时图。</summary>
    internal ImageEngine Engine => _engine;

    // ---- 尺寸 ----

    /// <summary>纯拉伸（等价于 <see cref="ResizeMode.Stretch"/>）。</summary>
    public FacadeImage Resize(int width, int height)
    {
        ThrowIfDisposed();
        ImageEngine.ValidateSize(width, height);
        ResizeCore(width, height, Sampler.Bicubic);
        return this;
    }

    /// <summary>六种模式；裁剪与补边由门面用像素搬运完成，保证各后端几何一致（plan §3.3）。</summary>
    public FacadeImage Resize(ResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ImageEngine.ValidateSize(options.Width, options.Height);

        ResizePlan plan = ResizePlanner.Plan(Width, Height, options.Width, options.Height, options.Mode);

        // 没有裁剪 / 补边：直接换帧，不多拷一次
        if (!plan.NeedsComposite)
        {
            ResizeCore(plan.ScaledWidth, plan.ScaledHeight, options.Sampler);
            return this;
        }

        // 合成需要的两块缓冲都可能到 MB 级（1080p 缩放约 8 MB），走池避免每帧进 LOH
        byte[] scaled = ArrayPool<byte>.Shared.Rent(checked(plan.ScaledWidth * 4 * plan.ScaledHeight));
        byte[] output = ArrayPool<byte>.Shared.Rent(checked(plan.OutputWidth * 4 * plan.OutputHeight));

        try
        {
            int scaledStride = plan.ScaledWidth * 4;
            int outputStride = plan.OutputWidth * 4;

            // 先缩放再合成：中间帧只在这里用一次，失败时不留下"已缩放未合成"的中间态
            IFrame scaledFrame = ResizeFrame(plan.ScaledWidth, plan.ScaledHeight, options.Sampler);
            try
            {
                if (!_backend.TryCopyPixels(scaledFrame, scaled.AsSpan(0, scaledStride * plan.ScaledHeight), scaledStride))
                {
                    throw new FeatureNotSupportedException(nameof(CopyPixels), _backend.Name);
                }
            }
            finally
            {
                scaledFrame.Dispose();
            }

            Span<byte> outputSpan = output.AsSpan(0, outputStride * plan.OutputHeight);
            Fill(outputSpan, options.PadColor);
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
                _backend.ImportPixels(frame, outputSpan, outputStride);
            }
            catch
            {
                frame.Dispose();
                throw;
            }

            ReplaceFrame(frame, plan.OutputWidth, plan.OutputHeight);
            return this;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scaled);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    public FacadeImage Clear(Rgba32 color)
    {
        ThrowIfDisposed();
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
            throw new FeatureNotSupportedException(nameof(DrawText), _backend.Name);
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

    /// <summary>
    /// ImageSharp 风格的原地作用域：把一串 <see cref="FacadeImage"/> 调用收进一个 lambda。
    /// </summary>
    /// <remarks>
    /// 纯语法糖：<c>img.Mutate(x => x.DrawLine(...))</c> 与直接 <c>img.DrawLine(...)</c> 完全等价——
    /// <see cref="FacadeImage"/> 的每个 <c>Draw*</c> 本身就地修改像素并返回 <c>this</c>，
    /// 不需要 ImageSharp 的 <c>IImageProcessingContext</c>（那个上下文存在的目的是避免中间副本）。
    /// 后端不受影响：ImageSharp 后端在自己的 <c>Try*</c> 里照旧调 <c>Image.Mutate(...)</c>。
    /// </remarks>
    public FacadeImage Mutate(Action<FacadeImage> draw)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(draw);
        draw(this);
        return this;
    }

    // ---- 生命周期 ----

    public FacadeImage Clone()
    {
        ThrowIfDisposed();
        IFrame copy = _backend.Clone(Frame);
        return new FacadeImage(_engine, _backend, copy, _metadata);
    }

    /// <summary>
    /// ImageSharp 风格的 <c>Clone(x => ...)</c>：先复制一份，在副本上执行 <paramref name="draw"/>，返回副本。
    /// 原图不受影响；回调抛异常时副本会被释放。
    /// </summary>
    public FacadeImage Clone(Action<FacadeImage> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        FacadeImage copy = Clone();
        try
        {
            draw(copy);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    // ---- 输出 ----

    /// <summary>
    /// 保存到文件，格式按扩展名推断。
    /// </summary>
    /// <remarks>
    /// 直接用 <see cref="File.Create(string)"/> 写目标路径：编码失败时会把写了一半的文件删掉，
    /// 并抛 <see cref="ImageSaveFailedException"/>（内含原始异常）。
    /// <b>目标路径原有文件在开始时就被截断，失败后不恢复</b>——需要保留原件请先自行备份，
    /// 或改用 <see cref="Encode"/> 拿字节后自己落盘。
    /// </remarks>
    /// <exception cref="ImageSaveFailedException">写入或编码失败；文件已删除（见 <see cref="ImageSaveFailedException.FileDeleted"/>）。</exception>
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

        FileStream? stream = null;
        try
        {
            stream = File.Create(path);
            using (stream)
            {
                SaveCore(stream, save, format);
            }
        }
        catch (Exception ex) when (stream is not null)
        {
            // 能走到这里说明文件已经被创建 / 截断过，留下的是半截内容：删掉它再报错。
            // File.Create 自己失败的情况（权限、目录不存在）走不到这里——那时文件没被碰过，不能删。
            throw ImageSaveFailedException.For(path, ex);
        }
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

    /// <summary>
    /// 异步保存到文件。编码成功后才碰磁盘；写盘失败同样删除半截文件并抛
    /// <see cref="ImageSaveFailedException"/>。被取消时删除文件后原样抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <exception cref="ImageSaveFailedException">写入或编码失败；文件已删除。</exception>
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

        // 编码失败时还没碰过文件，直接抛原异常
        byte[] encoded = await EncodeToArrayAsync(save, format, cancellationToken).ConfigureAwait(false);

        try
        {
            await File.WriteAllBytesAsync(path, encoded, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ImageSaveFailedException.TryDeleteFile(path);   // 取消也要删掉半截文件，但异常照原样抛
            throw;
        }
        catch (Exception ex)
        {
            throw ImageSaveFailedException.For(path, ex);
        }
    }

    public async Task SaveAsync(Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(stream);

        SaveOptions save = options ?? new SaveOptions();
        ImageFormat format = save.Format ?? ImageFormat.Png;
        ValidateSaveOptions(save);
        EnsureCanEncode(format);

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
        EnsureCanEncode(format);

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

    /// <summary>
    /// 编码能力检查。同步与异步路径共用：异步分支不能因为绕开 <see cref="SaveCore"/>
    /// 就放过"后端不支持该格式"。
    /// </summary>
    private void EnsureCanEncode(ImageFormat format)
    {
        if (!_backend.SupportedEncodeFormats.Contains(format))
        {
            throw new FeatureNotSupportedException("Encode", _backend.Name);
        }
    }

    // ---- 像素互操作 ----

    public Rgba32 GetPixel(int x, int y)
    {
        ThrowIfDisposed();
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
            : throw new FeatureNotSupportedException(nameof(GetPixel), _backend.Name);
    }

    public void CopyPixelsTo(Span<byte> destination, int destinationStride, PixelFormat format = PixelFormat.Rgba32)
    {
        ThrowIfDisposed();

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
                throw new FeatureNotSupportedException(nameof(CopyPixelsTo), _backend.Name);
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
                throw new FeatureNotSupportedException(nameof(CopyPixelsTo), _backend.Name);
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
        return _backend.TryAcquirePixels(Frame, out PixelLease lease)
            ? lease
            : throw new FeatureNotSupportedException(nameof(AcquirePixels), _backend.Name);
    }

    // ---- 路由 ----

    private FacadeImage Execute<TSpec>(string feature, in TSpec spec, SpecOperation<TSpec> operation)
    {
        ThrowIfDisposed();

        if (!operation(_backend, Frame, in spec))
        {
            throw new FeatureNotSupportedException(feature, _backend.Name);
        }

        return this;
    }

    /// <summary>折线 / 多边形用：<see cref="PolylineSpec"/> 是 ref struct，不能当泛型实参，所以单独走一条路径。</summary>
    private FacadeImage ExecutePoints(string feature, ReadOnlySpan<PointF> points, Rgba32 color, float width, bool closed, StrokeStyle? style, bool filled)
    {
        if (!TryPointOperation(_backend, Frame, points, color, width, closed, style, filled))
        {
            throw new FeatureNotSupportedException(feature, _backend.Name);
        }

        return this;
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

    private void ResizeCore(int width, int height, Sampler sampler)
    {
        if (width == Width && height == Height)
        {
            return;
        }

        ReplaceFrame(ResizeFrame(width, height, sampler), width, height);
    }

    /// <summary>
    /// 真正做一次拉伸，返回**新帧**（不改当前帧）。尺寸相同则返回当前帧的克隆，
    /// 这样调用方总能无条件 Dispose 拿到的帧。
    /// </summary>
    private IFrame ResizeFrame(int width, int height, Sampler sampler)
    {
        if (width == Width && height == Height)
        {
            return _backend.Clone(Frame);
        }

        var request = new ResizeRequest(width, height, sampler);
        if (!_backend.TryResize(Frame, in request, out IFrame resized))
        {
            throw new FeatureNotSupportedException(nameof(Resize), _backend.Name);
        }

        return resized;
    }

    internal TextMetrics MeasureSingleLine(string text, TextStyle style)
    {
        var spec = new TextSpec(text, PointF.Empty, style);
        if (!_backend.TryMeasureText(Frame, in spec, out TextMetrics metrics))
        {
            throw new FeatureNotSupportedException(nameof(MeasureText), _backend.Name);
        }

        return metrics;
    }

    private void SaveCore(Stream stream, SaveOptions save, ImageFormat format)
    {
        ValidateSaveOptions(save);
        EnsureCanEncode(format);

        var request = new EncodeRequest(format, save.Quality, save.CompressionLevel, save.PreserveMetadata);
        EncodableFrame encodable = PrepareForEncode(save, format);
        try
        {
            try
            {
                _backend.Encode(encodable.Frame, stream, in request);
            }
            catch (FeatureNotSupportedException)
            {
                throw;
            }
            catch (NotSupportedException ex)
            {
                // 后端因运行时条件（编码器缺失、参数不支持）拒绝：同样归为能力异常
                throw new FeatureNotSupportedException("Encode", _backend.Name, ex);
            }
            catch (Exception ex) when (ex is not ImageBackendException)
            {
                throw new ImageBackendException(_backend.Name, $"编码为 {format} 失败。", ex);
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
                throw new FeatureNotSupportedException(nameof(CopyPixels), _backend.Name);
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

    private void ReplaceFrame(IFrame frame, int width, int height)
    {
        IFrame previous = Frame;
        _frame = frame;
        previous.Dispose();
        IsZeroCopy = false;
        _metadata = new ImageMetadata(_metadata.Format, width, height, _metadata.Orientation, _metadata.HasAlpha, _metadata.IccProfile);
    }

    private static void Fill(Span<byte> buffer, Rgba32 color)
    {
        uint packed = (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24));
        MemoryMarshal.Cast<byte, uint>(buffer).Fill(packed);
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

        // 引擎释放后后端可能已经把字体 / 画布收掉了，继续用这张图会拿到半失效的对象
        if (_engine.IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(ImageEngine),
                "所属的 ImageEngine 已释放，这张图不能再使用。请先完成图片操作，再释放引擎。");
        }
    }

    public void Dispose()
    {
        IFrame? frame = _frame;
        _frame = null;
        frame?.Dispose();
    }
}
