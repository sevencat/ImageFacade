# ImageFacade 设计与计划

一句话：**一套 API，八个包**（轻量核心 `ImageFacade` + 纯托管 `ImageFacade.Software` + `SkiaSharp` 3/4、`OpenCvSharp` 4/5、`ImageSharp` 2/3 六个适配包），覆盖加载 / 保存 / Resize / 画点 / 画线 / 画文字，外加 letterbox、张量化、NMS、检测框绘制。某个后端缺的能力自动落到 `ImageFacade.Software` 的软渲染，调用方代码一个字都不用改。

> 本文只放设计与计划。命名取舍、许可证、逐 API 映射、各库的坑、被否决的方案全部在 [`misc.md`](misc.md)。

---

## 1. 目标与非目标

### 1.1 交付的能力

| 能力 | 说明 |
|---|---|
| 加载 | 文件 / `Stream` / `ReadOnlySpan<byte>`，按**内容**识别格式；EXIF 方向可选自动应用 |
| 保存 | 文件 / `Stream` / `byte[]`；`Quality`、PNG 压缩级别；无 alpha 格式（JPEG/BMP）自动合成背景 |
| Resize | 6 种模式（`Stretch`/`Crop`/`Pad`/`BoxPad`/`Max`/`Min`）× 8 种采样核，语义由门面定义，后端只负责"拉伸到指定尺寸" |
| 画点 | `DrawPoint`：以 `radius` 为半径的实心圆点 |
| 画线 | `DrawLine` / `DrawPolyline`，线宽、端点、连接、虚线 |
| 画形状 | 矩形（描边/填充）、椭圆（描边/填充）、多边形填充 |
| 画文字 | 单行/多行/自动折行/对齐，中英文，字体来自系统、文件、内存或内置 |
| 测量 | `MeasureText` 与绘制使用**同一套排版结果**（同一后端的测量与绘制必须自洽） |
| 像素互操作 | `CopyPixelsTo` / `FromPixels`（**缺省 RGBA32**，另支持 BGRA/RGB/BGR/Gray，任意 stride），进阶 `AcquirePixels` |
| 自动回退 | 后端不支持的能力 → 迁移到 `ImageFacade.Software` 的软后端执行；软后端也能当主角（纯托管、无原生依赖） |
| 视频帧友好 | 指针级导入/导出（ffmpeg `AVFrame`、相机缓冲、Avalonia `WriteableBitmap`），稳定态零额外分配 |
| 检测流水线默认件 | letterbox、张量化（NCHW/NHWC、RGB/BGR、归一化）、**NMS**、检测框+标签绘制、坐标反算 |
| 多主版本 | 同一套 API 适配 SkiaSharp 3/4、OpenCvSharp 4/5、ImageSharp 2/3，每个主版本一个适配包 |

### 1.2 非目标（v1 明确不做）

- **变换与裁剪栈**：没有 `PushTransform` / `PushClip` / 任意路径 API（v2 再议，见 misc §O）。
- **混合模式**：只有 source-over（sRGB 空间），没有 Multiply/Screen 等。
- **动画/多帧**：只处理第 0 帧。
- **元数据编辑**：EXIF/XMP 只读不写；ICC 只在后端支持时透传。
- **滤镜/色彩调整**：灰度、锐化、色彩空间转换不属于本库；YUV（NV12/I420）到 RGB 的转换也由 ffmpeg `sws_scale` 或 OpenCV `CvtColor` 在进入门面之前完成。
- **布局引擎**：不做 bidi、复杂整形、富文本多 run、路径文字（这些交给具体后端的原生能力，门面不暴露）。
- ~~**Async API**：v1 全同步~~ → **v1 提供异步**：只有加载/保存（I/O + 编解码）有 `*Async` 重载；绘制 / Resize / 测量保持同步（微秒级，套线程池只会更慢）。后端没有原生异步时，门面**默认用 `Task.Run` 包同步实现**（§5.6）。
- **统一像素级输出**：不同后端的抗锯齿、字体栅格化、缩放核实现不同，**同代码不同后端不保证逐像素一致**；需要确定性时用软后端或 `PreferSoftwareRendering`。
- **更老的主版本**：只承诺 SkiaSharp 3/4、OpenCvSharp 4/5、ImageSharp 2/3；SkiaSharp 2.x、ImageSharp 1.x、OpenCV 3 及更早不提供适配包。
- **平台**：v1 只验收 win-x64；Linux/macOS 只保证核心与纯托管适配包可编译，不作为发布门槛。

### 1.3 五条设计主张

1. **核心保持简单**：`ImageFacade` 核心包 = 契约 + 路由 + 排版/Resize/Pipeline 等纯托管默认实现 + 内置字体；**不依赖任何编解码库，也不含 ImgProcSharp 源码**。
2. **软后端单独成包**：`ImageFacade.Software` = 复制改造的 ImgProcSharp Core 源码 + `Sdcb.SimdCodecs 2.0` 编解码 + `ImageBackendBase`（只做加载/保存的抽象基类）+ `SoftwareBackend`（完整软渲染）。它是默认回退目标，也可独立使用。
3. **后端是"能力可缺"的**：SPI 的每个绘制方法都是 `Try*`，返回 `false` 表示"我干不了"，而不是抛异常。引擎拿到 `false` 再决定回退。
4. **回退 = 把像素交给软后端执行**：软后端也是 `IImageBackend`，回退就是"换个后端在同一份像素上画"（默认临时迁移，可选粘滞），不需要第二套渲染接口。
5. **语义由门面定义，不由后端定义**：坐标系、Resize 模式、文字锚点、透明度合成、JPEG 背景色、**缺省像素格式 RGBA32**，全部在门面层写死并由一致性测试锁定。
6. **主版本隔离在适配包**：同一程序集只能绑定一个三方库版本，所以"支持 SkiaSharp 3 和 4"落成两个适配包 + 一份共享源码；核心永远只认 `IImageBackend`。

### 1.4 典型场景：视频帧的检测与叠加

设计的主要驱动场景（不是唯一场景，但优先级最高）：

```
ffmpeg / 相机 ──► 解码后的视频帧 ──► letterbox + 张量化 ──► YOLO（ONNX Runtime 等，不在本库）
      │                    │                                         │
      │                    └──────────► FacadeImage ◄───────────────┘ 检测框/类别/置信度
      │                                      │
      │                    画框、画标签、画文字（本库）
      │                                      │
      └────────────► 输出 ◄──────────────────┘
              ├─ Avalonia WriteableBitmap（Bgra8888，锁帧内存直拷）
              └─ ffmpeg 编码器（BGR24 / BGRA 指针直拷，再 sws_scale 转 YUV）
```

由此推导出的硬需求：

| 需求 | 设计响应 |
|---|---|
| 每帧都要过一遍 | 绘制/输出阶段不产生临时 `byte[]`/`Bitmap`；张量走池化；稳定态只有 `FacadeImage` 与后端帧对象各一次分配（帧池 v1.1） |
| 数据在原生内存里 | `FromPixels(IntPtr, …)` / `CopyPixelsTo(IntPtr, …)`；`PixelFormat` 覆盖 BGR24/BGRA（ffmpeg、OpenCV 的常见输出） |
| 30–60 fps | 单帧叠加预算（1080p、100 个检测框）≤ 8 ms（Skia）/ ≤ 15 ms（软后端）；具体见 §12 |
| 一帧内要画几十上百个框 | 回退策略有 `Adaptive`：累计回退达到阈值就整帧切到软后端，避免每个框都做一次全图往返 |
| UI 与采集线程分离 | `ImageEngine` 可跨线程共享；`FacadeImage` 不线程安全，一帧一实例，用完即弃 |
| 换库不改上层代码 | 上层只依赖 `FacadeImage` + `ImageEngine`；换 `IImageBackend` 即可（相机/YOLO/UI 的胶水代码各自独立） |

不在本库范围内：YOLO 推理本身、NMS、跟踪、OSD 排版引擎、YUV 色彩空间转换（由 ffmpeg `sws_scale` 或 OpenCV `CvtColor` 在进入门面之前完成）。

**这个场景下的后端选择**：

| 后端 | 支持版本 | 适合度 | 理由 |
|---|---|---|---|
| SkiaSharp | 4.155 / 3.119 | ★★★ 首选 | 绘制/文字/alpha 全原生，1080p 叠加目标个位数毫秒；跨平台；AOT 可用 |
| `ImageFacade.Software` | Sdcb 2.0 | ★★ 兜底/AOT | 零原生依赖、输出确定；单帧全软渲染 15 ms 量级（目标），30 fps 够用；编码格式随 Sdcb 2.0 |
| OpenCvSharp | 4.13 / 5.0 | ★★ 已有 OpenCV 生态时 | `Mat` 的 BGR 数据按 `PixelFormat.Bgr24` 直接进门面；但中文标签/半透明底会回退，适合"OpenCV 做前处理，Skia 做叠加" |
| ImageSharp | 3.1 / 2.1 | ★ 服务端/批处理 | 纯托管、格式全；每帧处理分配较多，50/60 fps 不推荐 |

---

## 2. 总体架构

```
┌─────────────────────────────────────────────────────────────┐
│ 调用方                                                       │
│   var engine = new ImageEngine(new SkiaBackend());           │
│   using var img = engine.Load("in.jpg");                     │
│   img.Resize(...).DrawLine(...).DrawText(...).Save("out.png");│
└───────────────────────────┬─────────────────────────────────┘
                            │ 统一 API（FacadeImage / ImageEngine）
┌───────────────────────────▼─────────────────────────────────┐
│ ImageFacade（核心包 = 契约 + 纯托管默认实现）                  │
│  · Rgba32 / PointF / RectF / SizeF / PixelFormat             │
│  · LoadOptions / SaveOptions / ResizeOptions / TextStyle     │
│  · 路由引擎：能力判断 → 原生 → 回退 → 抛错                     │
│  · 排版 · Resize 六模式 · JPEG 背景合成 · 内置字体             │
│  · Pipeline：letterbox / 张量化 / NMS / 检测框 / 像素转换      │
│  · 不依赖编解码库，不含软渲染源码                              │
└───────┬───────────────────────┬─────────────────────────────┘
        │ IImageBackend（SPI）   │ 同一份 SPI
┌───────▼────────┐  ┌───────────▼──────────┐  ┌──────────────────┐
│ SkiaBackend    │  │ ImageSharpBackend    │  │ OpenCvBackend    │
│ SKBitmap/Canvas│  │ Image<Rgba32>        │  │ Mat (BGRA)       │
│ 4.x / 3.x 两包 │  │ 3.x / 2.x 两包       │  │ 4.x / 5.x 两包   │
└────────────────┘  └──────────────────────┘  └──────────────────┘
        ▲                   ▲                      ▲
        └──────────┬────────┴──────────────────────┘
                   │ 可选的默认回退目标（引用后才注册）
        ┌──────────▼──────────────────────────────────┐
        │ ImageFacade.Software（独立包）               │
        │  · ImageBackendBase：只做加载/保存的抽象层    │
        │  · SoftwareBackend：完整软渲染                │
        │  · Rendering/：复制改造的 ImgProcSharp Core   │
        │  · Sdcb.SimdCodecs 2.0 编解码                 │
        └─────────────────────────────────────────────┘
```

两个包的内部分层：

```
ImageFacade
├─ Abstractions/     IImageBackend、IFrame、选项、几何、颜色（无三方依赖）
├─ Pipeline/         letterbox、张量化、NMS、检测绘制、像素转换
└─ Internal/         Router、TextLayouter、ResizePlanner

ImageFacade.Software
├─ ImageBackendBase   Decode/Encode/Clone/Copy（Sdcb 2.0），绘制默认 false
├─ SoftwareBackend    : ImageBackendBase，补绘制/Resize/文字
└─ Rendering/         复制并改造的 ImgProcSharp Core（全部 internal）
```

### 2.1 一次绘制的数据流

```
img.DrawLine(a, b, color, width)
  ├─ engine.Options.PreferSoftwareRendering == false
  ├─ img.Backend.TryDrawLine(img.Frame, spec)
  │     ├─ true  → 完成（原生，零拷贝）
  │     └─ false → 进入回退
  ├─ fallback = Options.FallbackBackend ?? SoftwareFallback.Factory?.Invoke()
  │             // ImageFacade.Software 通过 ModuleInitializer 注册；没引用就没有回退
  ├─ PerOperation（默认）：
  │     using var temp = fallback.Import(img)      // 规范像素 → 软后端
  │     fallback.TryDrawLine(temp.Frame, spec)     // 软渲染（必定支持）
  │     img.ImportFrom(temp)                       // 写回原后端
  └─ SwitchAndStay：迁移一次，后续都留在软后端
```

### 2.2 能力 / 回退判定

三档，顺序固定：

| 档 | 条件 | 行为 |
|---|---|---|
| 1 | 后端 `Try*` 返回 `true` | 原生执行 |
| 2 | 有回退后端且 `Options.Fallback != Disabled` | 软渲染执行（按 `FallbackMode` 决定临时还是粘滞） |
| 3 | 无回退后端或 `Fallback == Disabled` | 抛 `FeatureNotSupportedException`（含后端名、能力名、建议） |

**硬规则**：`Try*` 必须在**任何像素被修改之前**完成所有校验（字体能否解析、文本是否全 ASCII、`color.A < 255` 是否支持），返回 `false` 时帧必须保持原样。否则回退会把"画了一半的图"再画一遍。

### 2.3 迁移与粘滞

- **PerOperation（默认）**：图始终留在原后端，每次回退做一次 `导出 → 软绘制 → 导入`。1080p 一次约 3–12 ms（含 unpremultiply / 通道重排 / memcpy），适合零散的中文文字或半透明线。
- **SwitchAndStay**：第一次回退就永久迁移到软后端。适合"整张图都要画中文"或"纯软渲染"的场景。
- **Adaptive（推荐给视频流水线）**：先按 PerOperation 做，**累计**回退达到 `AdaptiveThreshold`（默认 3）就整帧切到软后端并留下。按累计而不是连续计数，是因为"每个框先画框（成功）再画标签（回退）"会让连续计数永远到不了阈值。一帧 100 个检测框只付一次迁移成本，下一帧是新实例、重新从原生开始。
- **Disabled**：不回退，缺能力直接抛错（严格模式/测试用）。
- **显式切换**：`img.UseSoftware()` / `img.UseNative()`，不受 `FallbackMode` 影响。
- **确定性**：`ImageEngineOptions.PreferSoftwareRendering = true` 时所有绘制都走软后端，所有后端输出一致（编解码仍由原后端负责）。

---

## 3. 统一语义（"同一套接口"的真正含义）

### 3.1 坐标系

- 原点左上，x 向右，y 向下，单位是像素。
- 采用 **corner 坐标**：像素 `(i, j)` 覆盖 `[i, i+1) × [j, j+1)`，中心在 `(i+0.5, j+0.5)`。这与 Skia、ImageSharp、ImgProcSharp 一致。
- **OpenCV 后端内部做 `x - 0.5, y - 0.5` 后量化**（OpenCV 的整数坐标等价于像素中心）。亚像素精度损失 ≤ 0.5 px，记入已知差异；能用 `shift` 参数时优先用（1/16 像素）。
- 线宽、半径、字号都是像素单位，不受 DPI 影响。

### 3.2 颜色与 alpha

- `Rgba32`：**直通（非预乘）** 的 8 位 RGBA，`(0,0,0,0)` = 全透明。
- **缺省像素格式就是 `Rgba32`**：`FromPixels`/`CopyPixelsTo`/`CopyPixels`/`AcquirePixels` 不写 `PixelFormat` 时都是 RGBA32；BGR/BGRA 必须显式指定，避免"按来源猜格式"。
- 绘制语义是 **source-over**：`out = src·a + dst·(1−a)`，在 sRGB 空间做，不做线性光混合。
- 后端内部可以用预乘（Skia、ImgProcSharp），只在像素进出边界时转换。
- 半透明绘制是能力位 `AlphaBlending`；不支持的后端（OpenCV）在 `color.A < 255` 时 `Try*` 返回 `false` → 回退。

### 3.3 尺寸与 Resize

```csharp
public enum ResizeMode { Stretch, Crop, Pad, BoxPad, Max, Min }
public enum Sampler
{
    Nearest, Box, Bilinear, Bicubic,
    Lanczos2, Lanczos3, MitchellNetravali, CatmullRom
}
```

- 语义与 ImageSharp 的 `ResizeMode` 一致（`Stretch` 是门面默认值，最符合"我就想缩到这个尺寸"的直觉）。
- **六种模式在门面层实现**：先算出目标缩放尺寸与偏移，再调用后端的 `TryResize`（只负责保持比例的拉伸），最后用 `TryDrawFrame` 1:1 贴图 + `TryFillRectangle` 补边。这样所有后端的裁剪/补边几何完全一致。
- 采样核映射到各后端最接近的实现；映射不精确的（Skia 没有真 Lanczos）在 misc §F 列表里写清，`ResizeOptions.PreferExactSampler = true` 时改走软后端。

### 3.4 文字

```csharp
public enum TextAnchor { TopLeft, Baseline }   // 默认 TopLeft
public enum TextAlign { Left, Center, Right }

public sealed class TextStyle
{
    public FontRef Font { get; init; } = FontRef.BuiltIn;
    public float Size { get; init; } = 16f;
    public Rgba32 Color { get; init; } = Rgba32.Black;
    public float MaxWidth { get; init; }            // 0 = 不折行
    public float LineHeight { get; init; }          // 0 = 用字体的行高
    public TextAlign Align { get; init; }
    public TextAnchor Anchor { get; init; }
    public bool Antialias { get; init; } = true;
}
```

- **折行与对齐在门面层做**，后端只画单行：换行符 `\n` 硬断行，`MaxWidth > 0` 时按字（CJK）或词（拉丁）贪心断行；对齐以 `MaxWidth`（为 0 时用最宽行）为参照。
- 后端只需提供 **单行测量** `TryMeasureText`，门面用同一套测量结果排版，保证 `MeasureText` 与 `DrawText` 自洽。
- 锚点转换：`TopLeft` 时 `baselineY = y + ascent`（Skia 的 ascent 是负数，取反；OpenCV/ImgProcSharp 的 `PutText`/`DrawText` 用基线，直接传；ImageSharp 的 `RichTextOptions` 用布局框左上角，直接传）。
- 字体解析顺序：`FontRef.Data` → `FontRef.FilePath` → `FontRef.Family`（系统字体）→ `FontRef.BuiltIn`（内置文泉驿微米黑）。缺字时整段回退到内置字体（v1 不做逐字形回退，见 misc §J）。
- 字体没有 kerning/整形（软后端）或由后端原生引擎负责（Skia/ImageSharp）。**跨后端文字外观必然不同**，需要一致时 `PreferSoftwareRendering = true`。

### 3.5 描边样式

```csharp
public enum LineCap { Butt, Round, Square }
public enum LineJoin { Miter, Bevel, Round }

public sealed class StrokeStyle
{
    public LineCap Cap { get; init; } = LineCap.Round;
    public LineJoin Join { get; init; } = LineJoin.Round;
    public float MiterLimit { get; init; } = 4f;
    public float[]? DashPattern { get; init; }        // 线宽倍数
    public float DashOffset { get; init; }
    public bool Antialias { get; init; } = true;
}
```

- 后端原生支持就原生画；OpenCV 没有虚线/斜接连接 → `TryDrawLine` 对带 `DashPattern` 的请求返回 `false` → 回退。
- 折线在门面层拆成"线段 + 连接"只在后端完全没有折线能力时发生，正常路径交给后端（避免半透明接缝）。

### 3.6 格式与元数据

- 解码格式按内容识别，不信扩展名。
- `LoadOptions.AutoOrient` 默认 `true`（用户拿到照片就是正的），后端各自实现：Skia 用 `SKCodec.EncodedOrigin`，ImageSharp 用 `AutoOrient()`，软后端用 `ImageInfo.Orientation`，OpenCV 用 `SimdCodecs.TryReadInfo` 读方向后自己转。
- `SaveOptions.PreserveMetadata` 默认 `false`：v1 只保证 ICC 在支持的后端上透传，EXIF 不保证往返（写清楚，别让用户以为能保留 GPS）。
- 无 alpha 的格式（JPEG/BMP）：若图确实含透明像素，先按 `SaveOptions.BackgroundColor`（默认白色）合成，再编码。透明像素的检测与合成由门面统一处理，避免各后端"一个变黑一个变白"。

### 3.7 错误与诊断

- 参数问题抛标准 `ArgumentException` / `ArgumentOutOfRangeException`。
- 第三方后端抛出的异常统一包成 `ImageBackendException`（保留 `InnerException`），消息里带后端名。
- 能力不足抛 `FeatureNotSupportedException`，消息里带：后端名、能力名、当前 `FallbackMode`、可选的替代方案（例如"把 Fallback 从 Disabled 改成 Adaptive"）。
- `ImageEngineOptions.OnFallback`：每次回退回调一次（后端、操作、耗时），便于发现"看起来能跑其实很慢"的场景。
- `ImageEngineOptions.Strict`：为 `true` 时任何回退都视为错误（CI / 性能敏感服务用）。

---

## 4. 公开 API 草案

### 4.1 几何与颜色

```csharp
namespace ImageFacade;

public readonly struct Rgba32 : IEquatable<Rgba32>
{
    public byte R { get; } public byte G { get; } public byte B { get; } public byte A { get; }
    public Rgba32(byte r, byte g, byte b, byte a = 255);
    public Rgba32 WithAlpha(byte a);
    public static Rgba32 FromHex(uint rgb, byte a = 255);
    public static Rgba32 Parse(string text);            // "#RRGGBB" / "#RRGGBBAA" / 命名色
    public static Rgba32 Transparent { get; }
    public static Rgba32 Black { get; }
    public static Rgba32 White { get; }
    public static Rgba32 Red { get; }
    // … Gray / Orange / Yellow / Green / Blue / Magenta / Cyan
}

public readonly struct PointF { public float X { get; } public float Y { get; } }
public readonly struct Size   { public int Width { get; } public int Height { get; } }
public readonly struct SizeF  { public float Width { get; } public float Height { get; } }
public readonly struct RectF
{
    public float X { get; } public float Y { get; } public float Width { get; } public float Height { get; }
    public float Left { get; } public float Top { get; } public float Right { get; } public float Bottom { get; }
    public PointF Center { get; }
}
```

### 4.2 入口

```csharp
public sealed class ImageEngine : IDisposable
{
    public ImageEngine(IImageBackend backend, ImageEngineOptions? options = null);

    public IImageBackend Backend { get; }
    public ImageEngineOptions Options { get; }

    /// <summary>可选全局默认，必须显式赋值，不做自动探测。</summary>
    public static ImageEngine? Default { get; set; }

    public bool CanDecode(ImageFormat format);
    public bool CanEncode(ImageFormat format);

    public FacadeImage Load(string path, LoadOptions? options = null);
    public FacadeImage Load(Stream stream, LoadOptions? options = null);
    public FacadeImage Load(ReadOnlySpan<byte> data, LoadOptions? options = null);
    // 异步版本（§5.6）：I/O 真异步；解码有后端异步接口就用它，否则 Task.Run
    public Task<FacadeImage> LoadAsync(string path, LoadOptions? options = null, CancellationToken cancellationToken = default);
    public Task<FacadeImage> LoadAsync(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default);
    public Task<FacadeImage> LoadAsync(ReadOnlyMemory<byte> data, LoadOptions? options = null, CancellationToken cancellationToken = default);

    public FacadeImage Create(int width, int height, Rgba32? background = null);

    // 原始帧入口：支持 BGR24/BGRA 等 ffmpeg/相机常见格式，避免先转一遍 RGBA
    public FacadeImage FromPixels(ReadOnlySpan<byte> pixels, int width, int height, int stride,
                                  PixelFormat format = PixelFormat.Rgba32);
    public FacadeImage FromPixels(IntPtr pixels, int width, int height, int stride,
                                  PixelFormat format = PixelFormat.Rgba32, bool wrap = false);

    public ImageMetadata Identify(ReadOnlySpan<byte> data);   // 只读头，不解码全图
}

public enum PixelFormat
{
    Rgba32, Bgra32, Rgb24, Bgr24, Gray8,
    Bgra32Premultiplied,   // Avalonia 的 AlphaFormat.Premul / 预乘源
}

public sealed class ImageEngineOptions
{
    public IImageBackend? FallbackBackend { get; init; }           // null = 用已注册的 SoftwareBackend 工厂
    public FallbackMode Fallback { get; init; } = FallbackMode.PerOperation;
    public int AdaptiveThreshold { get; init; } = 3;               // Adaptive 模式下累计回退几次后整帧切换
    public bool PreferSoftwareRendering { get; init; }
    public bool Strict { get; init; }                              // 任何回退都抛错
    public Action<FallbackEvent>? OnFallback { get; init; }
}

public enum FallbackMode { Disabled, PerOperation, Adaptive, SwitchAndStay }
```

### 4.3 图像句柄

```csharp
public sealed class FacadeImage : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public ImageFormat SourceFormat { get; }
    public string BackendName { get; }
    public bool IsSoftwareSurface { get; }
    public ImageMetadata Metadata { get; }

    // 尺寸：原地替换底层帧，返回 this 以便链式
    public FacadeImage Resize(int width, int height);
    public FacadeImage Resize(ResizeOptions options);

    public FacadeImage Clear(Rgba32 color);

    // 绘制：原地修改，返回 this
    public FacadeImage DrawPoint(PointF point, Rgba32 color, float radius = 1.5f);
    public FacadeImage DrawLine(PointF from, PointF to, Rgba32 color,
                                float width = 1f, StrokeStyle? style = null);
    public FacadeImage DrawPolyline(ReadOnlySpan<PointF> points, Rgba32 color,
                                   float width = 1f, bool closed = false, StrokeStyle? style = null);
    public FacadeImage DrawRectangle(RectF rect, Rgba32 color, float width = 1f, StrokeStyle? style = null);
    public FacadeImage FillRectangle(RectF rect, Rgba32 color);
    public FacadeImage DrawEllipse(RectF bounds, Rgba32 color, float width = 1f);
    public FacadeImage FillEllipse(RectF bounds, Rgba32 color);
    public FacadeImage FillPolygon(ReadOnlySpan<PointF> points, Rgba32 color);

    public FacadeImage DrawText(string text, PointF at, TextStyle style);
    public SizeF MeasureText(string text, TextStyle style);

    // 生命周期 / 后端切换
    public FacadeImage Clone();
    public FacadeImage UseSoftware();
    public FacadeImage UseNative();

    // 输出
    public void Save(string path, SaveOptions? options = null);
public Task SaveAsync(string path, SaveOptions? options = null, CancellationToken cancellationToken = default);
public Task SaveAsync(Stream stream, SaveOptions? options = null, CancellationToken cancellationToken = default);
public Task<byte[]> EncodeAsync(SaveOptions? options = null, CancellationToken cancellationToken = default);
    public void Save(Stream stream, SaveOptions? options = null);
    public byte[] Encode(SaveOptions? options = null);

    // 像素互操作（顶行优先，stride 由调用方给；默认输出直通 RGBA）
    public Rgba32 GetPixel(int x, int y);   // 抽检用；批量请用 CopyPixelsTo
    public void CopyPixelsTo(Span<byte> destination, int destinationStride,
                             PixelFormat format = PixelFormat.Rgba32);
    public void CopyPixelsTo(IntPtr destination, int destinationStride,
                             PixelFormat format = PixelFormat.Rgba32);   // ffmpeg / Avalonia
    public byte[] CopyPixels(PixelFormat format = PixelFormat.Rgba32);
    public PixelLease AcquirePixels();     // 进阶：零拷贝或内部缓冲，Dispose 时写回

    /// <summary>true 表示帧内存直接来自 <see cref="ImageEngine.FromPixels(IntPtr,int,int,int,PixelFormat,bool)"/> 的 wrap。</summary>
    public bool IsZeroCopy { get; }

    public void Dispose();
}
```

### 4.4 选项

```csharp
public sealed class LoadOptions
{
    public bool AutoOrient { get; init; } = true;
    public bool PreserveMetadata { get; init; } = true;   // 仅 ICC / 方向
    public int FrameIndex { get; init; }                  // v1 只接受 0
}

public sealed class SaveOptions
{
    public ImageFormat? Format { get; init; }             // null = 按扩展名
    public int Quality { get; init; } = 90;               // 有损格式
    public int CompressionLevel { get; init; } = -1;      // PNG zlib 0..9，-1 = 默认
    public bool PreserveMetadata { get; init; }
    public Rgba32 BackgroundColor { get; init; } = Rgba32.White;   // 无 alpha 格式的合成底色
}

public sealed class ResizeOptions
{
    public int Width { get; init; }
    public int Height { get; init; }
    public ResizeMode Mode { get; init; } = ResizeMode.Stretch;
    public Sampler Sampler { get; init; } = Sampler.Bicubic;
    public Rgba32 PadColor { get; init; } = Rgba32.Transparent;
    public bool PreferExactSampler { get; init; }          // 映射不精确时改走软后端
}

public sealed class FontRef
{
    public string? Family { get; }
    public string? FilePath { get; }
    public ReadOnlyMemory<byte>? Data { get; }
    public FontStyle Style { get; }
    public bool UseBuiltInFallback { get; init; } = true;

    public static FontRef BuiltIn { get; }                 // 内嵌文泉驿微米黑
    public static FontRef Family(string name, FontStyle style = FontStyle.Regular);
    public static FontRef FromFile(string path, FontStyle style = FontStyle.Regular);
    public static FontRef FromData(ReadOnlyMemory<byte> data, FontStyle style = FontStyle.Regular);
}
```

### 4.5 典型用法

```csharp
using ImageFacade;
using ImageFacade.SkiaSharp;

using var engine = new ImageEngine(new SkiaBackend());
using var img = engine.Load("photo.jpg", new LoadOptions { AutoOrient = true });

img.Resize(new ResizeOptions { Width = 800, Height = 600, Mode = ResizeMode.Max, Sampler = Sampler.Lanczos3 })
   .Clear(Rgba32.White)
   .DrawLine(new PointF(20, 20), new PointF(300, 20), Rgba32.Red, width: 3)
   .DrawPoint(new PointF(300, 120), Rgba32.Orange, radius: 6)
   .DrawText("你好 ImageFacade", new PointF(40, 240), new TextStyle
   {
       Font = FontRef.BuiltIn,
       Size = 36,
       Color = Rgba32.FromHex(0x1A1A1A),
       MaxWidth = 720,
       Align = TextAlign.Left,
   });

img.Save("out.png", new SaveOptions { CompressionLevel = 6 });
```

换后端只改两行：

```csharp
using var engine = new ImageEngine(new OpenCvBackend());   // 其余代码完全不变
```

### 4.6 预处理与检测绘制（核心默认实现）

这一层是纯托管、与后端无关的"缺省函数"，放在 `ImageFacade.Pipeline` 命名空间；不引用任何推理库。

```csharp
namespace ImageFacade.Pipeline;

/// <summary>letterbox 的几何信息：从模型坐标反算回源图坐标。</summary>
public readonly struct LetterboxInfo
{
    public Size SourceSize { get; }
    public Size ModelSize { get; }
    public float Scale { get; }
    public int PadLeft { get; }
    public int PadTop { get; }
    public PointF ToSource(float x, float y);
    public RectF ToSource(RectF modelBox);
    public RectF ToModel(RectF sourceBox);
}

public sealed class TensorOptions
{
    public int Width { get; init; }                     // 模型输入宽
    public int Height { get; init; }
    public TensorLayout Layout { get; init; } = TensorLayout.Nchw;   // v1 只支持 3 通道
    public ChannelOrder Channels { get; init; } = ChannelOrder.Rgb;
    public bool Normalize { get; init; } = true;        // /255
    public ReadOnlyMemory<float> Mean { get; init; }    // 归一化后再减，长度 3
    public ReadOnlyMemory<float> Std { get; init; }     // 再除，长度 3
    public Rgba32 PadColor { get; init; } = new(114, 114, 114);
    public bool CenterPad { get; init; } = true;
}

public enum TensorLayout { Nchw, Nhwc }
public enum ChannelOrder { Rgb, Bgr }

public static class Tensorizer
{
    public static void ToTensor(this FacadeImage source, TensorOptions options, Span<float> destination,
                                out LetterboxInfo info);
    public static float[] ToTensor(this FacadeImage source, TensorOptions options, out LetterboxInfo info);
}

/// <summary>池化的 float 缓冲，避免 640×640×3（约 4.9 MB）每帧进 LOH。</summary>
public sealed class TensorBuffer : IDisposable
{
    public static TensorBuffer Rent(int elementCount);
    public Span<float> Span { get; }
    public void Dispose();                              // 归还池
}

/// <summary>letterbox 帮助函数：返回新图 + 几何信息，源图不变。</summary>
public static class Letterbox
{
    public static FacadeImage Apply(this FacadeImage source, int width, int height,
                                    Rgba32? padColor = null, bool center = true,
                                    out LetterboxInfo info);
    public static FacadeImage Apply(this FacadeImage source, LetterboxOptions options,
                                    out LetterboxInfo info);
}

public sealed class LetterboxOptions
{
    public int Width { get; init; }
    public int Height { get; init; }
    public Rgba32 PadColor { get; init; } = new(114, 114, 114);
    public bool Center { get; init; } = true;
}

public readonly struct Detection
{
    public RectF Box { get; }                           // 源图坐标
    public int ClassId { get; init; }                   // NMS 分组用；没有分类时保持 0
    public string? Label { get; }
    public float Score { get; }
    public Rgba32 Color { get; }
    public Detection(RectF box, string? label = null, float score = 0f, Rgba32 color = default);
}

/// <summary>贪心 NMS / IoU；不依赖任何推理库。</summary>
public static class Nms
{
    /// <summary>原地抑制：保留项前移，返回保留数量（span 前置段有效）。</summary>
    public static int Suppress(Span<Detection> detections, float iouThreshold = 0.45f, bool classAware = true);

    /// <summary>返回保留项在原数组中的下标（按分数降序）。</summary>
    public static int[] Select(ReadOnlySpan<Detection> detections, float iouThreshold = 0.45f, bool classAware = true);

    public static float IoU(RectF a, RectF b);
}

public sealed class DetectionStyle
{
    public float StrokeWidth { get; init; } = 2f;
    public float FontSize { get; init; } = 14f;
    public FontRef Font { get; init; } = FontRef.BuiltIn;
    public Rgba32 LabelColor { get; init; } = Rgba32.White;
    public Rgba32 LabelBackground { get; init; } = new(0, 0, 0, 160);
    public bool ShowScore { get; init; } = true;
    public float LabelPadding { get; init; } = 3f;
    public IReadOnlyList<Rgba32> Palette { get; init; } = DefaultPalette;
    public Func<Detection, string>? FormatLabel { get; init; }   // 默认 "label 0.92"

    public static IReadOnlyList<Rgba32> DefaultPalette { get; }
}

public static class DetectionDrawing
{
    /// <summary>画框 + 标签胶囊；标签宽度用 MeasureText 算，结果与当前后端自洽。</summary>
    public static FacadeImage DrawDetections(this FacadeImage image,
                                             ReadOnlySpan<Detection> detections,
                                             DetectionStyle? style = null);
}
```

实现要点：

- `ToTensor` 内部做 letterbox（等比缩放 + 补边），**不修改源图**；源图仍可用于把检测框画回原尺寸。临时帧由当前后端 `TryResize`/`TryFillRectangle` 生成（640×640 RGBA 约 1.6 MB，用完即弃），不产生托管大对象。
- 张量填充按 `Layout` 与 `Channels` 直接写目标 buffer，逐行处理，不产生中间 `byte[]`；`destination` 长度不足直接抛 `ArgumentException`。
- 颜色归一化默认 `/255`；`Mean`/`Std` 按通道应用（`(v/255 - mean) / std`），顺序是 `Channels` 指定的顺序。
- `TensorBuffer` 用**私有**的 `ArrayPool<float>.Create(maxArrayLength: 2_000_000, maxArraysPerBucket: 4)`：默认 `ArrayPool<float>.Shared` 的最大桶是 1,048,576 个元素，装不下 640×640×3 = 1,228,800，会退化成每帧新分配。
- `DrawDetections` 是默认实现，不是唯一实现：用户完全可以自己循环 `DrawRectangle` + `FillRectangle` + `DrawText`。
- 标签宽度按"同一 `(label, fontSize, font)` 只测一次"缓存；`ReadOnlySpan<Detection>` 入参避免每次复制数组。
- `Detection.Color` 为全 0（默认）时按 `DetectionStyle.Palette` 轮转，显式颜色优先。
- 视频场景的 `DetectionStyle.LabelBackground` 带 alpha；OpenCV 后端会因 `AlphaBlending` 缺失回退。把 `Fallback` 设为 `Adaptive` 后，整帧只付一次迁移成本。
- `Letterbox.Apply` 返回**新图**，源图保持原尺寸（便于把框画回原图）；`Tensorizer.ToTensor` 复用同一套几何计算，但不暴露中间图。
- `Nms` 是纯几何 + 排序：按 `Score` 降序，贪心保留，IoU > 阈值即抑制；`classAware` 用 `ClassId` 分组（不看 `Label` 字符串）。典型 n ≤ 300，O(n²) 足够；不要用 LINQ 每帧分配。退化框（宽或高 ≤ 0）IoU 记 0。
- `Nms.Suppress` 原地整理 `Span`，返回保留数量，调用方 `detections[..count]` 即可；`Select` 适合还需要原始顺序的场景。两者都不做分数阈值过滤（该在解码阶段做）。

### 4.7 输出适配：Avalonia / ffmpeg

门面只暴露"把规范像素写成指定格式 + 任意 stride"，UI 与编码器的胶水各写十几行即可，核心不引用 Avalonia 或任何 ffmpeg 绑定。

**Avalonia（Bgra8888）**

```csharp
using var fb = writeableBitmap.Lock();              // ILockedFramebuffer
image.CopyPixelsTo(fb.Address, fb.RowBytes, PixelFormat.Bgra32);
```

- 视频帧通常不透明：`WriteableBitmap` 创建时用 `AlphaFormat.Opaque`，直接 `Bgra32` 拷贝，不做预乘转换（不透明像素在 `Premul` 下等价）。
- 若 `fb.AlphaFormat == AlphaFormat.Premul` 且图确实半透明，用 `PixelFormat.Bgra32Premultiplied` 让门面直接输出预乘结果；不要先按 `Bgra32` 拷一遍再逐像素乘。
- 不建议每帧 `new WriteableBitmap` 或 `Bitmap`；尺寸不变时复用同一个 `WriteableBitmap`，只 `Lock()` 一次。
- `Lock()` 遵循 Avalonia 的线程模型，通常在 UI 线程执行；渲染在后台线程完成后再投递到 UI 线程拷贝。

**ffmpeg（BGR24 / BGRA）**

```csharp
// 输入：sws_scale 到 BGR24 后
using var img = engine.FromPixels((IntPtr)frame->data[0], w, h, frame->linesize[0], PixelFormat.Bgr24);

// 输出：直接写进目标 AVFrame 的 data[0]
img.CopyPixelsTo((IntPtr)dstFrame->data[0], dstFrame->linesize[0], PixelFormat.Bgr24);
```

- `linesize` 可能大于 `width * 3`（对齐），门面按 stride 逐行写，不要求连续。
- `linesize` 为负（自底向上）时门面不支持直接写；调用方传 `-linesize` 并把指针移到最后一行的起点，或在适配层翻转。
- 输出给编码器时通常是 BGR24/BGRA；YUV420/NV12 由 `sws_scale` 完成，门面不做色彩空间转换。
- 相机若给 MJPG，用 `engine.Load(bytes)` 解码；若给 NV12/I420，先用 `sws_scale`/`CvtColor` 转成 BGR24/BGRA 再进门面。

**一帧的完整流水线**

```csharp
using var engine = new ImageEngine(new SkiaBackend(),
    new ImageEngineOptions { Fallback = FallbackMode.Adaptive });

// 每帧（w/h/stride 来自 ffmpeg AVFrame 或相机缓冲）
using var img = engine.FromPixels(framePtr, w, h, stride, PixelFormat.Bgr24);

using var tensor = TensorBuffer.Rent(3 * 640 * 640);
img.ToTensor(new TensorOptions { Width = 640, Height = 640, Layout = TensorLayout.Nchw },
             tensor.Span, out LetterboxInfo lb);

IReadOnlyList<RawDetection> raw = detector.Run(tensor.Span);   // 用户自己的 YOLO（RawDetection 是用户类型）
var dets = new Detection[raw.Count];                           // 可换成 ArrayPool<Detection> 复用
for (int i = 0; i < raw.Count; i++)
{
    dets[i] = new Detection(lb.ToSource(raw[i].Box), raw[i].Label, raw[i].Score, raw[i].Color)
    {
        ClassId = raw[i].ClassId,
    };
}

int kept = Nms.Suppress(dets, iouThreshold: 0.45f, classAware: true);
img.DrawDetections(dets.AsSpan(0, kept), style);
img.CopyPixelsTo(fb.Address, fb.RowBytes, PixelFormat.Bgra32);
```

稳定态（尺寸不变、字体已热、`TensorBuffer` 池化）每帧不产生新的托管大对象；`FacadeImage` 与后端帧对象本身仍是每帧一次分配，帧池见 misc §S.6（v1.1）。

### 4.8 像素格式转换（公开 helper）

适配层经常只想"把这块 BGR24 转成 RGBA 写进另一块内存"，不需要建 `FacadeImage`。核心提供纯函数：

```csharp
public static class PixelConverter
{
    public static void Convert(ReadOnlySpan<byte> source, int sourceStride, PixelFormat sourceFormat,
                               Span<byte> destination, int destinationStride, PixelFormat destinationFormat,
                               int width, int height);

    public static void Convert(IntPtr source, int sourceStride, PixelFormat sourceFormat,
                               IntPtr destination, int destinationStride, PixelFormat destinationFormat,
                               int width, int height);
}
```

- 逐行处理，忽略 padding；`stride` 不足、格式未知、宽高为负直接抛 `ArgumentException`。
- 只在格式不同或 stride 不同时做转换；相同格式 + 相同 stride 时退化成 `CopyTo`。
- 这是 `FromPixels`/`CopyPixelsTo` 的底层实现，公开出来是为了让采集/UI 适配层不必为了转一次格式而创建 `FacadeImage`。

---

## 5. 后端 SPI

### 5.1 帧与接口

```csharp
namespace ImageFacade.Backends;

/// <summary>后端私有的图像句柄。门面只透传，绝不看内部。</summary>
public interface IFrame : IDisposable
{
    int Width { get; }
    int Height { get; }
}

[Flags]
public enum BackendCapabilities
{
    None = 0,
    Resize              = 1 << 0,
    DrawShapes          = 1 << 1,
    DrawPoints          = 1 << 2,
    DrawText            = 1 << 3,
    DrawTextUnicode     = 1 << 4,   // 非 ASCII（中文）
    AlphaBlending       = 1 << 5,   // color.A < 255 的绘制
    Dash                = 1 << 6,
    LossyEncode         = 1 << 7,
    ExifOrientation     = 1 << 8,
    IccProfile          = 1 << 9,
    ZeroCopyPixels      = 1 << 10,
    AotCompatible       = 1 << 11,
}

public interface IImageBackend : IDisposable
{
    string Name { get; }                                  // "skia" / "opencv" / "imagesharp" / "software"
    Version Version { get; }
    BackendCapabilities Capabilities { get; }              // 仅供参考，路由以 Try* 为准
    IReadOnlySet<ImageFormat> SupportedDecodeFormats { get; }
    IReadOnlySet<ImageFormat> SupportedEncodeFormats { get; }

    // 生命周期
    IFrame Create(int width, int height, Rgba32? background);
    bool TryWrapPixels(IntPtr pixels, int width, int height, int stride, bool ownsMemory, out IFrame frame);
    IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata);
    IFrame Clone(IFrame frame);
    void Encode(IFrame frame, Stream destination, in EncodeRequest request);

    // 几何
    bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result);  // 只做"拉伸"
    bool TryDrawFrame(IFrame destination, IFrame source, PointF offset);        // 1:1 贴图（Crop/Pad 用）

    // 绘制（返回 false = 不改像素）
    bool TryDrawPoint(IFrame frame, in PointSpec spec);
    bool TryDrawLine(IFrame frame, in LineSpec spec);
    bool TryDrawPolyline(IFrame frame, in PolylineSpec spec);
    bool TryDrawRectangle(IFrame frame, in StrokedRectSpec spec);
    bool TryFillRectangle(IFrame frame, in FilledRectSpec spec);
    bool TryDrawEllipse(IFrame frame, in StrokedEllipseSpec spec);
    bool TryFillEllipse(IFrame frame, in FilledEllipseSpec spec);
    bool TryFillPolygon(IFrame frame, in PolygonSpec spec);
    bool TryDrawText(IFrame frame, in TextSpec spec);       // 单行、已排版；Origin 语义由 Style.Anchor 决定
    bool TryMeasureText(IFrame frame, in TextSpec spec, out TextMetrics metrics);  // 宽 + ascent/descent/lineGap

    // 像素
    void Clear(IFrame frame, Rgba32 color);
    bool TryGetPixel(IFrame frame, int x, int y, out Rgba32 color);
    bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride);
    void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride);
    bool TryAcquirePixels(IFrame frame, out PixelLease lease);
}
```

`Spec` 都是 `readonly struct`（`PointF`、`Rgba32`、`float Width`、`StrokeStyle? Style`、`string Text`、`FontRef`…），不引入可变的中间对象。

`PixelLease` 是 `ref struct`：

```csharp
public ref struct PixelLease
{
    public Span<byte> Pixels { get; }   // 直通 Rgba32
    public int Stride { get; }
    public void Dispose();              // 归还锁 / 把内部副本写回
}
```

后端无法零拷贝时（ImageSharp 多缓冲、OpenCV 非连续 Mat）内部租一块缓冲，`Dispose` 时 `ImportPixels` 写回。调用方看到的语义一致。

`FromPixels(..., wrap: true)` 只有"规范 RGBA32 + 后端支持 `TryWrapPixels`"时才真正零拷贝；BGR24/BGRA 等格式由核心先做一次转换再导入，此时 `IsZeroCopy == false`，不报错也不假装零拷贝。

### 5.2 抽象基类：只做加载/保存（在 ImageFacade.Software 里）

`ImageBackendBase` 放在 **`ImageFacade.Software`** 包，不污染核心。这就是"Software 只做加载/保存"的落点：**抽象层**是 `ImageBackendBase`（解码/编码/克隆/拷像素），**具体层**是 `SoftwareBackend : ImageBackendBase`（再加 ImgProcSharp 渲染）。

它依赖 `Sdcb.SimdCodecs 2.0`，所以只能待在 Software 包里；核心要保持零编解码依赖。

```csharp
namespace ImageFacade.Backends;

/// <summary>
/// 只实现"加载/保存/拷像素"的抽象后端：
/// Decode 走 Sdcb.SimdCodecs 2.0（ImageDecoder），Encode 走 SimdCodecs.Encode；
/// 绘制与 Resize 默认返回 false，由派生类按需覆盖。
/// </summary>
public abstract class ImageBackendBase : IImageBackend
{
    public abstract string Name { get; }
    public virtual Version Version => GetType().Assembly.GetName().Version!;
    public virtual BackendCapabilities Capabilities => BackendCapabilities.None;
    public virtual IReadOnlySet<ImageFormat> SupportedDecodeFormats => DefaultFormats.Decode;
    public virtual IReadOnlySet<ImageFormat> SupportedEncodeFormats => DefaultFormats.Encode;

    // 派生类只需实现这 3 个抽象的"帧"成员（TryWrapPixels 可选）
    public abstract IFrame Create(int width, int height, Rgba32? background);
    public abstract bool TryCopyPixels(IFrame frame, Span<byte> destination, int destinationStride);
    public abstract void ImportPixels(IFrame frame, ReadOnlySpan<byte> source, int sourceStride);
    public virtual bool TryWrapPixels(IntPtr pixels, int width, int height, int stride,
                                      bool ownsMemory, out IFrame frame)
    { frame = null!; return false; }

    // 已实现：Decode / Encode / Clone / Clear / TryGetPixel
    public IFrame Decode(ReadOnlySpan<byte> data, in DecodeRequest request, out ImageMetadata metadata) { /* Sdcb */ }
    public void Encode(IFrame frame, Stream destination, in EncodeRequest request) { /* Png/Jpeg */ }
    public IFrame Clone(IFrame frame) { /* Create + 像素拷贝 */ }

    // 绘制/缩放：默认不支持，交给门面回退
    public virtual bool TryResize(IFrame frame, in ResizeRequest request, out IFrame result)
    { result = null!; return false; }
    public virtual bool TryDrawPoint(IFrame frame, in PointSpec spec) => false;
    // … 其余 Try* 同理
}
```

用途：

- **软件后端**：`SoftwareBackend : ImageBackendBase` 只补绘制/缩放/文字，加载保存直接复用。
- **第三方新后端**：继承它、实现 `Create`/`ImportPixels`/`TryCopyPixels`，立刻拥有 PNG/JPEG 加载保存；绘制可以慢慢补。
- **不想用 Sdcb 编解码的后端**（Skia/OpenCV/ImageSharp）直接实现 `IImageBackend`，用原生 codec，不继承基类。

### 5.3 门面侧的执行算法

```csharp
T Execute<T>(FacadeImage img, BackendOperation op, string feature, Func<IImageBackend, IFrame, T> run)
{
    if (!engine.Options.PreferSoftwareRendering && op.Try(img.Backend, img.Frame, out T result))
        return result;
    if (engine.Options.Strict) throw new FeatureNotSupportedException(feature, img.BackendName);
    var fallback = engine.GetFallback();
    if (fallback is null || engine.Options.Fallback == FallbackMode.Disabled)
        throw new FeatureNotSupportedException(feature, img.BackendName);

    bool switchNow = engine.Options.Fallback == FallbackMode.SwitchAndStay
                  || (engine.Options.Fallback == FallbackMode.Adaptive
                      && img.FallbackCount >= engine.Options.AdaptiveThreshold);

    return switchNow
        ? img.SwitchTo(fallback).Run(op, feature)           // 迁移一次，后续都留软后端
        : img.RunTemporary(fallback, op, feature);           // 导出 → 执行 → 导入
}
```

- `img.FallbackCount` 是**该实例累计**的回退次数（成功不归零）；`Adaptive` 命中阈值后切一次，之后不再是原生后端，也就不再计数。每帧新建 `FacadeImage` 意味着计数天然按帧复位。
- `TryResize` 失败时，不迁移整张图，只让回退后端算一次，再把结果 `ImportPixels` 回原后端的新帧（保持后端不变）。
- `Encode` 失败时，同样是一次性临时迁移，不改变 `img` 的后端。
- `Decode` 失败时，回退后端解码 → `Create` 原生帧 → `ImportPixels`。

### 5.4 编解码回退

- 后端支持目标格式就原生编码；否则临时迁移到软后端，用 `Sdcb.SimdCodecs 2.0` 的 `SimdCodecs.Encode(...)`。
- 2.0 内置编码器：PNG / JPEG / TIFF / WebP / GIF / BMP / ICO / QOI / PNM（以 `SimdCodecs.IsEncodable(format)` 为准）。所以软后端不再只有 PNG/JPEG，绝大多数输出都不需要原生后端。
- **JPEG/BMP 背景合成**：门面负责，先合成再交给后端编码，避免各后端自行处理透明通道。

### 5.5 注册软后端与所有权

```csharp
// ImageFacade.Software 里的模块初始化器
[ModuleInitializer]
internal static void Register() => SoftwareFallback.Register(() => new SoftwareBackend());
```

- `ImageEngine` 构造时**不**创建软后端；第一次需要回退时通过注册的工厂懒创建 `SoftwareBackend`，`Dispose` 时释放。
- 没有引用 `ImageFacade.Software` 就没有回退；`FeatureNotSupportedException` 的消息里明确提示"添加 `ImageFacade.Software` 或显式设置 `FallbackBackend`"。
- `ImageEngineOptions.FallbackBackend` 显式指定时优先用调用方的实例，且**不由引擎释放**。
- 主后端就是 `SoftwareBackend` 时，不再创建第二个实例，回退目标就是它自己（`Try*` 全支持，不会回退）。
- 后端实现无状态（字体缓存除外，用 `ConcurrentDictionary`）；`ImageEngine` 可跨线程共享，`FacadeImage` 不可。

---

### 5.6 异步（async）

**原则：只在"等得起"的地方异步，其余一律同步。**

| 操作 | 形态 | 理由 |
|---|---|---|
| `LoadAsync` / `SaveAsync` / `EncodeAsync` | `Task` / `Task<T>` + `CancellationToken` | 文件/网络 I/O + 编解码，几十到几百毫秒 |
| `Draw*` / `Fill*` / `Resize` / `MeasureText` / `CopyPixelsTo` | 只保留同步 | 单次微秒级，进线程池的调度开销大于工作本身 |

**两级实现（"库里没有异步就默认 `Task.Run`"）**

1. 后端可选实现 `IAsyncImageBackend`：原生库给了异步 API、或后端能异步分块时才实现；
2. 没实现的后端**零改动**：门面用 `AsyncBridge.Run(...)`（本质 `Task.Run`）把同步实现搬到线程池。

```csharp
public interface IAsyncImageBackend : IImageBackend
{
    Task<DecodedImage> DecodeAsync(ReadOnlyMemory<byte> data, DecodeRequest request, CancellationToken cancellationToken);
    Task EncodeAsync(IFrame frame, Stream destination, EncodeRequest request, CancellationToken cancellationToken);
}

public readonly struct DecodedImage
{
    public IFrame Frame { get; }
    public ImageMetadata Metadata { get; }
}
```

**取消语义**
- I/O 段（`File.ReadAllBytesAsync` / `CopyToAsync` / `WriteAllBytesAsync`）是真取消；
- 解码/编码：真异步后端自行贯穿令牌；走 `Task.Run` 回退时**只能在开始前取消**（CPU 密集的同步解码无法中途打断），XML 注释里已写明这一点。

**约定**
- 公开 API 一律返回 `Task`（不用 `ValueTask`，避免"只能 await 一次"的坑），内部统一 `ConfigureAwait(false)`；
- 不提供 `IAsyncDisposable`：`FacadeImage` 只持有内存，`Dispose` 是同步且必须同步执行；
- 后端自己有异步能力时，不要在实现里再套一层 `Task.Run`（否则线程池套线程池）。

## 6. 各后端实现

### 6.1 SoftwareBackend（独立包：回退基准 + 可独立使用）

依赖：**ImgProcSharp Core 源码复制进 `ImageFacade.Software`**（Apache-2.0，锁定 0.1.0 快照）+ `Sdcb.SimdCodecs` **2.0.0**（NuGet）。核心包不含这些。

| 能力 | 实现 |
|---|---|
| 帧 | 复制后的 `Canvas`（预乘 Rgba32）+ 一个 `DrawingContext` |
| 解码 | 继承 `ImageBackendBase.Decode`：`ImageDecoder.Open(data)` → `GetFrame(0).CopyPixels(null, PixelFormat.Rgba32, dest, stride)` → `ImportPixels` 预乘进 `Canvas` |
| 编码 | `Canvas.CopyToStraight` → `ImageBackendBase.Encode`：`SimdCodecs.Encode(pixels, w, h, PixelFormat.Rgba32, format, stride, options)`（PNG/JPEG/TIFF/WebP/GIF/BMP/ICO/QOI/PNM） |
| Resize | `Canvas.Resize(...)`（六模式都有，但门面只调它的 Stretch） |
| 画点/线/形状 | `DrawingContext.Fill/Draw` + `CirclePolygon`/`RectangularPolygon`/`Polygon`/`PathBuilder` |
| 文字 | `TextFont.Load(核心内置字体字节)` + `TextDrawing.DrawText`（基线锚点） |
| 像素 | `CopyToStraight` / 自己的 premultiply 循环导入；`TryWrapPixels` 返回 `false`（`Canvas` 只接受 `byte[]`） |

要点：

- 复制范围：ImgProcSharp 的 **Core 工程**（`Canvas`/`Drawing`/`Text`/`Rasterization`/`Resampling`/`Paths`/`Primitives`/`Brushes`/`Pens`/`Colors`）；**不复制**它的字体资源、`ImgProcSharp.SimdCodecs` 门面与 `Image` 类型（解码/编码由 `ImageBackendBase` 统一做）。
- 改造：命名空间改为 `ImageFacade.Software.Rendering`，全部类型 `internal`；删除它自己的 csproj/打包属性/内嵌字体资源；字体从核心的公开 `BuiltInFont.Data` 取，整个解决方案只有一份字体资源。
- ImgProcSharp 的 `Canvas` 是预乘，规范像素是直通，**边界各一次转换**；导出用 `CopyToStraight`（内部不透明时走 memcpy）。
- 软后端是**所有其他后端的回退目标**，因此它必须覆盖 v1 的全部能力位。
- 视频场景下软后端的优势是"无原生依赖 + AOT"，劣势是 `Canvas` 是托管数组、每帧进 LOH；帧池（misc §S.6）优先在软后端落地。
- 源码同步：以 ImgProcSharp 0.1.0 为基线，记录上游 commit/tag；上游改动用 diff 手动合并（misc §Y）。
- Sdcb 2.0 的 float/HDR 图像没有整数投影（`CopyPixels` 会抛）：v1 明确抛 `NotSupportedException` 并提示先做 tone/stretch，不静默转 8 位。

### 6.2 SkiaBackend

依赖：`SkiaSharp` **4.155.0**（当前包 `ImageFacade.SkiaSharp`）与 **3.119.1**（`ImageFacade.SkiaSharp3`），共享同一份源码 + 少量 `#if` shim（见 §6.5）。

| 能力 | 实现 |
|---|---|
| 帧 | `SKBitmap`（`Rgba8888` + 预乘 alpha，栅格）+ 复用的 `SKCanvas` |
| 解码 | `SKCodec.Create(SKData)`；`EncodedOrigin` 取方向；解码到目标 `SKImageInfo`；`SKCodec.IccProfile` 取 ICC |
| 编码 | `SKImage.Encode(SKEncodedImageFormat, quality)` / `SKPixmap.Encode`；PNG 压缩级别用 `SKPngEncoderOptions` |
| Resize | `SKBitmap.Resize(info, new SKSamplingOptions(...))`；Cubic 用 `SKCubicResampler.Mitchell/CatmullRom` |
| 绘制 | `SKCanvas.DrawCircle/DrawLine/DrawPath/DrawBitmap`，`SKPaint` + `SKFont` |
| 文字 | `SKTypeface.FromData`（内置）/`FromFile`/`FromFamilyName`；`SKFont.MeasureText` 单行测量 |
| 像素 | `SKPixmap.ReadPixels(dstInfo, ptr, rowBytes)` 做 premul→unpremul 转换；导入走 premultiply 循环 |

要点：

- 内部保留预乘，**只在跨边界时转直通**，绘制热路径不做转换。
- OpenCV 那种整数坐标问题不存在，但 Skia 的 corner 坐标与门面一致，不需要偏移。
- 不存在原生字体时的兜底：内置字体字节 → `SKTypeface.FromData`，因此 CJK 开箱可用。
- `SKCanvas` 的 `Save/Restore` 用于隔离每次绘制设置的 `SKPaint` 状态。
- 无原生库（Linux 缺 libfontconfig 等）时，构造 `SkiaBackend` 即抛 `ImageBackendException`，消息带安装提示。
- **视频叠加的首选后端**：绘制/文字/alpha 都是原生，1080p 画 100 个框 + 标签在个位数毫秒；`FromPixels` 用 `Bgr24` 时核心做一次通道重排，之后全在原生位图上画。
- 3.x 与 4.x 的差异集中在 `SKBitmap.Resize` 的重载、`SKSamplingOptions`/`SKCubicResampler` 的构造与少数已废弃 API；用 `#if SKIA3` 隔离，API 主体不复制两遍。

### 6.3 ImageSharpBackend

依赖：`SixLabors.ImageSharp` **3.1.12 + Drawing 2.1.3**（`ImageFacade.ImageSharp`）；老版本 **2.1.13 + Drawing 1.0.0**（`ImageFacade.ImageSharp2`）。两个适配包，源码部分共享（见 §6.5）。

> 实测更正（M0）：`ImageSharp.Drawing` **3.x 依赖 ImageSharp 4.x**（3.1.3 → `ImageSharp >= 4.1.3`），与 ImageSharp 3.1.x 配对的是 **Drawing 2.1.3**（→ `ImageSharp >= 3.1.4`）。所以"当前 = ImageSharp 3.x"这一档必须用 Drawing 2.x；若要支持 ImageSharp 4，需要再加一个 `ImageSharp4` 适配包（Drawing 3.x）。

| 能力 | 实现 |
|---|---|
| 帧 | `Image<Rgba32>` |
| 解码 | `Image.Load<Rgba32>(data)`；`AutoOrient()` 处理器；`Image.DetectFormat` 取格式 |
| 编码 | `Save(stream, new PngEncoder{CompressionLevel=...})` / `JpegEncoder{Quality}` / `WebpEncoder` |
| Resize | `Mutate(x => x.Resize(new ResizeOptions{ Size, Mode, Sampler }))`（门面只调 Stretch） |
| 绘制 | `Mutate(x => x.Paint(canvas => canvas.DrawLine(pen, a, b)))`；形状统一走 `PathBuilder` 以避免依赖 `DrawingCanvas` 的重载细节 |
| 文字 | `SixLabors.Fonts`：`SystemFonts.CreateFont` / `FontCollection.Add(流)`；`RichTextOptions` + `canvas.DrawText` |
| 像素 | `DangerousTryGetSinglePixelMemory`（失败则整图复制） |

要点：

- `ImageSharp.Drawing` 3.x 的绘制入口是 `Paint(canvas => ...)`，具体重载在 M5 逐个核对；不确定的统一用 `PathBuilder` + `Draw/Fill`。
- 没有 `SystemFonts` 的环境（容器）必须自备字体；门面默认用内置字体，所以不需要系统字体也能跑。
- **许可证**：ImageSharp 2.0+ 是 Six Labors Split License，不是 Apache-2.0。本仓库只做 `PackageReference`，不打包、不转发源码；README 显著提示使用方自行确认授权。
- 定位：纯托管、格式全，适合服务端批处理；`Mutate` 每帧分配较多，高频视频叠加不如 Skia，但可以作为无 native 依赖的替代。
- **2.x 适配差异**：常规 API 基本一致（`Image.Load<Rgba32>`、`Mutate`、`Resize`、`AutoOrient`），但 Drawing 1.x 的文字是 `ctx.DrawText(TextGraphicsOptions, ...)`（不是 3.x 的 `DrawingCanvas`/`RichTextOptions`），像素访问是 `TryGetSinglePixelSpan`（不是 `DangerousTryGetSinglePixelMemory`）。这两块用 `#if IMAGESHARP2` 单独实现，其余共享。

### 6.4 OpenCvBackend

依赖：`OpenCvSharp4` **4.13.0.20261107**（`ImageFacade.OpenCvSharp`）与 `OpenCvSharp5` **5.0.0.20261109**（`ImageFacade.OpenCvSharp5`），各自配对应的 runtime 包；共享源码 + `#if OPENCV5`（见 §6.5）。

| 能力 | 实现 |
|---|---|
| 帧 | `Mat`（`CV_8UC4`，BGRA，直通） |
| 解码 | `Cv2.ImDecode(bytes, ImreadModes.Unchanged | ImreadModes.IgnoreOrientation)` → 3 通道转 BGRA；方向用 `SimdCodecs.TryReadInfo` 读，再 `Cv2.Rotate/Flip` 自己转 |
| 编码 | `Cv2.ImEncode(".png"/".jpg"/".webp", mat, out byte[], new ImageEncodingParam(ImwriteFlags.JpegQuality, q))` |
| Resize | `Cv2.Resize(mat, dst, new Size(w, h), 0, 0, InterpolationFlags)` |
| 绘制 | `Cv2.Line/Circle/Rectangle/Ellipse/Polylines/FillPoly`，`LineTypes.AntiAlias` |
| 文字 | 默认**不使用** `Cv2.PutText`（Hershey 字体、仅 ASCII、外观与常规文字差别大）→ 直接返回 `false` 走软后端。`OpenCvBackendOptions.NativeAsciiText = true` 时才启用 |
| 像素 | `mat.Data` + `Step()` 逐行拷贝；BGRA ↔ RGBA 通道交换 |

要点：

- **不做 alpha 混合**：`color.A < 255` 直接 `false`。这是 OpenCV 作为"绘制后端"最大的短板，也是回退机制存在的首要理由。
- 坐标量化：门面 corner 坐标 → `x - 0.5` → `Round`；能用 `shift` 时优先保留 1/16 精度（M6 验证）。
- `imdecode` 是否自动应用 EXIF 方向需要在 M6 用带方向的 JPEG 实测，并把结论写进 misc §G；解码时显式带 `IgnoreOrientation` 以避免双重旋转。
- 原生依赖必须按 RID 部署（`OpenCvSharp4.runtime.win` / `.runtime.ubuntu.22.04-x64` 等），不支持 NativeAOT。
- `Mat` 不是线程安全的共享对象；每个 `FacadeImage` 独占一个 `Mat`。
- **视频场景的定位**：如果采集/AI 前处理已经全在 OpenCV（`Mat` BGR），用它能省一次通道转换（`FromPixels(..., PixelFormat.Bgr24)`）；但叠加中文标签/半透明底会触发回退，整帧画很多标签时把 `Fallback` 设为 `Adaptive`，或直接用软后端/ Skia 负责绘制、OpenCV 只做前处理。
- **4.x vs 5.x**：`Mat`/`Cv2` 主体不变，差异在 OpenCV 5 的枚举/常量（部分旧 flag 被移除或改名）与 `ImEncode`/`ImDecode` 的可用格式。用 `#if OPENCV4` / `#if OPENCV5` 隔离常量与少量重载，M6 用实际包逐个编译核对。

### 6.5 多主版本适配（SkiaSharp 3/4、OpenCvSharp 4/5、ImageSharp 2/3）

**为什么必须是多个包**：一个 .NET 程序集只能绑定一个 `SkiaSharp` / `OpenCvSharp` / `ImageSharp` 版本，`Assembly.Load` 不接受"3 或 4 都行"；绑定重定向不跨平台、也不解决 API 差异。因此每个主版本一个适配包，共享源码消除重复实现。

```
src/adapters/SkiaShared/*.cs                 # 共享实现（90%+ 代码）
src/ImageFacade.SkiaSharp/                   # SkiaSharp 4.155.0，DefineConstants=SKIA4
src/ImageFacade.SkiaSharp3/                  # SkiaSharp 3.119.1，DefineConstants=SKIA3
src/adapters/OpenCvShared/*.cs
src/ImageFacade.OpenCvSharp/                 # OpenCvSharp4 4.13，OPENCV4
src/ImageFacade.OpenCvSharp5/                # OpenCvSharp5 5.0，OPENCV5
src/adapters/ImageSharpShared/*.cs
src/ImageFacade.ImageSharp/                  # ImageSharp 3.1 + Drawing 3.1，IMAGESHARP3
src/ImageFacade.ImageSharp2/                 # ImageSharp 2.1 + Drawing 1.0，IMAGESHARP2
```

做法：

- 共享源码目录**不是工程**，只放 `.cs`；每个适配 csproj 用
  `<Compile Include="..\adapters\SkiaShared\**\*.cs" Exclude="...\bin\**;...\obj\**" Link="Shared\%(RecursiveDir)%(Filename)%(Extension)" />` 编进自己的程序集。绝不写 `ProjectReference` 到共享目录（那会变成引用 dll）。
- 差异只允许出现在 `ApiCompat.*.cs` 这样的 partial 文件里（或用 `#if SKIA3` 包住几行），主体逻辑只有一份。每次升级主版本先编译，把报错点收敛进 shim。
- 已知差异点：
  - Skia：`SKBitmap.Resize` 重载、`SKSamplingOptions`/`SKCubicResampler` 构造、`SKCodec`/`SKImage.Encode` 的选项类型。
  - OpenCV：`ImreadModes`/`InterpolationFlags`/`ImwriteFlags` 常量、`ImEncode` 支持格式、`Mat` 与 `MatType` 命名。
  - ImageSharp：Drawing 1.x 的 `DrawText(TextGraphicsOptions, ...)` vs 3.x 的 `DrawingCanvas.DrawText(RichTextOptions, ...)`；像素访问 `TryGetSinglePixelSpan` vs `DangerousTryGetSinglePixelMemory`；Fonts 版本不同。
- `ImageFacade.ImageSharp2` 的文字/像素两块允许**分叉实现**（不强行 `#if` 挤在一个方法里），其余（加载/保存/Resize/绘制路径）共享。
- 包命名：无后缀 = 当前主版本（SkiaSharp 4、OpenCvSharp 4、ImageSharp 3）；带数字 = 指定主版本（`SkiaSharp3`、`OpenCvSharp5`、`ImageSharp2`）。八个包同版本号发布，README 给一张"包 ↔ 三方库版本"对照表。
- **CPM 冲突**：`ManagePackageVersionsCentrally` 默认一个包只能有一个版本。核心与"当前版本"适配包用中央版本；老/新主版本适配 csproj 用 `<PackageReference Include="SkiaSharp" VersionOverride="3.119.1" />`（或该工程单独 `ManagePackageVersionsCentrally=false`），不要试图在 `Directory.Packages.props` 里写两个同名条目。
- 测试工程同样一个版本一个（测试项目也会绑定版本）：`tests/ImageFacade.Conformance.Skia4.Tests`、`.Skia3.Tests`、`.OpenCv4.Tests`、`.OpenCv5.Tests`、`.ImageSharp3.Tests`、`.ImageSharp2.Tests`、`.Software.Tests`，共享同一份测试源码。
- 初步只跑 **win-x64**：CI 矩阵只有一个 OS；Linux/macOS 只保证"代码可编译"，不作为 v1 验收项。

### 6.6 新增一个后端

1. 新建 `ImageFacade.Xxx` 工程，引用 `ImageFacade`。
2. 实现 `IFrame`（包一个原生句柄）。
3. 继承 `ImageBackendBase`（免费获得 PNG/JPEG 加载保存），实现 `Create`/`ImportPixels`/`TryCopyPixels`；或直接实现 `IImageBackend` 用原生 codec。
4. 在一致性测试的 `BackendFactory` 里加一行，跑通"只解码/只编码"的用例。
5. 按需逐个实现 `Try*`，每实现一个就删掉测试里的对应 skip。

不需要改核心包的任何代码（这是 SPI 设计的目标）。

---

## 7. 仓库结构与工程配置

```
f:/code/ImageFacade/
  ImageFacade.slnx
  Directory.Build.props            # Nullable / ImplicitUsings / LangVersion / 分析器
  Directory.Packages.props         # 集中包版本（CPM；旧主版本用 VersionOverride）
  LICENSE  README.md  plan.md  misc.md
  src/
    ImageFacade/                   # net8.0;net10.0，轻量核心（无编解码依赖）
      Abstractions/{Rgba32,PointF,Size,SizeF,RectF,PixelFormat,ImageFormat}.cs
      Backends/{IImageBackend,IFrame,BackendCapabilities,PixelLease,*Spec}.cs
      Drawing/{StrokeStyle,LineCap,LineJoin}.cs
      Text/{TextStyle,FontRef,TextAnchor,TextAlign,TextLayout}.cs
      Options/{LoadOptions,SaveOptions,ResizeOptions,ResizeMode,Sampler}.cs
      Pixels/PixelConverter.cs
      Pipeline/{Letterbox,Nms,Tensorizer,TensorBuffer,Detection,DetectionStyle,DetectionDrawing}.cs
      FacadeImage.cs  ImageEngine.cs  ImageEngineOptions.cs
      Internal/{Router,PixelBridge,ResizePlanner,TextLayouter,SoftwareFallback}.cs
      Fonts/WenQuanYiMicroHei.ttf.gz
    ImageFacade.Software/          # 独立包：Sdcb 2.0 + 复制改造的 ImgProcSharp Core
      Defaults/ImageBackendBase.cs        # 只有加载/保存的抽象实现
      SoftwareBackend.cs                  # 继承 Base + 渲染
      Rendering/**                        # 复制并改造的 ImgProcSharp Core（internal）
      ImageFacade.Software.csproj
  src/adapters/
    SkiaShared/*.cs                # Skia 适配共享源码
    OpenCvShared/*.cs
    ImageSharpShared/*.cs
  src/
    ImageFacade.SkiaSharp/         # SkiaSharp 4.155.0（当前）
    ImageFacade.SkiaSharp3/        # SkiaSharp 3.119.1
    ImageFacade.OpenCvSharp/       # OpenCvSharp4 4.13（当前）
    ImageFacade.OpenCvSharp5/      # OpenCvSharp5 5.0
    ImageFacade.ImageSharp/        # ImageSharp 3.1 + Drawing 3.1（当前）
    ImageFacade.ImageSharp2/       # ImageSharp 2.1 + Drawing 1.0
  tests/
    ImageFacade.Tests/             # 路由/回退/排版/Resize/NMS/letterbox（FakeBackend）
    Conformance.Shared/*.cs         # 一致性测试源码（被各测试工程 Link）
    ImageFacade.Conformance.Software.Tests/
    ImageFacade.Conformance.Skia4.Tests/
    ImageFacade.Conformance.Skia3.Tests/
    ImageFacade.Conformance.OpenCv4.Tests/
    ImageFacade.Conformance.OpenCv5.Tests/
    ImageFacade.Conformance.ImageSharp3.Tests/
    ImageFacade.Conformance.ImageSharp2.Tests/
  samples/
    ImageFacade.Samples/           # 当前版本四后端对比 + BGR24 帧 → 检测 → Bgra32 流水线
  benchmarks/
    ImageFacade.Benchmarks/        # BenchmarkDotNet
```

工程公共属性：

```xml
<PropertyGroup>
  <TargetFrameworks>net8.0;net10.0</TargetFrameworks>   <!-- ImageFacade.Software 例外：见下 -->
  <LangVersion>12.0</LangVersion>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <IsAotCompatible>true</IsAotCompatible>   <!-- 核心/软/Skia；ImageSharp/OpenCV 视情况关闭 -->
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <NoWarn>$(NoWarn);CS1591</NoWarn>
</PropertyGroup>
```

- **目标框架**：只支持 `net8.0` 与 `net10.0`，不提供 `net9.0`；且"目标库支持才保留 net8"——`Sdcb.SimdCodecs 2.0.0` 只有 `net10.0` 资产，因此 **`ImageFacade.Software` 单目标 `net10.0`**，核心与六个适配包仍是双目标。
- 包 ID（8 个）：`ImageFacade`、`ImageFacade.Software`、`ImageFacade.SkiaSharp`、`ImageFacade.SkiaSharp3`、`ImageFacade.OpenCvSharp`、`ImageFacade.OpenCvSharp5`、`ImageFacade.ImageSharp`、`ImageFacade.ImageSharp2`。
- 版本从 `0.1.0` 起；核心包、Software 与所有适配包同版本号，`Directory.Build.props` 里统一 `VersionPrefix`。
- `ImageFacade` 核心：只依赖 BCL + 内置字体，不含任何编解码库或渲染源码。
- `ImageFacade.Software`：依赖核心 + `Sdcb.SimdCodecs` 2.0.0，内含复制改造的 ImgProcSharp Core。
- 六个适配包：只引用核心包，并各自引用对应版本的三方库；适配包之间互不引用。
- 内置字体通过 `EmbeddedResource` + `LogicalName` 进核心包；ImgProcSharp 与文泉驿的许可证/NOTICE 放 `licenses/` 与 `THIRD-PARTY-NOTICES.txt`。

---

## 8. 里程碑

垂直切片优先：先让"同一段代码在软后端和 Skia 上跑通"，再补另外两个后端。

| # | 里程碑 | 内容 | 工时 |
|---|---|---|---|
| M0 | 骨架 | 仓库结构、props、8 个 csproj（核心 + Software + 6 适配）、共享源码链接、slnx、`dotnet build` 双目标通过；空后端 + `Hello` 用例 | 0.5d |
| M1 | 契约与引擎 | `Rgba32`/几何/格式/选项/`PixelFormat`；`IImageBackend`/`IFrame`；`ImageEngine`/`FacadeImage`；路由与四种回退模式（`FakeBackend` 验证）；`ResizePlanner`；`TextLayouter`；`PixelConverter`；单测 | 3.5d |
| M2 | Software 独立包 | 新建 `ImageFacade.Software`；复制 ImgProcSharp Core 源码、命名空间/可见性/字体改造；`ImageBackendBase`（Sdcb 2.0 解码/编码）；`SoftwareBackend : ImageBackendBase` 全能力；EXIF 方向；`ModuleInitializer` 注册回退；端到端测试 | 3.5d |
| M3 | Skia 3/4 适配 | 共享源码 + `SKIA3` shim；SkiaSharp 4.155 与 3.119 各编一遍；像素边界转换；原生字体 + 内置字体；错误信息 | 3.5d |
| M4 | 一致性套件 | 共享测试源码 + 每版本一个测试工程；容差与 skip 规则；`OnFallback` 断言 | 1.5d |
| M5 | ImageSharp 3/2 适配 | 3.1 + Drawing 3.1 与 2.1 + Drawing 1.0；Drawing 1.x 文字/像素分叉实现；字体集合；许可证提示 | 3d |
| M6 | OpenCV 4/5 适配 | 共享源码 + `OPENCV5` shim；BGRA/坐标/方向/alpha 四类适配；`PutText` 策略与实测 | 3.5d |
| M7 | 流水线默认件 | `Letterbox`、`Tensorizer`/`TensorBuffer`、`Nms`、`Detection`/`DrawDetections`；BGR/BGRA 路径；帧级基准 | 3d |
| M8 | 打磨 | 文字折行/对齐/锚点的跨后端校准；JPEG 背景合成；`Strict`/诊断；异常消息 | 1.5d |
| M9 | 示例与打包 | 当前版本四后端对比 + 流水线示例；8 包 pack 检查；README、benchmark | 2.5d |

合计 **26 人天**。关键路径：M0 → M1 → M2 → M3 → M4；M5、M6 需要 M4 的用例先立起来；M7 只依赖 M1，可与 M2/M3 并行。多主版本适配的额外成本主要落在 M3/M5/M6 的"第二遍编译 + shim 收敛"。

**测试范围**：v1 只要求 **win-x64** 全部通过；Linux/macOS 只保证核心与纯托管适配包可编译。

**DoD（每个后端）**：一致性测试里该后端的能力位全部覆盖（不支持的有明确 skip + 理由），`dotnet build` 0 warning，示例能出图，README 有该后端的安装说明（含原生依赖）。

---

## 9. 验收标准

### 功能

- 同一份示例代码（只替换 `new XxxBackend()`）在 `ImageFacade.Software` + 当前版本三后端上各生成一张同尺寸 PNG（示例为 800×450）；主要几何元素（线、点、矩形、文字墨迹）包围盒偏差 ≤ 1 px。
- 六个适配包（Skia3/4、OpenCV4/5、ImageSharp2/3）各自的一致性测试在 win-x64 全绿；旧版本包与当前版本包的公共 API 完全一致（用 `PublicAPI` 清单比对）。
- `Load → Resize → DrawLine → DrawPoint → DrawText → Save` 全链路在七个后端上无异常。
- OpenCV 画中文、画半透明线：自动回退到 `ImageFacade.Software`，`OnFallback` 能观察到；`Fallback = Disabled` 时抛 `FeatureNotSupportedException` 且消息可读。
- Skia/ImageSharp 在没有系统字体的容器里，用 `FontRef.BuiltIn` 能画出中文（内置字体生效）。
- JPEG 保存一张带透明像素的图：透明处为 `BackgroundColor`（默认白），不是黑色。
- `MeasureText` 返回的尺寸与 `DrawText` 实际墨迹包围盒偏差 ≤ 2 px（同一后端）。
- `Resize` 六种模式输出尺寸严格符合定义；纯色图缩放后颜色误差 ≤ 1/255。
- `LoadAsync` / `SaveAsync` / `EncodeAsync` 与同步版本结果逐字节一致；预先取消的令牌在**任何后端上**都抛 `OperationCanceledException`；实现了 `IAsyncImageBackend` 的后端被真正调用（用假后端计数验证）；未实现的后端确认工作发生在线程池线程上（`Thread.CurrentThread.IsThreadPoolThread`）。

### 场景（视频帧流水线）

- `FromPixels(Bgr24, stride != width*3)` → `ToTensor(640×640, NCHW, RGB)` → 100 个模拟检测框 → `Nms.Suppress` → `DrawDetections` → `CopyPixelsTo(Bgra32)`，七个后端各跑 30 帧无异常。
- `LetterboxInfo.ToSource` 反算：在 1920×1080 源图上放一个已知矩形，经 640×640 letterbox 再反算，误差 ≤ 1 px。
- 张量正确性：源图与模型同比例（无补边）时，纯色 640×640 输入经 `NCHW/RGB/÷255` 后首、中、尾元素值精确匹配（±1e-6）；BGR 顺序与 RGB 互换后 R/B 通道对调。补边场景单独断言填充区域等于 `PadColor`。
- NMS：两个同类框 IoU=0.8、阈值 0.45 → 保留 1 个（分数高的）；不同类型且 `classAware` → 保留 2 个；`classAware=false` → 保留 1 个；正反顺序输入结果一致；`Select` 返回的下标可还原原数组对象。
- Letterbox 帮助函数：640×480 → 640×640 时 `Scale=1`、`PadTop=80`；1280×720 → 640×640 时 `Scale=0.5`、`PadTop=140`；返回图尺寸正确、源图不变。
- Avalonia 输出：不透明帧 `Bgra32` 拷贝后与 `CopyPixels()` 逐字节一致；`Bgra32Premultiplied` 在 `a=128` 时每通道误差 ≤ 1。
- ffmpeg 输出：`linesize > width*bpp` 时只写有效像素，填充字节不被改写。
- 回退：OpenCV 后端画 100 个带半透明标签底的检测框，`FallbackMode.Adaptive` 下 `OnFallback` 次数 ≤ 3（一次迁移），且结果与软后端的墨迹包围盒一致。
- 稳定态：连续 30 帧、尺寸不变、字体已热，Skia/OpenCV 后端除 `TensorBuffer` 池化外无新增 LOH 分配（用 `GC.GetAllocatedBytesForCurrentThread` 断言上界）；软后端每帧 `Canvas` 仍是托管数组，等 v1.1 帧池。

### 一致性与数值

- 纯色填充：所有后端中心像素逐字节相同；边界 1 px 内允许差异。
- 水平/垂直线宽 4 的线：内部行整行命中颜色，外侧 2 px 外无墨迹（±1 px）。
- 半透明白底红字/红线：混合结果与标量公式误差 ≤ 2/255。
- Resize 缩放渐变图：与软后端结果的 MAE ≤ 3/255（Lanczos3），Bilinear ≤ 2/255（不同实现差异）。
- 软后端与 `PreferSoftwareRendering = true` 的任意适配后端：同输入同输出（逐字节）；六个适配包都跑这一条。

### 工程

- `dotnet build` 双目标 0 warning；核心包 `dotnet pack` 里没有 SkiaSharp/OpenCV/ImageSharp 的 dll，只有自己的 dll + 字体 + 复制源码编出的类型。
- 包依赖检查：`ImageFacade` 不依赖任何编解码/图形库；`ImageFacade.Software` 依赖 `Sdcb.SimdCodecs` 2.0；六个适配包只依赖核心包 + 各自版本的三方库。
- 在独立空项目里分别 `PackageReference` 八个包，各能编译并跑通示例（防止共享源码漏文件）。
- 原生库缺失时抛 `ImageBackendException`（含安装提示），不是 `DllNotFoundException` 裸栈。
- 复制源码的命名空间/可见性检查：`ImageFacade.Software.dll` 的公开 API 里不出现 `ImageFacade.Software.Rendering.*`（全部 internal），核心 `ImageFacade.dll` 里根本没有这些类型。

### 健壮性

- 0 宽/0 高、超大尺寸（`Width * Height * 4 > int.MaxValue`）抛 `ArgumentException`。
- 空文本、`MaxWidth <= 0`、`LineHeight < 0`、`DashPattern` 含 0：按文档行为处理，不崩。
- 坐标为 NaN/Inf 的绘制请求：跳过该次绘制（不抛、不写坏像素）。

---

## 10. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| 各后端语义漂移（坐标、alpha、字体） | "同一套接口"名不副实 | 语义写死在门面层 + 一致性测试 + 每项差异记录在 misc |
| 回退性能悬崖（每 op 两次全图拷贝） | 中文文字密集场景变慢 | 视频场景用 `Adaptive`（累计 3 次后整帧切换）；文档给出 `UseSoftware`/`SwitchAndStay`；`OnFallback` 可监控 |
| OpenCV 不支持 alpha / 无 CJK 文字 / 坐标量化 | 功能缺失、精度损失 | 回退软后端；坐标损失写进已知差异；`NativeAsciiText` 默认关闭 |
| 每帧 LOH 分配（张量 / 帧缓冲） | 30–60 fps 下 GC 抖动 | `TensorBuffer`（ArrayPool）v1 就有；帧池 v1.1；稳定态用 `GC.GetAllocatedBytesForCurrentThread` 断言 |
| Avalonia 预乘 / stride 不匹配 | 颜色发暗、花屏 | `CopyPixelsTo` 显式 `PixelFormat` + `stride`；`Bgra32Premultiplied`；示例对拍 |
| ffmpeg `linesize` 对齐/负值 | 画面撕裂/倒置 | 显式 stride；负值由适配层翻转；测试覆盖带填充的 stride |
| `Adaptive` 误切到软后端 | 该帧后续变慢 | 阈值可配；`OnFallback` 可观测；每帧新实例自动复位，不会跨帧粘滞 |
| ImageSharp 许可证 | 商用合规 | 不打包其 dll，仅依赖；README 显著提示；软后端/Skia 作为替代 |
| 多主版本维护（Skia 3/4、OpenCV 4/5、ImageSharp 2/3） | 每个版本都要编译/测试，shim 腐化 | 共享源码 + 每版本一个薄 shim + 每版本一个测试工程；CI 全量跑；`PublicAPI` 比对 |
| ImgProcSharp 源码复制后与上游漂移 | 修 bug 要手动合并 | 锁定 0.1.0 快照并记录 commit；改动集中在 `ImageFacade.Software/Rendering/`；misc §Y 维护清单 |
| CPM 一个包只能一个版本 | 新旧主版本无法共存于 `Directory.Packages.props` | 旧主版本适配工程用 `VersionOverride` 或关闭 CPM（§6.5） |
| `Sdcb.SimdCodecs` 2.0 API 较新 | `ImageBackendBase` 的解码/编码桥写错就整条链路失败 | 只在这一个文件里接触 Sdcb；锁 2.0.0；一致性测试覆盖 8/16 位、带 alpha、EXIF/ICC |
| 包体积（Software 携带渲染源码 + Sdcb） | 想只用 Skia 的用户被迫多装 | Software 是独立包，不引用就不进依赖图；核心包保持轻量 |
| 原生依赖部署（Skia native assets、OpenCV RID 包） | 部署失败 | 每个后端 README 写清 RID 包；构造后端时做一次自检；v1 只验 win-x64 |
| 后端 API 变动（SkiaSharp 4.x、ImageSharp.Drawing 3.x、OpenCV 5） | 实现返工 | 差异收敛在 `ApiCompat.*.cs`；核心不受影响；升级主版本先编译再改 shim |

---

## 11. 打包与发布检查单

1. 八个包同版本 `0.1.0`，`PackageLicenseExpression=Apache-2.0`（本仓库），第三方依赖的许可证在 README/`THIRD-PARTY-NOTICES.txt` 里说明。
2. `ImageFacade` 包内：1 个 dll + 字体资源，无编解码依赖；`ImageFacade.Software` 包内另有复制源码与 `Sdcb.SimdCodecs` 2.0 依赖。
3. 六个适配包只依赖 NuGet 上的原始包（不含源码内嵌）；共享源码只存在于本仓库，不进包。
4. 每个包带自己的 README（安装、依赖版本、原生依赖、能力表、许可证提示）。
5. 发布前在独立空项目按 RID 各做一次真实 `PackageReference` 验证，尤其要验证两个 Skia/ImageSharp/OpenCV 主版本不会同时被拖进来。
6. `ImageFacade.Software` 单独验证：`PublishAot=true` 能发布并跑通软后端示例（Sdcb 2.0 + 复制源码都必须是 AOT 友好的）；核心包本身无编解码依赖。
7. NuGet 包 ID 与三方库版本的对照表写进 README，避免用户装错 `SkiaSharp3`/`SkiaSharp`。

---

## 12. 性能

v1 只定"不拖后腿"的底线，不做大规模优化：

- 原生路径每操作开销 ≤ 直接调用后端的 5%（不产生额外全图拷贝）。
- 回退路径每次：1080p 导出 + 导入 ≤ 12 ms（通道重排 / 预乘 / memcpy 各约 1–3 ms）。
- `AcquirePixels` 在 Skia/OpenCV/软后端上应命中零拷贝；ImageSharp 允许内部复制。
- **视频帧预算**（1080p，100 个检测框 + 标签，不含 YOLO 推理，开发机实测目标）：
  - Skia：导入 BGR24 + 叠加 + 导出 Bgra32 ≤ 8 ms/帧（30 fps 有余量）。
  - 软后端：同口径 ≤ 15 ms/帧。
  - OpenCV：原生绘制 ≤ 5 ms，但触发回退后整帧 ≤ 20 ms；建议这类场景用 Skia 或 `UseSoftware`。
  - letterbox + 张量化（640×640，NCHW/RGB）≤ 5 ms（软后端同口径）。
- BenchmarkDotNet 覆盖：`ImageFacade.Software` + 六个适配包的 `Load` / `Resize` / `DrawLine×1000` / `DrawText` / `Encode`，外加"回退一次""整帧流水线""NMS/letterbox"的对照项。
- 这些数字在 M9 用真实基准校正，届时把结论回填到 misc §N；不在计划里写死未测量的绝对值。
