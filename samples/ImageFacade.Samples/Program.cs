using ImageFacade;
using ImageFacade.Backends;
using ImageFacade.SkiaSharp;
using ImageFacade.SdcbSimdCodecs;

// M3 冒烟：同一份绘制代码，只替换后端，验证语义一致（plan §9 功能验收第一条）。
Directory.CreateDirectory("artifacts");

await RunAsync(new SoftwareBackend(), "software");
await RunAsync(new SkiaBackend(), "skia");

Console.WriteLine("ok");

static async Task RunAsync(IImageBackend backend, string label)
{
    using var engine = new ImageEngine(backend, new ImageEngineOptions
    {
        Fallback = FallbackMode.Disabled,   // 缺能力就报错，别掩盖问题
    });

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
