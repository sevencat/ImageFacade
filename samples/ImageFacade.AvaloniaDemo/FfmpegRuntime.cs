using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ImageFacade.AvaloniaDemo;

/// <summary>
/// 定位 FFmpeg 原生库。<c>FFmpeg.AutoGen</c> 这个包按固定名 DllImport（DynamicallyLinked），
/// 所以不能靠 <c>ffmpeg.RootPath</c>（在 linked 模式下会抛 NotSupportedException），
/// 而是用 <c>SetDllDirectory</c> 把候选目录加进本进程的搜索路径。
/// 查找顺序：<c>FFMPEG_HOME\bin</c> → <c>FFMPEG_HOME</c> → 程序目录\ffmpeg → 程序目录 → 系统默认。
/// </summary>
internal static class FfmpegRuntime
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    public static bool TryInitialize(out string message)
    {
        var candidates = new List<string>(6);
        string? home = Environment.GetEnvironmentVariable("FFMPEG_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, "bin"));
            candidates.Add(home);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        candidates.Add(AppContext.BaseDirectory);

        List<string> tried = [];
        foreach (string candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                tried.Add($"{candidate}（目录不存在）");
                continue;
            }

            try
            {
                SetDllDirectory(candidate);
                string version = ffmpeg.av_version_info();
                message = $"FFmpeg {version}，原生库：{candidate}";
                return true;
            }
            catch (Exception ex)
            {
                tried.Add($"{candidate}（{ex.GetType().Name}: {FirstLine(ex.Message)}）");
            }
        }

        try
        {
            // 最后靠程序目录 / 系统 PATH 再试一次
            SetDllDirectory(string.Empty);
            string version = ffmpeg.av_version_info();
            message = $"FFmpeg {version}（系统默认路径）";
            return true;
        }
        catch (Exception ex)
        {
            message = "未找到 FFmpeg 原生库。请设置 FFMPEG_HOME 指向 ffmpeg 的 bin 目录，"
                + "或把 avcodec / avformat / avutil / swscale 等 DLL 放到程序目录或程序目录的 ffmpeg 子目录。"
                + $"已尝试：{string.Join("；", tried)}；（系统默认：{ex.GetType().Name}: {FirstLine(ex.Message)}）";
            return false;
        }
    }

    private static string FirstLine(string text)
    {
        int index = text.IndexOf('\n');
        return index < 0 ? text : text[..index].TrimEnd('\r');
    }
}
