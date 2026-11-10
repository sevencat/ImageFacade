# ImageFacade

一套与后端无关的图像处理 API：调用方只依赖 `ImageEngine` + `FacadeImage`，换一行构造代码就能在 **SkiaSharp / OpenCvSharp / ImageSharp / 纯托管软渲染** 之间切换，其余代码不变。

面向两个场景：视频帧的检测叠加（BGR24 进、Bgra32 出，稳定态不产生临时大对象），以及服务端批量出图（纯托管、无原生依赖可选用软后端）。

七个包同版本发布到 NuGet，见 [引用方式](#引用方式)。第三方库（SkiaSharp / OpenCvSharp / ImageSharp / Sdcb）一律 `PackageReference` 传递，不随本库打包。

---

## 状态

| 部分 | 状态 |
|---|---|
| 核心契约、路由、Resize 六模式、排版/测量、像素互操作、异步加载保存 | 已实现 |
| `ImageFacade.SdcbSimdCodecs`（软后端） | 已实现（全能力） |
| SkiaSharp 适配（编译目标 3.119.1，可提升到 4.x） | 已实现 |
| ImageSharp 3.1 / 2.1 适配 | 已实现 |
| OpenCvSharp 4.13 / 5.0 适配 | 已实现 |
| `ImageFacade.Pipeline`（letterbox、张量化、NMS、检测框绘制，**在核心包内**，不单独成包） | 已实现 |
| 跨后端一致性套件、benchmark、CI | 未实现 |

---

## 目录结构

```
src/
  ImageFacade/                     # 核心：契约 + 路由 + 排版 + 内置字体，无编解码依赖
  ImageFacade.SdcbSimdCodecs/      # 软后端：ImgProcSharp 渲染层 + Sdcb.SimdCodecs 编解码
  ImageFacade.SkiaSharp/           # SkiaSharp 3.119（编译目标，兼容 4.x）
  ImageFacade.ImageSharp/          # ImageSharp 3.x + Drawing 2.x
  ImageFacade.ImageSharp2/         # ImageSharp 2.x + Drawing 1.x（链接上一份源码）
  ImageFacade.OpenCvSharp/         # OpenCvSharp4
  ImageFacade.OpenCvSharp5/        # OpenCvSharp5（链接上一份源码）
tests/                             # 一后端一测试工程，共享测试源码
samples/ImageFacade.Samples/       # 冒烟示例：同一份绘制代码跑软后端与 Skia
```

---

## 包 ↔ 三方库版本

| 包 | 后端类 | 三方库 |
|---|---|---|
| `ImageFacade` | — | 无（仅 BCL + 内置字体） |
| `ImageFacade.SdcbSimdCodecs` | `SoftwareBackend` | `Sdcb.SimdCodecs` 2.0.0（**仅 `net10.0`**） |
| `ImageFacade.SkiaSharp` | `SkiaBackend` | `SkiaSharp` 3.119.1（可提升到 4.x） |
| `ImageFacade.ImageSharp` | `ImageSharpBackend` | `ImageSharp` 3.1.12 + `Drawing` 2.1.3 |
| `ImageFacade.ImageSharp2` | `ImageSharpBackend` | `ImageSharp` 2.1.13 + `Drawing` 1.0.0 |
| `ImageFacade.OpenCvSharp` | `OpenCvBackend` | `OpenCvSharp4` 4.13.0.20261107 |
| `ImageFacade.OpenCvSharp5` | `OpenCvBackend` | `OpenCvSharp5` 5.0.0.20261109 |

无后缀 = 当前主版本；带数字 = 指定的另一主版本。**同一进程只能绑定一个三方库主版本**，所以 `ImageSharp` / `ImageSharp2` 只能二选一，`OpenCvSharp` / `OpenCvSharp5` 同理。

SkiaSharp 只有一个工程，**编译目标是 3.119.1**，只使用 3.x 就有的 API。4.x 保留了这些成员（`SKPath` 写 API 与 4 参 `DrawBitmap` 只是标记为过时，没有删除），所以：

- 你的项目用 SkiaSharp 3.x → 直接工作；
- 你的项目用 SkiaSharp 4.x → NuGet 版本提升到 4.x，同样工作（已实测 4.155.0）；
- 反过来（库用 4.x 编译、项目锁 3.x）不行。

详见 plan.md 第 6.2 节。

目标框架：`net8.0;net10.0`（`ImageFacade.SdcbSimdCodecs` 因依赖只有 `net10.0` 资产，为单目标 `net10.0`）。

---

## 引用方式

核心包永远要引，再按需要加一个后端包：

```xml
<ItemGroup>
  <PackageReference Include="ImageFacade" Version="0.1.0" />
  <PackageReference Include="ImageFacade.SkiaSharp" Version="0.1.0" />
</ItemGroup>
```

后端包已经带上了对应三方库的依赖，不需要重复声明 `SkiaSharp` / `OpenCvSharp4` / `SixLabors.ImageSharp`。

在本仓库内开发时用 `ProjectReference`：

```xml
<ItemGroup>
  <ProjectReference Include="..\ImageFacade\ImageFacade.csproj" />
  <ProjectReference Include="..\ImageFacade.SkiaSharp\ImageFacade.SkiaSharp.csproj" />
</ItemGroup>
```

原生库按 RID 由**应用侧**引用（适配包不钉死 RID）：

```xml
<!-- Skia -->
<!-- 版本必须与项目解析到的 SkiaSharp 托管包一致：3.119.1 或 4.155.0 -->
<PackageReference Include="SkiaSharp.NativeAssets.Win32" Version="3.119.1" />
<!-- OpenCV：必须设 RuntimeIdentifier -->
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<PackageReference Include="OpenCvSharp4.runtime.win" Version="4.13.0.20261107" />
```

软后端 (`ImageFacade.SdcbSimdCodecs`) 无原生依赖，适合容器、AOT、精简运行时。

---

## 快速开始

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
     .DrawPoint(new PointF(300, 120), Rgba32.Orange, radius: 6)
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
using var engine = new ImageEngine(new OpenCvBackend());      // 其余代码完全不变
using var engine = new ImageEngine(new SoftwareBackend());    // 纯托管，无原生依赖
```

### 视频帧：原生内存进出

```csharp
// 指针来自 ffmpeg 的 avFrame->data[0] 与 avFrame->linesize[0]（或相机缓冲），
// 不先转 RGBA，直接按 BGR24 + stride 包一层。
IntPtr pixels = frameDataPtr;
int    stride = linesize;

using var frame = engine.FromPixels(pixels, width, height, stride, PixelFormat.Bgr24);

frame.DrawRectangle(new RectF(100, 80, 220, 160), Rgba32.Lime, width: 2);

// 直接写进 Avalonia 的 WriteableBitmap 锁帧内存
frame.CopyPixelsTo(fb.Address, fb.RowBytes, PixelFormat.Bgra32);
```

`stride` 由调用方给出，按行处理、不要求连续；格式缺省是 `Rgba32`，`Bgr24` / `Bgra32` / `Rgb24` / `Gray8` 需显式指定。

### 异步

只有 I/O + 编解码有异步版本，绘制与测量保持同步（微秒级）：

```csharp
using var image = await engine.LoadAsync("in.jpg", cancellationToken: ct);
await image.SaveAsync("out.webp", new SaveOptions { Quality = 85 }, ct);
byte[] bytes = await image.EncodeAsync(cancellationToken: ct);
```

---

## 检测流水线

`ImageFacade.Pipeline` 在核心包内，不依赖任何推理库：它只负责"把帧变成模型输入"和"把模型输出画回帧"这两段，中间跑什么模型由你决定。

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
- `Nms.Suppress` 是原地的：返回保留数量，有效段是 `detections[..kept]`，结果与输入顺序无关；`Nms.Select` 则返回原数组下标。
- `DrawDetections` 只是默认排版，标签宽度走 `MeasureText`，因此与当前后端的字体度量自洽；要别的排版就自己循环 `DrawRectangle` + `FillRectangle` + `DrawText`。
- OpenCV 后端不支持半透明形状：`DetectionStyle.LabelBackground` 默认的 `(0,0,0,160)` 会抛 `FeatureNotSupportedException`，需要改成不透明色。

完整可运行的版本在 `samples/ImageFacade.Samples`（`RunPipeline`），输出 `artifacts/pipeline-skia.png`。

## 后端能力矩阵

`✓` 原生支持；`✗` 不支持，调用时抛 `FeatureNotSupportedException`（消息含后端名与能力名）；`—` 不适用。

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

OpenCV 后端没有 alpha 混合与 stroke pattern：需要半透明叠加或虚线时换 Skia / ImageSharp / 软后端，或把颜色改成不透明的。这一限制来自 OpenCV 的 drawing 函数本身，不是适配实现的选择。详见 [misc.md](misc.md) 第 E 节。

**跨后端不保证逐像素一致**：抗锯齿、字体栅格化、缩放核的实现各不相同。需要确定性输出时把软后端当主后端。

---

## 出错时

| 情况 | 异常 |
|---|---|
| 参数非法 | `ArgumentException` / `ArgumentOutOfRangeException` |
| 后端不支持该能力 | `FeatureNotSupportedException`（含后端名、能力名、替代建议） |
| 三方库内部失败 | `ImageBackendException`（保留 `InnerException`，含后端名） |
| 原生库缺失 | `ImageBackendException`（含安装提示，不是裸 `DllNotFoundException`） |

路由只有两档：后端 `Try*` 返回 `true` 就原生执行，返回 `false` 就抛错。**没有跨后端自动回退**，行为在调用点可见。

---

## 中文与字体

- 核心内置文泉驿微米黑（`FontRef.BuiltIn`），**没有系统字体也能画中文**；容器 / CI / AOT 场景不需要额外准备字体。
- 系统字体族（`FontRef.Family(...)`）只有 Skia 与 ImageSharp 支持；OpenCV 与软后端没有系统字体管理器，会退回内置字体。
- 自备字体：`Font.Load*` 拿实例后塞进 `TextStyle.FontFace`，可跨多次绘制复用。
- `MeasureText` 与 `DrawText` 使用同一套排版结果，同一后端内自洽。

---

## 构建与测试

```powershell
dotnet build ImageFacade.slnx
dotnet test  ImageFacade.slnx          # win-x64
```

- 测试按主版本拆分工程（同一进程只能绑一个三方库版本），OpenCV 测试工程需 `RuntimeIdentifier=win-x64`。
- v1 只在 **win-x64** 验收；Linux/macOS 只保证核心与纯托管后端可编译。

---

## 文档

| 文件 | 内容 |
|---|---|
| [plan.md](plan.md) | 设计、公开 API 草案、SPI、里程碑、验收标准、性能目标 |
| [misc.md](misc.md) | 决策记录、许可证矩阵、逐库的坑、测试策略、已否决方案、落地记录（§Z） |
| [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) | 第三方组件与许可证清单 |

---

## 许可证

本仓库以 **Apache License 2.0** 发布，全文见 [LICENSE](LICENSE)。

第三方组件见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。其中两条必须注意：

1. **SixLabors.ImageSharp** 自 2.0 起是 **Six Labors Split License**（不是 Apache-2.0）。本仓库只做 `PackageReference`，但一旦引用 `ImageFacade.ImageSharp` / `ImageFacade.ImageSharp2`，你的项目就直接依赖它。请自行确认授权条件；不适用时改用 Skia 或软后端。
2. **文泉驿微米黑** 随核心程序集以嵌入资源分发，采用 Apache-2.0 / GPLv3 双许可 + 字体嵌入例外，本仓库走 Apache-2.0 分支。声明见 [licenses/LICENSE-WenQuanYiMicroHei.txt](licenses/LICENSE-WenQuanYiMicroHei.txt)。
