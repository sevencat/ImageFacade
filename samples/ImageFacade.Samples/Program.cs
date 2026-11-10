using ImageFacade;
using ImageFacade.Software;

// M2 冒烟：软后端（复制改造的 ImgProcSharp 渲染层）走完整绘制链路。
using var engine = new ImageEngine(new SoftwareBackend(), new ImageEngineOptions
{
    Fallback = FallbackMode.Disabled,   // 主后端就是软后端，缺能力会直接报错
});

using var image = engine.Create(800, 450, Rgba32.White);

Console.WriteLine($"backend    = {image.BackendName} {engine.Backend.Version}");
Console.WriteLine($"size       = {image.Width}x{image.Height}");
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

SizeF measured = image.MeasureText("你好 ImageFacade：软后端内置字体可画中文", titleStyle);
Console.WriteLine($"measure    = {measured}");
image.DrawText("你好 ImageFacade：软后端内置字体可画中文", new PointF(40, 360), titleStyle);

int ink = 0;
byte[] pixels = image.CopyPixels();
for (int i = 0; i < pixels.Length; i += 4)
{
    if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
    {
        ink++;
    }
}

Console.WriteLine($"ink pixels = {ink:N0}");
Console.WriteLine($"pixel(50,50)  = {image.GetPixel(50, 50)}");
Console.WriteLine($"pixel(700,300) = {image.GetPixel(700, 300)}");

image.Resize(new ResizeOptions { Width = 400, Height = 225, Mode = ResizeMode.Pad, PadColor = new Rgba32(114, 114, 114) });
Console.WriteLine($"after pad  = {image.Width}x{image.Height}, corner = {image.GetPixel(0, 0)}");

Directory.CreateDirectory("artifacts");
await image.SaveAsync(Path.Combine("artifacts", "demo.png"));
await image.SaveAsync(Path.Combine("artifacts", "demo.jpg"), new SaveOptions { Quality = 92 });
Console.WriteLine("saved      = artifacts/demo.png, artifacts/demo.jpg (async)");

using FacadeImage reloaded = await engine.LoadAsync(Path.Combine("artifacts", "demo.png"));
Console.WriteLine($"reload     = {reloaded.Width}x{reloaded.Height} {reloaded.SourceFormat}");

Console.WriteLine("ok");
