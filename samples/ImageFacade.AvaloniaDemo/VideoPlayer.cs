using System.Diagnostics;
using FFmpeg.AutoGen;

namespace ImageFacade.AvaloniaDemo;

/// <summary>
/// FFmpeg.AutoGen 解码线程。产出的每一帧是直通 RGBA32（stride = width * 4），
/// 通过“待取帧 + 空闲池”交接给 UI 线程，交接过程不复制像素。
/// </summary>
internal sealed unsafe class VideoPlayer : IDisposable
{
    /// <summary>帧缓冲池的固定容量：一次性按当前分辨率建满，此后只做整块替换，不扩容。</summary>
    private const int PoolSize = 4;

    private readonly object _gate = new();
    private readonly Queue<byte[]> _free = new();
    private readonly ManualResetEventSlim _resume = new(true);

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private byte[]? _pending;
    private bool _frameOut;
    private int _width;
    private int _height;
    private TimeSpan _position;
    private string? _error;

    public string? Error
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    public bool IsPaused => !_resume.IsSet;

    public void Open(string path)
    {
        Stop();
        lock (_gate)
        {
            _free.Clear();
            _pending = null;
            _frameOut = false;
            _error = null;
            _position = TimeSpan.Zero;
            _width = 0;
            _height = 0;
        }

        _resume.Set();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _thread = new Thread(() => Run(path, token))
        {
            IsBackground = true,
            Name = "ffmpeg-decode",
        };
        _thread.Start();
    }

    public void Pause() => _resume.Reset();

    public void Resume() => _resume.Set();

    /// <summary>取走最新一帧；返回 false 表示还没有新帧。取到的缓冲用完必须交给 <see cref="ReturnFrame"/>。</summary>
    public bool TryTakeFrame(out byte[] pixels, out int width, out int height, out TimeSpan position)
    {
        lock (_gate)
        {
            if (_pending is null)
            {
                pixels = [];
                width = height = 0;
                position = TimeSpan.Zero;
                return false;
            }

            pixels = _pending;
            _pending = null;
            _frameOut = true;
            width = _width;
            height = _height;
            position = _position;
            return true;
        }
    }

    public void ReturnFrame(byte[] pixels)
    {
        lock (_gate)
        {
            _frameOut = false;
            _free.Enqueue(pixels);
        }
    }

    public void Dispose()
    {
        Stop();
        _resume.Dispose();
        lock (_gate)
        {
            _free.Clear();
            _pending = null;
            _frameOut = false;
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void Run(string path, CancellationToken token)
    {
        AVFormatContext* format = null;
        AVCodecContext* codec = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        SwsContext* sws = null;
        AVPixelFormat swsSource = AVPixelFormat.AV_PIX_FMT_NONE;
        int swsWidth = 0;
        int swsHeight = 0;

        try
        {
            // 网络源（rtmp:// http:// …）没有本地文件可校验，读写都要给超时：
            // 服务器不可达时 avformat_open_input / av_read_frame 会一直阻塞，解码线程连
            // Dispose 的 Join 都退不出来。单位是微秒，这里 5 秒。
            //
            // 注意只能用 rw_timeout：rtmp 协议的 timeout 是"等推流连接"的监听超时，
            // 设了它 FFmpeg 会去 bind 服务器地址（listen 模式），必然报 -10049。
            AVDictionary* options = null;
            if (path.Contains("://", StringComparison.Ordinal))
            {
                ffmpeg.av_dict_set(&options, "rw_timeout", "5000000", 0);
            }

            AVFormatContext* opened = null;
            int ret = ffmpeg.avformat_open_input(&opened, path, null, &options);
            ffmpeg.av_dict_free(&options);
            if (ret < 0)
            {
                Fail("打开失败", ret);
                return;
            }

            format = opened;
            ret = ffmpeg.avformat_find_stream_info(format, null);
            if (ret < 0)
            {
                Fail("读取流信息失败", ret);
                return;
            }

            int streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                Fail($"没有视频流（{streamIndex}）");
                return;
            }

            AVStream* stream = format->streams[streamIndex];
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (decoder is null)
            {
                Fail($"找不到解码器：{stream->codecpar->codec_id}");
                return;
            }

            codec = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar);
            ret = ffmpeg.avcodec_open2(codec, decoder, null);
            if (ret < 0)
            {
                Fail("打开解码器失败", ret);
                return;
            }

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            double timeBase = ffmpeg.av_q2d(stream->time_base);
            double firstPts = double.NaN;
            var clock = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                WaitWhilePaused(clock, token);

                ret = ffmpeg.av_read_frame(format, packet);
                if (ret < 0)
                {
                    break;
                }

                if (packet->stream_index == streamIndex && ffmpeg.avcodec_send_packet(codec, packet) >= 0)
                {
                    while (ffmpeg.avcodec_receive_frame(codec, frame) == 0)
                    {
                        long stamp = frame->best_effort_timestamp;
                        double pts = stamp == ffmpeg.AV_NOPTS_VALUE ? 0d : stamp * timeBase;
                        if (double.IsNaN(firstPts))
                        {
                            firstPts = pts;
                            clock.Restart();
                        }

                        // 按 PTS 节流：解码快了就等，保证播放速度与真实时长一致
                        double target = pts - firstPts;
                        while (!token.IsCancellationRequested && target - clock.Elapsed.TotalSeconds > 0.002)
                        {
                            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(target - clock.Elapsed.TotalSeconds, 0.02)));
                        }

                        Publish(frame, target);

                        ffmpeg.av_frame_unref(frame);
                    }
                }

                ffmpeg.av_packet_unref(packet);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            if (sws is not null)
            {
                ffmpeg.sws_freeContext(sws);
            }

            if (frame is not null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (codec is not null)
            {
                ffmpeg.avcodec_free_context(&codec);
            }

            if (format is not null)
            {
                ffmpeg.avformat_close_input(&format);
            }
        }

        // 局部函数：把解码帧转成直通 RGBA32，再交给 UI 线程
        void Publish(AVFrame* decoded, double positionSeconds)
        {
            int width = decoded->width;
            int height = decoded->height;
            AVPixelFormat sourceFormat = (AVPixelFormat)decoded->format;

            if (sws is null || width != swsWidth || height != swsHeight || sourceFormat != swsSource)
            {
                if (sws is not null)
                {
                    ffmpeg.sws_freeContext(sws);
                    sws = null;
                }

                sws = ffmpeg.sws_getContext(
                    width, height, sourceFormat,
                    width, height, AVPixelFormat.AV_PIX_FMT_RGBA,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (sws is null)
                {
                    throw new InvalidOperationException("sws_getContext 失败。");
                }

                swsWidth = width;
                swsHeight = height;
                swsSource = sourceFormat;
            }

            byte[]? target = RentBuffer(width * height * 4);
            if (target is null)
            {
                // 4 块全在占用中（待取帧 / UI 正在渲染），这一帧不解码，直接丢掉
                return;
            }

            byte_ptrArray4 destination = new();
            int_array4 destinationStride = new();
            fixed (byte* pixels = target)
            {
                // arrify 指针，sws_scale 只写平面 0
                int ret = ffmpeg.av_image_fill_arrays(
                    ref destination, ref destinationStride, pixels,
                    AVPixelFormat.AV_PIX_FMT_RGBA, width, height, 1);
                if (ret < 0)
                {
                    throw new InvalidOperationException($"av_image_fill_arrays 失败：{ret}");
                }

                ret = ffmpeg.sws_scale(sws, decoded->data, decoded->linesize, 0, height, destination, destinationStride);
                if (ret < 0)
                {
                    throw new InvalidOperationException($"sws_scale 失败：{ret}");
                }
            }

            lock (_gate)
            {
                if (_pending is not null)
                {
                    // 上一帧还没被取走：块本来就属于池，直接还回去
                    _free.Enqueue(_pending);
                }

                _pending = target;
                _width = width;
                _height = height;
                _position = TimeSpan.FromSeconds(Math.Max(0d, positionSeconds));
            }
        }
    }

    private void WaitWhilePaused(Stopwatch clock, CancellationToken token)
    {
        if (_resume.IsSet)
        {
            return;
        }

        clock.Stop();
        try
        {
            _resume.Wait(token);
        }
        finally
        {
            clock.Start();
        }
    }

    /// <summary>
    /// 从固定池里借一块缓冲。池容量恒为 <see cref="PoolSize"/>：
    /// 首帧按当前分辨率一次建满；之后遇到尺寸不够的块就丢弃重建；池被占满则返回 null（本帧不解）。
    /// </summary>
    private byte[]? RentBuffer(int size)
    {
        lock (_gate)
        {
            // 池内块 = 空闲 + 待取帧 + UI 手上那块
            int pooled = _free.Count + (_pending is null ? 0 : 1) + (_frameOut ? 1 : 0);

            if (pooled == 0)
            {
                // 首帧：一次建满，避免运行中反复 new 大对象（LOH 抖动）
                for (int i = 0; i < PoolSize; i++)
                {
                    _free.Enqueue(new byte[size]);
                }

                return _free.Dequeue();
            }

            while (_free.Count > 0)
            {
                byte[] candidate = _free.Dequeue();
                if (candidate.Length >= size)
                {
                    return candidate;
                }

                // 分辨率变大，这块不够用了：丢掉它，下面按新尺寸补一块
                pooled--;
            }

            if (pooled >= PoolSize)
            {
                return null;
            }

            return new byte[size];
        }
    }

    private void Fail(string what, int code)
    {
        const int BufferSize = 1024;
        byte* buffer = stackalloc byte[BufferSize];
        ffmpeg.av_strerror(code, buffer, BufferSize);
        string detail = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"error {code}";
        Fail($"{what}：{detail}");
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _error = message;
        }

        // 无 UI 场景（--decode-check / --open）也要能看到失败原因
        Console.Error.WriteLine($"[decode] {message}");
    }
}
