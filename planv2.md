# planv2 — 安全与热路径优化

> 第 1 批（§1–§6）：解码尺寸上限、`PixelLease` 幂等与 Skia 字体缓存、OpenCV/Skia 像素搬运。
> 第 2 批（§8）：`BoxPad` 不放大、保存失败清理、异步编码能力检查、`Resize` 缓冲池化、
> Skia 解码零拷贝、`ImageEngine` 所有权、`Default` 原子读写、`BuiltInFont` 单例。
> 第 3 批（§9）：`Save` 改"失败即删 + 异常提示"、超大字号抛异常、Skia 自备字体缓存、
> 字形缓存 LRU、NMS O(n log n)、`Tensorizer` 去除法、`TensorBuffer` 可 Trim、引擎释放后报错。
> 其余已识别问题列入 §7「后续批次」，带状态标记。

## 0. 基线

- 构建：`dotnet build ImageFacade.slnx -c Release`，0 警告 0 错误。
- 测试：改动前 **202** 通过（ImageFacade 98 / OpenCv4 28 / OpenCv5 28 / ImageSharp2 15 / ImageSharp3 15 / Sdcb 18）；
  第 1 批后 236，第 2 批后 247，第 3 批后 **256**。
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
| 9 | `ImageMetadata` 的 `HasAlpha` / `Orientation` 在 `ReplaceFrame` 后不更新 | `FacadeImage.cs` | 待办 |
| 10 | `BoxPad` 文档与实现不符（文档说先裁；实现缺"不放大"） | `README.md` / `ResizePlanner.cs` | **已修（§8）** |
| 11 | 门面 `TextLayouter` 与 `Font.Layout` 两套折行/行高实现 | `Internal/TextLayouter.cs` / `Font.Layout.cs` | 待办 |
| 12 | 异步编码路径跳过 `SupportedEncodeFormats` 检查 | `FacadeImage.cs` | **已修（§8）** |
| 14 | 字形缓存满了整体清空（应 LRU）；按 1/4 px 量化但用真实 size 光栅化 | `Font.cs` | **已修（§9）** |
| 15 | 超大字号静默返回空白 mask | `GlyphRasterizer.cs` | **已修（§9）** |
| 18 | `TextLayouter.Wrap` O(n²) + 每次 substring 分配 | `TextLayouter.cs:112` | 待办 |
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
| — | `Load(Stream)` 编码字节数上限 | `ImageEngine.cs` | 待办 |
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
| 18 | `TextLayouter.Wrap` 改线性扫描会改变折行位置，要连带更新视觉基线 |
| 29 | CI 加 `NuGetAudit` 可能因存量告警卡住构建，需要先跑一遍看有多少 |
| 7、8 | 零拷贝与 `TryDrawFrame` 语义属于 SPI 设计决策，改动面大 |
| 9、32 | `ImageMetadata` 正确更新需要扫描全图判断 alpha，成本与收益要权衡 |
