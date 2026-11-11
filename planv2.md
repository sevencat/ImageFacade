# planv2 — 安全与热路径优化（第一批）

> 本文只覆盖第一批三项改动：解码尺寸上限、`PixelLease` 幂等与 Skia 字体缓存、OpenCV/Skia 像素搬运。
> 其余已识别问题（`wrap: true` 死路、`TryDrawFrame` 三后端语义不一致、`ImageMetadata` 不更新、
> 两套排版引擎、`Nms` O(n²)、`Save` 非原子等）列入文末「后续批次」，本批不动。

## 0. 基线

- 构建：`dotnet build ImageFacade.slnx -c Release`，0 警告 0 错误。
- 测试：`dotnet test`，202 通过（ImageFacade 98 / OpenCv4 28 / OpenCv5 28 / ImageSharp2 15 / ImageSharp3 15 / Sdcb 18）。
- 已修构建阻塞：`src/ImageFacade.SdcbSimdCodecs/ImageFacade.SdcbSimdCodecs.csproj` 的 XML 注释里含
  `--skip-duplicate`，XML 注释不允许出现 `--`，整个解决方案无法加载（MSB4025）。改为文字描述。

三条改动都必须在此之前保持 202 全绿，改动后全绿并新增用例。

---

## 1. 解码尺寸上限（对应评审第 1 条）

### 问题

`ImageEngine.ValidateSize` 只作用于 `Create` / `FromPixels`，`Load` / `LoadAsync` 完全不校验：

- `ImageEngine.cs:59-92` 任意字节直接交给 `Backend.Decode`；
- `ImageEngine.cs:46-57` / `:106-121` 的 `Stream` 路径 `CopyTo` 无上限。

20 KB 的 PNG 声明 30000×30000，`SKBitmap` / `Image.Load` / `Cv2.ImDecode` 会按声明直接分配
3.6 GB。四个后端都拿得到头部宽高，但没有一处比较上限。

### 设计

**校验放在门面，不进 SPI。** 理由：不破坏 `IImageBackend` 的公开契约，四个后端一次覆盖；
后端各自能"提前拿头"的差异不需要暴露成新接口。

两道闸：

1. **解码前嗅探**（真正防住内存暴涨）：`Internal/ImageHeaderReader.TryReadDimensions(data, out w, out h)`
   纯托管、零分配地解析头部尺寸。
2. **解码后兜底**（防住嗅探不认识的格式）：`Load` 拿到 `IFrame` 后比较 `Width * Height`，
   超限则 `frame.Dispose()` 并抛异常。

### 新增公开 API（只加不改）

```csharp
// LoadOptions
/// <summary>
/// 解码后允许的最大像素数（宽 × 高）。默认 100_000_000（约 400 MB RGBA）；
/// <= 0 表示不限制。用于阻断"小文件声明巨大尺寸"的解压炸弹。
/// </summary>
public long MaxPixels { get; init; } = 100_000_000;
```

```csharp
/// <summary>解码出的图像超过 LoadOptions.MaxPixels。</summary>
public sealed class ImageSizeLimitExceededException : Exception
{
    public int Width { get; }        // 声明 / 实际的宽（嗅探不到时可为 0）
    public int Height { get; }
    public long MaxPixels { get; }
}
```

为什么不复用现成类型：调用方需要把"图太大"和"图损坏"分开处理（HTTP 413 vs 400 / 500），
`ArgumentException` 无法区分选项写错还是数据超限。

### 头部嗅探支持范围

| 格式 | 依据 | 说明 |
|---|---|---|
| PNG | IHDR 宽高 | 8 字节签名 + 长度/类型校验 |
| JPEG | SOFn 段 | 跳过 `0xFFD8`，扫到 `SOF0..SOF3/SOF5..SOF7/SOF9..SOF11/SOF13..SOF15` |
| BMP | `BITMAPINFOHEADER` | 含负高度（自底向上）取绝对值 |
| GIF | Logical Screen Descriptor | 小端 u16 |
| WebP | VP8X / VP8L / VP8 | 三种头都覆盖 |
| TIFF | IFD0 的 ImageWidth/ImageLength | 大小端按 `II`/`MM` 判定 |
| ICO | ICONDIR 首个条目 | 0 表示 256，按 0 值处理时不参与上限 |
| PNM | P5/P6 的 maxval 行 | 可选 |

嗅探失败（未知格式、数据截断、字段异常）一律返回 `false`，退化为"只做解码后兜底"，不抛异常——
嗅探器本身不能成为新的失败点。

### 改动点

- `src/ImageFacade/Options/LoadOptions.cs`：加 `MaxPixels`。
- `src/ImageFacade/Exceptions.cs`：加 `ImageSizeLimitExceededException`。
- 新增 `src/ImageFacade/Internal/ImageHeaderReader.cs`。
- `src/ImageFacade/ImageEngine.cs`：
  - `Load(ReadOnlySpan<byte>)` 与 `LoadAsync(ReadOnlyMemory<byte>)` 各加 `ValidateDimensions(...)`
    （解码前）+ `EnsureWithinLimit(frame, maxPixels)`（解码后）。
  - 注意 `LoadAsync` 走 `IAsyncImageBackend` 的分支同样要过这两道。
- README「出错时」表格补一行，并在「30 秒上手」之后补一小节说明默认上限与关闭方式。

### 不做

`Load(Stream)` 的**编码字节数**上限（`CopyTo` 无限制）需要另一个选项 `MaxEncodedBytes`，
与本条的"像素"是两个维度，放后续批次。本批只在 README 注明：自带压缩炸弹防护的调用方
应先做长度检查再交给 `Load`。

---

## 2. `PixelLease` 幂等 + Skia 默认字体（对应评审第 2、3 条）

### 2.1 `PixelLease.Dispose` 非幂等

`PixelLease.cs:28` 的 `Dispose() => _onDispose?.Invoke()` 没有保护。软后端
`SoftwareBackend.cs:313-325` 的回调里 `ArrayPool<byte>.Shared.Return(buffer)`：
重复 `Dispose` 会把同一数组还回池子两次，池子随后可能把它同时发给两个租用者 → 数据串写。

修法（两层）：

1. `PixelLease` 加 `private bool _disposed`，`Dispose()` 首行判断后置位。`ref struct` 的
   `Dispose` 不是 `readonly`，可以直接改字段；`using (var lease = ...)` 用的是同一个局部变量，
   重复调用会被正确吞掉。
2. 软后端回调内部再加一层 `int disposed` + `Interlocked.Exchange`：`ref struct` 被按值复制时
   标志各自独立，第二层保护用引用类型捕获的计数器堵住"复制后各自 Dispose"这条路径。

`ImportPixels` 类回调（Skia / ImageSharp / OpenCV）本身幂等，不额外加锁。

### 2.2 Skia 可能缓存并释放全局 `SKTypeface.Default`

`SkiaBackend.cs:521-525`：`SKTypeface.FromData(builtIn) ?? SKTypeface.Default` 的结果被塞进
`_typefaces`，而 `Dispose()`（`:462-470`）遍历释放所有值。一旦走 fallback 分支，
进程内其他 Skia 使用方会拿到已释放的 typeface。

修法：`SKTypeface.Default` 与任何"创建失败"的结果都不入缓存。

```
TryGetValue 命中且非 null → 直接返回（不持有）
否则 TryCreateBuiltIn()
  ├ 成功 → GetOrAdd；如果本次创建没被采纳，Dispose 掉自己这份
  └ 失败 → ownsTypeface = false; return SKTypeface.Default   // 不入缓存
```

顺带把 `Dispose()` 的遍历改成跳过 `null`（防御性，正常情况下不会有 `null` 进来）。

### 改动点

- `src/ImageFacade/Backends/PixelLease.cs`
- `src/ImageFacade.SdcbSimdCodecs/SoftwareBackend.cs`（`TryAcquirePixels`）
- `src/ImageFacade.SkiaSharp/SkiaBackend.cs`（`ResolveTypeface` / `Dispose`）

### 测试

- `PixelLease` 直接单测：构造带计数的回调，`Dispose` 两次断言只调用一次。
- 软后端：`AcquirePixels` → `Dispose` 两次 → 再取一次，像素仍正确（不进池子双归还分支）。
- Skia：连续 `DrawText` 两次并在中间 `engine.Dispose()`，不出现访问已释放对象。

---

## 3. OpenCV / Skia 像素搬运（对应评审第 16、17 条）

### 3.1 OpenCV `SwapRedBlue` 逐像素标量

`OpenCvBackend.cs:531-552` 每像素 4 次字节读写 + 循环开销；1080p 每次 `FromPixels` /
`CopyPixelsTo` 要跑 200 万次。软后端 `PixelOps.cs:268-292` 已有逐字节等价的 `Vector128` 实现
（一次 shuffle 处理 4 像素）。

修法：按行做 `Vector128.Shuffle`，行尾不足 16 字节走标量；`Vector128.IsHardwareAccelerated`
为 false 时整行标量。保持"导入导出共用一份实现"的既有约定（交换是对合运算），不引入
`CvtColor`（那需要额外的 Mat 中转，多一次全图拷贝）。

不动的地方：`TryGetPixel`（单像素，向量化无意义）、`BlendGlyphs`（已是逐字形包围盒）。

### 3.2 Skia 的导入导出逐像素

- `SkiaBackend.cs:381-413` `CopyStraight`：预乘 → 直通，每像素 3 次整数除法。
- `SkiaBackend.cs:415-448` `ImportPixels`：直通 → 预乘，每像素 3 次乘除。

修法：交给 Skia 自己做 alpha type 转换（`SkConvertPixels` 内部的 unpremul 用倒数表 + SIMD，
比逐像素除法快一个量级）：

```
导出：bitmap.PeekPixels().ReadPixels(SKImageInfo(w, h, Rgba8888, Unpremul), dstPtr, dstStride)
导入：new SKPixmap(SKImageInfo(w, h, Rgba8888, Unpremul), srcPtr, srcStride)
           .ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes)
```

两者都返回 `bool`；返回 `false` 时**回退到现有标量实现**（保留原代码作为 `CopyStraightScalar` /
`ImportPixelsScalar`），保证"原生路径不可用时行为不变"。

兼容性：`SKPixmap.ReadPixels(SKImageInfo, IntPtr, int)` 与 `SKBitmap.PeekPixels()` 都是
SkiaSharp 3.x 已有的成员（4.x 保留），不越界到 4.x 专有 API。

预期偏差：Skia 的 unpremul 用倒数表四舍五入，与本仓库 `(c*255 + a/2)/a` 在半透明像素上
可能有 ±1 差异。README 已声明"跨后端不保证逐像素一致"，本批在测试里用容差断言锁定这一行为。

### 3.3 测试

- OpenCV：`width = 3 / 7 / 11 / 13`（行字节数不是 16 的倍数，覆盖行尾标量）的 `FromPixels` →
  `CopyPixels` 往返逐字节比对，以及 `Bgra32` 导出的通道顺序；现有 `CopyPixels_Bgra_Swaps_Channels`
  继续守着 2×2 小图。
- Skia：半透明像素（A = 64 / 128 / 200 / 254，宽度取 3 / 4 / 5 / 7）`CopyPixels` → 读回，
  通道偏差 ≤ 3。容差不取 1 是因为**预乘本身有损**：A 越小时量化误差越大，与转换实现无关；
  不透明像素仍要求逐字节相等（现有断言不变）。A 取 1 / 2 这类极值时偏差可达几十，
  属于 8 位预乘存储的固有精度，不进测试。

---

## 4. 执行顺序

1. 写 `planv2.md`（本文）。
2. ① 尺寸上限：`LoadOptions` + 异常 + `ImageHeaderReader` + `ImageEngine` 两道闸 + 测试。
3. ② `PixelLease` 幂等 + 软后端回调 + Skia 字体缓存 + 测试。
4. ③ OpenCV `SwapRedBlue` 向量化 + Skia 原生 `ReadPixels` + 测试。
5. 每一步单独 `dotnet build` + `dotnet test`，最后跑一次全量。
6. README：新增小节 + 异常表一行。

## 5. 验收

- [x] `dotnet build ImageFacade.slnx -c Release` 0 警告 0 错误（各工程 `TreatWarningsAsErrors=true`）。
- [x] `dotnet test ImageFacade.slnx -c Release` 全绿，236 通过（基线 202，新增 34）。
- [x] 声明 30000×30000 的 PNG 头在 `Load` 时抛 `ImageSizeLimitExceededException`，且**没有**分配像素
      （用"从不解码"的测试后端证明：抛的是尺寸异常，不是解码异常）。
- [x] `MaxPixels = 0` 时行为与改动前完全一致（现有 202 个用例即回归基线）。
- [x] 重复 `Dispose` 的 `PixelLease` 不会二次归还 `ArrayPool`。
- [x] 半透明像素经 Skia 导入导出，通道偏差 ≤ 1（实测 200 → 199）；不透明像素逐字节相等。
- [x] OpenCV 非 16 字节对齐宽度（3 / 7 / 11 / 13）往返逐字节相等。

## 6. 落地记录

改动：新增 `Internal/ImageHeaderReader.cs` + `ImageSizeLimitExceededException` + `LoadOptions.MaxPixels`；
`ImageEngine` 两道闸；`PixelLease.Dispose` 幂等；软后端回调加 `Interlocked` 二次保护；
`SkiaBackend.ResolveTypeface` 不再缓存 `SKTypeface.Default`；OpenCV `SwapRedBlue` 向量化；
Skia 导入导出改走 `SKPixmap.ReadPixels`（原生 alpha 类型转换，失败回退原标量实现）。

测试：+34 个用例（尺寸上限 15、`PixelLease` 3、Skia 字体隔离 1、Skia 像素往返 6、OpenCV 通道交换 4 等）。

### 实测（win-x64，1920×1080，Release，100 次均值）

| 项 | 改动前 | 改动后 | 倍数 |
|---|---:|---:|---:|
| OpenCV `SwapRedBlue`（4 字节通道交换内核） | 2.256 ms | 0.470 ms | **4.8×** |
| Skia 去预乘导出（旧标量 vs 原生 `ReadPixels`） | 30.809 ms | 4.799 ms | **6.4×** |
| OpenCV `FromPixels` + `CopyPixels`（端到端） | — | 3.553 ms | — |

Skia 端到端 4.799 ms 里包含一次 `Pin` + 原生转换；旧的标量内核单独跑就要 30.8 ms，
所以端到端提升与内核倍数同量级。`SwapRedBlue` 只到 4.8× 是因为这一步是纯内存搬运，
已经接近内存带宽上限。

另外确认：把 Skia 的标量兜底临时改成抛异常后 123 个测试仍全过 → 原生路径在两个方向上
都是**实际生效**的，不是静默回退。

## 7. 后续批次（本批不做）

| 编号 | 问题 | 位置 |
|---|---|---|
| 7 | `wrap: true` 零拷贝全后端返回 false，`IsZeroCopy` 恒 false，README 表述相反 | 各后端 `TryWrapPixels` |
| 8 | `TryDrawFrame` 三后端语义不同（ImageSharp 强制 A=255、OpenCV 忽略 alpha） | 各后端 |
| 9 | `ImageMetadata` 的 `HasAlpha` / `Orientation` 在 `ReplaceFrame` 后不更新 | `FacadeImage.cs:738` |
| 10 | `BoxPad` 文档与实现不符（文档说先裁，实现是 Pad） | `README.md:237` / `ResizePlanner.cs:69` |
| 11 | 门面 `TextLayouter` 与 `Font.Layout` 两套折行/行高实现 | `Internal/TextLayouter.cs` / `Font.Layout.cs` |
| 12 | 异步编码路径跳过 `SupportedEncodeFormats` 检查 | `FacadeImage.cs:333/368` |
| 14 | 字形缓存满了整体清空（应 LRU）；按 1/4 px 量化但用真实 size 光栅化 | `Font.cs:74-91` |
| 15 | 超大字号静默返回空白 mask | `GlyphRasterizer.cs:36` |
| 18 | `TextLayouter.Wrap` O(n²) + 每次 substring 分配 | `TextLayouter.cs:112` |
| 19 | `Nms` 插入排序 O(n²)，模型原始输出量级会爆 | `Nms.cs:176` |
| 20 | `Resize(ResizeOptions)` 合成缓动用 `new byte[]`，未走池 | `FacadeImage.cs:79` |
| 21 | 软后端每次导出全图扫 alpha（`assumeOpaque` 未传） | `Canvas.cs:186` |
| 22 | Skia 解码 `data.ToArray()` 多拷一份 | `SkiaBackend.cs:88` |
| 23 | `Tensorizer` 热路径每像素每通道浮点除法 | `Tensorizer.cs:77` |
| 24 | `EncodeWith` 预分配 `width*height` 的 writer | `ImageBackendBase.cs:266` |
| 25 | Skia 自备字体（`FontRef.Data`/`FromFile`）每次绘制重新解析 | `SkiaBackend.cs:499` |
| 26 | `TensorBuffer` 池常驻上限 32 MB，无 Trim | `TensorBuffer.cs:14` |
| 27 | `ImageEngine.Dispose` 释放外部注入的后端（DI 场景双重释放） | `ImageEngine.cs:306` |
| 28 | 引擎释放后存活 `FacadeImage` 行为未定义 | `FacadeImage.cs` |
| 29 | CI 无分析器 / 格式校验 / 覆盖率 / `NuGetAudit` | `.github/workflows/ci.yml` |
| 30 | `ImageEngine.Default` 可变静态、无同步 | `ImageEngine.cs:27` |
| 31 | `Resize` 失败后图像停在"已缩放未合成"的中间态 | `FacadeImage.cs:71` |
| 32 | `Capabilities` 无校验（Skia 声明 AOT 但 csproj 未设） | 各后端 |
| — | `Load(Stream)` 编码字节数上限 | `ImageEngine.cs:46` |
