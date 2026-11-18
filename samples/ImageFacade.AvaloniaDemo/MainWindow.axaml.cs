using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ImageFacade.AvaloniaDemo;

internal sealed partial class MainWindow : Window
{
    /// <summary>「播放 RTMP 流」按钮打开的测试流（腾讯云直播 demo 源）。</summary>
    private const string RtmpStreamUrl = "rtmp://liteavapp.qcloud.com/live/liteavdemoplayerstreamid";

    private readonly FrameComposer _composer = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

    private VideoPlayer? _player;
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _framesInWindow;
    private double _fps;
    private TimeSpan _lastPosition = TimeSpan.MinValue;

    // 临时诊断：--mem 每 2 秒往 stderr 打一行内存明细
    private readonly bool _traceMemory = Environment.GetCommandLineArgs().Contains("--mem");

    // --shot <png> / --save-frame <png>：攒够若干帧后自动执行并退出，窗口挪到屏幕外，不抢前台
    private readonly string? _shotPath;
    private readonly string? _saveFramePath;
    private int _renderedFrames;

    public MainWindow(string? initialPath = null, string? shotPath = null, string? saveFramePath = null)
    {
        _shotPath = shotPath;
        _saveFramePath = saveFramePath;
        InitializeComponent();

        if (shotPath is not null || saveFramePath is not null)
        {
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-20000, -20000);
        }

        if (FfmpegRuntime.TryInitialize(out string info))
        {
            StatusText.Text = info;
        }
        else
        {
            StatusText.Text = info;
            OpenButton.IsEnabled = false;
            RtmpButton.IsEnabled = false;
        }

        if (initialPath is not null)
        {
            OpenPath(initialPath);
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Render());
        _timer.Start();

        if (_traceMemory)
        {
            new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => DumpMemory()).Start();
        }
    }

    private void DumpMemory()
    {
        using Process self = Process.GetCurrentProcess();
        double mb = 1048576d;
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        ReadOnlySpan<GCGenerationInfo> gens = info.GenerationInfo;
        Console.Error.WriteLine(
            $"[mem] {DateTime.Now:HH:mm:ss} heap={info.HeapSizeBytes / mb:0.0} " +
            $"g0={gens[0].SizeAfterBytes / mb:0.0} g1={gens[1].SizeAfterBytes / mb:0.0} " +
            $"g2={gens[2].SizeAfterBytes / mb:0.0} loh={gens[3].SizeAfterBytes / mb:0.0} " +
            $"ws={self.WorkingSet64 / mb:0.0} priv={self.PrivateMemorySize64 / mb:0.0} " +
            $"alloc={GC.GetTotalAllocatedBytes() / mb:0} " +
            $"gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} fps={_fps:0.0}");
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _player?.Dispose();
        _bitmap?.Dispose();
        _composer.Dispose();
        base.OnClosed(e);
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();

    private void OnRtmpClick(object? sender, RoutedEventArgs e) => OpenPath(RtmpStreamUrl);

    private void OnPauseClick(object? sender, RoutedEventArgs e) => TogglePause();

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveFrameAsync();

    private async Task SaveFrameAsync()
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存当前帧",
            SuggestedFileName = $"frame_{DateTime.Now:HHmmss}.png",
            DefaultExtension = "png",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG") { Patterns = ["*.png"] },
            ],
        });

        if (file is null)
        {
            return;
        }

        try
        {
            // 走 Sdcb 软后端编码；想换成"零编解码依赖"的做法就改调 SaveFrameWithAvalonia
            _composer.SavePng(file.Path.LocalPath);
            StatusText.Text = $"已保存：{file.Path.LocalPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 备选方案，当前<b>没有</b>调用：如果不想引入 <c>ImageFacade.SdcbSimdCodecs</c> 和
    /// <c>Sdcb.SimdCodecs.Png</c>，可以直接用 Avalonia 主包自带的 PNG 编码器把位图写盘——
    /// 项目里一个编解码包都不用引（SimdSkia 后端已实现 <c>Bitmap.Save(Stream)</c>）。
    /// </summary>
    /// <remarks>
    /// 两条路径的差别：<see cref="FrameComposer.SavePng"/> 存的是 <see cref="ImageFacade.FacadeImage"/>
    /// 的像素，能带 <see cref="ImageFacade.SaveOptions"/>（压缩级别 / 质量 / 元数据）和
    /// 后续换成 JPEG、WebP 等格式；这里存的是已经交给 Avalonia 的 <see cref="WriteableBitmap"/>，
    /// 编码完全由渲染后端决定，拿不到那些选项。
    /// </remarks>
    private void SaveFrameWithAvalonia(string path)
    {
        if (_bitmap is not { } bitmap)
        {
            return;
        }

        using FileStream stream = File.Create(path);
        // 带 quality 的新重载 SimdSkia 还没实现，只能走旧签名
#pragma warning disable CS0618
        bitmap.Save(stream);
#pragma warning restore CS0618
    }

    private async Task OpenAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("视频")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.ts", "*.m4v"],
                },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        OpenPath(files[0].Path.LocalPath);
    }

    private void OpenPath(string path)
    {
        _player?.Dispose();
        _player = new VideoPlayer();

        try
        {
            _player.Open(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开失败：{ex.Message}";
            _player.Dispose();
            _player = null;
            return;
        }

        PauseButton.IsEnabled = true;
        PauseButton.Content = "暂停";
        SaveButton.IsEnabled = false;
        _lastPosition = TimeSpan.MinValue;
        StatusText.Text = $"解码中：{DescribeSource(path)}";
    }

    /// <summary>本地文件只显示文件名；<c>rtmp://</c> 之类的流地址没有文件名，按原样显示。</summary>
    private static string DescribeSource(string path) =>
        path.Contains("://", StringComparison.Ordinal) ? path : Path.GetFileName(path);

    private void TogglePause()
    {
        if (_player is null)
        {
            return;
        }

        if (_player.IsPaused)
        {
            _player.Resume();
            PauseButton.Content = "暂停";
        }
        else
        {
            _player.Pause();
            PauseButton.Content = "继续";
        }
    }

    private void Render()
    {
        VideoPlayer? player = _player;
        if (player is null)
        {
            return;
        }

        if (player.Error is { } error)
        {
            StatusText.Text = $"解码错误：{error}";
            player.Dispose();
            _player = null;
            PauseButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            return;
        }

        if (!player.TryTakeFrame(out byte[] pixels, out int width, out int height, out TimeSpan position))
        {
            return;
        }

        try
        {
            EnsureBitmap(width, height);
            using ILockedFramebuffer framebuffer = _bitmap!.Lock();

            // 直通 RGBA32 入 → 画 HUD → 直通 RGBA32 写进 WriteableBitmap（位图本身就是 Rgba8888）
            _composer.Compose(pixels, width, height, framebuffer.Address, framebuffer.RowBytes, position, _fps);

            VideoView.InvalidateVisual();

            if (position != _lastPosition)
            {
                _lastPosition = position;
                _framesInWindow++;
            }

            if (_fpsClock.ElapsedMilliseconds >= 500)
            {
                _fps = _framesInWindow * 1000d / _fpsClock.ElapsedMilliseconds;
                _framesInWindow = 0;
                _fpsClock.Restart();
            }

            StatusText.Text = $"{width}×{height}  ·  {position:mm\\:ss\\.fff}";
            SaveButton.IsEnabled = true;

            if ((_shotPath is not null || _saveFramePath is not null) && ++_renderedFrames == 20)
            {
                Dispatcher.UIThread.Post(AutoCapture, DispatcherPriority.Background);
            }
        }
        finally
        {
            player.ReturnFrame(pixels);
        }
    }

    /// <summary>命令行自动化：存帧 / 截图后退出。</summary>
    private void AutoCapture()
    {
        if (_saveFramePath is { } framePath)
        {
            try
            {
                _composer.SavePng(framePath);
                Console.Error.WriteLine($"[save-frame] {framePath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[save-frame] 失败：{ex}");
            }
        }

        if (_shotPath is { } shotPath)
        {
            CaptureWindow(shotPath);
        }

        Close();
    }

    /// <summary>把当前窗口内容渲染到 PNG；不依赖窗口在屏幕上可见。</summary>
    private void CaptureWindow(string path)
    {
        try
        {
            var size = new PixelSize((int)Math.Ceiling(Bounds.Width), (int)Math.Ceiling(Bounds.Height));
            using var target = new RenderTargetBitmap(size, new Vector(96, 96));
            target.Render(this);
            // SimdSkia 还没实现新重载（传 null options 会抛 NotSupportedException），只能走旧重载
#pragma warning disable CS0618
            target.Save(path);
#pragma warning restore CS0618
            Console.Error.WriteLine($"[shot] {path} {size.Width}x{size.Height}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[shot] 失败：{ex}");
        }
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _bitmapWidth == width && _bitmapHeight == height)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888,
            AlphaFormat.Opaque);
        _bitmapWidth = width;
        _bitmapHeight = height;
        VideoView.Source = _bitmap;
        VideoView.IsVisible = true;
    }
}
