using Avalonia;
using SimdSkia.Avalonia;

namespace ImageFacade.AvaloniaDemo;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 无 UI 自检：用于在无图形会话的机器上确认 FFmpeg 原生库与解码链路
        if (args.Length >= 2 && args[0] == "--decode-check")
        {
            return DecodeCheck.Run(args[1]);
        }

        return AppBuilder.Configure<App>()
            // 用 SimdSkia 的桌面宿主替换 Avalonia.Desktop：窗口、字体、排版、绘图全部走托管实现。
            // 不显式 UseCpu()：Windows 上默认走 Direct2D 交换链，CPU 仅作回退。
            .UseSimdSkiaDesktop()
            .StartWithClassicDesktopLifetime(args);
    }
}
