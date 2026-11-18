# planv2 — 安全与热路径优化

> 第 1 批（§1–§6）：解码尺寸上限、`PixelLease` 幂等与 Skia 字体缓存、OpenCV/Skia 像素搬运。
> 第 2 批（§8）：`BoxPad` 不放大、保存失败清理、异步编码能力检查、`Resize` 缓冲池化、
> Skia 解码零拷贝、`ImageEngine` 所有权、`Default` 原子读写、`BuiltInFont` 单例。
> 第 3 批（§9）：`Save` 改"失败即删 + 异常提示"、超大字号抛异常、Skia 自备字体缓存、
> 字形缓存 LRU、NMS O(n log n)、`Tensorizer` 去除法、`TensorBuffer` 可 Trim、引擎释放后报错。
> 第 4 批（§10）：`Load(Stream)` 编码字节数上限、`ReplaceFrame` 后的 `ImageMetadata` 一致、
> `TextLayouter.Wrap` 去掉重复测量与字典开销。
> 其余已识别问题列入 §7「后续批次」，带状态标记。

## 0. 基线

- 构建：`dotnet build ImageFacade.slnx -c Release`，0 警告 0 错误。
- 测试：改动前 **202** 通过（ImageFacade 98 / OpenCv4 28 / OpenCv5 28 / ImageSharp2 15 / ImageSharp3 15 / Sdcb 18）；
  第 1 批后 236，第 2 批后 247，第 3 批后 **256**，第 4 批后 **272**。
- 已修构建阻塞：`src/ImageFacade.SdcbSimdCodecs/ImageFacade.SdcbSimdCodecs.csproj` 的 XML 注释里含
  `--skip-duplicate`，XML 注释不允许出现 `--`，整个解决方案无法加载（MSB4025）。改为文字描述。

两条改动线都必须在此之前保持 202 全绿，改动后全绿并新增用例。

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

| 编号 | 问题 | 位置 | 状态 |
|---|---|---|---|
| 7 | `wrap: true` 零拷贝全后端返回 false，`IsZeroCopy` 恒 false，README 表述相反 | 各后端 `TryWrapPixels` | 待办 |
| 8 | `TryDrawFrame` 三后端语义不同（ImageSharp 强制 A=255、OpenCV 忽略 alpha） | 各后端 | 待办 |
| 9 | `ImageMetadata` 的 `HasAlpha` / `Orientation` 在 `ReplaceFrame` 后不更新 | `FacadeImage.cs` | **已修（§10）** |
| 10 | `BoxPad` 文档与实现不符（文档说先裁；实现缺"不放大"） | `README.md` / `ResizePlanner.cs` | **已修（§8）** |
| 11 | 门面 `TextLayouter` 与 `Font.Layout` 两套折行/行高实现 | `Internal/TextLayouter.cs` / `Font.Layout.cs` | 待办 |
| 12 | 异步编码路径跳过 `SupportedEncodeFormats` 检查 | `FacadeImage.cs` | **已修（§8）** |
| 14 | 字形缓存满了整体清空（应 LRU）；按 1/4 px 量化但用真实 size 光栅化 | `Font.cs` | **已修（§9）** |
| 15 | 超大字号静默返回空白 mask | `GlyphRasterizer.cs` | **已修（§9）** |
| 18 | `TextLayouter.Wrap` O(n²) + 每次 substring 分配 | `TextLayouter.cs:112` | **已修（§10，分配与字典开销部分）** |
| 19 | `Nms` 插入排序 O(n²)，模型原始输出量级会爆 | `Nms.cs` | **已修（§9）** |
| 20 | `Resize(ResizeOptions)` 合成缓动用 `new byte[]`，未走池 | `FacadeImage.cs` | **已修（§8）** |
| 21 | 软后端每次导出全图扫 alpha（`assumeOpaque` 未传） | `Canvas.cs:186` | 待办（用户待定） |
| 22 | Skia 解码 `data.ToArray()` 多拷一份 | `SkiaBackend.cs` | **已修（§8）** |
| 23 | `Tensorizer` 热路径每像素每通道浮点除法 | `Tensorizer.cs` | **已修（§9）** |
| 24 | `EncodeWith` 预分配 `width*height` 的 writer | `ImageBackendBase.cs` | **已修（§8）** |
| 25 | Skia 自备字体（`FontRef.Data`/`FromFile`）每次绘制重新解析 | `SkiaBackend.cs` | **已修（§9）** |
| 26 | `TensorBuffer` 池常驻上限 32 MB，无 Trim | `TensorBuffer.cs` | **已修（§9）** |
| 27 | `ImageEngine.Dispose` 释放外部注入的后端（DI 场景双重释放） | `ImageEngine.cs` | **已修（§8）** |
| 28 | 引擎释放后存活 `FacadeImage` 行为未定义 | `FacadeImage.cs` | **已修（§9）** |
| 29 | CI 无分析器 / 格式校验 / 覆盖率 / `NuGetAudit` | `.github/workflows/ci.yml` | 待办 |
| 30 | `ImageEngine.Default` 可变静态、无同步 | `ImageEngine.cs` | **已修（§8）** |
| 31 | `Resize` 失败后图像停在"已缩放未合成"的中间态 | `FacadeImage.cs` | **已修（§8）** |
| 32 | `Capabilities` 无校验（Skia 声明 AOT 但 csproj 未设） | 各后端 | 待办 |
| — | `Load(Stream)` 编码字节数上限 | `ImageEngine.cs` | **已修（§10）** |
| — | `Save(path)` 失败留下半截文件 / 覆盖原图（评审时新发现） | `FacadeImage.cs` | **已修（§9，语义按用户要求调整）** |
| — | `BuiltInFont` 双检锁对 `ReadOnlyMemory` 非原子发布（评审时新发现） | `Text/BuiltInFont.cs` | **已修（§8）** |
| — | `PixelConverter` 的 `width * bpp` 未 checked，宽高极大时溢出成负数（评审时新发现） | `Pixels/PixelConverter.cs` | **已修（§9）** |

---

## 8. 第二批：局部加固（本批）

挑的是"改动面小、风险低、不涉及 SPI 设计决策"的项。基线 236 → 247，+11 个用例。

### 8.1 `BoxPad` 不放大（原第 10 条，实为真 bug）

不只是文档错：`ResizePlanner` 的 `BoxPad` 分支和 `Pad` **完全一样**，"只缩小不放大"只在
软后端自己的 `Resizer.ResolveLayout` 里实现过。所以门面上用 Skia / ImageSharp / OpenCV 时，
100×100 源图配 400×400 + `BoxPad` 会被放大到 400×400，与 ImageSharp 语义相悖。

修：`ResizePlanner.Plan` 的 `BoxPad` 分支加 `scale = MathF.Min(scale, 1f)`；同时改掉
`ResizeMode.BoxPad` 的 XML 注释与 README 表格里的错误描述（原文写的是"先裁到目标比例再缩放，四角对齐"，
描述的是另一种算法）。

用例：`BoxPad_Does_Not_Upscale_Small_Source`（planner 层）、`Pad_Does_Upscale_Small_Source`（对照组）、
`Resize_BoxPad_Does_Not_Upscale`（Skia 端到端）。

### 8.2 `Save(path)` 失败清理（新发现）

原实现直接 `File.Create(path)`：编码抛异常就留下半截文件。

第一版改为"临时文件 + 改名"，**后来按用户要求改回直接写**（见 §9.1）：现在失败时删除目标文件，
并抛带提示的 `ImageSaveFailedException`。本节保留为历史记录。

用例：见 §9.1。

### 8.3 异步编码补能力检查（原第 12 条）

`SaveCore` 会查 `SupportedEncodeFormats`，但 `SaveAsync(Stream)` / `EncodeToArrayAsync` 的
`IAsyncImageBackend` 分支绕开了它。抽出 `EnsureCanEncode(format)`，三条路径共用。
目前没有真实后端实现该接口，属于埋伏的坑——顺带把测试替身 `AsyncFakeBackend` 补上格式声明
（它之前靠"没人检查"才通过）。

用例：`EncodeAsync_Checks_Supported_Formats`、`SaveAsync_Stream_Checks_Supported_Formats`。

### 8.4 `Resize` 合成缓冲池化 + 消除中间态（原第 20、31 条）

- 两块缓冲（缩放中间 + 输出）从 `new byte[]` 改成 `ArrayPool<byte>.Shared.Rent`；
  1080p 的 Pad/Crop 每帧少两次 8 MB 级的 LOH 分配。
- 顺序调整：先 `ResizeFrame` 拿到临时帧、拷出、立刻释放，再合成、再 `ReplaceFrame`。
  以前是"先把当前帧换成缩放宽高"，之后任何一步抛异常图就停在半成品状态。
- 无合成需求（Stretch / Max / Min）保留原来的直换帧快路径，不多拷一次。

### 8.5 Skia 解码少一次整段拷贝（原第 22 条）

`SKData.CreateCopy(data.ToArray())` → `fixed` 指针 + `SKData.CreateCopy(IntPtr, ulong)`。
4K JPEG 少一次 8 MB 级别托管分配。

### 8.6 `EncodeWith` 的 writer 初值（原第 24 条）

`new ArrayBufferWriter<byte>(Math.Max(1024, width * height))` 在 4K 上等于先占 8 MB；
JPEG 用不到，PNG 又常常要扩容。改成 64 KB 起步，让扩容策略自己长到合适大小。

### 8.7 `ImageEngine` 后端所有权（原第 27 条）

新增 `ImageEngine(IImageBackend backend, bool ownsBackend = true)`。默认保持原行为；
DI 容器托管后端生命周期时传 `false`，避免容器与引擎各释放一次。

用例：`Dispose_Releases_Owned_Backend_By_Default`、`Dispose_Leaves_Externally_Owned_Backend_Alive`。

### 8.8 `ImageEngine.Default` 原子读写（原第 30 条）

`get/set` 改 `Volatile.Read` / `Volatile.Write`。启动阶段多线程赋值不会再出现
"一个线程看到 A、另一个看到 B"。

### 8.9 `BuiltInFont` 单例发布（新发现）

手写双检锁 + 普通字段，而 `ReadOnlyMemory<byte>` 是"对象引用 + int + int"的多字段结构，
非原子发布，理论上可能读到引用与长度不匹配的撕裂值。改成 `Lazy<ReadOnlyMemory<byte>>`
（`ExecutionAndPublication`），与 `Font.BuiltIn` 的做法统一。

用例：`BuiltInFont_Data_Is_Stable_Across_Reads`。

### 8.10 本批验收

- [x] `dotnet build ImageFacade.slnx -c Release`：0 警告 0 错误。
- [x] `dotnet test`：**247 通过**（236 → 247，+11），六个测试工程全绿。
- [x] `BoxPad` 在 planner / Skia 端到端两层都有"不放大"断言。
- [x] 保存失败后目录里没有残留文件。
- [x] 异步编码遇到不支持格式抛 `FeatureNotSupportedException`（不是"编码成功但字节是垃圾"）。

### 8.11 本批未做（已在第三批处理，见 §9）

- **第 15 条（超大字号静默空白）**：语义决策，第三批按用户要求改为抛异常。
- **第 25 条（Skia 自备字体不缓存）**：第三批处理，并额外解决文件句柄占用问题。
- **第 21 条（软后端 alpha 扫描）**：`TryCopyPixels` 签名里没有"是否已不透明"的信息，
  要么扩 SPI、要么在软后端自己记住，都超出"局部改动"的范围。**用户表示待定，本批与第三批都未动。**

---

## 9. 第三批：按用户反馈调整 + 继续清理

基线 247 → 256（+9 个用例）。三个"改法由用户指定"的项 + 六个"其余能做的"。

### 9.1 `Save(path)` 改回直接写：失败即删 + 异常提示（用户指定）

第一版用的是"临时文件 + `File.Move`"，用户要求改回：**直接保存，失败就删掉目标文件，
并在异常里告诉用户已删除**。

实现：

```csharp
FileStream? stream = null;
try
{
    stream = File.Create(path);          // 直接写目标路径
    using (stream) { SaveCore(stream, save, format); }
}
catch (Exception ex) when (stream is not null)
{
    throw ImageSaveFailedException.For(path, ex);   // 删文件 + 包装异常
}
```

两个细节：

1. **`catch` 带 `when (stream is not null)`**：`File.Create` 自己失败（权限、目录不存在）时
   文件还没被碰过，不能删——那可能是用户的原有文件。
2. 新增 `ImageSaveFailedException`，带 `Path` / `FileDeleted` 属性，
   消息形如"保存到 'out.jpg' 失败：<真正原因> 已删除写了一半的目标文件。"
   `FileDeleted` 为 `false` 表示删除也失败、可能残留，不乱报"已删除"。

`SaveAsync(path)` 同步调整：编码阶段失败时文件还没碰过，异常原样抛出；写盘失败才删+包装；
被取消时先删半截文件再原样抛 `OperationCanceledException`（不吞掉取消语义）。

**行为变化（已在 README 与 XML 注释里写明）**：目标路径原有的文件在保存开始时就被截断，
失败后**不恢复**。这是"直接保存"的固有代价。

用例：`Save_Deletes_Target_And_Says_So_When_Encoding_Fails`、`Save_Leaves_No_File_Behind_On_Failure`、
`Save_Succeeds_And_Writes_Exact_Bytes`、`SaveAsync_Deletes_Target_When_Encoding_Fails`。

### 9.2 超大字号抛异常（原第 15 条，用户指定）

`GlyphRasterizer.Rasterize` 以前在位图超过 4096×4096 时**静默返回空白**：字号写错、
单位搞混（比如 px 当成 pt 再用 100 倍）时用户只会看到"什么都没画"。

改为抛 `ArgumentOutOfRangeException`，消息给出实际位图尺寸与上限，并提示减小字号或换矢量路径。
只影响**需要栅格化**的路径（软后端 / OpenCV 的文字）：Skia 与 ImageSharp 用各自的字体栈，
`GetGlyphPath` 这种矢量出口也不受限。

用例：`Oversized_Glyph_Throws_Instead_Of_Silently_Blank`。

### 9.3 Skia 自备字体缓存（原第 25 条，用户指定）

以前 `FontRef.FromData` / `FromFile` 每次 `TryDrawText` 都重建一个 `SKTypeface`（整表解析），
逐帧画字的场景等于按帧付费。现在统一走 `_typefaces` 缓存。

**过程中发现并解决了第二个问题**：直接缓存 `SKTypeface.FromFile` 的结果会让 Skia 一直持有
文件句柄，缓存期间字体文件无法替换或删除（测试里 `File.Delete` 直接 `UnauthorizedAccessException`）。
改为"读字节 → `SKData.CreateCopy` → `FromData`"：只读一次文件，句柄立即释放。

顺带把 `data.ToArray()` 这类中间拷贝也去掉，直接对 `ReadOnlySpan<byte>` 取固定指针。

用例：`Custom_Font_File_Renders_Consistently_Across_Calls`、`Disposing_One_Backend_Does_Not_Break_Text_On_Another`。

### 9.4 字形缓存改 LRU（原第 14 条）

`Font._maskCache` 满 4096 条时以前直接 `Clear()`：多字号混排时会把刚画过的字形全丢掉，
下一帧重新光栅化一遍。改成 `Dictionary + LinkedList` 的真 LRU：命中提升到队首，
超限只淘汰队尾一条。

用例：`Glyph_Mask_Is_Cached_Per_CodePoint_And_Size`（靠 `Font.CachedMaskCount` 观察缓存条数）。
1/4 px 量化的 key 保持不变——文字排版对 0.25 px 的差异不敏感，改 key 会显著降低命中率。

### 9.5 NMS 排序改 O(n log n)（原第 19 条）

`Nms` 两个入口都用插入排序。典型 n≈300 没问题，但模型原始输出常是 8400 / 25200 个候选框，
插入排序是 7000 万次比较量级。

改法：`count <= 32` 仍走插入排序（更快且零分配）；更大规模用「下标数组 + 双键排序」——
比较器同时比分数（降序）与原下标（升序），任何排序算法结果都唯一，等价于稳定排序。

用例：`Suppress_Handles_Many_Candidates_Deterministically`（2000 个框，两个方向结果一致）、
`Select_And_Suppress_Agree_On_Many_Candidates`（500 个框，两个入口结果对应）。

### 9.6 `Tensorizer` 去掉逐像素除法（原第 23 条）

原来每像素每通道一次浮点除法（`c /= std[i]`），外加 `hasMean` / `hasStd` 两个分支。
现在把 mean/std/归一化折成每通道一组 `(scale, offset)`：`c = px * scale + offset`，
内层循环只剩 3 次乘加，没有任何除法与分支。四种组合（有/无 mean × 有/无 std）自动被覆盖。

期望值 `(1.0 - 0.5) / 0.5 = 1` 之类断言不变，浮点结合律带来的差异远小于测试容差（1e-4 / 1e-5）。

### 9.7 `TensorBuffer` 换成可清理的自管池（原第 26 条）

原实现用 `ArrayPool<float>.Create(maxArrayLength: 2_000_000, maxArraysPerBucket: 4)`，
池子最多常驻 4×2M×4B = 32 MB 且**无法释放**——`ArrayPool<T>` 没有公开的清理入口。

改成 `ConcurrentStack<float[]>` 自管：最多 4 块、单块不超过 200 万元素，新增
`TensorBuffer.Trim()`（清空池）与 `PooledCount`（诊断）。批量推理结束后调用 `Trim()` 就能把常驻内存还回去。

行为差异：池里的块不再按长度分桶，租用时找"够大"的块复用，不够就丢弃——对同一模型的固定输入尺寸没有影响。

用例：`TensorBuffer_Trim_Releases_Pool_Without_Breaking_Rents`。

### 9.8 引擎释放后的图片明确报错（原第 28 条）

以前 `using (engine)` 提前结束时，仍持有的 `FacadeImage` 会打到已被释放的后端上：
形状绘制可能"看起来还行"，文字则因为 typeface 已释放而崩溃或未定义。

`FacadeImage.ThrowIfDisposed` 现在同时检查所属引擎，抛 `ObjectDisposedException` 并说明
"所属的 ImageEngine 已释放"。`Dispose()` 本身不做这个检查，仍在任何状态下都能安全调用。

用例：`Image_Throws_Clear_Error_After_Engine_Disposed`。

### 9.9 `PixelConverter` 补溢出检查（评审时新发现）

`width * GetBytesPerPixel()` 是未 `checked` 的 int 乘法：宽接近 `int.MaxValue` 时溢出成负数，
后续抛的是含义不明的 `ArgumentOutOfRangeException`，而不是清晰的溢出错误。三处补 `checked`。

### 9.10 本批验收

- [x] `dotnet build ImageFacade.slnx -c Release`：0 警告 0 错误。
- [x] `dotnet test`：**256 通过**（247 → 256，+9），六个测试工程全绿。
- [x] 保存失败后目标文件不存在，异常 `FileDeleted = true` 且消息含"已删除"。
- [x] 字号 20000 的 'A' 抛 `ArgumentOutOfRangeException`（不再静默空白）。
- [x] 缓存自备字体后，字体文件仍可删除（不占句柄）。
- [x] 2000 个候选框的 NMS 结果与输入顺序无关、可复现。
- [x] `TensorBuffer.Trim()` 之后仍能正常租用。

### 9.11 仍未做

| 编号 | 原因 |
|---|---|
| 21 | 用户表示"后面再想想"——需要决定是扩 `TryCopyPixels` 签名还是在软后端自己记状态 |
| 11 | 两套排版引擎合并涉及公开行为，需要先定"折行规则以谁为准" |
| 18 | **部分修（§10、§10.6）**：去掉了每行长度 1 的无用测量、"每个前缀一条字典记录"的哈希 / 长期持有开销，以及每个候选的 substring（§10.6）；折行位置与扫描顺序**刻意不变**（改成二分 / 跳跃扫描依赖"前缀宽度随长度单调不减"，无法对任意字体成立），所以测量次数仍是 O(n²) |
| 29 | CI 加 `NuGetAudit` 可能因存量告警卡住构建，需要先跑一遍看有多少 |
| 7、8 | 零拷贝与 `TryDrawFrame` 语义属于 SPI 设计决策，改动面大 |
| 32 | `Capabilities` 无校验需要先定各后端的能力语义（第 9 条已在 §10 用"静态推导"解决，不再需要扫全图） |

---

## 10. 第四批：流上限、元数据一致、折行开销

基线 256 → **272**（+16 个用例）。三项都只往公开面**加**东西（`LoadOptions` 一个属性、
异常类型一个重载 + 两个只读属性），不改已有成员签名，也不涉及第 7/8/11/21/29/32 条的 SPI 语义。

### 10.1 `Load(Stream)` / `LoadAsync(Stream)` 的编码字节数上限（§1「不做」的遗留项）

> 后记：文件路径那条（`Load(path)`）在 §10.7 也接上了这个上限——它是按文件长度**先拒后读**的。

**问题**：两条 Stream 路径用 `CopyTo` / `CopyToAsync` 把入站数据整段拷进 `MemoryStream`，
没有任何长度上限。`MaxPixels` 只拦"小文件声明巨大尺寸"，拦不住"合法尺寸但几百 MB"或者是
**根本不是图片**的巨型请求体——那部分内存在解码之前就已经吃掉了。

**改法**：

- `LoadOptions.MaxEncodedBytes`（`long`，默认 **0 = 不限制**）。默认不限制是为了**向后兼容**：
  现有调用方不会因为升级突然开始收到异常。README 与 XML 注释都写明这个默认值。
- `ImageSizeLimitExceededException` 增加 `(long maxEncodedBytes, long actualEncodedBytes)` 重载，
  以及只读属性 `MaxEncodedBytes` / `EncodedBytes`；原来的 `(int, int, long)` 重载与
  `Width` / `Height` / `MaxPixels` 一字未动，字节维度超限时这三个保持 0。
- 读入改成 `ArrayPool<byte>` + `Stream.Read` 循环，缓冲**最多长到"上限 + 1"字节**：多读 1 字节
  就够判定超限，不需要真的读完。`CanSeek` 时先按 `Length - Position` 快速拒绝（不可信，读循环
  仍然累计校验）。
- 读完把"池化缓冲 + 实际长度"交给既有的 `Load(ReadOnlySpan<byte>)`，头部嗅探与解码后兜底两道闸
  完全不变；`try/finally` 归还缓冲（包括抛异常、取消的路径）。
- `LoadAsync(Stream)` 用 `ReadAsync(Memory<byte>, ct)`，没有为了复用同步代码引入 sync-over-async：
  读循环里的扩容返回新缓冲（旧的就地归还池），跨 `await` 用普通局部变量而不是 `ref`。
- `MemoryStream` 且 `Position == 0` 的快路径保留（不额外拷贝），但同样过字节上限。
- 边界：`MaxEncodedBytes <= 0` 且数据真的超过 .NET 单数组上限（约 2 GB）时抛同一个异常，
  并把 `Array.MaxLength` 当作上限上报——否则只能 OOM，报一个明确数字更有用。

**用例**：`Default_MaxEncodedBytes_Is_Unlimited`、`Load_MemoryStream_Rejects_Data_Over_Limit`、
`Load_MemoryStream_Accepts_Data_At_Exact_Limit`、`Load_Stream_Rejects_Data_Over_Limit`、
`Load_Stream_Stops_Reading_Once_Over_Limit`、`Load_Stream_Rejects_By_Seekable_Length_Without_Reading`、
`LoadAsync_Stream_Rejects_Data_Over_Limit`、`Load_Stream_Decodes_Real_Image_Within_Limit`、
`LoadAsync_Stream_Decodes_Real_Image_Within_Limit`。

### 10.2 `ReplaceFrame` 后的 `ImageMetadata`（原第 9 条）

**问题**：`ReplaceFrame` 原样搬运旧的 `HasAlpha` / `Orientation`。`Pad` 补**透明**边之后图上
明明有透明像素，`Metadata.HasAlpha` 仍然是 `false`（`PadColor` 默认就是 `Rgba32.Transparent`，
所以这是默认路径上的错误）。

**改法**：`ReplaceFrame` 的签名加上 `hasAlpha` / `orientation`，由**调用方按这次变换做了什么静态推导**：

| 操作 | `HasAlpha` | `Orientation` |
|---|---|---|
| 纯拉伸 / 裁剪 / 补不透明边 | 继承原值（重采样不创造也不消灭 alpha） | 继承（不旋转） |
| `Pad` / `BoxPad` 且补边色 `A < 255` | 一定为 `true`（贴图盖不满，补出来的像素就是透明的） | 继承 |

判定"有没有补边区域"用 `ScaledWidth < OutputWidth || ScaledHeight < OutputHeight`：
`Crop` 虽然也先 `Fill` 整块，但贴图把它完全盖住了，不产生可见透明像素。

为什么不去问后端：`IFrame` 只有 `Width` / `Height`，没有 alpha 能力位；加能力位是 SPI 变更，
扫全图判断 alpha 是 §9.11 明确否掉的开销。等以后真的加了 `Rotate` / `Flip`，同一处按
"90° 交换宽高、180° 不改、翻转不改"继续往下推即可（`ReplaceFrame` 的注释里写了这个约定）。

**用例**：`Resize_Refreshes_Width_Height_Alpha_And_Orientation`（解码元数据 8×6 / orientation 6 /
hasAlpha true / 带 ICC → `Resize(4,3)` 后宽高、alpha、方向、格式、ICC 全部正确）、
`Resize_Pad_With_Transparent_Color_Sets_HasAlpha`、`Resize_Pad_With_Opaque_Color_Keeps_HasAlpha_False`、
`Resize_Crop_Keeps_HasAlpha`、`Resize_Stretch_Keeps_Opaque`。

### 10.3 `TextLayouter.Wrap` 的重复测量与字典开销（原第 18 条，**部分**）

**问题**：查找"第一个超宽前缀"时，每个候选长度都要
①分配一份 substring、②在 `Dictionary<string, TextMetrics>` 里查一次 / 写一条（`Ordinal` 等于对
整段前缀做一次 O(长度) 的哈希与比较）、③再让后端整形一遍。字典还把这些前缀字符串**全部留着**
——长段落等于按前缀长度累加一遍常驻内存（O(n²)），比时间更难看。另外长度 1 的候选也会量一次，
而溢出判定要求 `end - start > 1`，那次结果从头到尾没用过。

**改法（结果不变的部分）**：

- 候选的度量改成写进**按终点下标索引的池化 `TextMetrics[]`**（`ArrayPool<TextMetrics>`，每段一次
  租用、`finally` 归还），不再为每个候选建字典条目；行文本仍只在真正产出一行时分配一次。
- 长度 1 的候选不再测量（断点机会判定照旧做，`lastBreak == start` 的分支依赖它）。
- 产出一行时如果尾部没有空格被裁掉，行度量**直接复用**已经量过的那个前缀（宽度与行高都有），
  不回字典也不问后端；只有 `TrimEnd` 真的改变了内容、或整行只有 1 个字符时才补量一次。
- `Layout` 里"再遍历一遍量每行宽度"的数组随之去掉：宽度由折行阶段带出来，第一行的完整度量
  顺手带出给行高用，所以默认（`LineHeight` 未指定）那条路径也不会多量一次。

**明确放弃的部分**：

- ~~`ReadOnlySpan<char>` 切片：`IImageBackend.TryMeasureText` 吃的是 `TextSpec.Text`（`string`），
  要让它吃 span 就是改公开 SPI，本批不做。所以每个候选仍有一份 substring 分配。~~
  **已补做，见 §10.6**（只加不改，没动已有签名）。
- 把线性扫描换成二分 / 跳跃扫描：**唯一**能真正消掉 O(n²) 测量次数的办法，但它依赖
  "前缀宽度随长度单调不减"，对任意字体无法证明（负字距、组合字符都可能让更长的前缀更窄），
  会改变折行位置——那是本条的硬约束。所以测量次数仍是 O(n²)，扫描顺序逐字符不变。

**用例**：`Wrap_Result_Matches_Reference_Implementation`（差分测试：替身宽度对前缀长度**故意不单调**，
512 个"文本 × 最大宽度"用例，显式行高与 `LineHeight = 0` 各跑一遍，与照抄旧算法的参考实现逐行比对
行文本、行宽与排版尺寸）、`Wrap_Does_Not_Measure_One_Character_Candidates`（10 字符一行放得下时
只量 2..10 共 9 次）。

### 10.4 本批验收

- [x] `dotnet build ImageFacade.slnx -c Release`：0 警告 0 错误（各工程 `TreatWarningsAsErrors=true`）。
- [x] `dotnet test`：**272 通过**（256 → 272，+16），六个测试工程全绿。
- [x] 1 MB 输入配 1000 字节上限时只读了 1 KB 出头就抛 `ImageSizeLimitExceededException`（不是读完再报）。
- [x] `MaxEncodedBytes` 保持默认 0 时，256 KB 输入一路读到后端（`FeatureNotSupportedException`），没有新异常。
- [x] 可寻址流 `Length` 已超限时 `Read` 一次都没被调用。
- [x] PNG 经过不可寻址流（同步 / 异步两条路径）解码结果正确，说明交给 span 的长度精确、没带上池子的多余字节。
- [x] 补透明边后 `HasAlpha` 变 `true`；补不透明边、裁剪、拉伸保持原值。
- [x] `Resize` 后 `Metadata` 的宽高 / 方向 / 格式 / ICC 与新帧一致。
- [x] 折行差分测试 512 例 × 2 种行高设置全等（含非单调宽度），行文本逐字符相同。

### 10.5 本批未做

- 第 18 条只剩"测量次数"的 O(n²)：不做二分 / 跳跃扫描，理由见 §10.3。
- 第 7、8、11、21、29、32 条按要求不动（需要先定 SPI 语义或用户决策）。

### 10.6 折行候选的切片测量（补 §10.3 里"明确放弃"的第一项）

§10.3 把 `ReadOnlySpan<char>` 切片判成"要改公开 SPI 所以不做"，其实可以**只加不改**：

- `IImageBackend` 加一个**带默认实现**的 `TryMeasureText(IFrame, ReadOnlySpan<char>, in TextStyle, out TextMetrics)`，
  默认实现把切片 `new string(...)` 后转调原来的 `TextSpec` 重载，所以老后端一行都不必改、结果逐字节不变。
- 底层本来就吃切片的四个后端各写三行顶替它（Skia 的 `SKFont.MeasureText(ReadOnlySpan<char>)`、
  SixLabors 的 `TextMeasurer.MeasureAdvance(ReadOnlySpan<char>, …)`、托管字体栈新增的
  `Font.Measure(ReadOnlySpan<char>, float)`）。`ImageBackendBase` 也把这个槽位做成 `virtual`，
  派生类想接就 `override`。
- `FacadeImage.MeasureSingleLine` 多一个切片重载，`TextLayouter.MeasurePrefix` 直接传
  `paragraph.AsSpan(start, len)`：整条折行路径不再为每个候选分配一份 substring。
- **踩坑记录**：默认接口方法在类里**不能 `override`**，实现类直接声明同名 `public` 成员即可顶替；
  若基类已经实现了该接口，派生类必须**重新列出接口**才会重新做映射（测试替身里验证过）。
- 第 18 条的性质没变：**测量次数仍是 O(n²)**，扫描顺序逐字符不变。

**用例**：`Wrap_Measures_Prefixes_Through_Slice_Overload`（9 个候选全走切片版、string 版 0 次）、
`Wrap_Result_Is_Identical_With_And_Without_Slice_Overload`（重写切片版与走默认回退两条路，5 段文本 × 4 个宽度逐行相同）、
四个后端各自的 `MeasureText_Slice_Overload_Matches_String_Overload`（宽 / ascent / descent / lineGap 全等）。
测试数 272 → **280**（+8：核心 3 + Skia 1 + ImageSharp / OpenCV / Sdcb 各 1，其中 ImageSharp 与 OpenCV 的源码各被两个主版本工程编译一遍）。

### 10.7 `Load(path)` 改只读内存映射（顺带按文件长度查上限）

`Load(path)` / `LoadAsync(path)` 原来是一次 `File.ReadAllBytes` / `File.ReadAllBytesAsync`：
先按文件大小分配一份等长托管数组，再整体拷一遍，然后才交给解码器——大图就是一次纯浪费的 LOH 分配。
仿 Sdcb.SimdCodecs 2.0 `ByteSource.FromFile` 的思路改成**只读内存映射**（新文件 `Internal/MappedFileBuffer.cs`）：

- `FileStream` 打开 → 用**同一个句柄**量长度 → 检查 → `MemoryMappedFile` 映射 → 解码器直接吃
  `ReadOnlySpan<byte>`/`ReadOnlyMemory<byte>`（`MappedFileBuffer` 就是一块 `MemoryManager<byte>`）。
  长度和映射同源，中间不会被换文件；`FileShare.Read` 与 `File.ReadAllBytes` 一致（别人能读、不能写）。
- **顺带查长度**：空文件抛 `ArgumentException`（`ParamName = "path"`）；超过 `MaxEncodedBytes`
  （默认 0 = 不限制）或单块缓冲上限（`int.MaxValue`，span 长度是 `int`）抛
  `ImageSizeLimitExceededException`。两种都在映射之前判完，**一个字节都不读**——
  这条以前只管流，现在也管文件路径（`LoadOptions.MaxEncodedBytes` 的注释同步改了）。
- 映射生命周期只有一次解码那么长，`using` 罩住整个解码（异步那条也罩住 `await`），
  返回后文件句柄已释放（Windows 上可以立刻删除，有用例验证）。所以后端仍然不能把入参内存留在帧 / 元数据里。
- 文件内容按需分页：解码器没碰到的页不会被读进来（`ImageSharp` 之类的"读全图"型解码器差别不大，
  但 skip 型解码、超大文件只取元数据时省得很实在）。

**没改的地方**：`Font.LoadFromFile` / Skia 的 `SKTypeface` 自备字体缓存仍是整份读入。
字体字节要**跟着 Font 对象活到进程结束**（字形表是懒解析的），改映射就得让 `Font` 变成 `IDisposable`
或给缓存加引用计数——那是公开 API 破坏，不在这次范围内。

**用例**（核心 `EncodedBytesLimitTests`）：`Load_FilePath_Rejects_By_Length_Before_Decoding`（5000 字节文件 + 上限 100，
报的 `EncodedBytes` 必须是真实文件长度）、`LoadAsync_FilePath_Rejects_By_Length_Before_Decoding`、
`Load_FilePath_Rejects_Empty_File`（同步 / 异步都抛）、`Load_FilePath_Within_Limit_Reaches_The_Backend`（等于上限放行）、
`Load_FilePath_Decodes_Real_Image_And_Releases_The_Handle`（Skia 真 PNG：映射内存直接解码 + 解完能删文件）；
软后端另有 `Load_From_Path_Maps_File_And_Releases_It` / `LoadAsync_From_Path_Maps_File_And_Releases_It`（PNG 往返 + 句柄释放）。
测试数 280 → **287**。

### 10.8 "图里有没有半透明像素"：加载时查一次，之后按需重算

`Metadata.HasAlpha` 只是**解码器声明的通道能力位**：带 alpha 通道不等于真的有半透明像素（不透明 PNG 到处是），
而"要不要先把图合成到背景色再交给 JPEG"这类决定要的正是后者。补上 `ImageMetadata.HasTransparency`（`bool?`，`null` = 不知道）：

- **加载时查一次**：`ImageEngine.FinishLoad` 在解码完成、`ImageMetadata` 已就位后调一次检测；
  `LoadOptions.DetectTransparency`（默认 `true`）关掉就留 `null`。`HasAlpha == false` 时直接定死 `false`，一个字节都不扫。
- **用户自己再查**：`FacadeImage.DetectTransparency(bool force = false)`——有结论直接返回，`force: true` 无视缓存重算。
  平台/后端本来就知道答案的话，这次调用只是转个手（`IImageBackend.TryGetTransparency`，默认接口方法返回 `false`），
  不额外做事。
- **三条取答案的路**：后端直接答（四个后端都能在自己已经持有的缓冲上零拷贝扫一遍）→ 不行就 `TryAcquirePixels`
  拷一次再扫 → 都不行返回 `null`。**绝不猜**：给 `null` 比给一个错的 `false` 安全。
- **扫描本身**（`Internal/AlphaProbe.Scan`）：按行 `MemoryMarshal.Cast<byte, uint>`，一次比 4 字节判 alpha，
  支持任意 `stride`；1080p 约 0.3 ms，零分配。
- **结论的失效与静态推导**：绘制（`DrawLine` / `FillRectangle` / 文字…）之后置 `null`——半透明像素可能刚被画进去；
  `Clear(color)` 直接等于 `color.A < 255`；Resize 的 `Pad` / `BoxPad` / `Crop` 补边用不透明色时可以推出 `false`，
  `Stretch` 原样继承；`Create(background)` 按背景色定（`null` 背景 = 可能透明）。
- **顺带省功**：已知全不透明时，`PrepareForEncode` 跳过 JPEG / BMP"合成到背景色"整段工作（新帧、拷像素、逐像素混合），
  用 `EncodeRequest.AssumeOpaque` 把结论传给后端；软后端 `CopyToStraight(..., assumeOpaque)` 据此跳过反预乘前的 alpha 探测。
- **各后端零拷贝**：Skia `SKBitmap.GetPixels()`、ImageSharp `DangerousTryGetSinglePixelMemory`、OpenCV `CV_8UC4` 的
  `Mat.Data` + `Step()`、软后端 `Canvas.IsFullyOpaque()`。
- **顺手把 `HasAlpha` 修准**（以前只有软后端填）：Skia 用 `codec.Info.AlphaType != Opaque`；OpenCV 看补通道之前的
  `Channels() is 2 or 4`；ImageSharp 一律解码成 `Rgba32`，从像素类型上看不出源文件通道数，只认死 JPEG，
  其它按"可能有"处理（多扫一遍，也不给错答案）。

**用例**：核心 `TransparencyTests` 11 个（加载查一次并缓存、`DetectTransparency = false` 留 `null`、无通道格式不打扰后端、
后端能答就不扫像素、后端既不答也导不出像素时 `null`、绘制后失活再重算、`Clear` 直接定论、Pad 推导、`AssumeOpaque` 传递、
`Scan` 的 stride 越界）；
Skia `Load_Reports_Transparency_From_Real_Bitmap` / `Load_Reports_Opaque_When_All_Pixels_Are_255`；
软后端 `Load_Reports_Transparency_From_Canvas`；ImageSharp `Load_Reports_Transparency`、OpenCV `Load_Reports_Transparency_And_Alpha_Channel`
（这两份源码各被两个主版本工程编译一遍）。测试数 287 → **305**。

面向使用者的版本（三层结论的语义、失效时机表、性能量级、9 条注意事项）写在
README 的「半透明像素：判断依据、性能与注意事项」一节，这里只留实现视角的记录。

---

## 11. 第五批：软后端解码零拷贝 + 光栅兜底

> 两条互不依赖的线：
> **A** 软后端 `Decode` 不再生成中间缓冲（解压直写帧内存 + 原地预乘 + 真正能命中大块的缓冲池）；
> **B** 后端不支持的那一次操作，交给自带光栅器生成一张小贴图再合成回去（可选装饰器，**不动 SPI**）。
> 基线 305 通过。

### 11.0 现状（已核对源码）

`ImageBackendBase.Decode`（`Defaults/ImageBackendBase.cs:192-241`）目前碰全图三次：

1. `ArrayPool<byte>.Shared.Rent(stride * height)` → `TryDecodeInto` 解成直通 RGBA（缓冲 A）；
2. `Import` → `Create(w, h, null)` 里 `new byte[w * h * 4]`（缓冲 B，创建即清零）;
3. `ImportPixels` 逐行 `PixelOps.Premultiply` 把 A 预乘进 B，然后 `finally` 归还 A。

1080p 就是「解一次 + 新建 8 MB（清零）+ 读 8 MB + 写 8 MB」。带 EXIF 方向时更糟：
`PixelTransforms.ApplyOrientation` 再返回一个新 `byte[]`（缓冲 C），又 `Import` 一次（缓冲 D）。

另外两个已核对的事实：

- **现在的"池化"在 1080p 上等于没池化**：`ArrayPool<byte>.Shared` 的最大桶是 2^20 元素，
  超过就每次新分配、`Return` 直接丢弃。软后端所有 `Share.Rent(stride * height)` 都落在这条路上。
- `PixelOps.PremultiplyInPlace` 已存在（`PixelOps.cs:65`，内核本身允许源目标同址），
  `Canvas.FlipHorizontal/FlipVertical/Rotate90/180/270` 也都在；缺的只是"Canvas 接管一块池化缓冲、
  Dispose 时归还"这条路（`Canvas` 的类注释早就写了"便于零拷贝包装解码缓冲"）。
- Sdcb `Abstractions` 2.0 的 `PixelFormat` 没有预乘变体（XML 文档里搜不到 `Premul`），
  所以"解出来是直通像素"这一点改不掉，原地预乘这一步省不掉。落地时用反射再确认一次。

### 11.1 A. 解码零拷贝

目标：`Decode` 全程只有**一块**像素缓冲；解压直写这块内存，预乘原地做；稳定态 LOH 分配为 0。

#### 11.1.1 `ByteBufferPool`（新文件 `Defaults/ByteBufferPool.cs`，internal）

不复用 `ArrayPool<byte>.Shared`（它 1 MB 封顶），也不用公共池（大块进公共池会把内存钉在进程里）：

- `Rent(int length)` 返回**精确长度**的数组（不做 2 的幂上取整，避免 8 MB 变成 16 MB）；
- `Return(byte[])`：单块 ≤ 64 MB、池内总量 ≤ 64 MB，超了直接丢；`ConcurrentStack<byte[]>` 存放；线程安全；
- `Trim()` / `PooledBytes` / `PooledCount`：与 `TensorBuffer`（§9.7）同一套诊断与清理入口。

#### 11.1.2 `Canvas` 的所有权与池化

- 新增 `internal static Canvas Adopt(byte[] buffer, int width, int height, int stride, bool returnToPool)`：
  接管外部（池化）缓冲，内容必须已是预乘；`Dispose` 时按标记归还池，且只归还一次（`_disposed` 已经幂等）。
- 新增 `internal static Canvas CreatePooled(int width, int height, Color? background = null)`：
  只给"整幅都会被写满"的内部路径用（`Rotate90/180/270` 的返回值），不做公开。
- `Create` / `Wrap` / `Clone` 的语义**不变**（`Clone` 仍 `new byte[]`，克隆出来的与池无关）。
- **缓冲可能比 `Stride * Height` 长**：审计全部使用点。已知要改的一处是
  `SoftwareBackend.Import` 的整块快路径——现在写的是 `PixelOps.Premultiply(straight[..], canvas.Buffer)`，
  这个方法要求两块长度相等，池化后 `canvas.Buffer` 更长会直接 `ArgumentException`，
  改成 `canvas.Buffer.AsSpan(0, rowBytes * canvas.Height)`。

#### 11.1.3 `Decode` 改写

```csharp
byte[] buffer = ByteBufferPool.Rent(checked(stride * height));
bool transferred = false;
try
{
    Span<byte> pixels = buffer.AsSpan(0, stride * height);
    if (!decodeInto(data, pixels, stride, SdcbPixelFormat.Rgba32, null, default)) { throw ...; }

    PixelOps.PremultiplyInPlace(pixels);                       // 原地，第二次不再需要一块缓冲

    Canvas canvas = Canvas.Adopt(buffer, width, height, stride, returnToPool: true);
    transferred = true;                                        // 所有权已交出，finally 不再归还

    canvas = ApplyOrientation(canvas, orientation, out int w, out int h);  // 见下
    metadata = new ImageMetadata(MapFormat(info.Format), w, h, orientation, hasAlpha: info.Channels == 4);
    return new SoftwareFrame(canvas);
}
finally
{
    if (!transferred) { ByteBufferPool.Return(buffer); }
}
```

方向处理（`request.AutoOrient && orientation is > 1 and <= 8`）：

| orientation | 做法 | 额外缓冲 |
|---|---|---|
| 2（水平镜像） | `canvas.FlipHorizontal()` 原地 | 无 |
| 3（180°） | `canvas.Rotate180()` 原地 | 无（改造成原地：逐行交换 + 行内反转） |
| 4（垂直镜像） | `canvas.FlipVertical()` 原地（复用一行 scratch） | 一行 |
| 5..8（转置类） | `Rotate90/Rotate270` + 一次 `Flip*`，结果来自 `CreatePooled` | 一块（池化，无 LOH 分配） |

顺序变化：以前是「先转直通像素、再预乘」，现在是「先预乘、再转预乘像素」。
逐像素通道置换与预乘可交换，结果逐字节相同——写进注释并用用例守住。

#### 11.1.4 用例（新增 `ByteBufferPool` 与解码路径）

需要先给测试工程开内视：`src/ImageFacade.SdcbSimdCodecs/ImageFacade.SdcbSimdCodecs.csproj` 加
`InternalsVisibleTo Include="ImageFacade.SdcbSimdCodecs.Tests"`（现在只对两个 OpenCV 后端开）。

- `Decode_Still_Matches_Png_RoundTrip`：现有 PNG 往返用例原样保留（回归基线）。
- `Decode_Does_Not_Allocate_LOH_In_Steady_State`：预热两次（解码 + Dispose）后，
  1080p PNG 解码期间 `GC.GetAllocatedBytesForCurrentThread` < 64 KB（小对象不强制）。
- `Decode_Reuses_Pooled_Buffer`：连续解码 + Dispose 三次，`ByteBufferPool.PooledCount` 稳定在 1，`PooledBytes` 不变大。
- `Decoded_Frame_Survives_Other_Decodes`：A 帧解码后保留 → 再解 B 并 Dispose → A 的像素逐字节不变（池没串号）。
- `Decode_Failure_Returns_Buffer_Exactly_Once`：头部合法但数据截断的 PNG 抛异常后，`PooledCount` 回到解码前的值。
- `Decode_Applies_Exif_Orientation`：手工造一张带 APP1（`Orientation = 6`）的 JPEG，
  解码结果与「直通解码 + `PixelTransforms.ApplyOrientation`」逐字节相等；2..8 全覆盖。
- `Trim_Releases_Decode_Buffers`：`ByteBufferPool.Trim()` 后 `PooledBytes == 0`，随后解码仍正常。

### 11.2 B. 核心自带光栅：把 `Rendering/` 搬进主包

**不做装饰器、不做 fallback。** 光栅器是核心能力，后端缺什么就用自己的（核心的）绘制原语补，
而不是外面套一层"试试看不行就换人"。

#### 11.2.1 缺口清单（已核对）

| 后端 | 现状 |
|---|---|
| Software | 全支持 |
| Skia | 全支持（形状 / 虚线 / 半透明 / 文字） |
| ImageSharp | 全支持（虚线走 `PatternPen`，半透明由 ImageSharp 合成） |
| **OpenCV** | `color.A < 255` 的 `Fill/Draw/Line` 与**所有虚线**一律 `return false`（`CanDraw` + `Style.HasDash`，`OpenCvBackend.cs:265-405`），门面抛 `FeatureNotSupportedException` |

今天真正被卡住的只有 **OpenCV 的半透明形状与虚线**；其余后端没有缺口。

#### 11.2.2 搬迁清单（纯机械，零第三方依赖）

`src/ImageFacade.SdcbSimdCodecs/Rendering/` 共 **39 个文件、约 4500 行**，只依赖 BCL
（`System.Numerics` 的 `Matrix3x2`、`Vector128` 加速路径），**完全不引用 Sdcb / 任何编解码类型**
（已逐个文件核对 `using`）。所以可以原样搬：

| 项 | 变化 |
|---|---|
| 物理路径 | `src/ImageFacade.SdcbSimdCodecs/Rendering/**` → `src/ImageFacade/Rendering/**` |
| 命名空间 | `ImageFacade.SdcbSimdCodecs.Rendering` → `ImageFacade.Rendering`（39 个文件一次性替换） |
| 可见性 | 保持不变（`Canvas`、`Color`、`PixelOps`、`RowBlender`… 仍是 `internal`；核心内部谁都能用） |
| 依赖 | 无新增 `PackageReference`；核心仍是零第三方依赖，`IsAotCompatible` 不变（`Vector128` 在 net8 就有） |

**这是核心包定位的公开变化**：`ImageFacade.csproj` 的 Description 现在写"不含任何编解码或渲染依赖"、
plan §1.3 / §7 也这么写。本批之后改成"**自带 CPU 光栅（无编解码、零第三方依赖）**"，
并说明为什么值得：光栅器只有一份，四个后端 + 无后端的裸用场景（§13）共用，不再各自实现。

#### 11.2.3 核心新增：`RasterBackend` / `RasterFrame`（都 internal，**不新增公开类型**）

**为什么不直接把 `Canvas` 给用户**：`Canvas` 现在是 `internal sealed`，它的参数与返回值用到
`Color`、`Rectangle`、`IPath` / `PathBuilder`、`Pen`、`LineCap` / `LineJoin`、`IBrush`、
`ResizeOptions` 等——`Rendering/` 里**除了 `RasterBackend` 全是 internal**（已逐个核对）。
公开 `Canvas` 等于一次性公开 40 个图形学内部类型，还要把
`Buffer`（裸 `byte[]`）、"预乘存储"、`Wrap` / `Adopt` 的缓冲所有权立成永久合同；
而核心已经有 `Rgba32` / `RectF` / `Drawing.StrokeStyle` 这套公开模型，会变成两套并存、且收不回来。
所以**不公开 `Canvas`**，绘图能力只经已有门面暴露：

```csharp
// src/ImageFacade/Rendering/RasterBackend.cs —— internal
internal sealed class RasterBackend : IImageBackend
{
    public string Name => "raster";   // 只出现在异常消息里，不是公开概念
    // Create / Clone / Clear / TryGetPixel / TryCopyPixels / ImportPixels / Resize / Draw* / Measure*
    // 全部实现：直接调 Rendering 里的 Canvas / DrawingContext / Resizer / TextDrawing

    // 编解码：没有——核心不带任何 codec
    public IReadOnlySet<ImageFormat> SupportedDecodeFormats => FrozenSet<ImageFormat>.Empty;
    public IFrame Decode(...) => throw new FeatureNotSupportedException(Name, "核心光栅不带编解码……");
    public void Encode(...) => throw new FeatureNotSupportedException(...);

    // 本批**不做**「直接接管外部缓冲」：wrap: true 静默退化为一次拷贝（ImportPixels 顺手预乘）。
    // 真零拷贝要把 Canvas 的存储从 byte[] 改成 Span<byte>，等有实际性能诉求时再开（见 §13.7）。
    public bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame);
}

internal sealed class RasterFrame : IFrame   // 现在的 SoftwareFrame，改名并在核心落地
```

**用户侧唯一的新增公开入口是一行构造**：

```csharp
// src/ImageFacade/ImageEngine.cs
/// <summary>用核心自带的光栅器建引擎：不编解码，只负责建图 / 画 / 交像素。</summary>
public ImageEngine() : this(new RasterBackend(), ownsBackend: true) { }
```

于是"裸用核心"的代码里不出现任何后端类型（§13 的例子就是这样写的）：

```csharp
var engine = new ImageEngine();                                   // 自带光栅
using var img = engine.Create(1920, 1080, Rgba32.Black);          // 输入
img.DrawText("hello", new PointF(16, 16), TextStyle.Default);     // 画
img.CopyPixelsTo(destPtr, stride, PixelFormat.Bgra32);            // 输出
```

`ImageEngine.Default` 仍是"必须显式赋值、不做自动探测"，无参构造只是显式选择核心光栅，语义一致。

`RasterBackend` 保持 internal 够用的原因：全仓只有 **OpenCV 4/5** 需要复用它画（已有
`InternalsVisibleTo`），Sdcb 包也有内视；第三方要写自己的后端，实现 `IImageBackend` 即可，我们不把
"内部光栅实现"当成公开扩展点。

`Capabilities` = `Resize | DrawShapes | DrawPoints | DrawText | DrawTextUnicode | AlphaBlending | Dash | ExifOrientation | AotCompatible`
（**不含** `LossyEncode`：那是 Sdcb 包的能力）。

`Name` 用 `"raster"` 而不是 `"software"`：`"software"` 已经被 `SoftwareBackend`（带编解码）占用，
不能改（公开 API）。

#### 11.2.4 Sdcb 包收缩为"编解码一层皮"（组合，不是继承）

| 现在 | 之后 |
|---|---|
| `ImageBackendBase : IImageBackend`（309 行，编解码 + 6 个 abstract + 一堆 `return false`） | `public abstract class ImageBackendBase : IImageBackend`（约 230 行）：`private readonly RasterBackend _raster = new();`，6 个 abstract（`Create`/`Clone`/`Clear`/`TryGetPixel`/`TryCopyPixels`/`ImportPixels`）改成 override 转发，`TryResize`/`TryDraw*`/`TryMeasureText`/`TryAcquirePixels`/`TryGetTransparency`/`TryWrapPixels` 也转发；只留 codec 注册 / `Decode` / `Encode` / `Supported*` 与 `Name`/`Version`/`Capabilities` 的 abstract |
| `SoftwareBackend : ImageBackendBase`（438 行，绘制全在里面） | `public sealed class SoftwareBackend : ImageBackendBase`（约 40 行：`Name`、`Version`、`Capabilities + LossyEncode`） |
| `SoftwareFrame`（15 行，internal） | 删除，统一用核心 `RasterFrame` |

**为什么用组合而不是 `: RasterBackend`**：`RasterBackend` 是 internal，而 `SoftwareBackend` 是 public，
public 类型不能继承 internal 基类（CS0060：基类的可访问性不得低于派生类）。组合还有额外好处：
`ImageBackendBase` 的 6 个 abstract 变成有默认实现，下游自建后端只要实现 `Name`/`Version`/`Capabilities`
就能跑绘制，属于非破坏性的负担减轻。

公开 API 不变：`ImageBackendBase.RegisterCodec / RegisterDecoder / RegisterEncoder / ClearCodecs` 全部保留，
`SoftwareBackend` 的类名、`Name => "software"`、`LossyEncode` 能力位都不变 → 使用方一行代码不用改。
帧类型是 `internal` 且只经 `IFrame` 暴露，所以也没人能看到 `SoftwareFrame` 的消失。

#### 11.2.5 缺能力怎么补：后端内部用核心光栅（不是外层装饰器）

只改 **OpenCV 两个后端**（4/5 共用一份源码），每个原本 `return false` 的分支改成"自己画"：

```csharp
// OpenCvBackend.cs 内部
private static readonly RasterBackend Raster = new();   // 无状态，进程内共享一份

public override bool TryFillPolygon(IFrame frame, in PolygonSpec spec)
{
    if (spec.Style.Color.A == 255)
    {
        return FillPolygonOpaque(frame, spec);          // 原有 CV 路径，一个字节都不多搬
    }

    // 半透明：用核心光栅画一小块，再合成回这张 Mat
    return RasterCompose.Paint(this, frame, Bounds(spec),
        (target, dx, dy) => Raster.TryFillPolygon(target, Translate(spec, dx, dy)));
}
```

`Raster` 是核心的 internal `RasterBackend`（§11.2.3），靠已有的 `InternalsVisibleTo` 在 OpenCV 包里可见。
`RasterCompose.Paint`（核心 `ImageFacade.Rendering` 里的 `internal static` 助手）负责固定的四步：

1. bbox 与帧矩形求交，空则直接返回 `true`（本来就没有像素会变）；
2. `Raster.Create(bw, bh, null)` → 把 spec 按 `-bbox.TopLeft` 平移后画上去；
3. `Raster.TryCopyPixels(temp, rented, bw * 4)` 取回直通 RGBA（缓冲走 §11.1 的 `ByteBufferPool`）；
4. 写回目标帧：优先 `target.TryAcquirePixels(frame, out lease)`，否则
   `TryCopyPixels` 全图 + `ImportPixels` 回来；在 bbox 内做 source-over，`aOut = aS + aD*(1-aS)`。

**为什么不再要装饰器**：装饰器要"先试 inner、失败再换人"，能力位只能声明"不支持"，
用户还得知道有这层、还得配 `Shapes/Text` 开关、还得处理 `FallbackCount`；而能力其实是这个后端**真实拥有**的，
就该在 `Capabilities` 里如实声明 `AlphaBlending | Dash`，门面照常画，没有第二套语义。

**为什么不能 `TryDrawFrame` 贴上去**：`IFrame` 是后端私有类型，核心的 `RasterFrame` 丢给
`OpenCvBackend.TryDrawFrame` 必然 `InvalidCastException`。像素通道
（`TryAcquirePixels` / `TryCopyPixels` + `ImportPixels`）是唯一合法的跨后端通道。

#### 11.2.6 命名冲突清单（搬迁前必须处理）

核心已有 `ImageFacade.PointF`、`Size`、`ResizeOptions`、`ImageFacade.Drawing.StrokeStyle`，
`Rendering/` 里也有同名类型（不同命名空间）。规则：

- 公开面**不新增**这些名字的歧义：`Rendering` 下所有类型保持 `internal`（`PointF`/`Size`/`Rectangle`/
  `StrokeStyle`/`ResizeOptions`/`Canvas`/`RasterBackend`/`RasterFrame` 一个都不公开，见 §11.2.3）；
- 核心内部 `RasterBackend.cs` 同时用到 `ImageFacade.Backends.*`（`PointF`、`ResizeRequest`）和
  `ImageFacade.Rendering.*`（`PointF`、`Size`、`ResizeOptions`）→ 用 4 个 `using` 别名；
- `ImageFacade.SdcbSimdCodecs` / `OpenCv*` 里同理，别名写在文件头，不用全局 using。

#### 11.2.7 精度与成本

- 合成在**直通 RGBA** 空间，半透明像素与"原生就用光栅画"逐字节一致；不透明像素走原路径，完全不经过光栅。
- OpenCV 一次补齐 = 全图 RGBA↔BGRA 往返（1080p 1 ms 级）+ bbox 合成。**这是"正确性优先"路径**：
  每帧都走到这里，说明要么换后端，要么改用不透明色 / 实线（写进 README）。
- 支持的操作零额外开销（不透明形状、实线、文字仍走 CV 原路径）。

#### 11.2.8 用例

核心（`tests/ImageFacade.Tests`，已开内视）：

- `Raster_Creates_Draws_And_Exports`：搬过来的回归基线（`Create`/`Clear`/`GetPixel`/`CopyPixels`）。
- `Raster_TryWrapPixels_Returns_False`：本批不开零拷贝，`wrap: true` 走拷贝 + 预乘，结果与 `wrap: false` 逐字节相等。
- `Raster_Without_Codecs_Throws_On_Decode`：`Decode`/`Encode` 抛 `FeatureNotSupportedException`。
- `RasterCompose_Paints_Only_Inside_Box`：bbox 外像素逐字节不变；完全在画外时返回 `true` 且零改动。
- `ImageEngine_DefaultConstructor_Draws_And_Exports`：`new ImageEngine()` → `Create` → `DrawText` →
  `CopyPixelsTo` 全链路可用（用户侧唯一入口，不出现任何后端类型）。
- `Public_Api_Gains_No_New_Backend_Type`：反射扫 `ImageFacade` 程序集的公开类型，
  断言 `RasterBackend`/`RasterFrame`/`Canvas` 都不在其中（代替"不新增公开类型"的口头约定）。

OpenCV（`tests/ImageFacade.OpenCv4.Tests` + `OpenCv5` 同一份源码）：

- `OpenCv_Draws_Dashed_Line`：带 `DashPattern` 的 `DrawLine` 不再抛；与 `RasterBackend` 画的同一笔对比，容差 ≤ 3。
- `OpenCv_Blends_SemiTransparent_Fill`：`(255,0,0,160)` 压在不透明蓝底上 ≈ `(160R + 95B)/255`，容差 2。
- `OpenCv_Capabilities_Advertise_AlphaBlending_And_Dash`：能力位如实声明（不是"不支持 + 外挂"）。
- `OpenCv_Offscreen_Geometry_Is_NoOp`：一半在画外不抛、不越界写；完全画外时像素不变。

软后端（`tests/ImageFacade.SdcbSimdCodecs.Tests`）：原有绘制用例全部保留（搬走实现但行为不变，就是回归）。

### 11.3 执行顺序

1. 本节进 `planv2.md`。
2. **A1**：`ByteBufferPool` + `Canvas.Adopt/CreatePooled` + `Dispose` 归还 + `SoftwareBackend.Import` 长度修正 →
   `dotnet test` 全绿（这一步**不改** `Decode`，单独可回滚）。
3. **A2**：`Decode` 直写 + 原地预乘 + 方向处理 + 7 个新用例 → 全绿；顺手记录实测耗时。
4. **B1（纯搬迁）**：`Rendering/` 整目录移到核心 + 命名空间替换 + 4 个别名 → 只改路径不动逻辑，
   构建通过、软后端用例全绿（这一步单独可回滚，是后面所有事情的地基）。
5. **B2（拆类）**：核心 internal `RasterBackend` + `RasterFrame` + 公开入口 `ImageEngine()` →
   `ImageBackendBase` 改为组合转发并缩身 → `SoftwareBackend` 只剩 40 行 → 删 `SoftwareFrame`；用例全绿。
   （`TryWrapPixels` 本批返回 false，`wrap: true` 退化拷贝；真零拷贝见 §13.7 待办。）
6. **B3（补能力）**：`RasterCompose` 助手 + OpenCV 4/5 内部补齐（半透明形状、虚线）+ 能力位如实声明 +
   新用例（虚线/半透明/画外几何/能力位）。
7. **B4（例子）**：按 §13 做 `samples/ImageFacade.AvaloniaDemo`（ffmpeg → `WriteableBitmap`），
   验证核心包脱离任何后端/编解码也能用。
8. README：解码那段补"零拷贝"一句；新增「核心自带光栅」一节与「不支持的操作：后端内部补齐」一节；
   改第 39 行「后端互不兜底」、第 318 行 OpenCV 那句、核心包"不含渲染"的定位描述与 plan §1.3 / §7；
   能力矩阵补一行「核心光栅（`new ImageEngine()`，不引任何后端包）」；文档里**不出现** `RasterBackend` 这个名字
   （它是 internal，用户只需要 `ImageEngine`）。
9. 最后全量 `dotnet build ImageFacade.slnx -c Release` + `dotnet test`，把实测数据写回本节。

### 11.4 验收

- [ ] 构建 0 警告 0 错误（各工程 `TreatWarningsAsErrors=true`）。
- [ ] 测试全绿（基线 305，预计 +20 ~ +24），六个测试工程都跑。
- [ ] A：1080p PNG / JPEG 解码稳定态 LOH 分配为零（`GC.GetAllocatedBytesForCurrentThread` < 64 KB）。
- [ ] A：连续 100 次"解码 + Dispose"后 `ByteBufferPool.PooledBytes` 不超过 64 MB 上限，`Trim()` 后归零。
- [ ] A：EXIF 方向 2..8 的解码结果与改前逐字节相等。
- [ ] A：改前 / 改后 1080p PNG、4K JPEG 解码耗时对比表（Release，100 次均值）。
- [ ] B：`Rendering/` 全部 39 个文件只在核心一份，Sdcb 包里不再有 `Rendering` 目录（`git grep` 为零）。
- [ ] B：核心包仍**零第三方依赖**、`net8.0` 可编译、`IsAotCompatible` 不变；`SoftwareBackend` 的公开 API 一个不少。
- [ ] B：OpenCV 虚线 / 半透明形状不再抛，与核心光栅输出容差 ≤ 3；不透明路径像素与改前逐字节相等。
- [ ] B：OpenCV 的 `Capabilities` 如实包含 `AlphaBlending | Dash`（不再依赖外挂声明）。
- [ ] B：`new ImageEngine()` 全链路可用（`Create` → `DrawText` → `CopyPixelsTo`）；
  `wrap: true` 退化为拷贝，结果与 `wrap: false` 逐字节一致。
- [ ] B：`ImageFacade` 程序集的**公开类型清单没有新增**（`Canvas`/`RasterBackend`/`RasterFrame` 都是 internal）。
- [ ] B：`samples/ImageFacade.AvaloniaDemo` 不引 Sdcb / Skia / OpenCV，1080p30 稳定。

### 11.5 本批不做

| 项 | 原因 |
|---|---|
| `Encode` / `TryAcquirePixels` / `TryDrawFrame` 的缓冲换成 `ByteBufferPool` | 同一模式但会铺开改动面；先让 `Decode` 单独可回归 |
| 帧池（`Create` 出来的帧也池化） | 生命周期由调用方掌握，池化会把"忘 Dispose"变成额外内存泄漏 |
| 重名类型合并（`Rendering.PointF/Size/ResizeOptions` 并入 `ImageFacade.*`） | 纯搬迁风险已经够大；合并要动几百处调用点，单独一轮（§11.2.6） |
| 公开路径模型 `FillPath(IPath, ...)` | 核心现在有 `IPath` 了，但 SPI 加接口会影响所有第三方后端；先让后端内部用 `RasterCompose` |
| 按 bbox 局部导出 / 导入（`TryCopyPixels(rect, ...)`） | 需要扩 SPI；现在用全图往返顶着，等 §13 的实测数据再决定 |
| `TryWrapPixels` 真零拷贝（§7 第 7 条） | 本批不做，核心与其他后端一起延后（§13.7） |

---

## 12. 第六批：SimdSkia 后端（`ImageFacade.SimdSkia`）

> 目标：再加一个**纯托管、零原生依赖**的 2D 后端，复用现有 `SkiaBackend.cs` 那份源码，不重写一遍。

### 12.0 上游调研（nuget.org/packages/SimdSkia，0.1.6）

| 项 | 情况 |
|---|---|
| 定位 | **SkiaSharp 的源码兼容门面**：直接暴露 `SkiaSharp` 命名空间与 `SKBitmap` / `SKCanvas` / `SKPaint` / `SKFont` / `SKPath`，基线对齐 SkiaSharp **3.119.4** 的"已实现子集"（公共签名 3230/3235，类型 201，仍留 5 项原生合同缺口） |
| 实现 | 纯 C# + 通用 SIMD 光栅/合成（`SimdSkia.Core/Cpu`），文字是托管静态 glyf TrueType 解析（`SimdSkia.Text`），编解码直接用 **Sdcb.SimdCodecs 2.0.0**（`SimdSkia.Imaging`） |
| 依赖 | `SimdSkia.Compatibility >= 0.1.6`；三方 `Sdcb.SimdCodecs 2.0.0`、`Clipper2 2.0.0`。**与核心已用的 `Sdcb.SimdCodecs.Abstractions 2.0.0` 同版本，不冲突** |
| TFM | **只 `net10.0`**（连 `-android/-browser/...` 都是 NuGet 推导值） |
| 许可 | MIT（© 2026，13 个包统一版本号） |
| 缺口 | 无 complex shaping、无 CFF/可变/彩色字体、无 hinting；画质/性能与原生 Skia 仍有差距；JPEG 编码质量不如 libjpeg |
| GPU | Windows Direct2D/D3D11、Linux Vulkan（回读模式）/GL、Metal 未验证 |
| AOT | 未声明 NativeAOT 支持，只有 Android 16 ARM64 的 **Mono AOT** 实机验证 |
| 成熟度 | 0.1.x，总下载 ~1.5K，当前版 0 下载，无下游依赖 → **按实验性对待，版本钉死** |

### 12.1 策略：源码链接，不重写

`SkiaBackend.cs` 只有 668 行，用到的 `SK*` 面已经不宽（见 12.2 清单）。所以走 **ImageSharp / ImageSharp2 那条老路**：
新工程链接同一份源码，两处差异用 `DefineConstants` 隔离。

```xml
<!-- src/ImageFacade.SimdSkia/ImageFacade.SimdSkia.csproj -->
<PropertyGroup>
  <Version>0.4.0</Version>
  <TargetFramework>net10.0</TargetFramework>            <!-- 上游只 net10.0 -->
  <AssemblyName>ImageFacade.SimdSkia</AssemblyName>
  <RootNamespace>ImageFacade.SkiaSharp</RootNamespace>   <!-- 与 ImageSharp2 同款：命名空间跟源码走，不跟工程名 -->
  <DefineConstants>$(DefineConstants);SIMDSKIA</DefineConstants>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\ImageFacade\ImageFacade.csproj" />
  <PackageReference Include="SimdSkia" />
</ItemGroup>
<ItemGroup>
  <Compile Include="..\ImageFacade.SkiaSharp\**\*.cs"
           Exclude="..\ImageFacade.SkiaSharp\bin\**;..\ImageFacade.SkiaSharp\obj\**"
           Link="Shared\%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

结果：两个包的**类型全名完全相同**（`ImageFacade.SkiaSharp.SkiaBackend`），使用方换包不用改代码。
代价也和老规矩一样：**`Sevencat.ImageFacade.SkiaSharp` 与 `Sevencat.ImageFacade.SimdSkia` 互斥**
（同一个 `SkiaSharp` 命名空间被两个程序集实现，同工程共存必然 `CS0433`）。README 必须写死这一条。

### 12.2 第一步是"对账"，不是"写代码"

已核对的 `SkiaBackend.cs` 依赖面（对账时按这张表逐个消）：

| 用途 | 用到的成员 |
|---|---|
| 帧 | `SKBitmap(SKImageInfo)`、`Bitmap.GetPixels()/PeekPixels()/Info/RowBytes`、`SKColorType.Rgba8888`、`SKAlphaType.Premul`、`SKCanvas.Clear/DrawBitmap/DrawRect/DrawPath/DrawText/DrawLine/DrawPoints` |
| 解码 | `SKData.CreateCopy`、`SKCodec.Create`、`codec.Info`、`codec.GetPixels(SKImageInfo, IntPtr)`、`SKCodecResult.Success/IncompleteInput` |
| 编码 | `SKImage.FromBitmap`、`image.Encode(SKEncodedImageFormat, int)`、`SKData.SaveTo` |
| 像素搬运 | `new SKPixmap(SKImageInfo, IntPtr, stride)`、`pixmap.ReadPixels(...)`、`bitmap.InstallPixels`（等价替身） |
| 路径 | `SKPath` 写 API、`SKPathEffect.CreateDash`、`SKStrokeCap/Join` |
| 文字 | `SKTypeface.FromData(SKData)` / `SKTypeface.Default`、`SKFont(SKTypeface, float)`、`font.Metrics`、`font.MeasureText(ReadOnlySpan<char>)`、`SKFont.Edging`、`SKTextAlign` |
| 缩放 | `SKSamplingOptions`、`SKFilterMode`、`SKMipmapMode` |

缺成员的处置顺序（**不许猜 API**）：

1. 语义等价改写（如 `SKPixmap.ReadPixels` → `SKBitmap.InstallPixels`）→ 直接改，`#if SIMDSKIA` 隔离；
2. 实在没有的（某个 `SKCodecResult` 分支、某个 paint 字段）→ **能力位降级**（`return false`），
   由 §11.2 的 `RasterFallbackBackend` 兜底；
3. 都做不到 → 该能力在 SimdSkia 后端明确不支持并写进 README，**绝不静默给错结果**。

共享源码目前**一个 `#if` 都没有**（已确认），本批会加进第一批：`Name`（`"skia"` / `"simdskia"`，
异常消息与日志要能分辨）和 `Capabilities` 里的 `AotCompatible`（上游未声明 NativeAOT，能力位要摘）。

### 12.3 已知行为差异（用例与 README 都要钉住）

| 点 | SkiaSharp | SimdSkia | 处理 |
|---|---|---|---|
| 编解码 | 自带 SKCodec，6 种格式 | 走 Sdcb codec（**按需注册**） | `SupportedDecodeFormats/EncodeFormats` 取"实际能建 codec 的格式"；未注册的格式抛 `FeatureNotSupportedException` 而不是笼统的"无法识别" |
| 文字 | HarfBuzz 整形 + 系统字体 | 静态 glyf 解析、无整形/无 hinting | 内置文泉驿是**静态 glyf**，中文可用；度量与原生 Skia 有 ±1px 级差，比对用例容差 3 |
| 像素布局 | 显式 `Rgba8888 + Premul` | 默认 N32 预乘（门面已校正） | 代码里本来就显式传 `Rgba8888 + Premul`；再加一条**运行时断言**：`ColorType/AlphaType` 不符就抛清晰异常，不带着错的布局往下算 |
| JPEG / WebP 画质 | libjpeg / skia | Sdcb 实现 | 编码用例按"视觉接近"判（沿用 `Jpeg_RoundTrip_Is_Visually_Close` 的容差思路） |
| GPU | — | 有，但不要把不确定性引进来 | **只走 CPU 光栅**（画到 `SKBitmap`），不引 `SimdSkia.Gpu*`；GPU 对象还要求创建线程 Dispose，和我们的帧生命周期模型冲突 |
| AOT | 需 RID 原生资产 | 纯托管但未声明 AOT | 能力位摘掉 `AotCompatible`；NativeAOT 实测留作后续结论 |

### 12.4 用例：顺手把 Skia 后端一直缺的测试补上

现状：`tests/` 下只有 core / ImageSharp×2 / OpenCV×2 / Sdcb 六个工程，**SkiaSharp 后端没有测试工程**（只有 samples 出图）。
本批一次补齐两个：

- `tests/ImageFacade.SkiaSharp.Tests`：新建 `SkiaBackendTests.cs`（引用 `SkiaSharp` + `SkiaSharp.NativeAssets.Win32`）；
- `tests/ImageFacade.SimdSkia.Tests`：`Compile Include="..\ImageFacade.SkiaSharp.Tests\SkiaBackendTests.cs"` 链接同一份源码，
  引用 `ImageFacade.SimdSkia`，**不引任何原生资产**（这本身就是"零原生依赖"的验收手段）。

用例内容（约 25 个）：`Create/GetPixel` 往返、`Clear`、直线/矩形/椭圆/多边形（描边 + 填充）、虚线、
半透明叠加、中文文字 + 折行 + `MeasureText` 与墨迹一致、Resize 六模式、`CopyPixels(Bgra)`、非紧凑 stride 的
`FromPixels`、PNG/JPEG 往返、`Decode` 的 `HasTransparency`、Dispose 后再用抛异常、`ColorType/AlphaType` 断言。

SimdSkia 侧需要注册 Sdcb codec：直接链接 `tests/ImageFacade.SdcbSimdCodecs.Tests/TestCodecs.cs`（注册是进程级，
PNG/JPEG/BMP 三个足够），避免再抄一份。

### 12.5 构建与打包改动清单

1. `Directory.Packages.props`：`<PackageVersion Include="SimdSkia" Version="0.1.6" />`（**钉死**，13 个 `SimdSkia.*`
   必须同版本；只引门面包 `SimdSkia`，它会把 Core/Cpu/Text/Imaging/Compatibility 拉齐）。
2. `ImageFacade.slnx`：加 `src/ImageFacade.SimdSkia` 与两个测试工程。
3. 新包 `Sevencat.ImageFacade.SimdSkia`：`<Version>0.4.0</Version>`，Description 写明
   "实验性、纯托管、仅 net10.0、依赖 SimdSkia 0.1.x 子集"。
   同批把**全部 8 个包统一到 `0.4.0`**，见 12.5.1。
4. `THIRD-PARTY-NOTICES.txt`：补 SimdSkia（MIT）+ 其链式依赖 `Sdcb.SimdCodecs`、`Clipper2`。
5. README：
   - 徽章/包表加一行 `SimdSkia`；
   - 「后端」表加一行（`SimdSkiaBackend`？不——类型名仍是 `SkiaBackend`，包名不同）；
   - 能力矩阵加一列；
   - 显式写两条约束：**与 `SkiaSharp` 包互斥**、**编解码要先注册 Sdcb codec**；
   - 「换一行构造代码」那段示例补 `new ImageEngine(new SkiaBackend())` 用任一包皆可；
   - 版本相关行按 12.5.1 改成 `0.4.0`。

#### 12.5.1 版本收口：全部统一到 `0.4.0`

现在是 `ImageFacade` 0.3.1、其余 0.3.0（README 第 61/65/66 行的"七个包 `0.3.0`"写的就是这个基线）。
本批两件事都够一个 minor（§11 的 OpenCV 虚线/半透明从**抛异常**变成**能画**，这是行为变化；
§12 直接多一个包），所以本次发布把 8 个包**一起抬到 `0.4.0`**，不再保留"核心 0.3.1、其余 0.3.0"的参差。

改动点（全部是纯文本，`Directory.Build.props` 的 `<VersionPrefix>0.3</VersionPrefix>` 只是兜底，也要跟着改）：

| 位置 | 改成 |
|---|---|
| `Directory.Build.props` 的 `<VersionPrefix>` | `0.4` |
| `src/ImageFacade/ImageFacade.csproj` | `<Version>0.4.0</Version>` |
| 其余 6 个既有后端 csproj | `<Version>0.4.0</Version>` |
| 新工程 `src/ImageFacade.SimdSkia` | `<Version>0.4.0</Version>` |
| README 第 61 行 | "八个包……当前版本 `0.4.0`" |
| README 第 65-66 行安装示例 | `Version="0.4.0"` |
| README 第 73 行 | "`0.4.0` 是当前八个包统一的基线" |
| README 第 86 行 | 安装示例里的 `Version="0.4.0"` |

注意两点：

- `SimdSkia` **上游**版本（0.1.6）与我们的包版本无关，只钉在 `Directory.Packages.props` 里，别混进 README 的版本叙事；
- 版本抬升放在**本批最后一步、`dotnet pack` 之前**（12.6 第 7 步），中途构建日志里的版本号不影响任何用例。

### 12.6 执行顺序

1. 本节进 `planv2.md`。
2. 建工程 + 中央版本 + 链接源码 → `dotnet build` **对账**，把所有缺口列成清单（先不修，只记录）。
3. 按 12.2 的三级处置补齐/隔离，跑到 0 警告；同时加 `Name` / `Capabilities` 两个 `#if`。
4. `tests/ImageFacade.SkiaSharp.Tests` 先绿（补历史缺口，与 SimdSkia 无关也要绿）。
5. `tests/ImageFacade.SimdSkia.Tests` 链接同一份用例 → 绿（差异按 12.3 的容差判）。
6. 实测对比写回本节：画 1000 段线、画一段中文、Resize 1080p→640、PNG 编解码，各 100 次均值 + 分配。
7. **版本收口（12.5.1）**：`VersionPrefix` → `0.4`、8 个 csproj → `0.4.0`、README 版本行同步；
   随后 `dotnet pack` 检查 nuspec 里全是 `0.4.0`。
8. README / notices 收尾，最后全量 `dotnet build ImageFacade.slnx -c Release` + `dotnet test`。

### 12.7 验收

- [ ] 8 个 src 工程 0 警告 0 错误（`TreatWarningsAsErrors`），8 个包都能 `dotnet pack`。
- [ ] 8 个包 nuspec 版本统一为 `0.4.0`，`VersionPrefix` 为 `0.4`，README 不再出现 `0.3.0` 基线。
- [ ] 同一份 `SkiaBackendTests.cs` 在两个测试工程全绿（差异只在 12.3 的容差内）。
- [ ] SimdSkia 侧**无原生依赖**：把测试输出目录里的 `libSkiaSharp*` 全删掉再跑一遍仍然绿。
- [ ] `SK*` 缺口清单归档进本节：哪些等价改写、哪些降级为能力位、哪些明确不支持。
- [ ] 性能对比表（SkiaSharp vs SimdSkia，同一场景）。
- [ ] SimdSkia 后端默认 `Capabilities` 不含 `AotCompatible`；NativeAOT 实测只记录结论，不在本批承诺。

### 12.8 本批不做 / 风险

| 项 | 原因 |
|---|---|
| GPU 路径（Direct2D / Vulkan / GL） | 我们只画到 `SKBitmap`；GPU 资源要求创建线程 Dispose，与帧生命周期模型冲突 |
| 复杂文字整形（阿拉伯 / 印度语系） | 上游 `SimdSkia.Text` 就没有，这是包本身的边界，README 写明 |
| SimdSkia 后端用核心光栅补缺口 | 对账若发现确有补不上的能力，就在 `SkiaBackend.cs` 里用核心光栅（§11.2）画：核心 csproj 的 `InternalsVisibleTo` 加 `ImageFacade.SimdSkia` 即可，**不引** `ImageFacade.SdcbSimdCodecs`；本批先不改，等对账单出来再定 |
| 把 SimdSkia 放进默认推荐路径 | 0.1.6、1.5K 下载、画质/性能仍落后原生 Skia → 标实验性、版本钉死 |
| samples 增加 SimdSkia 出图 | 出图脚本要动 `docs/images`，与本批目标无关 |

---

## 13. 例子：ffmpeg → Avalonia `WriteableBitmap`（只引核心包）

> 这条链路的证明价值：**不解码、不编码、不画到文件**，所以不需要 Sdcb / Skia / OpenCV 任何包。
> `Rendering/` 进核心（§11.2）之后，核心主包自己就能完成"接外部像素 → 画 → 交出像素"。

### 13.1 为什么现在能做到（前置条件）

| 能力 | 谁提供 | 现状 |
|---|---|---|
| 零拷贝接管外部缓冲 | 核心光栅的 `TryWrapPixels`（`new ImageEngine()` 背后的 internal `RasterBackend`） | **本批不做**：返回 false，`wrap: true` 退化成一次拷贝；真零拷贝见 §13.7 |
| `FromPixels(ptr, …, wrap: true)` 落到 wrap 路径 | `ImageEngine.cs:364-393` | 已存在，后端返回 false 时退化成拷贝（本批正是这条路径） |
| 画字 / 形状 / 半透明 / 虚线 | 核心 `Rendering` | 本批搬进来 |
| 导出成 Avalonia 能吃的像素 | `FacadeImage.CopyPixelsTo(IntPtr, stride, PixelFormat.Bgra32)` | 已存在（`PixelFormat.Bgra32` 的注释就写着 "Avalonia `Bgra8888`"，`Bgra32Premultiplied` 对应 `AlphaFormat.Premul`） |

### 13.2 工程

```
samples/ImageFacade.AvaloniaDemo/
  ImageFacade.AvaloniaDemo.csproj   OutputType=WinExe, IsPackable=false
  Program.cs / App.axaml / MainWindow.axaml(.cs)
```

- 依赖：`Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent`（12.x，与 SimdSkia 那边观测到的版本线一致）、
  `ProjectReference ../../src/ImageFacade/ImageFacade.csproj`；
- **不引** `Sdcb.SimdCodecs`、`SkiaSharp`、`OpenCvSharp`、`ImageSharp`（`dotnet list package` 要能自证）；
- 加到 `ImageFacade.slnx` 的 `/samples/` 下。

### 13.3 一帧的流程

```
ffmpeg -i in.mp4 -f rawvideo -pix_fmt rgba -          ← stdout 吐裸帧（直通）
   ↓ 读满 w*h*4（一个循环复用的缓冲，不每帧分配）
engine.FromPixels(ptr, w, h, stride, PixelFormat.Rgba32, wrap: true)   ← 本批退化：一次拷贝 + 预乘
   ↓ 画 HUD：DrawText / DrawRectangle / FillPolygon（半透明、虚线都走核心光栅）
image.CopyPixelsTo(fb.Address, fb.RowBytes, PixelFormat.Bgra32)        ← 直通 Bgra，写进 WriteableBitmap
   ↓ Source 重绑 / InvalidateVisual
```

骨架（只贴关键 20 行，细节实现时补）：

```csharp
var engine = new ImageEngine();                             // 核心自带光栅，不引任何编解码包

// UI 线程：一次建好、逐帧复用
var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                 Avalonia.PixelFormat.Bgra8888, AlphaFormat.Opaque);

// UI 线程：wrap: true 本批退化成「拷贝 + 预乘」，外部缓冲照常复用
fixed (byte* p = frames[latest])
using (FacadeImage image = engine.FromPixels(p, w, h, stride, PixelFormat.Rgba32, wrap: true))
{
    image.DrawText($"fps {fps:0.0}", new PointF(16, 16), TextStyle.Default with { Color = Rgba32.White });
    using ILockedFramebuffer fb = bitmap.Lock();
    image.CopyPixelsTo(fb.Address, fb.RowBytes, PixelFormat.Bgra32);
}
```

`wrap: true` 与否在这一版结果相同（后端都返回 false），写上只是为了以后开了真零拷贝时代码不用改。

### 13.4 线程与丢帧

- 读线程只做：`ffmpeg` 进程 stdout → 双缓冲（两个 `byte[]`，复用不每帧分配）→ 发布"最新帧序号"；
  本批 `wrap: true` 是拷贝，UI 线程不持有读线程的缓冲；等开了真零拷贝再升三缓冲（§13.7）；
- UI 线程在 `CompositionTarget.Rendering` 里取最新一帧画，**读不过来就丢**（视频叠加场景的正确策略）；
- `WriteableBitmap` 只在 UI 线程 `Lock`，不跨线程；
- `wrap: true` 的 `FacadeImage` 必须在缓冲被复用的**同一个作用域内** Dispose（读下一帧之前），否则读到撕裂画面。

### 13.5 性能目标（要实测并写回本节）

- 1080p30 / 1080p60 稳定；HUD 每帧一次中文绘制 + 两三个形状；
- 每帧托管分配 < 1 KB（`GC.GetAllocatedBytesForCurrentThread` 自己打印出来，顺带当演示的调试信息）；
- CPU 占用与纯 `WriteableBitmap` 拷贝（无 HUD）对比，差值就是绘制成本。

### 13.6 注意事项（README 例子段落同步）

| 点 | 说明 |
|---|---|
| ffmpeg 的像素格式必须与 `PixelFormat` 对上 | `-pix_fmt rgba` + `Rgba32`；`bgra` + `Bgra32`。对不上是静默的通道错位，最难查 |
| 预乘 | 本批不要求外部预乘：`Rgba32` + `wrap: true` 由 `ImportPixels` 顺手转预乘，每帧多一次全帧拷贝 + 一次遍历（1080p 约 1~2 ms） |
| stride | 1080p 时 ffmpeg 的 `rgba` 是紧凑的 `w*4`；但相机/别的源可能带 padding，所以例子必须按 `stride` 读，不能假设 |
| `WriteableBitmap` 的像素格式 | 用 `Bgra8888` + `CopyPixelsTo(..., Bgra32)`（一次通道转换，SIMD 实现）；想省掉这次转换可以试 `Rgba8888`，但要实测 Avalonia 渲染开销 |
| 分辨率变化 | `ffprobe` 先读尺寸；播放中途变分辨率就重建 `WriteableBitmap` 与缓冲，并 `Dispose` 旧帧 |
| 为什么不用 `ImageFacade.SdcbSimdCodecs` | 这条链路一帧都不编解码，引它就是白白拖一个包 + 一套 codec 注册 |

### 13.7 真零拷贝（本批不做，留作待办）

外部缓冲"直接可用、不再复制一次"需要两件事同时成立：

1. `Canvas` 的存储从 `byte[]` 改成 `Span<byte>`（`_pixels` + 池租 / 外部 / 新建三态所有权），
   否则 `TryWrapPixels` 拿到 `IntPtr` 也画不进去。改动集中在 `Canvas` 的字段、
   `Create`/`Wrap`/`Dispose`、`Buffer` 属性与约 20 处 `_buffer.AsSpan`；
2. `PixelFormat` 加 `Rgba32Premultiplied`（与已有 `Bgra32Premultiplied` 对称），
   `wrap: true` 只对预乘缓冲走 `TryWrapPixels`。ffmpeg 的 `rgba` 是直通：
   要么调用方先用 `PixelConverter.Premultiply` 预乘一次，要么确认整帧 `A == 255`（不透明视频）。

收益：1080p 每帧省掉 ~8 MB 拷贝 + 一次全帧遍历（约 1~2 ms）。等 B4 例子跑起来、
按 §13.5 实测后再决定是否值得开。

### 13.8 验收

- [ ] `dotnet run --project samples/ImageFacade.AvaloniaDemo` 能播视频，HUD 中文正常、半透明矩形正确。
- [ ] `dotnet list package` 只有 Avalonia + 核心包（证明"零编解码依赖"）。
- [ ] 断流 / 分辨率变化 / 关闭窗口不崩（帧与缓冲都已 Dispose）。
- [ ] 实测数据写回 13.5（fps、每帧分配、与纯拷贝的 CPU 差）。
- [ ] README 加一小节「例子：ffmpeg 拉流 + Avalonia 显示」，只贴 13.3 那段骨架。
