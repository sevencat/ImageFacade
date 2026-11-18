using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace ImageFacade.AvaloniaDemo;

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --open <文件>       直接放一个视频，省得每次点文件对话框（也方便脚本验证）
            // --shot <png>        渲染若干帧后把窗口截到文件并退出；窗口摆在屏幕外，不抢前台
            // --save-frame <png>  同上，但走"保存图片"按钮的 Sdcb 软后端，存的是纯帧（含 HUD）
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--open");
            string? initialPath = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            int shotIndex = Array.IndexOf(args, "--shot");
            string? shotPath = shotIndex >= 0 && shotIndex + 1 < args.Length ? args[shotIndex + 1] : null;
            int frameIndex = Array.IndexOf(args, "--save-frame");
            string? framePath = frameIndex >= 0 && frameIndex + 1 < args.Length ? args[frameIndex + 1] : null;
            desktop.MainWindow = new MainWindow(initialPath, shotPath, framePath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
