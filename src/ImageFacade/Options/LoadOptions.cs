namespace ImageFacade;

public sealed class LoadOptions
{
    /// <summary>按 EXIF 方向自动转正（用户拿到照片就是正的）。</summary>
    public bool AutoOrient { get; init; } = true;

    /// <summary>只透传 ICC / 方向，不保证 EXIF 往返。解码阶段仅作为提示，门面统一走 <see cref="AutoOrient"/>。</summary>
    public bool PreserveMetadata { get; init; } = true;

    /// <summary>v1 只接受 0（只处理第 0 帧）。</summary>
    public int FrameIndex { get; init; }

    /// <summary>
    /// 解码后允许的最大像素数（宽 × 高）。默认 1 亿像素（约 400 MB 的 RGBA 缓冲），
    /// <c>&lt;= 0</c> 表示不限制。
    /// </summary>
    /// <remarks>
    /// 用来阻断"小文件声明巨大尺寸"的解压炸弹：能读到头部时在**解码前**就拒绝，
    /// 读不到头部时在解码后再兜一次底。<see cref="ImageEngine.Create"/> 不受这个上限约束——
    /// 那里的尺寸来自你写的代码，不是外部输入。
    /// </remarks>
    public long MaxPixels { get; init; } = 100_000_000;

    /// <summary>
    /// 加载时允许读入的最大**编码字节数**（压缩后的大小）。默认 <c>0</c>：不限制，
    /// 与加这个选项之前的行为完全一致——现有调用方不会因为升级突然开始抛异常。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 作用于 <see cref="ImageEngine.Load(Stream, LoadOptions)"/> /
    /// <see cref="ImageEngine.LoadAsync(Stream, LoadOptions, CancellationToken)"/> 的读入过程：
    /// 累计读到的字节数超过上限就**停止读取**并抛 <see cref="ImageSizeLimitExceededException"/>，
    /// 不会先把这个超大文件整个吞进内存。已经拿到完整字节的 <c>Load(ReadOnlySpan&lt;byte&gt;)</c>
    /// 不受这个上限约束（内存已经分配过了，拦不住也没有意义）。
    /// </para>
    /// <para>
    /// 也作用于 <see cref="ImageEngine.Load(string, LoadOptions)"/> /
    /// <see cref="ImageEngine.LoadAsync(string, LoadOptions, CancellationToken)"/>：那条路径走只读内存映射，
    /// 文件长度在建立映射之前就能读到，所以超过上限时**一个字节都不读**就抛异常。
    /// </para>
    /// <para>
    /// 与 <see cref="MaxPixels"/> 是两个独立维度，互不影响：20 KB 的 PNG 可以声明 30000×30000
    /// （字节数很小、像素数极大，由 <see cref="MaxPixels"/> 拦），任意压缩的高熵图片也可以
    /// 是合法尺寸但几百 MB（由本选项拦）。两道闸各自挡一类输入。
    /// </para>
    /// </remarks>
    public long MaxEncodedBytes { get; init; }
}
