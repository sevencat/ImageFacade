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
