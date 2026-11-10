# 杂项、决策记录与坑

`plan.md` 放设计与计划；这里放其余全部：命名取舍、许可证、依赖现状、逐功能 API 映射、各库的坑、测试策略、被否决方案、未决问题。

> 说明：需求里的 "siximage" 按 **SixLabors.ImageSharp** 理解（NuGet 上没有名为 SixImage 的图像库）。若实际指别的库，只需新增一个 `IImageBackend` 实现，核心设计不变。

---

## A. 命名与类型决策

| 决策 | 选择 | 理由 / 放弃的候选 |
|---|---|---|
| 门面类型 | `FacadeImage` | `Image` 会与 `SixLabors.ImageSharp.Image`、`System.Drawing.Image` 冲突（ImgProcSharp 的 `Image` 已 internal）；`Picture`/`Bitmap` 语义不准；`Img` 太短 |
| 入口 | `ImageEngine` | 持有后端、选项、回退实例与缓存；`ImageFactory` 听起来只创建不处理；`ImageContext` 容易被当成绘制上下文 |
| SPI | `IImageBackend` + `IFrame` | `IFrame` 与"动画帧"撞词，但短且准确（后端私有的图像句柄）；候选 `ISurface`（更准，但和"画布"概念重复） |
| 能力约定 | `Try*` 返回 `bool` | 比"能力位 + 抛异常"更诚实，不会出现"声明支持但跑起来崩"；能力位保留只做展示 |
| 颜色 | `Rgba32` | 自描述、与 ImageSharp 的 `Rgba32` 同名便于迁移；`Color` 会与三家的 `Color` 冲突 |
| 几何 | `PointF` / `Size` / `SizeF` / `RectF` | 与 .NET 习惯一致；若调用方同时 `using System.Drawing`，需要 `using PointF = ImageFacade.PointF;`（写进 README） |
| 采样器 | `Sampler` | ImageSharp 叫 `IResampler`/`KnownResamplers`，这里是枚举，叫 `Resampler` 会和"对象"混淆 |
| 描边 | `StrokeStyle` | 与 `System.Drawing.Drawing2D` / WPF 的命名习惯一致；`PenStyle` 容易和颜色混淆 |
| 回退模式 | `FallbackMode` | 取值 `Disabled` / `PerOperation` / `Adaptive` / `SwitchAndStay` 直接描述行为，不用 `Auto` 这种含糊词 |
| 包名 | `ImageFacade.*` | 与命名空间同根；`ImageFacade.Core` 多余（核心就是 `ImageFacade`） |
| 主版本包名 | 无后缀 = 当前主版本；数字后缀 = 指定主版本 | `ImageFacade.SkiaSharp`（4.x）/ `.SkiaSharp3`（3.x）；`.OpenCvSharp`（4.x）/ `.OpenCvSharp5`（5.x）；`.ImageSharp`（3.x）/ `.ImageSharp2`（2.x） |
| 软件后端 | 独立包 `ImageFacade.SdcbSimdCodecs` | 核心保持零编解码依赖；`ImageBackendBase`（只做加载/保存）与 `SoftwareBackend`（完整渲染）都在 Software 包里 |

公开 API 全部在根命名空间 `ImageFacade`；只有 SPI（后端作者才需要）放 `ImageFacade.Backends`。使用方通常只需 `using ImageFacade;` 和 `using ImageFacade.SkiaSharp;` 两条。

---

## B. 许可证矩阵

| 依赖 | 许可 | 处理 |
|---|---|---|
| 本仓库 | Apache-2.0（`LICENSE` 已存在） | 保持 |
| **SkiaSharp** | MIT | NuGet 引用；不打包 |
| Skia（native） | BSD-3-Clause | 由 SkiaSharp 的 native 包带进来 |
| **OpenCvSharp4** | Apache-2.0 | NuGet 引用；不打包 |
| OpenCV（native） | Apache-2.0（4.x） | 由 runtime 包带进来 |
| **SixLabors.ImageSharp / .Drawing / .Fonts** | **Six Labors Split License**（2.0 起；1.x 才是 Apache-2.0） | **只做 PackageReference，不打包、不转发源码**；README 显著提示：开源项目与营收低于阈值的组织免费，其余需商业授权。这是本项目不把 ImageSharp 设为默认后端的直接原因 |
| **ImgProcSharp** | Apache-2.0 | **源码复制**进 `ImageFacade.SdcbSimdCodecs`（锁定 0.1.0 快照），保留文件头注释与 NOTICE；不再以 NuGet 依赖出现 |
| **Sdcb.SimdCodecs** | MIT | `ImageFacade.SdcbSimdCodecs` 直接 NuGet 引用 **2.0.0**（解码 + PNG/JPEG/TIFF/WebP/GIF/BMP/ICO/QOI/PNM 编码） |
| **文泉驿微米黑** | Apache-2.0 / GPLv3 双许可，**含字体嵌入例外** | 随核心包分发，必须带 `licenses/LICENSE-WenQuanYiMicroHei.txt`，文件内容不得修改 |
| 未来的 Magick.NET | Apache-2.0（ImageMagick 许可） | 可作为第五后端，与现有设计无冲突 |

Clean-room / 来源声明（写进 README）：

- 本库的 API 形状参考了 SkiaSharp、ImageSharp.Drawing、OpenCV 的公开文档；`ImgProcSharp` 是同一作者的 Apache-2.0 库，按 §Q/§Y 的清单复制并改造其 Core 源码，保留文件头与 NOTICE。
- 未反编译、未逐行对照任何第三方实现；语义差异通过一致性测试暴露，不靠"看起来一样"。

---

## C. 依赖现状（2026-11 查证）

| 包 | 锁定版本 | 备注 |
|---|---|---|
| `SkiaSharp`（当前） | **4.155.0** | 最新稳定；4.156–4.158 只有 preview/rc。`SKBitmap.Resize` / `SKSamplingOptions` / `SKFont` 的具体签名在 M3 核对 |
| `SkiaSharp`（3.x） | **3.119.1** | 3.x 最后一版稳定；显式要求支持，用 `VersionOverride` 装进 `ImageFacade.SkiaSharp3` |
| `OpenCvSharp4` | **4.13.0.20261107** | 当前；另需按 RID 引 `OpenCvSharp4.runtime.win`（v1 只验 win-x64） |
| `OpenCvSharp5` | **5.0.0.20261109** | 已发正式版（2026-07 起）；配套 `OpenCvSharp5.runtime.win` 同日期版本。OpenCV 5 的枚举/常量有变动，M6 核对 |
| `SixLabors.ImageSharp`（当前） | **3.1.12** | 核心最新是 4.1.3，但 `ImageSharp.Drawing` 最新只到 3.1.3（2026-10-09），两者大概率不配套；**锁 3.1.x** |
| `SixLabors.ImageSharp.Drawing`（当前） | **2.1.3** | 与 ImageSharp 3.1.x 配对的唯一选择（3.1.3 → 依赖 ImageSharp 4.1.3，装不上） |
| `SixLabors.ImageSharp`（2.x） | **2.1.13** | 显式要求支持 2.x；2.x 的核心库仍是 Six Labors Split License |
| `SixLabors.ImageSharp.Drawing`（2.x） | **1.0.0** | 1.x 分支唯一 GA；2.x 的文字是 `ctx.DrawText(TextGraphicsOptions, ...)`，与 3.x 不同 |
| `SixLabors.Fonts` | 由 Drawing 传递 | 2.x/3.x 各自解析，不要手写 |
| `Sdcb.SimdCodecs` | **2.0.0**（只有 `net10.0` 资产） | 只被 `ImageFacade.SdcbSimdCodecs` 引用（因此该工程单目标 net10.0）。**0.2 实测：2.0 拆成 `Sdcb.SimdCodecs.Abstractions` + 按格式的 codec 包**（`PngImageDecoder`/`JpegImageDecoder` 各自有 `Registration`）+ `CodecRegistry.Register(...)` 注册模型——plan 里"`ImageDecoder.Open` + `SimdCodecs.Encode` 一把梭"的说法已过时；软后端编解码按新模型接入（见 §Z） |
| ImgProcSharp 源码 | **0.1.0 快照** | 只复制 Core（渲染），不含 SimdCodecs 门面；见 §Y |

CPM（`Directory.Packages.props`）统一管理"当前版本"；旧/新主版本的适配工程用 `VersionOverride`（或单独关闭 CPM），因为 CPM 不允许同一个包出现两个中央版本。版本升级必须重跑一致性套件——像素结果变了要更新基线，而不是"测试太严"。

---

## D. 像素与 alpha 细则

规范格式（门面唯一对外格式）：**直通（非预乘）RGBA8，顶行优先，`stride >= width * 4`**。

**缺省就是 RGBA**：`FromPixels`/`CopyPixelsTo`/`CopyPixels`/`GetPixel`/`AcquirePixels` 的默认格式全部是 `PixelFormat.Rgba32`；只有明确需要 ffmpeg/Avalonia 的布局时才写 `Bgr24`/`Bgra32`。不要"按来源猜格式"。

| 后端 | 内部存储 | 导出到规范格式 | 从规范格式导入 |
|---|---|---|---|
| Software（独立包） | 复制后的 `Canvas`（**预乘** Rgba32） | `Canvas.CopyToStraight`（不透明时内部走 memcpy） | 自己 premultiply 后写 `Canvas` 缓冲（或 `Canvas.Wrap`） |
| Skia | `SKBitmap`（`Rgba8888` + `Premul`） | `SKPixmap.ReadPixels(dstInfo=Unpremul)` 做预乘→直通 | 逐行 premultiply 写 `bitmap.GetPixels()` |
| ImageSharp | `Image<Rgba32>`（**直通**） | `DangerousTryGetSinglePixelMemory` 或整图复制 | 直接写回像素内存 |
| OpenCV | `Mat` `CV_8UC4`（**BGRA 直通**） | `Cv2.CvtColor(BGRA2RGBA)` 或 `MixChannels` | 反向通道交换 |

要点：

- **只在边界转换**。Skia/软后端内部保持预乘，绘制热路径一次转换都不做。
- 预乘/反预乘在 sRGB 8 位上不是可逆的：`a < 255` 时往返有 ±1 的量化误差。一致性测试的容差按这个来定，不要写成 0。
- OpenCV 的 `Mat` 可能带行填充（`Step() > Width * 4`），永远逐行拷贝，不要假设连续。
- ImageSharp 的 `DangerousTryGetSinglePixelMemory` 在多段缓冲/多帧图上会失败，失败就退化成 `CopyPixelDataTo`。
- `AcquirePixels` 返回的 `PixelLease` 可能指向后端原生内存，也可能是内部租的副本；**必须 `Dispose`**，不要在 lease 存活期间调用同一图像的其它操作。
- 对外的 `PixelFormat`：`Rgba32` / `Bgra32` / `Rgb24` / `Bgr24` / `Gray8` / `Bgra32Premultiplied`。转换集中在核心的 `PixelConverter`（逐行、显式 stride），后端永远只看到自己的内部格式；`Bgra32Premultiplied` 只用于输出（Avalonia `AlphaFormat.Premul`），输入时按直通处理再预乘。
- **BGR24 → RGBA 是视频流水线的主要固定成本**（3 读 4 写 + 通道重排，1080p 约 3–6 ms 标量）。M7 视基准决定是否 SIMD；在那之前不要为了"少一次转换"把 BGR 泄漏进后端接口。

---

## E. 后端能力矩阵（v1 目标）

`✔` = 原生；`✖` = 返回 `false` 走回退；`△` = 有条件（见备注）。

| 能力 | Software | SkiaSharp | ImageSharp | OpenCvSharp |
|---|---|---|---|---|
| 解码格式 | PNG/JPEG/TIFF/WebP/GIF/BMP/ICO/CUR/QOI/PNM/JBIG2/JPEG2000（Sdcb 2.0；HEIF/AVIF 需额外包） | 同左 + WBMP/HEIF(平台相关) | PNG/JPEG/WebP/GIF/BMP/TIFF/TGA/PBM | PNG/JPEG/WebP/BMP/TIFF（取决于 native 构建） |
| 编码格式 | PNG/JPEG/TIFF/WebP/GIF/BMP/ICO/QOI/PNM（Sdcb 2.0 `IsEncodable`） | 多 | 多 | 多 |
| Resize（拉伸） | ✔ | ✔ | ✔ | ✔ |
| 六种模式 | 门面层实现（所有后端一致） | | | |
| 画点 / 线 / 折线 | ✔ | ✔ | ✔ | ✔（整数坐标） |
| 矩形 / 椭圆 / 多边形 | ✔ | ✔ | ✔ | ✔ |
| 虚线 | ✔ | ✔ | ✔ | ✖ |
| 抗锯齿 | ✔ | ✔ | ✔ | ✔ |
| 半透明绘制（source-over） | ✔ | ✔ | ✔ | ✖（**不做 alpha 混合**） |
| ASCII 文字 | ✔ | ✔ | ✔ | ✔（net10.0）；net8.0 回退 |
| CJK 文字 | ✔（内置字体） | ✔（内置/指定字体） | ✔（内置/指定字体） | ✖ |
| 文字测量 | ✔ | ✔ | ✔ | △（仅 ASCII） |
| EXIF 自动转正 | ✔（Sdcb） | ✔（`SKCodec.EncodedOrigin`） | ✔（`AutoOrient()`） | △（自己读方向再转） |
| ICC 透传 | ✔ | ✔ | △（覆盖范围待核） | ✖ |
| 零拷贝像素 | ✔（数组） | ✔ | △（多缓冲时失败） | ✔ |
| BGR24/BGRA 出入 | ✔（核心转换） | ✔ | ✔ | ✔ |
| NativeAOT | ✔ | ✔（带 native assets） | △（可做但要裁剪配置） | ✖ |

备注：

- OpenCV 的 `Cv2.Line` 等函数在 4 通道图上**不会**按 `Scalar.A` 做 alpha 混合，而是直接写像素。所以 `color.A < 255` 必须在绘制前返回 `false`。
- OpenCV 的抗锯齿是通过掩码模糊后与原图混合实现的，3 通道与 4 通道行为是否一致需要在 M6 实测（misc §G）。
- ImageSharp 的文字能力依赖 `SixLabors.Fonts`，容器里没有系统字体时必须用 `FontCollection` 加载字体；门面的内置字体就是为此准备的。
- 这张表对"当前主版本"成立；SkiaSharp 3、OpenCvSharp5、ImageSharp 2 的能力集合相同，差异只在 API 形状（§X）。ImageSharp 2 的文字/像素实现分叉，但对外能力位一致。
- `Software` 行的 `ImgProcSharp.*` 指复制进 `ImageFacade.SdcbSimdCodecs` 后的内部类型（`ImageFacade.SdcbSimdCodecs.Rendering.*`），不再有 ImgProcSharp NuGet 依赖；编解码来自 `Sdcb.SimdCodecs 2.0`。

---

## F. 逐功能 API 映射

### F.1 加载 / 保存

| 门面 | Skia | ImageSharp | OpenCV | Software |
|---|---|---|---|---|
| `Load` | `SKCodec.Create(SKData)` → `GetPixels` | `Image.Load<Rgba32>` | `Cv2.ImDecode` | `ImageDecoder.Open(data)` → `GetFrame(0).CopyPixels(null, PixelFormat.Rgba32, …)` |
| 格式识别 | `SKCodec.EncodedFormat` | `Image.DetectFormat` | `Cv2.ImDecode` 自带 | `ImageDecoder.Format` / `SimdCodecs.DetectFormat` |
| 方向 | `SKCodec.EncodedOrigin` | `Mutate(x => x.AutoOrient())` | `SimdCodecs.TryReadInfo` + `Cv2.Rotate/Flip` | `frame.Info.Orientation`，由 `ImageBackendBase` 统一转 |
| 保存 | `SKImage.Encode(format, quality)` | `Save(stream, encoder)` | `Cv2.ImEncode(ext, mat, out bytes)` | `SimdCodecs.Encode(pixels, w, h, PixelFormat.Rgba32, format, stride, options)` |
| PNG 级别 | `SKPngEncoderOptions` | `PngEncoder.CompressionLevel` | `ImwriteFlags.PngCompression` | `PngWriteOptions` |
| JPEG 质量 | `SKImage.Encode(..., quality)` | `JpegEncoder.Quality` | `ImwriteFlags.JpegQuality` | `JpegEncodeOptions` |

### F.2 Resize

| 门面 `Sampler` | Skia | ImageSharp | OpenCV | Software |
|---|---|---|---|---|
| `Nearest` | `SKFilterMode.Nearest` | `KnownResamplers.NearestNeighbor` | `InterpolationFlags.Nearest` | `NearestNeighbour` |
| `Box` | 最近邻 + mipmap（近似） | `Box` | `Area`（缩小）/`Linear`（放大） | `Box` |
| `Bilinear` | `SKFilterMode.Linear` | `Triangle` | `Linear` | `Bilinear` |
| `Bicubic` | `SKCubicResampler.Mitchell` | `Bicubic` | `Cubic` | `Bicubic` |
| `Lanczos2` | `CatmullRom`（近似） | `Lanczos2` | `Lanczos4`（核半径不同） | `Lanczos2` |
| `Lanczos3` | `CatmullRom` + mipmap（**无真 Lanczos**） | `Lanczos3` | `Lanczos4` | `Lanczos3` |
| `MitchellNetravali` | `SKCubicResampler.Mitchell` | `MitchellNetravali` | `Cubic`（近似） | `MitchellNetravali` |
| `CatmullRom` | `SKCubicResampler.CatmullRom` | `CatmullRom` | `Cubic`（近似） | `CatmullRom` |

`ResizeOptions.PreferExactSampler = true` 时，标"近似"的行改走软后端。Skia 的 `SKSamplingOptions` 具体构造在 4.155 上以实际 API 为准。

### F.3 绘制

| 门面 | Skia | ImageSharp | OpenCV | Software |
|---|---|---|---|---|
| `DrawPoint` | `DrawCircle` + 填充 `SKPaint` | `FillEllipse` | `Cv2.Circle(..., -1)` | `Fill(CirclePolygon)` |
| `DrawLine` | `DrawLine` + `SKPaint` | `canvas.DrawLine(pen, a, b)` | `Cv2.Line` | `Draw(Pen, Path)` |
| `DrawPolyline` | `SKPath` + `DrawPath` | `PathBuilder` + `Draw(pen, path)` | `Cv2.Polylines` | `PathBuilder` + `Draw` |
| `DrawRectangle` | `DrawRect` | `Draw(pen, RectangleF)` 或路径 | `Cv2.Rectangle` | `Draw(Pen, RectangularPolygon)` |
| `FillRectangle` | `DrawRect` + 填充 | `Fill(brush, RectangleF)` | `Cv2.Rectangle(..., -1)` | `Fill(SolidBrush, RectangularPolygon)` |
| `DrawEllipse` | `DrawOval` | `PathBuilder.AddEllipse` + `Draw` | `Cv2.Ellipse` | `Draw(Pen, EllipsePolygon)` |
| `FillEllipse` | `DrawOval` + 填充 | `Fill(brush, ellipse path)` | `Cv2.Ellipse(..., -1)` | `Fill(SolidBrush, EllipsePolygon)` |
| `FillPolygon` | `SKPath` + 填充 | `PathBuilder` + `Fill` | `Cv2.FillPoly` | `Fill(SolidBrush, Polygon)` |
| `DrawText` | `SKCanvas.DrawText(text, x, baseline, SKFont, SKPaint)` | `DrawingCanvas.DrawText(RichTextOptions, ...)` | 复用 `ImageFacade.SdcbSimdCodecs` 的字形光栅化（net10.0），不用 `Cv2.PutText` | `TextDrawing.DrawText` |
| `MeasureText` | `SKFont.MeasureText` | `TextMeasurer`/`TextBlock.Measure` | `Cv2.GetTextSize` | `TextFont.Measure` |

### F.4 像素

| 门面 | Skia | ImageSharp | OpenCV | Software |
|---|---|---|---|---|
| `CopyPixelsTo` | `SKPixmap.ReadPixels` | `DangerousTryGetSinglePixelMemory` | 逐行 `Marshal.Copy` + 通道交换 | `CopyToStraight` |
| `FromPixels` | 预乘循环写入 | 直接写 | 通道交换 + 写入 | 预乘循环 |
| `AcquirePixels` | `GetPixels()` + 预乘转换（可能复制） | 单缓冲时零拷贝 | 复制 + `Dispose` 写回（BGRA↔RGBA 要换通道，见 §G.16） | `Canvas.Buffer` 零拷贝（预乘，需复制转换） |

`AcquirePixels` 的语义是"给我规范格式的可写像素"，因此只要后端内部不是直通 RGBA，就允许内部复制并在 `Dispose` 时写回；"零拷贝"只是优化目标，不是契约。

---

## G. OpenCV 的坑（逐条）

1. **没有 alpha 混合**：`Cv2.Line/Rectangle/Ellipse/FillPoly` 对 4 通道图直接写 `Scalar`，不看 `A`。必须 `color.A == 255` 才返回 `true`。
2. **坐标是整数**：`Point` 只有 `int`。门面 corner 坐标要 `-0.5` 再四舍五入，精度 ±0.5 px。`shift` 参数可以做到 1/16 像素，但会影响所有绘制函数，M6 先验证再决定是否启用。
3. **文字不要用 `Cv2.PutText`**：Hershey 字体只支持 ASCII，字形与常规 TTF 完全不同，中文会画出乱码方块。做法改为"嵌入一个字体、自己解析"（与 ImgProcSharp 同原理）：net10.0 目标直接复用 `ImageFacade.SdcbSimdCodecs` 的 `TextFont.Layout` + 字形 A8 覆盖位图，按覆盖率把 BGRA 像素混一遍；这样中文和半透明文字都能画，也不需要把整帧倒腾到软后端。net8.0 目标没有该程序集（`Sdcb.SimdCodecs` 只有 net10.0 资产），`DrawText` 返回 `false` 交给回退。
4. **EXIF 方向（M6 实现）**：不自己解析 EXIF，改成显式控制解码 flag——`AutoOrient = true` 时用 `ImreadModes.Unchanged`（让 OpenCV 自己转正），`false` 时加 `ImreadModes.IgnoreOrientation`（避免"以为没转其实转了"）。代价：`ImageMetadata.Orientation` 报 0（OpenCV 解完就把方向吃了）。真实带方向的 JPEG 仍需 M8 用固定样本回归一遍。
5. **通道顺序**：`ImDecode(ImreadModes.Color)` 给 BGR 3 通道；带 alpha 的图用 `Unchanged` 可能是 BGRA 4 通道。统一转 `CV_8UC4`。
6. **行填充**：`Mat.Step()` 可能大于 `Width * 4`（尤其 ROI 子矩阵），逐行处理。
7. **编码参数**：`Cv2.ImEncode` 用 `ImageEncodingParam(ImwriteFlags.JpegQuality, q)`；PNG 用 `ImwriteFlags.PngCompression`；未知扩展名会抛异常。
8. **原生依赖（M6 实现）**：每个 RID 单独引 runtime 包（`OpenCvSharp4.runtime.win` / `OpenCvSharp5.runtime.win`），测试工程要显式设 `RuntimeIdentifier=win-x64`，否则原生库不会被拷到输出目录。构造函数里用 `Lazy` 打一次 `Cv2.GetVersionString()` 自检，缺库就抛 `ImageBackendException` 并点名该引哪个 runtime 包。Linux 上缺 `libGL`/`libgomp` 同样会在这一步失败。
9. **AOT**：OpenCvSharp 的 P/Invoke + 动态加载在 NativeAOT 下没有官方支持声明，不要承诺。
10. **线程**：`Mat` 不是线程安全的；不要把同一个 `FacadeImage` 跨线程用。
11. **4.x vs 5.x（M6 实测）**：目标 API 比预想稳定——`ImDecode/ImEncode/Resize/Line/Circle/Rectangle/Ellipse/FillPoly/Polylines/GetTextSize/PutText`、`Mat` 的 `Data/Step/Rows/Cols`、以及 `ImreadModes/InterpolationFlags/ImwriteFlags/LineTypes/ColorConversionCodes` 我们用到的成员**两代完全一致**。5.0 只是给 `CvtColor` 等加了 `AlgorithmHint` 之类的可选参数、`Cv2.Merge` 换成 `ReadOnlySpan<Mat>` 重载（`Mat[]` 隐式转换后照样编译）。所以 `OpenCvBackend` 一份源码两个包各编一遍，不需要 `#if`。两套 runtime 不能同时加载，测试工程必须分开。
12. **OCVS002 分析器**：OpenCvSharp 自带 Roslyn 分析器，把"循环条件里访问 `Mat.Rows`/`Cols`"当成**编译错误**（每次都是 P/Invoke）。循环前先 `int height = mat.Rows;`。
13. **Scalar 顺序**：`Mat` 内部是 BGRA，`Scalar` 按 `(v0,v1,v2,v3)` 写通道 0..3，所以 `ToScalar(rgba)` 要写成 `new Scalar(color.B, color.G, color.R, color.A)`。
14. **椭圆/圆**：`Cv2.Ellipse` 用 `RotatedRect(center, size, 0)`，`CV_8UC4` 下厚度 `-1` 为实心；圆点用 `Cv2.Circle(..., -1)`。坐标仍要 `-0.5` 量化到像素中心。
15. **gray+alpha**：PNG 用 `Unchanged` 解码可能得到 `CV_8UC2`，要 `Cv2.Split` 后 `Merge` 成 `CV_8UC4`（灰度复制到 B/G/R，第二通道当 A），否则会丢 alpha。
16. **`TryWrapPixels` 恒为 false**：门面的规范像素是直通 RGBA，`Mat` 内部是 BGRA，接管外部缓冲就得逐行换通道，不算零拷贝——所以本后端**不声明** `ZeroCopyPixels`，`AcquirePixels` 是"复制 + Dispose 写回"。
17. **贴图不混合**：`TryDrawFrame` 用 ROI `CopyTo`（直接覆盖），因为该后端没有 alpha 混合；只用于 letterbox 这类"源图不透明"的场景。

---

## H. Skia 的坑

1. **默认预乘**：`SKBitmap.Decode` 给的是预乘；要么全程预乘、只在边界转，要么创建 `Unpremul` 位图（绘制可能变慢）。本库选前者。
2. **4.x API 变动**：`SKBitmap.Resize`、`SKSamplingOptions`、`SKCubicResampler`、`SKFont.MeasureText` 的签名在 4.155 上要逐一核对；`SKPaint` 的文字属性在 3.x 已迁到 `SKFont`，4.x 不要再用旧写法。
3. **字体**：`SKTypeface.FromFamilyName` 在 Linux 容器里常常找不到字体；`SKFontManager.Default.MatchCharacter` 可以按字符兜底，但 v1 只做"整段回退到内置字体"。
4. **ascent 是负数**：`SKFontMetrics.Ascent` 为负，`TopLeft → baseline` 时取 `-Ascent`。
5. **JPEG 无 alpha**：`SKImage.Encode(Jpeg)` 会把透明区域画成黑色（或未定义）。门面统一先合成背景色（§3.6）。
6. **Linux native assets**：需要 `SkiaSharp.NativeAssets.Linux`（依赖 fontconfig）或 `.NoDependencies`；后者没有字体管理器，必须用内置字体。
7. **SKCanvas 状态**：每个绘制方法内部 `Save/Restore`，不要依赖调用顺序；`SKPaint` 用完即弃（4.x 里 `SKPaint` 是轻量对象，但别跨线程共享）。
8. **`SKCodec` 缩放解码**：`GetScaledDimensions` 可以省内存，但方向与 ICC 逻辑要一起处理；v1 先全尺寸解码再 Resize。
9. **3.x 与 4.x 的差异**：4.x 移除/改名的旧 API 集中在 `SKFilterQuality`、部分 `SKPaint` 文字属性、`SKBitmap.Resize` 重载；共享源码用 `#if SKIA3` 包住差异，`SKFont`/`SKSamplingOptions`/`SKCodec` 这套新 API 两边都保留。升级 3.119.1 时先按 4.x 写，再倒推 shim。

---

## I. ImageSharp 的坑

1. **许可证**：2.0+ 是 Six Labors Split License，不是 Apache-2.0。不要把它设为默认后端，不要打包它的 dll。
2. **版本配对（M0/M5 实测）**：Drawing 3.x 依赖 ImageSharp 4.x，与 ImageSharp 3.1.x 配对的是 **Drawing 2.1.3**（依赖 `ImageSharp >= 3.1.4` + `Fonts 2.0.3`）；老档是 **ImageSharp 2.1.13 + Drawing 1.0.0**（依赖 `ImageSharp >= 2.1.5` + `Fonts 1.0.0`）。`3.1.12 + 3.1.3` 还原不出来。
3. **Drawing 2.x 的入口（M5 实测）**：没有 `Paint(canvas => ...)`/`DrawingCanvas`（那是 1.0 早期预览），就是 `image.Mutate(ctx => ctx.Fill(...)/Draw(...)/DrawLine(...)/DrawText(...))` 这一套扩展方法，参数顺序是"`DrawingOptions?` 在前、颜色/画笔居中、路径最后"（`Fill(ctx, color, path)`，不是 `Fill(ctx, path, color)`）。
4. **字体**：`SystemFonts.CreateFont` 依赖系统字体；容器里没有字体就抛异常。用 `FontCollection.Add(stream)` 加载内置字体（核心 `BuiltInFont.Data`）。
5. **文字锚点（M5 实测）**：`RichTextOptions.Origin` = 排版框**左上角**（Origin=(10,10) 时墨迹从 y=11 开始）。所以 `TopLeft` 直接传，`Baseline` 用 `FontMetrics.HorizontalMetrics.Ascender × size/UnitsPerEm` 反推。注意 `FontMetrics` 是**设计单位**（UnitsPerEm=2048），必须自己按 `size/UnitsPerEm` 缩放。
6. **虚线**：`SolidPen` 会**忽略** `PenOptions.StrokePattern`，必须用 `PatternPen(PenOptions)`；`StrokePattern` 是只读属性，只能在 `PenOptions(color, width, pattern)` 构造里给。
7. **圆/椭圆构造**：`EllipsePolygon(x, y, radius)`、`(PointF center, float radius)`、`(PointF center, SizeF size)` 的 x/y 都是**圆心**，尺寸是外接框；要"内切于矩形"得传 `center + Rect.Size`，直接传 `(rect.X, rect.Y, rect.W, rect.H)` 会画到左上角去。
8. **DrawText 的 brush/pen 参数**：想带 `DrawingOptions`（控制抗锯齿）就必须走 `DrawText(DrawingOptions, RichTextOptions, text, Brush, Pen)`——这个重载要求同时给 brush 和 pen（pen 可传 null）；只要颜色的话有 `DrawText(RichTextOptions, text, Color)`，但没法关抗锯齿。门面 v1 只填充不描边。
9. **像素访问**：`DangerousTryGetSinglePixelMemory`（2.1.13 与 3.1.12 都有）在多段内存/多帧时返回 false；`CopyPixelDataTo(Span<byte>)` 与 `ProcessPixelRows` 是安全兜底。注意 `ProcessPixelRows` 的回调里不能捕获 `Span`（`ImportPixels` 的 `ReadOnlySpan` 入参要先落到 `byte[]`）。
10. **ICC**：不是所有格式都保留 ICC，`PreserveMetadata` 的实际覆盖范围要按格式测一遍；v1 只承诺"尽力"。
11. **AOT/裁剪**：ImageSharp 有反射式解码器注册，NativeAOT 需要额外配置；后端工程不要开 `IsAotCompatible`。
12. **2.x 与 3.x 的差异（M5 实测）**：只有三处——(a) EXIF 读取是 `ExifProfile.GetValue(ExifTag.Orientation)`（2.x）vs `TryGetValue`（3.x），是唯一需要 `#if IMAGESHARP2` 的地方；(b) Fonts 1.0 vs 2.0；(c) ImageSharp 2.1.13 vs 3.1.12 的其余 API（`Image.Load<TPixel>`、`Mutate/Resize/AutoOrient`、`DangerousTryGetSinglePixelMemory`、编码器）签名一致。Drawing 1.0.0 与 2.1.3 的绘制/文字 API 也一致（文字都是 `RichTextOptions`，`TextGraphicsOptions` 那套已不存在）。所以 `ImageSharpBackend` 一份源码两个包各编一遍即可，无需分叉。

---

## J. 文字与字体

### J.1 内置字体

- 用 **文泉驿微米黑**（和 ImgProcSharp 同一个），TTF 约 4.4 MB，gzip 后约 2 MB，Apache-2.0/GPLv3 + 字体嵌入例外。
- 放在核心包，公开 `BuiltInFont.Data`（懒解压的 `ReadOnlyMemory<byte>`），所有后端共用同一份字节：Skia `SKTypeface.FromData`、ImageSharp `FontCollection.Add(stream)`、软后端 `TextFont.Load(bytes)`。
- 复制 ImgProcSharp 源码时**删掉它自带的字体资源**；字体引擎与字库统一放在核心：`ImageFacade.Font`（TrueType 解析 + 字形光栅化 + 排版/测量）、`ImageFacade.BuiltInFont.Data`（文泉驿微米黑，唯一一份字节）。整个解决方案只有一份字体资源、一份解析实现。
- 许可证文件必须随包：`licenses/LICENSE-WenQuanYiMicroHei.txt`。

### J.2 锚点与度量

| 后端 | 绘制 API 的原点 | 转换 |
|---|---|---|
| Skia | 基线 | `baselineY = topY + (-font.Metrics.Ascent)` |
| ImageSharp | `RichTextOptions.Origin` = 排版框左上角（M5 实测） | `TopLeft` 直接传；`Baseline` 用 `FontMetrics.HorizontalMetrics.Ascender × size/UnitsPerEm` 反推 |
| OpenCV | 与 Software 同一套托管排版（`TextFont.Layout`），原点 = 排版框左上角 | `TopLeft` 直接传；`Baseline` 用 `layout.Ascent` 上移 |
| Software | ImgProcSharp `TextDrawing.DrawText` 的 `location` 是基线 | 同上，`TextFont.GetAscent(size)` |

### J.3 缺字与回退

- 后端先判断"整段文本的每个码点在该字体里都有字形"；有缺字就返回 `false`，由门面用内置字体重画整段。
- v1 不做逐字形 run 拆分（那需要后端返回缺字位置，SPI 会变复杂）。代价：指定字体缺一个生僻字，整段变成文泉驿；可以接受，写进 README。
- 字体缓存：`ConcurrentDictionary<(backend, FontRef), nativeFont>`；`FontRef` 的相等性按 Family/Path/Data 内容哈希/Style 实现。字体文件内容哈希要缓存，别每次算。

### J.4 折行与对齐

- 门面层实现，保证 `MeasureText` 与 `DrawText` 使用同一套行宽数据。
- 断行规则：`\n` 硬断；`MaxWidth > 0` 时，CJK 按字断，拉丁按空格断，超长单词强制按字断。
- v1 不做避头尾（行首标点）、不做 hyphenation、不做 bidi；遇到 RTL 文本交给后端原生能力（Skia/ImageSharp 能处理一部分），软后端会把 RTL 按逻辑顺序画。
- 对齐：`Left`/`Center`/`Right` 以 `MaxWidth` 为参照；`MaxWidth == 0` 时以最宽行为参照。
- 行高：`LineHeight > 0` 用它，否则用字体的 `GetLineHeight(size)`（软后端）或度量推导。

---

## K. 回退与粘滞的取舍

### 为什么回退目标是"另一个后端"而不是"一组软渲染函数"

- 如果软渲染是一组独立函数，它要重新实现 `IFrame`、像素导入导出、字体缓存、Resize；本质上就是把 `IImageBackend` 写第二遍。
- 用同一套 SPI，软后端既能当回退目标，也能当主角（无原生依赖、AOT 场景），还能被一致性测试直接覆盖。
- 迁移就是"`Export` 到规范像素 → 在软帧上继续 → 需要时 `Import` 回去"，引擎代码不到 100 行。

### 为什么默认 PerOperation 而不是 SwitchAndStay

- 大多数调用是"画几笔 + 一段中文标题"，per-op 只付 1–2 次全图拷贝，用户无感。
- 默认粘滞会让"只画了一行中文"的 OpenCV 用户后续 1000 条线全部变慢，而且很难解释。
- 需要批量中文/半透明绘制时，显式 `UseSoftware()` 或 `FallbackMode.SwitchAndStay` 语义清晰。
- 视频流水线例外：一帧里经常有几十上百个带中文标签的检测框，per-op 会做上百次全图往返。`Adaptive` 在累计 3 次回退后整帧切到软后端，帧末 `Dispose` 自动复位，正好匹配"一帧一实例"的生命周期。按累计而不是连续计数，是因为"画框成功、画标签回退"会交替出现。默认值保持 `PerOperation` 是为了让普通用户看到可预测的行为，README 的视频示例显式写 `Fallback = Adaptive`。

### 批量作用域：`Mutate` / `BeginDraw`（M8 追加）

- **动因**：per-op 回退的代价是"导出 + 软绘制 + 导入"两次全图往返（1080p 约 3–12 ms）。一帧画 50 个中文标签就是 50 次往返；`PreferSoftwareRendering = true` 更糟——每个 op 都走回退，等于把整条绘制流水线按 op 拆成 50 次拷贝。
- **ImageSharp 的 `Mutate` 不是直接对照物**：它的价值在于把操作攒进 `IImageProcessingContext`，避免每次 `Clone` 一份像素。我们的 `FacadeImage` 本来就原地改后端帧，没有中间副本问题；缺的是"作用域"——把**回退**合并成一次。
- **实现方式：就地粘滞，不排队**。`PolylineSpec`/`PolygonSpec` 是 `ref struct`（持有 `ReadOnlySpan<PointF>`），收集进队列要么复制点数组、要么改 SPI；而"第一次回退就把整帧导到软帧、后续 op 直接在软帧上执行"既不用排队，也天然保证绘制顺序（切走之后连原生能做的 op 也按原顺序在软帧上跑），实现只在 `FacadeImage` 内部，不动任何后端。
- **计数与事件粒度**：一段软帧算一次回退（`FallbackCount` +1、`OnFallback` 一次，`ElapsedMilliseconds` 是整段耗时）。不是按 op 计数——按 op 计会让 `Adaptive` 阈值语义变形，也与"实际发生了几次全图往返"不符。
- **`flushOnNative`**：默认 `false`（sticky，往返最少）。`true` 时每个 op 开始前先写回，让原生能做的 op 切回原生；适合"少量中文标签 + 大量原生绘制"，纯回退场景反而会退化回 per-op 次数。
- **接管**：`SwitchAndStay` 与达到阈值的 `Adaptive` 在 flush 时直接接管软帧（`_frame` 换过去），省掉最后一次写回；因为回退计数已经 +1，`Adaptive` 的阈值判断与 per-op 路径一致。
- **异常**：`Mutate` 的回调抛异常时丢弃软帧、不写回（半成品不落盘），`FallbackCount` 也不增加；`using` 式 `BeginDraw` 无法区分异常，仍会 flush，这是使用 `using` 的既定语义。
- **会强制 flush 的 op**：`Resize` / `Clone` / `Save` / `Encode` / `GetPixel` / `CopyPixelsTo` / `AcquirePixels` / `UseSoftware` / `UseNative`——它们要么需要完整帧，要么会替换帧，flush 之后作用域继续有效（再次回退会重新建软帧）。

### 被否决的回退方案

| 方案 | 否决理由 |
|---|---|
| 以 Skia 作为唯一回退后端 | 引入原生依赖，Linux/容器/AOT 场景反而更脆；不能作为"无依赖兜底" |
| 把 ImgProcSharp 作为 NuGet 依赖 | 需要一个额外的包、字体资源重复、软后端无法覆盖内部细节；改为源码复制（§Q/§Y），代价是手动同步 |
| 为每个缺失功能写一段门面内联实现 | 等于在核心里重写一个渲染器，且没有测试载体 |
| 后端缺能力时直接抛异常 | 违背"同一套 API 到处能用"的目标；OpenCV 会一半功能不可用 |
| 每个操作都自动迁移（含 Resize/Encode） | 迁移会改变后端，用户无法预期；改为"临时迁移 + 结果导回" |
| 把绘制 op 收集成队列、作用域结束时统一执行 | `PolylineSpec`/`PolygonSpec` 是 `ref struct`（持 span），排队要复制点数组或改 SPI；而"就地粘滞到软帧"顺序天然正确、不改 SPI，收益相同 |
| 照搬 ImageSharp 的 `Mutate(Action<IImageProcessingContext>)` | 它的上下文主要用于缓冲 op 以避免 `Clone` 副本；`FacadeImage` 本就原地修改，复制一套上下文类型只增加 API 面，实际只需一个作用域开关 |

---

## L. 测试策略

### L.1 三层测试

| 层 | 工程 | 内容 |
|---|---|---|
| 单元 | `ImageFacade.Tests` | `Rgba32`、几何、`ResizePlanner`（六模式）、`TextLayouter`（断行/对齐/锚点）、`Nms`（IoU/同类/跨类/顺序无关）、`Letterbox`（缩放/补边/反算）、路由（`FakeBackend`，断言 `Try*` 调用次数、回退次数、`Strict` 抛错） |
| 一致性 | 每版本一个工程（见 L.2） | 同一份测试源码；按能力位 skip；容差断言 |
| 端到端 | `samples` + CI | 当前版本四后端跑同一段代码，出图并拼对比图；肉眼 + 哈希 |

### L.2 后端工厂与 skip（按主版本分工程）

一个测试工程只能绑定一个三方库版本，所以"一套用例 × 多后端"落成"一份测试源码 × 多个测试工程"：

```
tests/Conformance.Shared/ConformanceTests.cs      # 用例源码
tests/ImageFacade.Conformance.Software.Tests/     # ImageFacade.SdcbSimdCodecs
tests/ImageFacade.Conformance.Skia4.Tests/        # SkiaSharp 4
tests/ImageFacade.Conformance.Skia3.Tests/        # SkiaSharp 3
tests/ImageFacade.Conformance.OpenCv4.Tests/      # OpenCvSharp4
tests/ImageFacade.Conformance.OpenCv5.Tests/      # OpenCvSharp5
tests/ImageFacade.Conformance.ImageSharp3.Tests/  # ImageSharp 3
tests/ImageFacade.Conformance.ImageSharp2.Tests/  # ImageSharp 2
```

每个工程只有一个工厂：

```csharp
public static IEnumerable<object[]> Backends => new[]
{
    new object[] { new BackendCase("skia4", () => new SkiaBackend()) },
};

// 原生库缺失时跳过，而不是让整个测试套红
// xUnit v3 用 Assert.Skip；旧版用 SkippableFact 或自定义 SkipException
```

- 金标目录按 `{backend-major}` 分（如 `golden/skia4/...`、`golden/skia3/...`），两个主版本的 AA/采样差异不强行对齐。
- CI 在 **win-x64** 上跑全部七个测试工程；Linux/macOS 只编译核心与两个 ImageSharp 测试工程（不跑），避免 v1 被原生依赖拖住。
- `PublicAPI.Shipped.txt` 每个适配包各一份，内容必须与核心版本一致（只允许 `InternalsVisibleTo` 不同）。

### L.3 容差

| 断言 | 容差 |
|---|---|
| 纯色填充/不透明线内部 | 逐字节（所有后端都应命中同一颜色） |
| 半透明合成 | 每通道 ≤ 2/255（定点/浮点差异） |
| 边缘抗锯齿 | 只断言"有中间值"，不断言具体值 |
| 几何包围盒 | ±1 px（OpenCV 坐标量化） |
| Resize（渐变图，Lanczos3） | MAE ≤ 3/255 |
| Resize（纯色） | 每通道 ≤ 1/255 |
| 文字墨迹包围盒 | ±2 px；软后端内部 ±0 |
| `MeasureText` vs 实际墨迹 | ≤ 2 px |

### L.4 金标图

- 软后端：**逐字节**金标，任何改动都要更新基线并说明原因。
- 原生后端：每后端独立基线，比较用"逐像素差超过阈值的数量占比 < 0.5%"，不做逐字节。
- 金标文件命名 `tests/golden/{backend}/{case}.png`，随仓库提交；CI 差异产物上传为 artifact。

### L.5 回退专项

- `FakeBackend` 声明支持 `DrawLine`、不支持 `DrawText`：断言文字走软后端、线走原生，`OnFallback` 只触发一次。
- `FallbackMode.SwitchAndStay`：断言迁移只发生一次，后续操作 `BackendName == "software"`。
- `FallbackMode.Adaptive`：`FakeBackend` 的 `DrawText` 返回 `false`、`DrawLine` 返回 `true`，交替调用；断言累计第 3 次回退后 `BackendName == "software"`，`OnFallback` 共 3 次；换一个新的 `FacadeImage` 后计数复位。
- `Strict = true`：断言抛 `FeatureNotSupportedException`。
- `PreferSoftwareRendering = true`：断言每个适配后端输出与 `ImageFacade.SdcbSimdCodecs` 逐字节一致（编码除外）。
- `Fallback = Disabled`：断言抛 `FeatureNotSupportedException`，消息含后端名与能力名。
- 流水线：`FromPixels(Bgr24, stride 带填充)` → `ToTensor` → `DrawDetections` → `CopyPixelsTo(Bgra32)`，断言 letterbox 反算误差、张量首尾值、填充字节未被改写。

---

## M. AOT / 裁剪 / 部署

| 后端 | NativeAOT | 裁剪 | 说明 |
|---|---|---|---|
| `ImageFacade` 核心 | ✔ | ✔ | 纯托管、无编解码依赖；字体是嵌入资源 |
| `ImageFacade.SdcbSimdCodecs` | ✔ | ✔ | 复制源码 + Sdcb 2.0 都是纯托管；AOT 冒烟主要压这个包 |
| Skia 3/4 | ✔ | ✔ | 需要 native assets；Linux 上注意 fontconfig 依赖 |
| ImageSharp | △ | △ | 反射式解码器注册与字体集合需要配置；后端工程不要开 `IsAotCompatible` |
| OpenCV | ✖ | ✖ | P/Invoke + 动态加载 + 大 native 库；不承诺 AOT |

部署：

- Skia：`SkiaSharp`（+ 平台 native 包）；Linux 容器里没有 fontconfig 时用 `NativeAssets.Linux.NoDependencies` 并只使用内置字体。
- OpenCV：按 RID 与主版本引 `OpenCvSharp4.runtime.*` / `OpenCvSharp5.runtime.*`；Windows 需要 VC++ 运行时（由 runtime 包带）；两套 native 不能同时加载，测试/示例分开工程。
- ImageSharp：纯托管，无额外部署。
- Software：纯托管，`PublishAot` 可直接发布。

Blazor WebAssembly：Skia（WASM native）/ ImageSharp / Software 可用；OpenCV 不可用。

---

## N. 性能笔记

- **不要用单次 `Stopwatch` 下结论**：同一段代码在不同进程里能差 8 倍（ImgProcSharp 的实测记录）。正式数据用 BenchmarkDotNet，`--job default`。
- 边界转换的近似成本（1080p，200 万像素，参考 ImgProcSharp 的实测量级）：
  - 预乘/反预乘：标量 4–40 ms，SIMD 1–5 ms（取决于实现）。
  - 纯 memcpy：0.3–0.6 ms。
  - BGRA↔RGBA 通道交换：1–2 ms。
  - 因此一次回退（导出 + 导入）在 3–12 ms 量级；`OnFallback` 的耗时字段就是给这个用的。
- 原生路径的开销目标是"测量不出来"：不做额外分配、不做全图扫描；`Try*` 之前的校验只碰元数据（字体解析结果、颜色 alpha、文本是否 ASCII）。
- 流水线的固定成本（1080p，标量实现，量级估计，M7 用基准校正）：
  - BGR24 → 内部 RGBA：3–6 ms（3 读 4 写 + 通道重排）。
  - RGBA → BGRA（Avalonia）：1–2 ms（仅通道交换）。
  - letterbox + 张量化 640×640：3–6 ms（含 4.9 MB 浮点写）。
  - 100 个框 + 标签（Skia）：2–5 ms；软后端：8–15 ms。
  - 结论：30 fps（33 ms）有富余；60 fps（16.7 ms）需要把 BGR 导入与张量化 SIMD 化，或把采集侧直接切成可被后端接受的格式。
- 性能优化放到 M9 之后，且必须先有基准；不要在 v1 引入第二条像素路径（例如"某些情况直接操作规范像素"）——那会破坏"后端内部格式自洽"的简单模型。

---

## O. 未决问题

1. ~~**ImageSharp 3.x 配对**~~（M0 已定论）：Drawing 3.1.3 依赖 `ImageSharp >= 4.1.3`，所以 3.x 档必须锁 **3.1.12 + Drawing 2.1.3**；2.x 用 `2.1.13 + Drawing 1.0.0`。若要覆盖 ImageSharp 4，另加 `ImageSharp4` 适配包（Drawing 3.x）。
2. **SkiaSharp 4.155 的 API**：`SKBitmap.Resize` 是否还在？`SKSamplingOptions` 的构造与 `SKCubicResampler` 是否改名？M3 同时用 3.119.1 编译一遍，把差异收敛进 `ApiCompat.Skia3.cs`。
3. **OpenCvSharp5 的 API 差异**：`ImreadModes`/`InterpolationFlags`/`ImwriteFlags` 常量是否改名、`ImEncode` 支持格式、`Mat` 构造/`Step` 是否一致；M6 用 5.0.0.20261109 实测。
4. **OpenCV 的 EXIF 行为**：`imdecode` 是否默认转正；用带 Orientation=6 的 JPEG 实测，结论写进 §G。
5. **OpenCV 4 通道 AA**：`LineTypes.AntiAlias` 在 `CV_8UC4` 上是否与 3 通道一致（是否会把 alpha 也模糊）。
6. **ImageSharp ICC 覆盖范围**：哪些格式读写时保留 ICC；`PreserveMetadata` 的实际语义。
7. **ImageSharp 文字原点**：`RichTextOptions.Origin` 是布局框左上角还是基线，需要实测校准（影响 `TopLeft` 锚点）；2.x 的 `TextGraphicsOptions` 锚点同样要校准。
8. **`AcquirePixels` 是否进 v1**：`ref struct` 在公共 API 里的兼容性成本（不能用于 `async`、不能存字段）；如果反对声大，降级为 `CopyPixelsTo` + `WithPixels(Action<Span<byte>, int>)`。
9. **内置字体是否拆包**：核心包 +2 MB；若多数用户自带字体，可拆成 `ImageFacade.Fonts`。
10. **软后端编码格式**：2.0 已内置 PNG/JPEG/TIFF/WebP/GIF/BMP/ICO/QOI/PNM，v1 全部暴露；HEIF/AVIF 需要额外 codec 包，先不做。
11. **OpenCV 的 `shift` 亚像素**：是否在所有绘制函数上可用、是否影响性能；M6 实测后再决定。
12. **逐字形字体回退**：SPI 需要新增"缺字查询"或"分段绘制"；v2 再议。
13. **`ImageEngine.Default` 是否保留**：全局可变状态有风险；若示例不需要，v1 可以删掉。
14. **YUV 是否进 v1**：当前要求调用方先 `sws_scale`/`CvtColor`。如果相机场景确实无法避免 NV12，再评估 `PixelFormat.Nv12/I420` + BT.601/709 矩阵（会引入色彩范围语义）。
15. **帧池是否 v1**：目前只做 `TensorBuffer`；`SKBitmap`/`Mat` 每帧分配是否成为瓶颈，M9 基准后决定。SPI 预留 `TryRentFrame` 钩子但 v1 不实现。
16. **`Adaptive` 的阈值**：默认 3 是拍脑袋；用"迁移成本 / 单次回退成本"的比值算更合理，M7 基准后定。
17. **`Bgra32Premultiplied` 的舍入**：预乘是 `round(c*a/255)` 还是 `(c*a+127)/255`；与 Avalonia 的预期要对拍，避免 1 LSB 抖动。
18. **标签文本的零分配格式化**：`label + score` 每帧每框一个字符串；如果 GC 压力明显，给 `DetectionStyle` 加 `TryFormatLabel(Span<char>)`。
19. **检测框绘制的裁剪**：默认实现跳过完全在画布外的框；部分在外的框目前依赖后端裁剪，OpenCV 可能画到边界外被 `Mat` 静默裁掉（需实测）。
20. **复制源码的上游同步频率**：ImgProcSharp 若继续演进，是逐次合并还是等大版本整体替换；先按 `Y.4` 手工合并。

---

## P. 被否决的方案

| 方案 | 否决理由 |
|---|---|
| 一个包吞掉所有后端 | 依赖爆炸（Skia + OpenCV 上百 MB），许可证混在一起，用户无法只选一个 |
| 暴露原生句柄（`AsSKBitmap()` / `AsMat()`） | 锁定实现细节，回退/迁移后句柄失效，用户会写出只在某个后端成立的代码；v2 可作为 `Advanced` 扩展再议 |
| 常驻一份规范像素缓冲 | 双份内存（原生 + 规范），且两份数据同步是 bug 温床；只在边界转换更简单 |
| 用 `System.Drawing.Common` 做第五后端 | .NET 6+ 非 Windows 直接抛 `PlatformNotSupportedException`，与"跨平台门面"冲突 |
| 把六种 Resize 模式交给各后端 | 裁剪/补边的取整规则不同，输出尺寸会漂移；门面层统一规划更可控 |
| 所有操作返回新图（函数式） | 每画一条线复制整图，内存和 GC 都受不了；原地修改 + 返回 `this` 更符合图像库习惯 |
| ~~异步 API~~（已改：见 §Z） | 原判断"全同步"已被推翻：加载/保存是 I/O + 编解码，值得异步；绘制/Resize/测量仍是同步（微秒级异步只会更慢） |
| 用 `object` + 模式匹配传帧 | 丢掉类型安全，后端作者容易写错；`IFrame` 泛型化又会让 SPI 复杂化，当前方案够用 |
| 用 Skia 的 `SKSurface` 统一所有后端 | 等于让 Skia 成为唯一实现，其他后端退化成编解码器，失去意义 |
| 一个适配包同时支持两个主版本（binding redirect / 反射） | 一个程序集只能绑定一个版本；反射调用性能差、AOT 不友好，且解决不了 API 形状差异；改为每主版本一个薄适配包 |
| 复制 ImgProcSharp 时保留它的 public 类型 | 公开 API 会泄漏 `ImageFacade.SdcbSimdCodecs.Rendering.Canvas` 等实现细节，且与根 `ImageFacade` 类型混淆；全部 internal |

---

## Q. 与 ImgProcSharp 的关系

- **源码复制，不再 NuGet 引用**：把 ImgProcSharp **Core 工程**整体复制进 `src/ImageFacade.SdcbSimdCodecs/Rendering/`（锁定 0.2 快照，记录上游 tag/commit）；其中"纯字体"部分后来上移到核心 `src/ImageFacade/Text/Engine/` + `ImageFacade/Font.cs`（见 §Y.1 的后续调整）。许可 Apache-2.0，与本仓库兼容；保留每个文件头注释，`THIRD-PARTY-NOTICES.txt` 里注明来源。
- **不复制** ImgProcSharp 的 `ImgProcSharp.SimdCodecs` 工程（`Image` 门面、`ImageDecoderBridge`、`IImageProcessingContext`）：解码/编码由我们的 `ImageBackendBase` 统一走 Sdcb，避免两套门面。
- 复制后的改造：
  - `namespace ImgProcSharp*` → `ImageFacade.SdcbSimdCodecs.Rendering`；
  - 所有类型加 `internal`（`Canvas`、`DrawingContext`、`TextFont`、`Pen`、`SolidBrush`、`PathBuilder`、图元、光栅化器……），公开 API 不泄漏；
  - 删除 csproj 打包属性、`InternalsVisibleTo`、自带的字体资源；
  - `EmbeddedFonts` 改为读取核心公开的 `BuiltInFont.Data`，`TextFont.BuiltIn` 不再使用；
  - `TextPainter.cs`（自带 RGB24/BGRA 混合）不复制，文字混合统一走 `DrawingContext.BlendCoverage`。
- 依赖与版本：复制进来的 ImgProcSharp Core 本身**不接触 Sdcb**；`ImageBackendBase` 按 `Sdcb.SimdCodecs 2.0` 编写（`ImageDecoder` / `SimdCodecs.Encode` / `PixelFormat` 结构体），Sdcb 再升级只改这一个文件。
- 能力对应：
  - ImgProcSharp 没有 Save → 编码由 `ImageBackendBase` 用 `SimdCodecs.Encode`（2.0，多格式）。
  - `Canvas` 是**预乘** → 边界转换由 `SoftwareBackend` 负责。
  - 文字是**基线**锚点 → 门面负责 `TopLeft → baseline` 换算。
  - 软后端覆盖 v1 全部绘制能力（含虚线、miter/bevel/round、裁剪变换的底层实现）。
- 维护：上游修 bug 用 `git diff` 对照合并；改造点集中在命名空间/可见性/字体三处，避免在渲染代码里写"我们自己的逻辑"，否则下次同步会痛。

---

## R. 片段

### R.1 后端骨架

```csharp
public sealed class MyFrame : IFrame
{
    public int Width { get; }
    public int Height { get; }
    // 自己的原生句柄/缓冲
    public void Dispose() { /* … */ }
}

/// <summary>只写帧相关 4 个成员，加载/保存由基类提供。</summary>
public sealed class MyBackend : ImageBackendBase
{
    public override string Name => "my";
    public override BackendCapabilities Capabilities => BackendCapabilities.Resize | BackendCapabilities.DrawShapes;

    public override IFrame Create(int width, int height, Rgba32? background) { /* … */ }
    public override bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride) { /* … */ }
    public override void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride) { /* … */ }
    public override bool TryWrapPixels(IntPtr pixels, int width, int height, int stride,
                                       bool ownsMemory, out IFrame frame)
    { frame = null!; return false; }

    // Decode / Encode / Clone / Clear / TryGetPixel 已由基类实现

    public override bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result) { /* … */ }
    public override bool TryDrawLine(IFrame frame, in LineSpec spec) { /* … */ }
    // 其余绘制默认 false，按需覆盖
}
```

先把"能创建、能加载、能保存、能拷像素"跑通，其余方法全 `false`，一致性测试会明确告诉你还缺什么。要原生 codec 的适配包（Skia/OpenCV/ImageSharp）直接实现 `IImageBackend` 即可。

### R.2 临时回退（引擎内部）

```csharp
internal FacadeImage RunTemporary(IImageBackend fallback, string feature)
{
    IFrame temp = fallback.Create(Width, Height, background: null);
    byte[] scratch = ArrayPool<byte>.Shared.Rent(Width * Height * 4);
    try
    {
        int stride = Width * 4;
        CopyPixelsTo(scratch, stride);              // 规范像素
        fallback.ImportPixels(temp, scratch, stride);

        // 在 temp 上执行本次操作（由调用方传入的委托完成）
        ExecuteOn(fallback, temp, feature);

        fallback.TryCopyPixels(temp, scratch, stride);   // 导出
        ImportPixels(scratch, stride);                   // 写回原后端
        return this;
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(scratch);
        temp.Dispose();
    }
}
```

### R.3 一致性用例的写法

```csharp
[Theory]
[MemberData(nameof(Backends))]
public void DrawLine_Horizontal_IsSolidInInterior(BackendCase c)
{
    if (!c.Supports(BackendCapabilities.DrawShapes)) return;   // 或 Assert.Skip
    using var engine = new ImageEngine(c.CreateBackend());
    using var img = engine.Create(100, 50, Rgba32.White);

    img.DrawLine(new PointF(10, 20.5f), new PointF(90, 20.5f), Rgba32.Red, width: 4);

    Assert.Equal(Rgba32.Red, img.GetPixel(50, 19));   // 内部行
    Assert.Equal(Rgba32.White, img.GetPixel(50, 10)); // 远处
    Assert.True(img.GetPixel(50, 23).A == 255);        // 边缘外仍不透明（白底）
}
```

---

## S. 视频帧流水线：格式、步长与生命周期

### S.1 入口格式怎么选

| 来源 | 典型格式 | 进门面的方式 |
|---|---|---|
| ffmpeg 解码 + `sws_scale` | BGR24（packed） | `FromPixels(ptr, w, h, linesize, PixelFormat.Bgr24)` |
| ffmpeg 解码 + `sws_scale`（要 alpha） | BGRA | `PixelFormat.Bgra32` |
| OpenCV `VideoCapture` | `Mat` BGR 3 通道 | `FromPixels(mat.Data, cols, rows, (int)mat.Step(), PixelFormat.Bgr24)` |
| 相机 MJPG | JPEG 字节流 | `engine.Load(bytes)`（后端解码） |
| 相机 NV12/I420 | YUV 平面 | **不入库**：先 `sws_scale`/`Cv2.CvtColor` 转 BGR24/BGRA |
| 红外/灰度相机 | Gray8 | `PixelFormat.Gray8`（输出时 alpha 补 255） |

- 入口刻意不做 YUV：色彩矩阵、limited/full range、chroma 位置三件事都要按来源约定，放进门面只会制造"看起来对其实偏色"的 bug。YUV 转换交给 ffmpeg 或 OpenCV。
- `Gray8` 转 RGBA 时 `R=G=B=gray, A=255`，没有隐式的色彩空间假设。
- BGR24 → 内部 RGBA 是逐像素 3 读 4 写；1080p 约 3–6 ms 标量实现，M7 视基准决定是否 SIMD 化。

### S.2 stride（步长）

- **永远不要假设 `stride == width * bytesPerPixel`**。ffmpeg 的 `linesize`、OpenCV 的 `Step()`、Avalonia 的 `RowBytes` 都可能带对齐填充。
- 门面所有 `FromPixels` / `CopyPixelsTo` 都显式收 `stride`，只写有效像素，**不碰填充字节**（有测试守住）。
- `stride < width * bpp` 抛 `ArgumentException`，不做"猜测"。
- 负 `linesize`（自底向上）不在门面支持：适配层把指针移到末行并翻转，或先 `sws_scale` 成正向。

### S.3 生命周期与线程

- `ImageEngine` 可跨线程共享（后端无状态，字体缓存用 `ConcurrentDictionary`），构造一次、进程级复用。
- `FacadeImage` 与 `IFrame` **一帧一实例、不跨线程**；解码/绘制在工作线程，`CopyPixelsTo` 到 Avalonia 时再回 UI 线程。
- `PixelLease` 是 `ref struct`，必须在同一方法内 `Dispose`；lease 存活期间不要对同一图像做其它操作。
- `FromPixels(wrap: true)` 时，外部缓冲的生命周期必须覆盖 `FacadeImage` 的生命周期；`Dispose` 不释放外部内存（`ownsMemory` 只在后端支持接管时用）。
- 一帧处理完立即 `Dispose`，不要缓存 `FacadeImage` 做"上一帧"——需要历史帧就自己保留像素副本。

### S.4 每帧分配从哪来

| 分配 | 大小（1080p） | 现状 | 计划 |
|---|---|---|---|
| 后端帧（`SKBitmap`/`Mat`） | 8.3 MB native | 每帧一次 | v1.1 帧池（S.6） |
| 软后端 `Canvas` | 8.3 MB 托管（LOH） | 每帧一次 | v1.1 帧池优先在这里落地 |
| `FacadeImage` 对象 | 小 | 每帧一次（Gen0） | 可忽略 |
| 张量 `float[3×640×640]` | 4.9 MB（LOH） | 每帧一次 | **v1 就上 `TensorBuffer`（ArrayPool）** |
| 检测数组 | 小 | 每帧一次 | 调用方用 `ArrayPool<Detection>` 复用 |
| 标签字符串 | 小 | 每帧 N 个（Gen0） | 可接受；需要极致时用 `DetectionStyle.FormatLabel` 的 span 重载（v1.1） |
| 回退临时缓冲 | 8.3 MB | 每次回退一次 | 用 `ArrayPool<byte>`（R.2 已如此） |

判断标准：**LOH（≥ 85 KB）分配必须为零**，Gen0 小对象不强制。`GC.GetAllocatedBytesForCurrentThread` 做稳定态断言。

### S.5 回退策略在流水线里的选择

- OpenCV 后端 + 中文标签：`FallbackMode.Adaptive`，阈值 3。累计第 3 次回退时整帧切到软后端并留在那里；一帧只付一次迁移。
- 整帧确定要用软件渲染（例如要逐像素一致的输出）：直接 `engine.Options.PreferSoftwareRendering = true`，连原生帧都不建。
- 逐帧创建 `FacadeImage` 意味着"粘滞"只影响当前帧，下一帧重新从原生开始——这正是想要的。
- `OnFallback` 在生产环境接到计数器/日志；单帧回退次数异常升高说明后端选错了。
- `Strict = true` 放 CI：任何回退都失败，迫使你在测试里显式 `UseSoftware`。

### S.6 帧池（v1.1，先留钩子）

- 可选 SPI：`bool TryRentFrame(int width, int height, out IFrame frame)`；`FacadeImage.Dispose` 时把帧还给池而不是释放。
- 池按 `(backend, width, height, pixelFormat)` 分桶，单桶上限 2–4 个（流水线深度 + 1）。
- 不放进 v1：它会引入"帧被复用时旧内容未清"的一整类 bug，先把 API 稳定下来；基准确认分配是瓶颈后再做。

---

## T. YOLO 预处理与坐标映射

### T.1 letterbox 数学

```
scale = min(modelW / srcW, modelH / srcH)
newW  = round(srcW * scale)
newH  = round(srcH * scale)
padX  = round((modelW - newW) / 2)     // CenterPad = false 时为 0
padY  = round((modelH - newH) / 2)
```

- 反算：`srcX = (modelX - padX) / scale`，`srcY = (modelY - padY) / scale`；矩形按左上/右下分别反算，再与源图求交。
- YOLO 训练时如果用 top-left 对齐（pad 全在右下），把 `CenterPad = false`；两种都支持，不要靠猜。
- 填充色默认 `(114,114,114)`（YOLO 系列常用）；填充色会参与张量化，所以必须和推理端一致。
- `round` 而不是 `floor`：与 OpenCV/ffmpeg 的 letterbox 实现更接近；误差 ≤ 1 px，金标用例覆盖。
- `Letterbox.Apply`（返回新图，由调用方 `Dispose`）与 `Tensorizer.ToTensor`（不产出中间图）共用同一份几何计算；不要在两处各写一套 `scale/pad` 公式。

### T.2 张量

| 选项 | 说明 |
|---|---|
| `Layout` | `Nchw`（ONNX/OpenCV DNN 常见）或 `Nhwc`（TFLite/部分移动端） |
| `Channels` | `Rgb` 或 `Bgr`；YOLO 的 ONNX 一般要 RGB，OpenCV 读进来是 BGR，别忘转 |
| `Normalize` | `/255`；不做 mean/std 时是 `[0,1]` |
| `Mean`/`Std` | 归一化后按通道 `(v - mean) / std`；长度必须 3 |
| 类型 | 只输出 `float32`；FP16 由推理端自己转 |
| 通道数 | v1 只支持 3 通道（RGB/BGR）；单通道/四通道模型自己拼 |

- 实现顺序：先按 `Layout` 决定目标下标，再按 `Channels` 取源通道；**不要先转 `byte[]` 再循环**，直接在缩放后的像素行上取值。
- 性能：640×640×3 = 122 万次乘加，标量约 2–4 ms；`TensorBuffer` 复用后没有 LOH 分配。
- `ArrayPool<float>.Shared` 的最大桶只有 1,048,576 个元素，装不下 1,228,800，`TensorBuffer` 必须自建池（`ArrayPool<float>.Create(2_000_000, 4)`），否则"池化"是假的。
- 张量化前必须先 letterbox（等比 + 补边），不能直接 `Stretch`；否则检测框位置全错。
- 反算的是**检测框**，不是把整张图缩回去；框画在源图上，图的尺寸不变。

### T.3 明确不做

- YOLO 输出解码（`cxcywh`/`xyxy`、objectness、多尺度、anchor）、跟踪、类别名映射：这些跟具体模型强绑定，放用户侧或未来的 `ImageFacade.Yolo` 适配包。
- 本库只提供 `Letterbox`/`LetterboxInfo`、`Nms` 和 `RectF` 级别的 `ToSource`，够画框和裁 ROI。

### T.4 NMS 帮助函数

```csharp
public static class Nms
{
    public static int Suppress(Span<Detection> detections, float iouThreshold = 0.45f, bool classAware = true);
    public static int[] Select(ReadOnlySpan<Detection> detections, float iouThreshold = 0.45f, bool classAware = true);
    public static float IoU(RectF a, RectF b);
}
```

- **语义**：按 `Score` 降序贪心；与已保留框的 IoU **大于** 阈值就抑制（等于阈值保留）；`classAware` 用 `ClassId` 分组，不看 `Label` 字符串（避免每帧字符串比较）。
- `Suppress` 原地整理：保留项移到 `[0, count)`，返回 `count`；调用方 `span[..count]`。不分配、不 LINQ。
- `Select` 返回原数组下标（按分数降序），适合"检测结果还要按原始顺序对应别的数组"。
- 复杂度 O(n²)，n ≤ 300 时 < 0.1 ms；n > 1024 时内部用 `ArrayPool<int>`，不做 KD-tree 之类的过度设计。
- 退化框（宽或高 ≤ 0）IoU 视为 0；坐标先不做 clip（负坐标框按数学矩形算 IoU）。
- 不做分数阈值过滤、不做 soft-NMS、不做按面积加权——这些在模型后处理阶段做更合适。

---

## U. 检测绘制默认实现

`DrawDetections` 是"缺省函数"的样板：它只组合 `FillRectangle` + `DrawRectangle` + `DrawText` + `MeasureText`，不碰后端内部。

- **框**：`RectF` 四边描边，线宽默认 2；坐标先与图像边界求交，完全在外的跳过（避免后端做无效裁剪）。
- **标签胶囊**：优先放在框上方；上方放不下（`box.Top - labelHeight < 0`）就放进框内左上角。
- **颜色**：`Detection.Color` 为默认值（全 0）时按 `DetectionStyle.Palette` 轮转；显式给色则用显式值。
- **测量缓存**：`(label + score 文本, fontSize, font)` → `SizeF`，用 `ConcurrentDictionary` 或帧内 `Dictionary`。同一个 label 在 100 个框里只测一次。
- **分数文本**：`"person 0.92"` 每帧每个框一个字符串（Gen0），可接受；要省这点分配就自己拼 `Detection` 并只用 `Label`。
- **半透明标签底**：`LabelBackground.A < 255` 在 OpenCV 上会触发回退；视频场景建议把 `Adaptive` 打开，或用不透明底色。
- **不要在循环里 `Clone()` 图像**；所有绘制直接落在当前帧上。

---

## V. Avalonia / ffmpeg 适配细节

### V.1 Avalonia `WriteableBitmap`

```csharp
// 复用同一个 bitmap，尺寸不变就不要重建
using (var fb = writeableBitmap.Lock())
{
    image.CopyPixelsTo(fb.Address, fb.RowBytes, PixelFormat.Bgra32);
}
```

- `WriteableBitmap` 的默认 `PixelFormat` 是 `Bgra8888`；`fb.Format` 不匹配时先判断，别硬拷贝。
- 视频帧不透明时创建 `AlphaFormat.Opaque`（或 `Premul`，不透明像素两者等价）；确实是 `Premul` 且含半透明时用 `PixelFormat.Bgra32Premultiplied` 一次完成（不要先直通再逐像素乘）。
- 创建/`Lock()` 遵循 Avalonia 的线程模型，通常在 UI 线程；绘制/编码在工作线程，跨线程投递用 `Dispatcher.UIThread.Post`，并实现丢帧策略（队列满丢旧帧）。
- 尺寸不变时复用 bitmap；尺寸变化才重建，并同步 `RenderScaling`/DPI（门面不做 DPI 缩放）。
- 不要每帧 `new Bitmap`/`RenderTargetBitmap`：那会走 GPU 上传与 GC，30 fps 下很快就看出来。

### V.2 ffmpeg

- **输入**：`AVFrame` 的 `data[0]` + `linesize[0]`；`sws_scale` 到 `AV_PIX_FMT_BGR24` 或 `AV_PIX_FMT_BGRA` 后进门面。`linesize` 对齐（常见 32 字节）由 stride 参数吸收。
- **输出**：把 `BGR24`/`BGRA` 写进目标 `AVFrame` 的 `data[0]`，再 `sws_scale` 转 `YUV420P` 给编码器。不要直接写 YUV 平面。
- `linesize < 0`：自底向上，门面不支持；适配层翻转或先转正向。
- 编码器异步消费 `AVFrame` 时注意引用计数：门面写入的是你自己的缓冲，转成 `AVFrame` 后再 `av_frame_ref`，不要在编码未完成时复用同一块内存。
- 编码线程与绘制线程分离时，一帧一个 `FacadeImage`，写完就 `Dispose`；不要跨线程共享 `SKBitmap`/`Mat`。
- 色彩范围：`sws_scale` 的 `srcRange`/`dstRange` 与摄像头/编码器约定一致；门面按 sRGB 处理，不做 limited/full range 转换。

### V.3 适配层放哪

- 核心不引用 Avalonia、不引用任何 ffmpeg 绑定；`CopyPixelsTo(IntPtr, stride, PixelFormat)` 就是全部契约。
- Avalonia 的扩展方法 10 行，放应用工程或 `ImageFacade.Avalonia`（可选包，取决于是否需要跨项目复用）。
- ffmpeg 绑定（FFmpeg.AutoGen / Sdcb.FFmpeg / Xabe）各有各的 `AVFrame` 类型，核心不绑定任何一个；示例里只写"指针 + stride"。

---

## W. 与"换库"目标的关系

- 上层代码只出现：`ImageEngine`、`FacadeImage`、`Rgba32`、`RectF`、`Detection`、`PixelFormat`。
- 换库 = 换 `new XxxBackend()`（以及对应 NuGet 包/原生依赖），其余不动。
- 采集、推理、UI 三块的胶水各自独立：采集只依赖 `FromPixels`，推理只依赖 `ToTensor`/`LetterboxInfo`，UI 只依赖 `CopyPixelsTo`。
- 真的需要某个库的特有能力（例如 OpenCV 的 `dnn`、Skia 的 `SKCanvas` 特效），走 `IImageBackend` 的自定义扩展或 `Advanced` 逃生口，不要污染核心 API（v1 不提供 `AsSKBitmap()`，见 §P）。

---

## X. 多主版本适配（3/4、4/5、2/3）

### X.1 为什么不能一个包通吃

- CLR 的程序集引用是**按版本绑定**的：一个 `ImageFacade.SkiaSharp.dll` 只能引用一个 `SkiaSharp` 主版本。绑定重定向在 .NET Core 默认不启用、跨平台也不可靠，而且解决不了 `SKBitmap.Resize` 这类 API 变更。
- `extern alias` 只能在一个工程里同时引用两个版本（且要求强名称/不同别名），发布出去的包无法替消费者决定用哪个。
- 结论：**主版本 = 包边界**。共享源码把重复降到只差 shim。

### X.2 共享源码的编译方式

```xml
<!-- src/ImageFacade.SkiaSharp/ImageFacade.SkiaSharp.csproj（当前版：源码就在这里，默认 glob 收进来） -->
<PropertyGroup>
  <DefineConstants>$(DefineConstants);SKIA4</DefineConstants>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="SkiaSharp" />                     <!-- 版本在 Directory.Packages.props -->
</ItemGroup>
```

```xml
<!-- src/ImageFacade.SkiaSharp3/ImageFacade.SkiaSharp3.csproj（3.x：链接上面那份源码） -->
<PropertyGroup>
  <DefineConstants>$(DefineConstants);SKIA3</DefineConstants>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="SkiaSharp" VersionOverride="3.119.1" />
</ItemGroup>
<ItemGroup>
  <Compile Include="..\ImageFacade.SkiaSharp\**\*.cs"
           Exclude="..\ImageFacade.SkiaSharp\bin\**;..\ImageFacade.SkiaSharp\obj\**"
           Link="Shared\%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

- 源码只有一份，放在**当前主版本**的适配工程目录里（不另设 `src/adapters/`）；老/新主版本链接编译。唯一单独放的是 `ImageFacade.SdcbSimdCodecs/Rendering/`（复制改造的 ImgProcSharp Core，它不是适配层）。
- 链接时必须 `Exclude` 对方的 `bin`/`obj`，否则生成的 `AssemblyInfo`/`GlobalUsings` 会被编两遍。
- 另一个适配工程**不要** `ProjectReference`——那样就变成引用 dll，类型会在两个程序集里各有一份。
- `VersionOverride` 是 CPM 下唯一干净的"同包不同版本"写法；若嫌它隐晦，给旧版本工程单独设 `ManagePackageVersionsCentrally=false`。
- 定义符号统一：`SKIA3` / `SKIA4`、`OPENCV4` / `OPENCV5`、`IMAGESHARP2` / `IMAGESHARP3`。

### X.3 差异收敛

- 允许的差异只在 `ApiCompat.Skia3.cs` / `ApiCompat.OpenCv5.cs` 这类 partial 文件里，且尽量小（枚举常量、单个重载）。
- ImageSharp 2/3 实际只差 EXIF 读取（`#if IMAGESHARP2`），一个文件即可；将来真出现大差异再按 `TextDrawing.Legacy.cs` 这样的 partial 文件分叉。
- 每个适配包都跑一遍 `PublicAPI` 比对：公开 API 必须完全一致，否则"换库不改代码"不成立。
- 每升级一个三方主版本：先加测试工程 → 编译报错 → 收敛 shim → 跑一致性 → 更新 README 对照表。

### X.4 包 ↔ 版本对照

| 包 | 三方库 | 备注 |
|---|---|---|
| `ImageFacade` | 无 | 核心：契约 + 默认实现 + 字体 |
| `ImageFacade.SdcbSimdCodecs` | Sdcb.SimdCodecs 2.0.0 | 软后端：ImgProcSharp Core 源码 + 编解码 |
| `ImageFacade.SkiaSharp` | SkiaSharp 4.155.0 | 当前 |
| `ImageFacade.SkiaSharp3` | SkiaSharp 3.119.1 | 兼容旧项目 |
| `ImageFacade.OpenCvSharp` | OpenCvSharp4 4.13.0 + runtime.win | 当前 |
| `ImageFacade.OpenCvSharp5` | OpenCvSharp5 5.0.0 + runtime.win | 新主版本 |
| `ImageFacade.ImageSharp` | ImageSharp 3.1.12 + Drawing 2.1.3 | 当前 |
| `ImageFacade.ImageSharp2` | ImageSharp 2.1.13 + Drawing 1.0.0 | 兼容旧项目 |

### X.5 测试与 CI

- 一个版本一个测试工程（见 §L.2），共享测试源码；`dotnet test` 在 solution 级别全部跑。
- v1 只要求 **win-x64**；OpenCvSharp4/5 的 native DLL 在各自输出目录，互不干扰。
- `dotnet pack` 时检查每个适配包只依赖自己的三方库版本，避免 `ImageFacade.SkiaSharp3` 被 `SkiaSharp 4` 污染。

---

## Y. ImgProcSharp 源码复制清单

### Y.1 复制范围

从 `F:\code\ImgProcSharp\src\ImgProcSharp.Core` 复制到 `src/ImageFacade.SdcbSimdCodecs/Rendering/`（**不含** `ImgProcSharp.SimdCodecs`）：

```
Canvas/            Canvas, Canvas.Resize, PixelOps
Colors/            Color, ColorStop, Blend
Primitives/        PointF, Size, SizeF, Rectangle, 图元（Polygon/Rect/…）
Paths/             IPath, Path, PathBuilder, PathBuilder.Arc, PathFlattener
Resampling/        IResampler, KnownResamplers, ResizeOptions, Resizer
Rasterization/     ScanlineRasterizer, CoverageBuffer, FillRule, RectangleFill, RowBlender
Brushes/           IBrush, SolidBrush, GradientBrush, PatternBrush
Pens/              IPen, Pen, LineCap, LineJoin, StrokeStyle, StrokeOutline
Drawing/           DrawingContext
Text/              TrueTypeFont*, Glyph*, PathCollection, TextLayout, TextFont, TextFont.Layout,
                   TextOptions, TextAlign, TextDrawing, GlyphPathBridge
Fonts/             （不复制字体，改用核心的 WenQuanYiMicroHei.ttf.gz）
```

**后续调整（M6 之后）**：`Text/` 里"纯字体"的那部分（`TrueTypeFont*`、`GlyphOutline/Rasterizer/Mask/Path`、`PathCollection`、`TextLayout`、`TextFont`→`Font`、字体度量/排版）**已上移到核心** `src/ImageFacade/Text/Engine/` + `src/ImageFacade/Font.cs`，
因为它是"嵌入字体 + 自己解析"的通用能力，OpenCV 5 后端也要用；`EmbeddedFonts`/`TextAlign` 已删除（前者改用核心 `BuiltInFont.Data`，后者复用核心 `ImageFacade.TextAlign`）。
留在 `ImageFacade.SdcbSimdCodecs/Rendering/Text/` 的只剩三件与渲染层强耦合的东西：`TextOptions`（绑 `IBrush`/`Color`）、`TextDrawing`（绑 `DrawingContext`/`Canvas`）、`GlyphPathBridge`（把字形轮廓转成渲染层的 `Path`）。

**不复制**：`ImgProcSharp.SimdCodecs/Image.cs`、`ImageDecoderBridge.cs`、`IImageProcessingContext.cs`、`ImageProcessingContext.cs`；`Text/TextPainter.cs`（自带 RGBA/BGRA 混合，和门面的混合路径重复）。

### Y.2 改造清单

| 项 | 动作 |
|---|---|
| namespace | `ImgProcSharp` → `ImageFacade.SdcbSimdCodecs.Rendering`（`ImgProcSharp.Text` 同理） |
| 可见性 | 所有类型 `public` → `internal`；上移到核心的字体部分只把 `ImageFacade.Font` 做成 public，其余（`GlyphMask`/`TextLayout`/`TrueTypeFont`…）保持 internal 并由 `InternalsVisibleTo` 供软后端与 OpenCV 5 后端使用 |
| 字体 | 删除 `Fonts/` 资源与 `EmbeddedFonts` 的旧 LogicalName；指向核心字体 |
| 打包属性 | 删除 `PackageId`/`Version`/`IsPackable`/`InternalsVisibleTo` |
| 文档注释 | 保留；`<see cref>` 路径随命名空间调整 |
| 命名冲突 | 复制代码内的 `PointF`/`Color` 是它自己的类型；文件里不要 `using ImageFacade;`，需要根类型时用别名 |
| 许可 | 保留文件头；`THIRD-PARTY-NOTICES.txt` 写明 ImgProcSharp 0.1.0 / Apache-2.0 |

### Y.3 验证

- `ImageFacade.SdcbSimdCodecs.dll` 的公开 API 清单里没有 `ImageFacade.SdcbSimdCodecs.Rendering.*`；核心 `ImageFacade.dll` 里根本没有这些类型。
- 软后端的像素结果与上游 ImgProcSharp 0.1.0 的渲染结果在相同输入上逐字节一致——迁移前后各跑一遍金标。
- `TextFont.BuiltIn` 不再存在（或仅 internal 且指向核心 `BuiltInFont.Data`），确保只有一份字体资源。
- AOT 发布：`ImageFacade.SdcbSimdCodecs` `PublishAot=true` 跑软后端示例，输出哈希与 JIT 一致。

### Y.4 同步策略

- 记录基线：ImgProcSharp 0.1.0 的 tag/commit 写进 `ImageFacade.SdcbSimdCodecs/Rendering/README.md`。
- 上游升级时：`git diff` 出改动 → 只挑渲染相关文件 → 按 Y.2 的改造点重新打补丁（用脚本或手工都行，不要直接覆盖）。
- 不在复制代码里加"我们自己的功能"；需要扩展时写在 `SoftwareBackend` 或门面层。
- 上游若发新版，先看渲染层有没有 bugfix；有就按 Y.4 合并，不跟随它的发包节奏（Sdcb 版本由我们自己控制）。
- 基线快照取 ImgProcSharp **0.2**（`4f6c5e8`，已升级 Sdcb 2.0 并适配编码门面），Core 仍是 net8+net10。

---

## Z. 落地记录（M0 / M1 实际约束，与 §C/§O 的出入以此为准）

| 项 | 实际决定 | 原因 |
|---|---|---|
| 目标框架 | `net8.0;net10.0`，**无 net9**；`ImageFacade.SdcbSimdCodecs` **只 net10.0** | 需求方指定；`Sdcb.SimdCodecs 2.0.0` 只有 net10 资产，按"目标库支持才留 net8"处理 |
| ImageSharp 当前档 | `3.1.12` + **Drawing 2.1.3** | Drawing 3.x 依赖 ImageSharp 4.x，`3.1.12 + 3.1.3` 无法还原 |
| `FontRef` | 属性改名 `FamilyName`，静态工厂仍是 `Family(...)` | C# 不允许属性与方法同名；工厂名是用户侧常用的那个 |
| 文字测量 SPI | `TryMeasureText(..., out TextMetrics)`（宽 + ascent/descent/lineGap） | 只返回 `SizeF` 无法区分"墨迹高"与"行高"，门面排多行需要行高 |
| 文字原点 SPI | `TextSpec.Origin`，语义由 `TextStyle.Anchor` 决定 | 后端掌握自己的字体度量，`TopLeft → baseline` 由后端换算最准（§J.2） |
| 六种 Resize | 门面算几何 + **像素搬运**合成（Crop/Pad），不依赖 `TryDrawFrame` | 各后端几何完全一致；`TryDrawFrame` 留给 letterbox（M7 减少一次全图往返） |
| `DrawText` 快路径 | 单行 + 左对齐 + `MaxWidth == 0` 时不测量直接画 | 后端即使不会测量也能工作；也避免每帧标签多一次测量调用 |
| 回退注册 | `[ModuleInitializer]` + `SoftwareBackend.UseAsFallback()` | 模块初始化器只在程序集被加载时跑，需要确定性时显式调用；软后端工程 `NoWarn=CA2255` |
| 回退计数 | `FallbackCount` / `OnFallback` 同时覆盖绘制、`Resize`、`Encode` 的临时迁移 | 否则"看起来能跑其实很慢"的场景观测不到 |
| 异步 | `LoadAsync` / `SaveAsync` / `EncodeAsync` 三个入口；SPI 是**可选**的 `IAsyncImageBackend`，未实现则门面用 `AsyncBridge`（即 `Task.Run`）包同步实现；绘制/Resize/测量保持同步，`Dispose` 不提供异步 | 微秒级操作套线程池只会更慢；真异步能力应该由后端自己声明（plan §5.6） |
| 已落地测试 | `tests/ImageFacade.Tests`（xunit.v3，57 例）：几何 / 像素转换 / 六种 Resize / 四种回退模式 / 折行对齐锚点 / 软后端端到端 | 路由与排版先于真实后端锁定行为 |
| 渲染源码复制 | ImgProcSharp **0.2** Core 的 47 个 `.cs` → `src/ImageFacade.SdcbSimdCodecs/Rendering/`；命名空间改 `ImageFacade.SdcbSimdCodecs.Rendering[.Text]`，**所有类型 internal**（残留 public 类型 0 处） | plan §Y.2；公开 API 不泄漏实现 |
| 内置字体 | 删除上游 `Fonts/` 与资源名；字体引擎上移核心后统一用 `ImageFacade.Font.BuiltIn`（首次访问解压 + 解析一次）与 `BuiltInFont.Data` | 全解决方案只有一份字体字节、一份解析实现 |
| 系统字体 | 复制层没有系统字体管理器：软后端遇到 `FontRef.Family(...)` 统一退回内置字体 | 宁可画出来，也不要抛异常 |
| 软后端编解码 | `ImageBackendBase` 已接 Sdcb 2.0：**解码** = `ImageDecoderRegistration.TryReadInfo` + `TryDecodeInto(Rgba32)`；**编码** = `PngWriter.Encode` / `JpegEncoder.Encode` / `BmpEncoder.Encode`（都是直通 RGBA8，正好是我们的规范格式）。支持 PNG/JPEG/BMP，`Decode` 顺带做 EXIF 1..8 转正 | 2.0 没有统一的 `SimdCodecs.Encode`，编码是按格式包的 writer；加格式 = 加包 + 加一个 `switch` 分支 |
| M8 底色合成 | 编码前若 `!format.SupportsAlpha()`（JPEG/BMP）且存在透明像素，就在门面把直通像素按 `SaveOptions.BackgroundColor` 合成成不透明临时帧再编码；全不透明时**不复制**（用假后端断言 `Create` 次数不变） | 透明处不能变黑；合成属于"语义"而非"渲染"，放门面层各后端一致；PNG/WebP/TIFF/GIF 仍保留 alpha |
| M3 Skia 后端 | `SkiaBackend`：帧 = `SKBitmap(Rgba8888, Premul)` + 常驻 `SKCanvas`；解码 PNG/JPEG/BMP/GIF/WebP/ICO，编码 PNG/JPEG/WebP；EXIF 转正复用核心 `PixelTransforms` | 一份源码两个包：`#if SKIA4` 用 `SKPathBuilder` 与 `DrawBitmap(..., SKSamplingOptions, paint)`；`#if SKIA3` 用 `SKPath` 与 4 参 `DrawBitmap`（4.155 把 `SKPath` 写 API 和 4 参 `DrawBitmap` 标记过时，项目开了 TreatWarningsAsErrors，必须切新 API） |
| Skia 原生资产 | 适配包**不引** `SkiaSharp.NativeAssets.*`，由应用/示例/测试按 RID 引 | 不把 win-x64 二进制钉进库；同一份源码要能跑 Linux/ARM |
| EXIF 转正 | 逻辑提到核心 `PixelTransforms.ApplyOrientation`（直通 RGBA 进/出），软后端与 Skia 共用 | 避免两套实现走偏；Skia 解码后先导直通再转正（一次额外拷贝，只在有方向信息时发生） |
| 跨后端一致性 | 示例同一份代码跑 software / skia：`MeasureText` 完全一致（720×42.205），墨迹像素 44,386 vs 44,269（AA 差异 0.26%） | 语义对齐达标（plan §9 要求包围盒 ≤ 1 px） |
| M5 ImageSharp 后端 | `ImageSharpBackend`：帧 = `Image<Rgba32>`；解码 `Image.Load<Rgba32>` + `AutoOrient`；编码 PNG/JPEG/BMP/GIF/WebP/TIFF；绘制/文字走 Drawing 的 `Fill`/`Draw`/`DrawLine`/`DrawText(RichTextOptions)`；虚线用 `PatternPen`；像素首选中 `DangerousTryGetSinglePixelMemory`，多段时退 `CopyPixelDataTo`/`ProcessPixelRows` | 一份源码两个包（3.1.12+Drawing 2.1.3 / 2.1.13+Drawing 1.0.0），唯一 `#if IMAGESHARP2` 是 EXIF 读取；两代 Drawing 的目标 API 实测一致 |
| 适配层源码放置 | 源码放在**当前主版本**工程目录（`src/ImageFacade.Xxx/XxxBackend.cs`），另一版本 csproj 用 `Compile Include` 链接；`src/adapters/` 已删除。唯一单独放的第三方源码仍是 `ImageFacade.SdcbSimdCodecs/Rendering/`（ImgProcSharp） | 需求方指定：除 ImgProcSharp 外，后端代码都待在自己的工程下；链接时必须 `Exclude` 对方 `bin`/`obj` |
| M5 测试 | `tests/ImageFacade.ImageSharp3.Tests`（16 例）与 `tests/ImageFacade.ImageSharp2.Tests`（同一份测试源码，16 例）：创建/回读、半透明混合、线/虚线/椭圆/多边形、内置字体中文、测量与墨迹一致、Baseline 锚点、多行行高、Pad、PNG 往返（含 alpha）、JPEG 底色、BGRA、`AcquirePixels`，外加与软后端的一致性（墨迹包围盒 ≤ 2 px、文字宽 ≤ 2 px） | 两个主版本各跑一遍即 M5 门禁；两者不冲突的前提是**一个版本一个测试工程**（同一进程只能绑一个 ImageSharp） |
| M6 OpenCV 后端 | `OpenCvBackend`：帧 = `CV_8UC4` BGRA 的 `Mat`；解码把 1/2/3/4 通道统一成 BGRA；编码 PNG/JPEG/BMP/WebP；绘制走 `Cv2.Line/Circle/Rectangle/Ellipse/Polylines/FillPoly`；坐标 `-0.5` 量化；构造时 `GetVersionString()` 打原生库自检 | 半透明（`A<255`）形状与虚线返回 `false` 交给门面回退；`TryWrapPixels` 恒 `false`（BGRA vs RGBA），不声明 `ZeroCopyPixels` |
| M6 文字 | 不用 `Cv2.PutText`：**OpenCV 5** 复用核心 `ImageFacade.Font.Layout`（`InternalsVisibleTo`）拿 A8 字形覆盖位图，按覆盖率把 BGRA 像素混一遍（直通空间源-覆盖），中文/半透明文字直接画进 `Mat`，**不触发回退**；**OpenCV 4** 的字体模块在 contrib 扩展包里，缺省认为没有 → `DrawText`/`MeasureText` 返回 `false`，交给回退 | 用 `OPENCV4`/`OPENCV5` 编译常量区分，不再按 TFM 区分；`FontFace` 非空时直接复用调用方实例 |
| M6 测试 | `tests/ImageFacade.OpenCv4.Tests`（16 例）与 `tests/ImageFacade.OpenCv5.Tests`（同一份测试源码，16 例）：BGRA 回读、`A<255` 形状抛 `FeatureNotSupportedException`、虚线被拒、线/椭圆/多边形、**中文直接绘制且 `FallbackEvent` 为空**、**半透明文字混合**、虚线回退到软后端并上报事件、Pad、PNG（含 alpha）、JPEG 底色、BGRA 导出、`AcquirePixels`、与软后端一致性（形状 + 文字测量） | 测试工程必须设 `RuntimeIdentifier=win-x64` 并各自引 runtime 包；两套 OpenCV runtime 不能同进程，所以两个工程分开 |
| 工程改名 | `ImageFacade.Software` → **`ImageFacade.SdcbSimdCodecs`**（目录/csproj/AssemblyName/RootNamespace/命名空间全改，类型名 `SoftwareBackend` 与路由名 `"software"` 保留）；它现在是"ImgProcSharp 渲染层（形状/画布）+ Sdcb.SimdCodecs 编解码"的落点 | 除这一处外，各后端源码都在自己的工程目录里 |
| 字体引擎上移核心 | `TrueTypeFont*`/`GlyphOutline`/`GlyphRasterizer`/`GlyphMask`/`GlyphPath`/`PathCollection`/`TextLayout` 移到 `src/ImageFacade/Text/Engine/`（命名空间 `ImageFacade.Text.Engine`），`TextFont`→公共 `ImageFacade.Font`（`Font.cs` + `Font.Layout.cs`）；`EmbeddedFonts`、`TextAlign` 删除（后者复用核心同名枚举） | 核心 `InternalsVisibleTo` 给 `ImageFacade.SdcbSimdCodecs` 与 `ImageFacade.OpenCvSharp5`；核心因此多目标 net8.0/net10.0 都带字体能力 |
| 字体复用 | `Font.BuiltIn` 是全局单例（首次解压+解析一次）；`Font.Load/LoadFromFile/LoadFromStream` 各解析一次后一直复用；`TextStyle.FontFace` 非空时后端直接用它，不再走 `FontRef` 解析（字形位图按 码点×字号 缓存，上限 4096） | 不传 `FontFace` 时行为不变：`FontRef` → 内置字体；原生文字后端（Skia/ImageSharp）忽略 `FontFace` |
