using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ImageFacade.AvaloniaDemo;

/// <summary>
/// 无 UI 的自检入口：<c>ImageFacade.AvaloniaDemo.exe --decode-check &lt;video&gt;</c>。
/// 跑一遍解码 → 交接 → 像素抽样，把结果写到 &lt;程序目录&gt;\decode-check.log，用退出码表示成败。
/// 用来在没有图形会话的机器上确认 FFmpeg 原生库与解码链路是否正常。
/// </summary>
internal static class DecodeCheck
{
    public static int Run(string videoPath)
    {
        var log = new List<string>();
        try
        {
            if (!File.Exists(videoPath))
            {
                log.Add($"文件不存在：{videoPath}");
                return 2;
            }

            if (!FfmpegRuntime.TryInitialize(out string info))
            {
                log.Add(info);
                return 2;
            }

            log.Add(info);

            using var player = new VideoPlayer();
            player.Open(videoPath);

            var clock = Stopwatch.StartNew();
            var sizes = new List<string>();
            int frames = 0;
            int visible = 0;

            while (clock.Elapsed < TimeSpan.FromSeconds(30) && frames < 150)
            {
                if (!player.TryTakeFrame(out byte[] pixels, out int width, out int height, out TimeSpan position))
                {
                    if (player.Error is { } error)
                    {
                        log.Add($"解码错误：{error}");
                        return 3;
                    }

                    Thread.Sleep(5);
                    continue;
                }

                try
                {
                    frames++;
                    string size = $"{width}x{height}";
                    if (sizes.Count == 0 || sizes[^1] != size)
                    {
                        sizes.Add(size);
                    }

                    if (HasVisiblePixels(pixels))
                    {
                        visible++;
                    }

                    if (frames <= 3)
                    {
                        log.Add($"#{frames} {size} t={position:mm\\:ss\\.fff}");
                    }
                }
                finally
                {
                    player.ReturnFrame(pixels);
                }
            }

            log.Add($"共 {frames} 帧，尺寸序列：{string.Join(" → ", sizes)}，含可见像素的帧：{visible}");

            int hudPixels = ComposeAll(videoPath, log);
            return frames > 0 && visible > 0 && hudPixels > 0 ? 0 : 4;
        }
        catch (Exception ex)
        {
            log.Add($"异常：{ex}");
            return 5;
        }
        finally
        {
            string target = Path.Combine(AppContext.BaseDirectory, "decode-check.log");
            File.WriteAllLines(target, log);
        }
    }

    /// <summary>
    /// 对全部帧走一遍完整的 ImageFacade 合成（同一块画布复用，尺寸变化时重建），
    /// 最后统计 HUD 区域里的白色像素。
    /// </summary>
    private static unsafe int ComposeAll(string videoPath, List<string> log)
    {
        using var player = new VideoPlayer();
        player.Open(videoPath);

        using var composer = new FrameComposer();
        var clock = Stopwatch.StartNew();
        var sizes = new List<string>();
        int composed = 0;
        int bright = 0;

        while (clock.Elapsed < TimeSpan.FromSeconds(20) && composed < 150)
        {
            if (!player.TryTakeFrame(out byte[] pixels, out int width, out int height, out TimeSpan position))
            {
                if (player.Error is { } error)
                {
                    log.Add($"合成阶段解码错误：{error}");
                    return 0;
                }

                Thread.Sleep(5);
                continue;
            }

            try
            {
                int stride = width * 4;
                IntPtr target = Marshal.AllocHGlobal(stride * height);
                try
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    composer.Compose(pixels, width, height, target, stride, position, 59.9);
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                    composed++;
                    string size = $"{width}x{height}";
                    if (sizes.Count == 0 || sizes[^1] != size)
                    {
                        sizes.Add(size);
                    }

                    bright = CountHudPixels((byte*)target, width, height, stride);
                    if (composed <= 3)
                    {
                        log.Add($"合成 #{composed} {size} 分配 {allocated} B");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(target);
                }
            }
            finally
            {
                player.ReturnFrame(pixels);
            }
        }

        log.Add($"合成校验：{composed} 帧，画布尺寸序列 {string.Join(" → ", sizes)}，最后一帧 HUD 白色像素 {bright} 个");
        return bright;
    }

    private static unsafe int CountHudPixels(byte* buffer, int width, int height, int stride)
    {
        int bright = 0;
        for (int y = 12; y < Math.Min(height, 80); y++)
        {
            for (int x = 12; x < Math.Min(width, 240); x++)
            {
                byte* pixel = buffer + (y * stride) + (x * 4);
                if (pixel[0] > 240 && pixel[1] > 240 && pixel[2] > 240)
                {
                    bright++;
                }
            }
        }

        return bright;
    }

    private static bool HasVisiblePixels(byte[] pixels)
    {
        // 抽样 1/64 的像素，只要有明显非黑就算通过
        for (int i = 0; i + 3 < pixels.Length; i += 64 * 4)
        {
            if (pixels[i] > 16 || pixels[i + 1] > 16 || pixels[i + 2] > 16)
            {
                return true;
            }
        }

        return false;
    }
}
