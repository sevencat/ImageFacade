using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageFacade.AvaloniaDemo;

namespace ImageFacade.AvaloniaNativeDemo;

internal sealed partial class MainWindow : Window
{
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

    // --mem：每 2 秒打一次内存明细，格式与 SimdSkia 示例完全一致，方便直接对比
    private readonly bool _traceMemory = Environment.GetCommandLineArgs().Contains("--mem");

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();

        if (FfmpegRuntime.TryInitialize(out string info))
        {
            StatusText.Text = info;
        }
        else
        {
            StatusText.Text = info;
            OpenButton.IsEnabled = false;
        }

        if (initialPath is not null)
        {
            OpenPath(initialPath);
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Render());
        _timer.Start();

        if (_traceMemory)
        {
            var memoryTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => DumpMemory());
            memoryTimer.Start();
        }
    }

    private void DumpMemory()
    {
        using Process self = Process.GetCurrentProcess();
        double mb = 1048576d;
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        ReadOnlySpan<GCGenerationInfo> gens = info.GenerationInfo;
        string gen = string.Join(
            " ",
            $"g0={gens[0].SizeAfterBytes / mb:0.0}",
            $"g1={gens[1].SizeAfterBytes / mb:0.0}",
            $"g2={gens[2].SizeAfterBytes / mb:0.0}",
            $"loh={gens[3].SizeAfterBytes / mb:0.0}");
        Console.Error.WriteLine(
            $"[mem] heap={info.HeapSizeBytes / mb:0.0} {gen} " +
            $"ws={self.WorkingSet64 / mb:0.0} priv={self.PrivateMemorySize64 / mb:0.0} " +
            $"pool={(_player?.BufferedBytes ?? 0) / mb:0.0} drop={_player?.DroppedFrames} " +
            $"bmp={_bitmapWidth * _bitmapHeight * 4d / mb:0.0} fps={_fps:0.0} " +
            $"alloc={GC.GetTotalAllocatedBytes() / mb:0} " +
            $"gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
            $"codec=[{_player?.Description}]");
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

    private void OnPauseClick(object? sender, RoutedEventArgs e) => TogglePause();

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
        _lastPosition = TimeSpan.MinValue;
        StatusText.Text = $"解码中：{Path.GetFileName(path)}";
    }

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
            return;
        }

        if (!player.TryTakeFrame(out byte[] pixels, out int width, out int height, out TimeSpan position))
        {
            return;
        }

        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();

            EnsureBitmap(width, height);
            using ILockedFramebuffer framebuffer = _bitmap!.Lock();
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

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            StatusText.Text = $"{width}×{height}  ·  {position:mm\\:ss\\.fff}  ·  每帧分配 {allocated} B";
        }
        finally
        {
            player.ReturnFrame(pixels);
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
            Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        _bitmapWidth = width;
        _bitmapHeight = height;
        VideoView.Source = _bitmap;
        VideoView.IsVisible = true;
    }
}
