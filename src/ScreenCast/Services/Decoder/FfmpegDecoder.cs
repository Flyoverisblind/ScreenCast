using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFmpeg.AutoGen;

namespace ScreenCast.Services.Decoder;

/// <summary>
/// 基于 FFmpeg（FFmpeg.AutoGen）的 H.264 解码器。
/// 需要：1) FFmpeg.AutoGen NuGet 包；2) 原生 ffmpeg dll（avcodec/avutil/swscale）位于运行目录。
/// </summary>
public sealed unsafe class FfmpegDecoder : IFrameDecoder
{
    private readonly Dispatcher _uiDispatcher = Application.Current.Dispatcher;
    private readonly object _lock = new();

    private AVCodecContext* _ctx;
    private AVCodecParserContext* _parser;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _sws;
    private long _lastPts;

    private int _width;
    private int _height;
    private bool _disposed;
    private int _decodedFrames;

    public event Action<byte[], int, int>? FrameDecoded;

    public FfmpegDecoder()
    {
        ffmpeg.RootPath = ResolveFfmpegRoot();
        ffmpeg.avformat_network_init();
        InitCodec();
    }

    private void InitCodec()
    {
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
            throw new InvalidOperationException("未找到 H.264 解码器，请确认 ffmpeg 原生库已加载。");

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null)
            throw new InvalidOperationException("无法分配解码上下文。");

        // 低延迟：不缓存过多帧
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        if (ffmpeg.avcodec_open2(_ctx, codec, null) < 0)
            throw new InvalidOperationException("无法打开 H.264 解码器。");

        _parser = ffmpeg.av_parser_init((int)AVCodecID.AV_CODEC_ID_H264);
        if (_parser == null)
            throw new InvalidOperationException("无法初始化 H.264 解析器。");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    public void Feed(ReadOnlyMemory<byte> data)
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (data.IsEmpty) return;

            fixed (byte* p = data.Span)
            {
                var dataPtr = p;
                var size = data.Length;

                while (size > 0)
                {
                    // 解析出完整 NAL 单元
                    byte* outData;
                    int outSize;
                    var consumed = ffmpeg.av_parser_parse2(
                        _parser, _ctx,
                        &outData, &outSize,
                        dataPtr, size,
                        _lastPts, _lastPts, 0);

                    if (consumed < 0) break;
                    dataPtr += consumed;
                    size -= consumed;
                    _lastPts += consumed;

                    if (outSize <= 0) continue;

                    _packet->data = outData;
                    _packet->size = outSize;

                    if (ffmpeg.avcodec_send_packet(_ctx, _packet) == 0)
                    {
                        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
                        {
                            PublishFrame();
                        }
                    }
                }
            }
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_ctx == null) return;
            ffmpeg.avcodec_flush_buffers(_ctx);
        }
    }

    private void PublishFrame()
    {
        var w = _frame->width;
        var h = _frame->height;
        if (w <= 0 || h <= 0) return;

        _decodedFrames++;
        if (_decodedFrames <= 10 || _decodedFrames % 60 == 0)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "screencast_stream.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] decoded frame #{_decodedFrames}: {w}x{h}{Environment.NewLine}");
            }
            catch { }
        }

        if (_sws == null || _width != w || _height != h)
        {
            if (_sws != null) ffmpeg.sws_freeContext(_sws);
            _width = w;
            _height = h;
            _sws = ffmpeg.sws_getContext(
                w, h, (AVPixelFormat)_frame->format,
                w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_FAST_BILINEAR, null, null, null);
            if (_sws == null) return;
        }

        var rgba = new byte[w * h * 4];
        fixed (byte* dst0 = rgba)
        {
            // FFmpeg.AutoGen 的 sws_scale 直接接收 byte*[] / int[] 数组
            var srcData = _frame->data.ToArray();
            var srcStride = _frame->linesize.ToArray();
            var dstData = new[] { dst0 };
            var dstStride = new[] { w * 4 };

            ffmpeg.sws_scale(_sws, srcData, srcStride, 0, h, dstData, dstStride);
        }

        // 重要：在后台线程解码，只把原始 BGRA 像素传回 UI 线程，由 UI 线程创建/更新 WriteableBitmap
        var rgbaCopy = rgba;
        var width = w;
        var height = h;
        _uiDispatcher.BeginInvoke(new Action(() => FrameDecoded?.Invoke(rgbaCopy, width, height)));
    }

    private static string ResolveFfmpegRoot()
    {
        // FFmpeg.AutoGen 的 RootPath 应指向包含 avcodec-*.dll 的目录。
        // 构建时通过 Sdcb.FFmpeg.runtime.windows-x64 把原生 dll 复制到输出目录 ffmpeg/ 下。
        var candidate = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        return Directory.Exists(candidate) ? candidate : AppContext.BaseDirectory;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_parser != null) { ffmpeg.av_parser_close(_parser); _parser = null; }
            if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
            ffmpeg.avformat_network_deinit();
        }
    }
}
