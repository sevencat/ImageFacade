using ImageFacade;
using ImageFacade.Backends;
using ImageFacade.Pipeline;
using ImageFacade.SkiaSharp;
using ImageFacade.SdcbSimdCodecs;

// M3 冒烟：同一份绘制代码，只替换后端，验证语义一致（plan §9 功能验收第一条）。
Directory.CreateDirectory("artifacts");

await RunAsync(new SoftwareBackend(), "software");
await RunAsync(new SkiaBackend(), "skia");

// M7 流水线：letterbox -> 张量 -> 模拟推理 -> NMS -> 反算回源图 -> 画框。
RunPipeline(new SkiaBackend(), "skia");

Console.WriteLine("ok");

static async Task RunAsync(IImageBackend backend, string label)
{
    using var engine = new ImageEngine(backend);

    using var image = engine.Create(800, 450, Rgba32.White);

    Console.WriteLine($"--- {label} ({engine.Backend.Version}) ---");
    Console.WriteLine($"backend    = {image.BackendName}");
    Console.WriteLine($"caps       = {engine.Backend.Capabilities}");

    image
        .FillRectangle(new RectF(40, 40, 240, 120), new Rgba32(0x20, 0x60, 0xC0))
        .DrawRectangle(new RectF(40, 40, 240, 120), Rgba32.Black, width: 3)
        .DrawLine(new PointF(320, 40), new PointF(760, 160), Rgba32.Red, width: 4)
        .DrawPoint(new PointF(560, 300), Rgba32.Orange, radius: 24)
        .DrawEllipse(new RectF(320, 200, 200, 120), Rgba32.Green, width: 4);

    var titleStyle = new TextStyle
    {
        Font = FontRef.BuiltIn,
        Size = 36,
        Color = new Rgba32(0x1A, 0x1A, 0x1A),
        MaxWidth = 720,
        Align = TextAlign.Left,
    };

    const string title = "你好 ImageFacade：内置字体可画中文";
    SizeF measured = image.MeasureText(title, titleStyle);
    Console.WriteLine($"measure    = {measured}");

    image.DrawText(title, new PointF(40, 360), titleStyle);

    byte[] pixels = image.CopyPixels();
    int ink = 0;
    for (int i = 0; i < pixels.Length; i += 4)
    {
        if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
        {
            ink++;
        }
    }

    Console.WriteLine($"ink pixels = {ink:N0}");
    Console.WriteLine($"pixel      = {image.GetPixel(50, 50)}");

    image.Resize(new ResizeOptions
    {
        Width = 400,
        Height = 225,
        Mode = ResizeMode.Pad,
        PadColor = new Rgba32(114, 114, 114),
    });

    string png = Path.Combine("artifacts", $"demo-{label}.png");
    string jpg = Path.Combine("artifacts", $"demo-{label}.jpg");
    await image.SaveAsync(png);
    await image.SaveAsync(jpg, new SaveOptions { Quality = 92 });

    using FacadeImage reloaded = await engine.LoadAsync(png);
    Console.WriteLine($"saved      = {png}, {jpg}");
    Console.WriteLine($"reload     = {reloaded.Width}x{reloaded.Height} {reloaded.SourceFormat}");
}

// 典型的 YOLO 式用法：一帧进来，letterbox 成模型输入，拿到框后再画回原帧。
// 这里刻意写成同步方法：张量化要传 Span<float>，而 C# 12 的 async 方法里不能有 ref struct 局部变量。
static void RunPipeline(IImageBackend backend, string label)
{
    using var engine = new ImageEngine(backend);

    // 1) 造一张 1280x720 的"视频帧"：两个色块当作目标。
    using var frame = engine.Create(1280, 720, Rgba32.White);
    frame.FillRectangle(new RectF(120, 180, 260, 420), Rgba32.FromHex(0x2E86DE))
         .FillRectangle(new RectF(760, 240, 300, 360), Rgba32.FromHex(0xEE5253));

    // 2) letterbox 到 640x640。源图保持 1280x720 不变，拿到的是新图 + 反算几何。
    using FacadeImage padded = frame.Apply(640, 640, out LetterboxInfo info);
    Console.WriteLine($"[{label}] letterbox  = {info.SourceSize} -> {info.ModelSize}, scale={info.Scale:0.###}, pad=({info.PadLeft},{info.PadTop})");

    // 3) 张量化：NCHW + RGB + 归一到 0..1；float 缓冲从池里租，避免每帧进 LOH。
    var tensorOptions = new TensorOptions { Width = 640, Height = 640 };
    using TensorBuffer tensor = TensorBuffer.Rent(tensorOptions.Width * tensorOptions.Height * 3);
    frame.ToTensor(tensorOptions, tensor.Span, out _);

    float min = float.MaxValue;
    float max = float.MinValue;
    foreach (float value in tensor.Span)
    {
        if (value < min) { min = value; }
        if (value > max) { max = value; }
    }

    Console.WriteLine($"[{label}] tensor     = {tensor.Length:N0} floats, range=[{min:0.###}, {max:0.###}]");

    // 4) 假装模型吐了 3 个框（模型坐标系）：两个 person 互相重叠，一个 car 独立。
    Detection[] detections =
    [
        new Detection(new RectF(60, 230, 130, 210), "person", 0.94f) { ClassId = 0 },
        new Detection(new RectF(66, 236, 130, 210), "person", 0.81f) { ClassId = 0 },
        new Detection(new RectF(380, 260, 150, 180), "car", 0.77f) { ClassId = 2 },
    ];

    int kept = Nms.Suppress(detections.AsSpan(), iouThreshold: 0.45f);
    Console.WriteLine($"[{label}] nms        = {detections.Length} -> {kept}");

    // 5) 模型坐标 -> 源图坐标，然后画回**源帧**。这一步是最容易做错的：
    //    除以 scale、减去 pad，两个都要做，顺序不能反。
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

    string output = Path.Combine("artifacts", $"pipeline-{label}.png");
    frame.Save(output);
    Console.WriteLine($"[{label}] saved      = {output}");
}
