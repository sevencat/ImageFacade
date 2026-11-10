<div align="center">

# ImageFacade

**一套与后端无关的图像处理 API**。换一行构造代码，就能在 SkiaSharp / OpenCvSharp / ImageSharp / 纯托管软渲染之间切换，其余代码一个字都不用改。

<p>
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade"><img alt="NuGet" src="https://img.shields.io/nuget/v/Sevencat.ImageFacade?label=nuget"></a>
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade"><img alt="Downloads" src="https://img.shields.io/nuget/dt/Sevencat.ImageFacade"></a>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-net8.0%20%7C%20net10.0-512BD4">
  <img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-blue">
  <img alt="Tests" src="https://img.shields.io/badge/tests-200%20passed-brightgreen">
  <img alt="Backends" src="https://img.shields.io/badge/backends-4-9cf">
</p>

<p>
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade">Sevencat.ImageFacade</a> ·
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade.SdcbSimdCodecs">SdcbSimdCodecs</a> ·
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade.SkiaSharp">SkiaSharp</a> ·
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade.ImageSharp">ImageSharp</a> ·
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade.ImageSharp2">ImageSharp2</a> ·
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade.OpenCvSharp">OpenCvSharp</a> ·
  <a href="https://www.nuget.org/packages/Sevencat.ImageFacade.OpenCvSharp5">OpenCvSharp5</a>
</p>

</div>

![Resize 六模式对比](docs/images/resize-modes.png)

---

## 为什么

图像处理的库很多，但它们的 API 互不兼容：换一个后端，`Resize`、画笔、文字排版、像素布局全都要重写一遍。项目一旦因为许可证、平台、部署形态（容器 / AOT / 无原生依赖）不得不换库，改动面通常是整个业务层。

ImageFacade 把这层差异收在 `IImageBackend` 后面：

- **调用方只依赖一个门面**：`ImageEngine` + `FacadeImage`。
- **后端互不兜底**：支持的能力原生执行，不支持的直接抛 `FeatureNotSupportedException`（带后端名与能力名）。没有隐藏的性能悬崖，也没有"看起来一样其实不一样"的输出。
- **六个工程覆盖三个主版本组合**：SkiaSharp 4、OpenCvSharp 4/5、ImageSharp 2/3，一份源码，不复制实现。
- **中文开箱可用**：核心包内置文泉驿微米黑，容器 / CI / NativeAOT 环境下不需要准备任何字体。

## 特性

| | |
|---|---|
| **绘制** | 点、线、折线、矩形、椭圆、多边形，描边与填充；虚线；半透明叠加 |
| **文字** | 字号 / 颜色 / 字体；左中右对齐；`MaxWidth` 自动换行；`MeasureText` 与绘制使用同一套排版 |
| **缩放** | `Stretch` / `Max` / `Min` / `Crop` / `Pad` / `BoxPad` 六种模式，一次性完成"缩放 + 补边 / 裁切" |
| **像素互操作** | `Bgra32` / `Bgr24` / `Rgb24` / `Gray8` 直接进出，支持任意 `stride`，不强制行连续 |
| **原生句柄** | OpenCV 后端可拿底层 `Mat`：`SharedMat()` 共享、`SubMat()` 取 ROI 视图、`CloneMat()` 深拷贝 |
| **检测流水线** | letterbox + 坐标反算、NCHW/NHWC 张量化、池化缓冲、NMS、检测框与标签绘制 |
| **异步** | 加载 / 保存 / 编码有 `Async` 版本；绘制保持同步（微秒级，包一层 Task 只会更慢） |
| **AOT** | 核心与软后端声明 `IsAotCompatible`，无反射、无动态代码生成 |

## 安装

七个包都已发布到 [nuget.org](https://www.nuget.org/packages/Sevencat.ImageFacade)，当前版本 `0.2.0`。

```xml
<!-- 核心包永远要引，再按需加一个后端 -->
<PackageReference Include="Sevencat.ImageFacade" Version="0.2.0" />
<PackageReference Include="Sevencat.ImageFacade.SkiaSharp" Version="0.2.0" />
```

后端包已带上对应三方库的依赖，不需要重复声明 `SkiaSharp` / `OpenCvSharp4` / `SixLabors.ImageSharp`。

| 包 | 后端类 | 三方库 | 说明 |
|---|---|---|---|
| [`Sevencat.ImageFacade`](https://www.nuget.org/packages/Sevencat.ImageFacade) | — | 无 | 核心：契约 + 路由 + 排版 + 内置字体 + Pipeline |
| [`Sevencat.ImageFacade.SdcbSimdCodecs`](https://www.nuget.org/packages/Sevencat.ImageFacade.SdcbSimdCodecs) | `SoftwareBackend` | `Sdcb.SimdCodecs` 2.0 | 纯托管，无原生依赖，**仅 `net10.0`** |
| [`Sevencat.ImageFacade.SkiaSharp`](https://www.nuget.org/packages/Sevencat.ImageFacade.SkiaSharp) | `SkiaBackend` | `SkiaSharp` 3.119.1 | 编译目标 3.x，**可提升到 4.x**（4.x 兼容 3.x） |
| [`Sevencat.ImageFacade.ImageSharp`](https://www.nuget.org/packages/Sevencat.ImageFacade.ImageSharp) | `ImageSharpBackend` | `ImageSharp` 3.1 | 纯托管，格式最全 |
| [`Sevencat.ImageFacade.ImageSharp2`](https://www.nuget.org/packages/Sevencat.ImageFacade.ImageSharp2) | `ImageSharpBackend` | `ImageSharp` 2.1 | 同上，2.x 分支 |
| [`Sevencat.ImageFacade.OpenCvSharp`](https://www.nuget.org/packages/Sevencat.ImageFacade.OpenCvSharp) | `OpenCvBackend` | `OpenCvSharp4` 4.13 | 内部 BGRA，API 层统一 RGBA |
| [`Sevencat.ImageFacade.OpenCvSharp5`](https://www.nuget.org/packages/Sevencat.ImageFacade.OpenCvSharp5) | `OpenCvBackend` | `OpenCvSharp5` 5.0 | 同上，5.x 分支 |

无后缀 = 当前主版本；带数字 = 指定的另一主版本。**同一进程只能绑定一个三方库主版本**，所以 `Sevencat.ImageFacade.ImageSharp` / `...ImageSharp2` 只能二选一。

原生库按 RID 由应用侧引用，适配包不钉死 RID：

```xml
<PackageReference Include="SkiaSharp.NativeAssets.Win32" Version="3.119.1" />
<!-- OpenCV 必须设 RuntimeIdentifier -->
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<PackageReference Include="OpenCvSharp4.runtime.win" Version="4.13.0.20261107" />
```

## 30 秒上手

```csharp
using ImageFacade;
using ImageFacade.SkiaSharp;

using var engine = new ImageEngine(new SkiaBackend());
using var image  = engine.Load("in.jpg", new LoadOptions { AutoOrient = true });

image.Resize(new ResizeOptions
     {
         Width = 800, Height = 600,
         Mode = ResizeMode.Max,
         Sampler = Sampler.Lanczos3,
     })
     .DrawLine(new PointF(20, 20), new PointF(300, 20), Rgba32.Red, width: 3)
     .DrawText("你好 ImageFacade", new PointF(40, 240), new TextStyle
     {
         Font = FontRef.BuiltIn,
         Size = 36,
         Color = Rgba32.FromHex(0x1A1A1A),
         MaxWidth = 720,
         Align = TextAlign.Left,
     });

image.Save("out.png", new SaveOptions { CompressionLevel = 6 });
```

换后端只改一行：

```csharp
// using ImageFacade.OpenCvSharp; / using ImageFacade.SdcbSimdCodecs;
using var engine = new ImageEngine(new OpenCvBackend());
using var engine = new ImageEngine(new SoftwareBackend());    // 纯托管，无原生依赖
```

## 截图

**同一段代码，四个后端**（software / skia / imagesharp / opencv）：

![四后端对比](docs/images/backend-comparison.png)

| 文字排版 | 检测流水线 |
|---|---|
| ![文字排版](docs/images/text-layout.png) | ![检测流水线](docs/images/pipeline-detection.png) |

| 水印叠加 | 基础绘制 |
|---|---|
| ![水印](docs/images/watermark.png) | ![基础绘制](docs/images/basic-skia.png) |

| OpenCV Mat 互操作 |
|---|
| ![OpenCV Mat 互操作](docs/images/opencv-interop.png) |

以上图片全部由 `samples/ImageFacade.Samples` 生成（`dotnet run` 输出到 `docs/images/`），可以直接改代码看效果。检测流水线那张里的**虚线框是 NMS 抑制掉的候选框**，实线是保留的。

## Resize 六模式

上图（顶部那张）就是同一张 800×300 源图跑六种模式的结果：

| 模式 | 行为 | 输出尺寸 |
|---|---|---|
| `Stretch` | 直接拉伸，不保持比例 | 目标尺寸 |
| `Max` | 等比缩放到**完全放进**目标框 | 缩放后尺寸 |
| `Min` | 等比缩放到**覆盖**目标框 | 缩放后尺寸 |
| `Crop` | 等比铺满后居中裁切 | 目标尺寸 |
| `Pad` | 等比放进后补边 | 目标尺寸 |
| `BoxPad` | 先裁到目标比例再缩放，四角对齐 | 目标尺寸 |

`Max` / `Min` 的命名沿用 .NET 与 SixLabors 的习惯：`Max` 是"最大尺寸不超过目标框"。

## 检测流水线

`ImageFacade.Pipeline` 在核心包内，不依赖任何推理库：它只负责"把帧变成模型输入"和"把模型输出画回帧"，中间跑什么模型由你决定。

```csharp
using ImageFacade;
using ImageFacade.Pipeline;

// 1) letterbox：源图不变，返回新图 + 反算几何
using FacadeImage padded = frame.Apply(640, 640, out LetterboxInfo info);
// 1280x720 -> 640x640：info.Scale = 0.5，info.PadTop = 140

// 2) 张量化：NCHW + RGB + 归一到 0..1，写入池化缓冲
var options = new TensorOptions { Width = 640, Height = 640 };
using TensorBuffer tensor = TensorBuffer.Rent(640 * 640 * 3);
frame.ToTensor(options, tensor.Span, out _);
// tensor.Memory 可直接交给 onnxruntime

// 3) 假设模型返回了模型坐标下的框
Span<Detection> detections = /* 从模型输出解码 */;

// 4) NMS：原地抑制，返回保留数量
int kept = Nms.Suppress(detections, iouThreshold: 0.45f);

// 5) 反算回源图坐标，再画到**源帧**上
var onSource = new Detection[kept];
for (int i = 0; i < kept; i++)
{
    Detection detection = detections[i];
    onSource[i] = new Detection(info.ToSource(detection.Box), detection.Label, detection.Score)
    {
        ClassId = detection.ClassId,
    };
}

frame.DrawDetections(onSource);
```

要点：

- `Apply` **不改源图**，所以画框可以直接画在源帧上，坐标不用二次换算。
- `LetterboxInfo.ToSource` 是唯一的反算入口；手写 `(x - pad) / scale` 容易把顺序写反。
- 张量缓冲用 `TensorBuffer.Rent` 池化：640×640×3 是 4.9 MB，每帧新分配会直接进 LOH。
- `Nms.Suppress` 原地重排，有效段是 `detections[..kept]`，结果与输入顺序无关；`Nms.Select` 返回原数组下标。
- `DrawDetections` 只是默认排版，标签宽度走 `MeasureText`，与当前后端自洽；要别的排版就自己循环 `DrawRectangle` + `FillRectangle` + `DrawText`。
- OpenCV 后端不支持半透明形状：`DetectionStyle.LabelBackground` 默认的 `(0,0,0,160)` 会抛 `FeatureNotSupportedException`，需要改成不透明色。

## 视频帧：原生内存进出

```csharp
// 指针来自 ffmpeg 的 avFrame->data[0] 与 linesize[0]（或相机缓冲），不先转 RGBA。
IntPtr pixels = frameDataPtr;
int    stride = linesize;

using var frame = engine.FromPixels(pixels, width, height, stride, PixelFormat.Bgr24);

frame.DrawRectangle(new RectF(100, 80, 220, 160), Rgba32.Lime, width: 2);

// 直接写进 Avalonia 的 WriteableBitmap 锁帧内存
frame.CopyPixelsTo(fb.Address, fb.RowBytes, PixelFormat.Bgra32);
```

`stride` 由调用方给出，按行处理、不要求连续；格式缺省是 `Rgba32`，`Bgr24` / `Bgra32` / `Rgb24` / `Gray8` 需显式指定。

> **关于通道顺序**：门面层没有"这个图是 RGB 还是 BGR"的概念，`Rgba32`、`PixelFormat.Rgba32`（默认）、`GetPixel`、`CopyPixels` 全是 RGBA 语义。后端内部怎么存是它自己的事——OpenCV 用 `CV_8UC4`（BGRA），Skia 用 `Rgba8888 Premul`，都与调用方无关。
>
> 唯一要显式声明顺序的地方是 `FromPixels(..., PixelFormat.Bgr24)` 这类入参：**你告诉门面源缓冲是什么格式**，它负责转换。反过来 `CopyPixels(PixelFormat.Bgr24)` 也一样。
>
> 代价是 OpenCV 后端不做零拷贝包装（BGRA↔RGBA 必须逐行换通道），`FromPixels` 会拷贝一次；Skia / 软后端才能直接接管外部缓冲。

### 像素出入只有一条路

**校验在门面做一次，后端只负责"按 stride 逐行搬运"。**

- `FromPixels` / `CopyPixelsTo` 的尺寸、`bytesPerPixel`、`stride` 下界、缓冲区长度（`(height-1)*stride + rowBytes`，`long` 计算防溢出）全部由门面统一校验；后端 `ImportPixels` / `TryCopyPixels` 拿到的一定是"已验证过"的参数。
- 输入不是 `Rgba32` 时，门面先用 `PixelConverter` 归一成规范 RGBA32（`stride = width * 4`），再交给后端——所以**每个后端只需要实现一种像素布局**，不用为 BGR / BGRA / Gray 各写一套分支。
- OpenCV 后端因此只有一份 `SwapRedBlue`（RGBA32 ↔ BGRA）：通道交换是对合运算，**导出与导入共用同一份实现**，不会出现"两个方向各写一遍、只修了其中一个"的情况，也天然覆盖 `Mat.step > cols*4` 的对齐填充和外部缓冲的非紧凑 stride。

## OpenCV Mat 互操作

OpenCV 后端内部就是一整块 `CV_8UC4` 的 **BGRA** `Mat`。要把它交给 `Cv2`、DNN 或标定代码，不必绕 `CopyPixels` 转一趟缓冲：

```csharp
using ImageFacade.OpenCvSharp;
using OpenCvSharp;

Mat shared = image.SharedMat();                              // 后端保存的 Mat，不复制
Cv2.Rectangle(shared, new Rect(10, 10, 100, 60), new Scalar(0, 0, 255, 255), 3);

using Mat roi = image.SubMat(100, 40, 200, 120);             // ROI 视图，共享像素
Cv2.GaussianBlur(roi, roi, new Size(5, 5), 0);               // 只影响这 200x120

using Mat copy = image.CloneMat();                           // 深拷贝，改它不影响原图
```

| 方法 | 内存 | 用途 |
|---|---|---|
| `SharedMat()` | 共享 | 把整帧交给 OpenCV（`Cv2.*`、DNN 输入、`ImWrite`） |
| `SubMat(x, y, w, h)` / `SubMat(Rect)` | 共享 | 只处理一块区域；写进去就是写原图 |
| `CloneMat()` | 复制 | 要一份能随便改、能脱离图片生命周期的副本 |

- 通道顺序是 **BGRA**：`Scalar` 的 v0/v1/v2 依次是 B/G/R，v3 是 alpha，**不写 v3 会画成透明**。
- `SharedMat` / `SubMat` 返回的 `Mat` 归门面所有：不要 `Dispose`、不要跨线程、图片释放后不要再碰；`CloneMat` 的副本归调用方，自己 `using`。
- 只有 `OpenCvBackend` 的帧能这样拿，其他后端调用抛 `InvalidOperationException`；与后端无关的像素级互操作用 `CopyPixels` / `AcquirePixels`。

![OpenCV Mat 互操作](docs/images/opencv-interop.png)

## 后端能力矩阵

`✓` 原生支持；`✗` 不支持，调用时抛 `FeatureNotSupportedException`（消息含后端名与能力名）。

| 能力 | Skia | ImageSharp | OpenCV | 软后端 |
|---|:---:|:---:|:---:|:---:|
| Resize / 六种模式 | ✓ | ✓ | ✓ | ✓ |
| 点 / 线 / 折线 / 矩形 / 椭圆 / 多边形 | ✓ | ✓ | ✓ | ✓ |
| 半透明形状（`color.A < 255`） | ✓ | ✓ | **✗** | ✓ |
| 虚线（`StrokeStyle.DashPattern`） | ✓ | ✓ | **✗** | ✓ |
| 文字（含中文） | ✓ | ✓ | ✓ | ✓ |
| 半透明文字 | ✓ | ✓ | ✓ | ✓ |
| 解码格式 | PNG/JPEG/BMP/GIF/WebP/ICO | PNG/JPEG/BMP/GIF/WebP/TIFF | PNG/JPEG/BMP/WebP | PNG/JPEG/BMP |
| 编码格式 | PNG/JPEG/WebP | PNG/JPEG/BMP/GIF/WebP/TIFF | PNG/JPEG/BMP/WebP | PNG/JPEG/BMP |
| NativeAOT | 已声明兼容 | 未声明 | 不支持 | 已声明兼容 |
| 原生依赖 | SkiaSharp RID 包 | 无 | OpenCV RID 包 | 无 |

OpenCV 后端没有 alpha 混合与 stroke pattern——这两条限制来自 OpenCV 的 drawing 函数本身，不是适配实现的选择。需要半透明叠加或虚线时换 Skia / ImageSharp / 软后端。

**跨后端不保证逐像素一致**：抗锯齿、字体栅格化、缩放核的实现各不相同。需要确定性输出时把软后端当主后端。

## 中文与字体

- 核心内置文泉驿微米黑（`FontRef.BuiltIn`），**没有系统字体也能画中文**；容器 / CI / AOT 场景不需要额外准备字体。
- 系统字体族（`FontRef.Family(...)`）只有 Skia 与 ImageSharp 支持；OpenCV 与软后端没有系统字体管理器，会退回内置字体。
- 自备字体：`Font.Load*` 拿到实例后塞进 `TextStyle.FontFace`，可跨多次绘制复用。

## 出错时

| 情况 | 异常 |
|---|---|
| 参数非法 | `ArgumentException` / `ArgumentOutOfRangeException` |
| 后端不支持该能力 | `FeatureNotSupportedException`（含后端名、能力名、替代建议） |
| 三方库内部失败 | `ImageBackendException`（保留 `InnerException`，含后端名） |
| 原生库缺失 | `ImageBackendException`（含安装提示，不是裸 `DllNotFoundException`） |

路由只有两档：后端 `Try*` 返回 `true` 就原生执行，返回 `false` 就抛错。**没有跨后端自动回退**，行为在调用点可见。

## 构建与测试

```powershell
dotnet build ImageFacade.slnx
dotnet test  ImageFacade.slnx          # win-x64
dotnet pack  ImageFacade.slnx -c Release -o artifacts/packages
```

- 测试按主版本拆分工程（同一进程只能绑一个三方库版本），OpenCV 测试工程需 `RuntimeIdentifier=win-x64`。
- v1 只在 **win-x64** 验收；Linux/macOS 只保证核心与纯托管后端可编译。
- CI 见 `.github/workflows/ci.yml`：Windows + .NET 10，跑 build → test → pack。

## 项目结构

```
src/
  ImageFacade/                     # 核心：契约 + 路由 + 排版 + 内置字体 + Pipeline
  ImageFacade.SdcbSimdCodecs/      # 软后端：ImgProcSharp 渲染层 + Sdcb.SimdCodecs 编解码
  ImageFacade.SkiaSharp/           # SkiaSharp 3.119（编译目标，兼容 4.x）
  ImageFacade.ImageSharp/          # ImageSharp 3.x + Drawing 2.x
  ImageFacade.ImageSharp2/         # ImageSharp 2.x + Drawing 1.x（链接上一份源码）
  ImageFacade.OpenCvSharp/         # OpenCvSharp4
  ImageFacade.OpenCvSharp5/        # OpenCvSharp5（链接上一份源码）
tests/                             # 一后端一测试工程，共享测试源码
samples/ImageFacade.Samples/       # 示例 + README 截图生成器
docs/images/                       # 截图
```

## 文档

| 文件 | 内容 |
|---|---|
| [plan.md](plan.md) | 设计、公开 API、SPI、里程碑、验收标准、性能目标 |
| [misc.md](misc.md) | 决策记录、许可证矩阵、逐库的坑、测试策略、已否决方案、落地记录（§Z） |
| [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) | 第三方组件与许可证清单 |

## 许可证

本仓库以 **Apache License 2.0** 发布，全文见 [LICENSE](LICENSE)。

第三方组件见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。其中两条必须注意：

1. **SixLabors.ImageSharp** 自 2.0 起是 **Six Labors Split License**（不是 Apache-2.0）。本仓库只做 `PackageReference`，但一旦引用 `Sevencat.ImageFacade.ImageSharp` / `Sevencat.ImageFacade.ImageSharp2`，你的项目就直接依赖它。请自行确认授权条件；不适用时改用 Skia 或软后端。
2. **文泉驿微米黑** 随核心程序集以嵌入资源分发，采用 Apache-2.0 / GPLv3 双许可 + 字体嵌入例外，本仓库走 Apache-2.0 分支。声明见 [licenses/LICENSE-WenQuanYiMicroHei.txt](licenses/LICENSE-WenQuanYiMicroHei.txt)。
