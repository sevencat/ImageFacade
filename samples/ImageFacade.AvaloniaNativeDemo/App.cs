using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace ImageFacade.AvaloniaNativeDemo;

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 与 SimdSkia 示例一致：--open <文件> 直接放一个视频
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--open");
            string? initialPath = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            desktop.MainWindow = new MainWindow(initialPath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
