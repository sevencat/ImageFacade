using System.Runtime.InteropServices;
using ImageFacade;
using ImageFacade.Backends;
using ImageFacade.ImageSharp;
using ImageFacade.OpenCvSharp;
using ImageFacade.Pipeline;
using ImageFacade.SkiaSharp;
using ImageFacade.SdcbSimdCodecs;
using Cv2 = OpenCvSharp.Cv2;
using CvInterpolationFlags = OpenCvSharp.InterpolationFlags;
using CvMat = OpenCvSharp.Mat;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;
using CvScalar = OpenCvSharp.Scalar;
using CvSize = OpenCvSharp.Size;

// 示例集：生成 README 用的截图，同时充当跨后端的冒烟测试。
// 输出固定写到仓库根的 docs/images，与当前工作目录无关。
string outputDirectory = Path.Combine(FindRepositoryRoot(), "docs", "images");
Directory.CreateDirectory(outputDirectory);

Console.WriteLine($"output = {outputDirectory}");

// 同一段绘制代码跑四个后端，输出可直接对比。
// engine 的生命周期要盖住后面的拼图步骤，所以在这里创建、最后统一释放。
using var softwareEngine = new ImageEngine(new SoftwareBackend());
using var skiaEngine = new ImageEngine(new SkiaBackend());
using var imageSharpEngine = new ImageEngine(new ImageSharpBackend());
using var openCvEngine = new ImageEngine(new OpenCvBackend());

var basics = new List<FacadeImage>();
try
{
    basics.Add(await RunBasicAsync(softwareEngine, "software", outputDirectory));
    basics.Add(await RunBasicAsync(skiaEngine, "skia", outputDirectory));
    basics.Add(await RunBasicAsync(imageSharpEngine, "imagesharp", outputDirectory));
    basics.Add(await RunBasicAsync(openCvEngine, "opencv", outputDirectory));

    RunBackendComparison(basics, outputDirectory);
}
finally
{
    foreach (FacadeImage image in basics)
    {
        image.Dispose();
    }
}
RunResizeModes(outputDirectory);
RunTextLayout(outputDirectory);
RunWatermark(outputDirectory);
RunPipeline(new SkiaBackend(), "skia", outputDirectory);
RunOpenCvInterop(outputDirectory);

Console.WriteLine("ok");

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "ImageFacade.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

// ---------------------------------------------------------------------------
// 1) 基础绘制：同一段代码跑不同后端
// ---------------------------------------------------------------------------
static async Task<FacadeImage> RunBasicAsync(ImageEngine engine, string label, string outputDirectory)
{
    FacadeImage image = engine.Create(800, 450, Rgba32.White);

    Console.WriteLine($"--- {label} ({engine.Backend.Version}) ---");

    image
        .FillRectangle(new RectF(40, 90, 240, 120), Rgba32.FromHex(0x2060C0))
        .DrawRectangle(new RectF(40, 90, 240, 120), Rgba32.Black, width: 3)
        .DrawLine(new PointF(320, 90), new PointF(760, 210), Rgba32.Red, width: 4)
        .DrawPoint(new PointF(500, 330), Rgba32.Orange, radius: 22)
        .DrawEllipse(new RectF(320, 200, 200, 120), Rgba32.Green, width: 4)
        .FillEllipse(new RectF(600, 260, 120, 120), Rgba32.FromHex(0x8E44AD))
        .FillPolygon(
            [new PointF(80, 240), new PointF(180, 240), new PointF(130, 330)],
            Rgba32.FromHex(0x16A085));

    var titleStyle = new TextStyle
    {
        Font = FontRef.BuiltIn,
        Size = 34,
        Color = Rgba32.FromHex(0x1A1A1A),
        MaxWidth = 720,
        Align = TextAlign.Left,
    };

    const string title = "你好 ImageFacade：内置字体可画中文";
    SizeF measured = image.MeasureText(title, titleStyle);
    Console.WriteLine($"measure    = {measured}");

    image.DrawText(title, new PointF(40, 370), titleStyle);

    string png = Path.Combine(outputDirectory, $"basic-{label}.png");
    await image.SaveAsync(png);
    Console.WriteLine($"saved      = {png}");

    return image;
}

// 四个后端的输出缩到 400x225，拼成 2x2 对比图。
static void RunBackendComparison(List<FacadeImage> basics, string outputDirectory)
{
    string[] labels = ["software", "skia", "imagesharp", "opencv"];
    const int cellWidth = 400;
    const int cellHeight = 225;

    var cells = new FacadeImage[basics.Count];
    try
    {
        for (int i = 0; i < basics.Count; i++)
        {
            FacadeImage cell = basics[i].Clone();
            cell.Resize(new ResizeOptions { Width = cellWidth, Height = cellHeight, Mode = ResizeMode.Stretch });
            cells[i] = cell;
        }

        using var engine = new ImageEngine(new SkiaBackend());
        using FacadeImage comparison = Compose(engine, cells, labels, columns: 2, cellWidth, cellHeight, gap: 10, Rgba32.FromHex(0x1B1B1B));

        string path = Path.Combine(outputDirectory, "backend-comparison.png");
        comparison.Save(path);
        Console.WriteLine($"saved      = {path}");
    }
    finally
    {
        foreach (FacadeImage cell in cells)
        {
            cell?.Dispose();
        }
    }
}

// ---------------------------------------------------------------------------
// 2) Resize 六模式对比：同一张源图，六种算法拼成一张图
// ---------------------------------------------------------------------------
static void RunResizeModes(string outputDirectory)
{
    using var engine = new ImageEngine(new SkiaBackend());
    // 源图 800x300（2.67:1），目标格 320x200（1.6:1），六种模式的差异才看得出来。
    using var source = CreateSampleFrame(engine, withCaption: false);

    const int cellWidth = 320;
    const int cellHeight = 200;
    const int gap = 12;

    // 命名按 .NET / SixLabors 的习惯：Max = 不超过目标框（完整放下），Min = 覆盖目标框。
    (string Name, ResizeMode Mode)[] modes =
    [
        ("Stretch", ResizeMode.Stretch),
        ("Max 完整放下", ResizeMode.Max),
        ("Min 覆盖目标", ResizeMode.Min),
        ("Crop", ResizeMode.Crop),
        ("Pad", ResizeMode.Pad),
        ("BoxPad", ResizeMode.BoxPad),
    ];

    var cells = new FacadeImage[modes.Length];
    var labels = new string[modes.Length];
    try
    {
        for (int i = 0; i < modes.Length; i++)
        {
            (string name, ResizeMode mode) = modes[i];
            FacadeImage cell = source.Clone();
            cell.Resize(new ResizeOptions
            {
                Width = cellWidth,
                Height = cellHeight,
                Mode = mode,
                PadColor = Rgba32.FromHex(0x2B2B2B),
            });

            cells[i] = cell;
            labels[i] = $"{name}  {cell.Width}x{cell.Height}";
        }

        using FacadeImage composite = Compose(engine, cells, labels, columns: 3, cellWidth, cellHeight, gap, Rgba32.FromHex(0x1B1B1B));
        string path = Path.Combine(outputDirectory, "resize-modes.png");
        composite.Save(path);
        Console.WriteLine($"saved      = {path}");
    }
    finally
    {
        foreach (FacadeImage cell in cells)
        {
            cell?.Dispose();
        }
    }
}

// ---------------------------------------------------------------------------
// 3) 文字排版：对齐、最大宽度换行、中英混排、字号
// ---------------------------------------------------------------------------
static void RunTextLayout(string outputDirectory)
{
    using var engine = new ImageEngine(new SkiaBackend());
    using var image = engine.Create(860, 520, Rgba32.White);

    image.FillRectangle(new RectF(0, 0, 860, 8), Rgba32.FromHex(0x2060C0));

    image.DrawText("文字排版 / Text Layout", new PointF(30, 30), new TextStyle
    {
        Font = FontRef.BuiltIn,
        Size = 30,
        Color = Rgba32.FromHex(0x141414),
    });

    // 三种对齐：同一段文字在 800 宽的区域里靠左 / 居中 / 靠右。
    var body = new TextStyle { Font = FontRef.BuiltIn, Size = 18, Color = Rgba32.FromHex(0x333333), MaxWidth = 800 };

    image.DrawText("Align.Left：这一行靠左", new PointF(30, 100), new TextStyle
    {
        Font = body.Font, Size = body.Size, Color = body.Color, MaxWidth = body.MaxWidth, Align = TextAlign.Left,
    });

    image.DrawText("Align.Center：这一行居中", new PointF(30, 140), new TextStyle
    {
        Font = body.Font, Size = body.Size, Color = body.Color, MaxWidth = body.MaxWidth, Align = TextAlign.Center,
    });

    image.DrawText("Align.Right：这一行靠右", new PointF(30, 180), new TextStyle
    {
        Font = body.Font, Size = body.Size, Color = body.Color, MaxWidth = body.MaxWidth, Align = TextAlign.Right,
    });

    // MaxWidth 触发自动换行。
    const string paragraph =
        "MaxWidth 触发的自动换行：ImageFacade 随核心包内置了文泉驿微米黑，" +
        "因此在容器、CI、NativeAOT 这类没有系统字体的环境里，中文也不会变成方块。";
    SizeF paragraphSize = image.MeasureText(paragraph, body);
    image.DrawText(paragraph, new PointF(30, 230), body);

    image.DrawLine(new PointF(30, 230 + paragraphSize.Height + 10), new PointF(830, 230 + paragraphSize.Height + 10),
        Rgba32.FromHex(0xDDDDDD), width: 1);

    // 字号梯度。
    int x = 30;
    foreach (float size in new[] { 14f, 20f, 28f, 40f })
    {
        image.DrawText("Aa 字号", new PointF(x, 400), new TextStyle
        {
            Font = FontRef.BuiltIn,
            Size = size,
            Color = Rgba32.FromHex(0x2060C0),
        });

        x += (int)size * 4;
    }

    image.DrawText($"MeasureText = {paragraphSize.Width:0.#} x {paragraphSize.Height:0.#}", new PointF(30, 470),
        new TextStyle { Font = FontRef.BuiltIn, Size = 13, Color = Rgba32.FromHex(0x888888) });

    string path = Path.Combine(outputDirectory, "text-layout.png");
    image.Save(path);
    Console.WriteLine($"saved      = {path}");
}

// ---------------------------------------------------------------------------
// 4) 水印：右下角半透明条 + 文字
// ---------------------------------------------------------------------------
static void RunWatermark(string outputDirectory)
{
    using var engine = new ImageEngine(new SkiaBackend());
    using var image = CreateSampleFrame(engine);

    const string watermark = "ImageFacade · demo";
    var style = new TextStyle
    {
        Font = FontRef.BuiltIn,
        Size = 22,
        Color = Rgba32.White,
    };

    SizeF size = image.MeasureText(watermark, style);
    float padding = 14f;
    float barWidth = size.Width + (padding * 2f);
    float barHeight = size.Height + (padding * 1.2f);
    float left = image.Width - barWidth - 24f;
    float top = image.Height - barHeight - 24f;

    image.FillRectangle(new RectF(left, top, barWidth, barHeight), new Rgba32(0, 0, 0, 140));
    image.DrawText(watermark, new PointF(left + padding, top + (padding * 0.6f)), style);

    string path = Path.Combine(outputDirectory, "watermark.png");
    image.Save(path);
    Console.WriteLine($"saved      = {path}");
}

// ---------------------------------------------------------------------------
// 5) 检测流水线：letterbox -> 张量 -> 模拟推理 -> NMS -> 反算回源图 -> 画框
// ---------------------------------------------------------------------------
// 刻意写成同步方法：张量化要传 Span<float>，而 C# 12 的 async 方法里不能有 ref struct 局部变量。
static void RunPipeline(IImageBackend backend, string label, string outputDirectory)
{
    using var engine = new ImageEngine(backend);
    using var frame = engine.Create(1280, 720, Rgba32.FromHex(0x14181D));

    // 画点网格，让"视频帧"看起来有内容，也顺便演示半透明线条。
    var grid = new Rgba32(255, 255, 255, 18);
    for (int x = 0; x <= 1280; x += 80)
    {
        frame.DrawLine(new PointF(x, 0), new PointF(x, 720), grid);
    }

    for (int y = 0; y <= 720; y += 80)
    {
        frame.DrawLine(new PointF(0, y), new PointF(1280, y), grid);
    }

    // 色块比框略小，框线才不会和色块糊在一起。
    frame.FillRectangle(new RectF(136, 196, 228, 388), Rgba32.FromHex(0x2E86DE))
         .FillRectangle(new RectF(776, 256, 268, 328), Rgba32.FromHex(0xEE5253))
         .DrawText("simulated frame 1280x720", new PointF(24, 24), new TextStyle
         {
             Font = FontRef.BuiltIn,
             Size = 20,
             Color = new Rgba32(255, 255, 255, 110),
         });

    // letterbox：源图保持 1280x720，返回新图 + 反算几何。
    using FacadeImage padded = frame.Apply(640, 640, out LetterboxInfo info);
    Console.WriteLine($"[{label}] letterbox  = {info.SourceSize} -> {info.ModelSize}, scale={info.Scale:0.###}, pad=({info.PadLeft},{info.PadTop})");

    // 张量化：NCHW + RGB + 归一到 0..1，缓冲从池里租。
    var tensorOptions = new TensorOptions { Width = 640, Height = 640 };
    using TensorBuffer tensor = TensorBuffer.Rent(tensorOptions.Width * tensorOptions.Height * 3);
    frame.ToTensor(tensorOptions, tensor.Span, out _);
    Console.WriteLine($"[{label}] tensor     = {tensor.Length:N0} floats");

    // 假装模型吐了 4 个框（模型坐标系）：两个 person 重叠、两个 car 重叠。
    Detection[] detections =
    [
        new Detection(new RectF(60, 230, 130, 210), "person", 0.94f) { ClassId = 0 },
        new Detection(new RectF(66, 236, 130, 210), "person", 0.81f) { ClassId = 0 },
        new Detection(new RectF(380, 260, 150, 180), "car", 0.77f) { ClassId = 2 },
        new Detection(new RectF(386, 266, 150, 180), "car", 0.71f) { ClassId = 2 },
    ];

    int[] kept = Nms.Select(detections, iouThreshold: 0.45f);
    Console.WriteLine($"[{label}] nms        = {detections.Length} -> {kept.Length}");

    // 被抑制的框用灰色虚线画出来，便于对照"NMS 到底去掉了什么"。
    var keptSet = new HashSet<int>(kept);
    var dashed = new StrokeStyle { DashPattern = [6f, 4f] };
    for (int i = 0; i < detections.Length; i++)
    {
        if (keptSet.Contains(i))
        {
            continue;
        }

        frame.DrawRectangle(info.ToSource(detections[i].Box), new Rgba32(255, 255, 255, 90), width: 1.5f, style: dashed);
    }

    // 模型坐标 -> 源图坐标，再画回源帧。
    var onSource = new Detection[kept.Length];
    for (int i = 0; i < kept.Length; i++)
    {
        Detection detection = detections[kept[i]];
        onSource[i] = new Detection(info.ToSource(detection.Box), detection.Label, detection.Score)
        {
            ClassId = detection.ClassId,
        };
    }

    frame.DrawDetections(onSource);

    string path = Path.Combine(outputDirectory, "pipeline-detection.png");
    frame.Save(path);
    Console.WriteLine($"[{label}] saved      = {path}");
}

// ---------------------------------------------------------------------------
// 6) OpenCV Mat 互操作：SharedMat / SubMat / CloneMat
// ---------------------------------------------------------------------------
// 三种拿法共用一句话：SharedMat 与 SubMat 写进去就是写原图，CloneMat 是独立副本。
static void RunOpenCvInterop(string outputDirectory)
{
    using var engine = new ImageEngine(new OpenCvBackend());
    using FacadeImage image = engine.Create(900, 460, Rgba32.White);

    image.FillRectangle(new RectF(0, 0, 900, 8), Rgba32.FromHex(0x2060C0));

    var title = new TextStyle { Font = FontRef.BuiltIn, Size = 24, Color = Rgba32.FromHex(0x141414) };
    var caption = new TextStyle { Font = FontRef.BuiltIn, Size = 16, Color = Rgba32.FromHex(0x444444) };

    image.DrawText("OpenCV Mat 互操作", new PointF(28, 24), title);

    // ① SharedMat：拿到的就是后端保存的那块 BGRA Mat。Cv2 画什么，这张图就多什么。
    //    Scalar 的 v0..v2 是 B/G/R，v3 是 alpha —— 不写 255 会画成透明。
    CvMat shared = image.SharedMat();
    Cv2.Rectangle(shared, new CvRect(40, 120, 240, 160), new CvScalar(0, 0, 255, 255), thickness: 4);
    Cv2.Circle(shared, new CvPoint(160, 200), 40, new CvScalar(0, 200, 0, 255), thickness: -1);
    Cv2.Line(shared, new CvPoint(40, 320), new CvPoint(280, 320), new CvScalar(255, 0, 0, 255), thickness: 4);

    image.DrawText("① SharedMat → Cv2 直接画在帧上", new PointF(40, 88), caption);
    image.DrawText("② CloneMat → 副本独立，改它原图不变", new PointF(40, 344), caption);
    image.DrawText("③ SubMat → 把副本缩略贴进 ROI，只改这块", new PointF(470, 88), caption);

    // ② CloneMat：深拷贝，在副本上画一圈很粗的红框。
    using CvMat copy = image.CloneMat();
    Cv2.Rectangle(copy, new CvRect(0, 0, copy.Cols, copy.Rows), new CvScalar(0, 0, 255, 255), thickness: 12);

    // ③ SubMat：把副本缩小后贴回右下角。写入的是 ROI，原图其它区域不受影响。
    using var thumb = new CvMat();
    Cv2.Resize(copy, thumb, new CvSize(400, 204), interpolation: CvInterpolationFlags.Area);
    using (CvMat roi = image.SubMat(470, 130, 400, 204))
    {
        thumb.CopyTo(roi);
    }

    image.DrawText("缩略图 = CloneMat 副本（带红框），原图无红框。",
        new PointF(470, 348), caption);

    string path = Path.Combine(outputDirectory, "opencv-interop.png");
    image.Save(path);
    Console.WriteLine($"saved      = {path}");
}

// ---------------------------------------------------------------------------
// 公用：造一张有内容的源图 / 把多张图拼成网格
// ---------------------------------------------------------------------------
static FacadeImage CreateSampleFrame(ImageEngine engine, bool withCaption = true)
{
    FacadeImage image = engine.Create(800, 300, Rgba32.FromHex(0xF5F6FA));

    image.FillRectangle(new RectF(0, 0, 800, 170), Rgba32.FromHex(0x2060C0))
         .FillRectangle(new RectF(0, 170, 800, 130), Rgba32.FromHex(0xE8F0FE));

    image.FillEllipse(new RectF(60, 40, 120, 120), Rgba32.FromHex(0xFFC107))
         .FillPolygon(
             [new PointF(520, 40), new PointF(680, 40), new PointF(600, 155)],
             Rgba32.FromHex(0xFF7043))
         .FillRectangle(new RectF(230, 195, 60, 40), Rgba32.FromHex(0x2ECC71))
         .DrawLine(new PointF(40, 250), new PointF(760, 250), Rgba32.FromHex(0x203040), width: 3);

    image.DrawText("ImageFacade", new PointF(230, 92), new TextStyle
    {
        Font = FontRef.BuiltIn,
        Size = 36,
        Color = Rgba32.White,
    });

    if (withCaption)
    {
        image.DrawText("800 x 300 源图 / source", new PointF(40, 262), new TextStyle
        {
            Font = FontRef.BuiltIn,
            Size = 17,
            Color = Rgba32.FromHex(0x203040),
        });
    }

    return image;
}

static FacadeImage Compose(
    ImageEngine engine,
    FacadeImage[] cells,
    string[] labels,
    int columns,
    int cellWidth,
    int cellHeight,
    int gap,
    Rgba32 background)
{
    int rows = (cells.Length + columns - 1) / columns;
    int width = (columns * cellWidth) + ((columns + 1) * gap);
    int height = (rows * cellHeight) + ((rows + 1) * gap);

    var canvas = new byte[width * height * 4];
    uint packed = (uint)(background.R | (background.G << 8) | (background.B << 16) | (background.A << 24));
    MemoryMarshal.Cast<byte, uint>(canvas.AsSpan()).Fill(packed);

    for (int i = 0; i < cells.Length; i++)
    {
        FacadeImage cell = cells[i];
        int gridX = gap + ((i % columns) * (cellWidth + gap));
        int gridY = gap + ((i / columns) * (cellHeight + gap));

        // 尺寸小于格子时居中留背景，大于格子时裁切——正好展示 Max / Min 的区别。
        // offset 是相对格子左上角的偏移，负数表示图比格子大、左边/上边要裁掉。
        int offsetX = (cellWidth - cell.Width) / 2;
        int offsetY = (cellHeight - cell.Height) / 2;

        int sourceX = Math.Max(0, -offsetX);
        int sourceY = Math.Max(0, -offsetY);
        int targetX = gridX + Math.Max(0, offsetX);
        int targetY = gridY + Math.Max(0, offsetY);

        int copyWidth = Math.Min(cell.Width - sourceX, cellWidth - Math.Max(0, offsetX));
        int copyHeight = Math.Min(cell.Height - sourceY, cellHeight - Math.Max(0, offsetY));
        if (copyWidth <= 0 || copyHeight <= 0)
        {
            continue;
        }

        byte[] pixels = cell.CopyPixels();
        for (int y = 0; y < copyHeight; y++)
        {
            int sourceOffset = ((sourceY + y) * cell.Width * 4) + (sourceX * 4);
            int targetOffset = ((targetY + y) * width * 4) + (targetX * 4);
            pixels.AsSpan(sourceOffset, copyWidth * 4).CopyTo(canvas.AsSpan(targetOffset, copyWidth * 4));
        }
    }

    FacadeImage composite = engine.FromPixels(canvas, width, height, width * 4);

    // 标签画在每格左上角，加深色底，任何底图上都读得清。
    var labelStyle = new TextStyle { Font = FontRef.BuiltIn, Size = 15, Color = Rgba32.White };
    for (int i = 0; i < cells.Length; i++)
    {
        string label = labels[i];
        if (string.IsNullOrEmpty(label))
        {
            continue;
        }

        int gridX = gap + ((i % columns) * (cellWidth + gap));
        int gridY = gap + ((i / columns) * (cellHeight + gap));

        SizeF size = composite.MeasureText(label, labelStyle);
        composite.FillRectangle(new RectF(gridX, gridY, size.Width + 16, size.Height + 10), new Rgba32(0, 0, 0, 190));
        composite.DrawText(label, new PointF(gridX + 8, gridY + 5), labelStyle);
    }

    return composite;
}
