using Avalonia;

namespace ImageFacade.AvaloniaNativeDemo;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return AppBuilder.Configure<App>()
            // 官方后端：Win32 窗口 + Skia 渲染
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
