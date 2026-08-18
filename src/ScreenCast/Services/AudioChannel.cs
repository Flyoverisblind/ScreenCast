using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using NAudio.Wave;

namespace ScreenCast.Services;

/// <summary>
/// 音频通道：接收 scrcpy 推来的 OPUS 音频流，用 FFmpeg 解码为 PCM(16bit/48kHz/双声道)，
/// 再通过 NAudio 播放到电脑扬声器。
/// 帧协议：dummy(1) + codecId(4) + [frameMeta(12) + OPUS]*
/// </summary>
public sealed class AudioChannel : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();

    // 用 IntPtr 保存原生指针，避免把整个类标记为 unsafe（否则 async 方法无法 await）
    private IntPtr _ctx;
    private IntPtr _packet;
    private IntPtr _frame;
    private IntPtr _extradataPtr;

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _waveProvider;

    public event Action<string>? Error;

    public AudioChannel(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public void Start()
    {
        _ = Task.Run(RunAsync, CancellationToken.None);
    }

    private async Task RunAsync()
    {
        try
        {
            // 注意：音频 socket 没有 dummy 字节，直接从 codec id 开始
            var codecIdBuf = new byte[4];
            if (!await ReadFullAsync(_stream, codecIdBuf, 4)) return;
            Log($"audio codec id=0x{BinaryPrimitives.ReadInt32BigEndian(codecIdBuf):X8}");

            var meta = new byte[12];
            var first = true;
            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadFullAsync(_stream, meta, 12)) break;
                var pts = BinaryPrimitives.ReadInt64BigEndian(meta);
                var size = BinaryPrimitives.ReadInt32BigEndian(meta.AsSpan(8, 4));
                if (size < 0 || size > 8 * 1024 * 1024) break;

                var payload = new byte[size];
                if (!await ReadFullAsync(_stream, payload, size)) break;

                var isConfig = (pts & 0x4000_0000_0000_0000L) != 0;

                if (isConfig)
                {
                    Log($"audio config received, size={size}");
                    InitDecoder(payload);
                    continue;
                }

                if (first)
                {
                    first = false;
                    Log("audio data started");
                    // 如果没有收到配置包，用默认参数初始化 OPUS 解码器
                    EnsureDecoderInitialized();
                }
                Decode(payload);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke("音频通道异常：" + ex.Message);
            Log("audio channel error: " + ex.Message);
        }
        finally
        {
            Log("audio channel ended");
        }
    }

    private void EnsureDecoderInitialized()
    {
        lock (this)
        {
            if (_ctx == IntPtr.Zero)
            {
                Log("no OPUS config received, initializing with defaults");
                InitDecoder(new byte[0]);
            }
        }
    }

    private unsafe void InitDecoder(byte[] config)
    {
        lock (this)
        {
            if (_ctx != IntPtr.Zero) return;

            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_OPUS);
            if (codec == null)
            {
                Log("OPUS decoder not found");
                return;
            }

            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (ctx == null) return;

            // 设置多通道/采样率，以防没有 extradata 的情况
            ctx->ch_layout.nb_channels = Channels;
            ctx->sample_rate = SampleRate;

            if (config.Length > 0)
            {
                var extradata = (byte*)ffmpeg.av_malloc((ulong)config.Length);
                if (extradata == null) return;
                Marshal.Copy(config, 0, (IntPtr)extradata, config.Length);
                ctx->extradata = extradata;
                ctx->extradata_size = config.Length;
                _extradataPtr = (IntPtr)extradata;
            }

            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
            {
                Log("failed to open OPUS decoder");
                ffmpeg.avcodec_free_context(&ctx);
                return;
            }

            _ctx = (IntPtr)ctx;
            _packet = (IntPtr)ffmpeg.av_packet_alloc();
            _frame = (IntPtr)ffmpeg.av_frame_alloc();

            _waveProvider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_waveProvider);
            _waveOut.Play();
            Log("audio playback started");
        }
    }

    private unsafe void Decode(byte[] data)
    {
        lock (this)
        {
            if (_ctx == IntPtr.Zero || _packet == IntPtr.Zero || _frame == IntPtr.Zero) return;

            var ctx = (AVCodecContext*)_ctx;
            var packet = (AVPacket*)_packet;
            var frame = (AVFrame*)_frame;

            fixed (byte* p = data)
            {
                packet->data = p;
                packet->size = data.Length;

                if (ffmpeg.avcodec_send_packet(ctx, packet) == 0)
                {
                    while (ffmpeg.avcodec_receive_frame(ctx, frame) == 0)
                    {
                        var samples = frame->nb_samples;
                        if (samples <= 0) continue;

                        var pcm = new byte[samples * Channels * 2];
                        var f0 = (float*)frame->data[0];
                        var f1 = (float*)frame->data[1];
                        for (var i = 0; i < samples; i++)
                        {
                            var l = f0[i];
                            var r = Channels > 1 ? f1[i] : f0[i];
                            WriteS16(pcm, i * 4, l);
                            WriteS16(pcm, i * 4 + 2, r);
                        }

                        _waveProvider?.AddSamples(pcm, 0, pcm.Length);
                    }
                }
            }
        }
    }

    private static void WriteS16(byte[] buffer, int offset, float sample)
    {
        var s = (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
        buffer[offset] = (byte)(s & 0xFF);
        buffer[offset + 1] = (byte)((s >> 8) & 0xFF);
    }

    private static async Task<bool> ReadFullAsync(NetworkStream stream, byte[] buffer, int count)
    {
        var off = 0;
        while (off < count)
        {
            var r = await stream.ReadAsync(buffer.AsMemory(off, count - off));
            if (r == 0) return false;
            off += r;
        }
        return true;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "screencast_stream.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [audio] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public unsafe void Dispose()
    {
        _cts.Cancel();
        try { _waveOut?.Stop(); } catch { }
        _waveOut?.Dispose();
        _stream.Dispose();
        _client.Dispose();

        lock (this)
        {
            if (_packet != IntPtr.Zero)
            {
                var p = (AVPacket*)_packet;
                ffmpeg.av_packet_free(&p);
                _packet = IntPtr.Zero;
            }
            if (_frame != IntPtr.Zero)
            {
                var f = (AVFrame*)_frame;
                ffmpeg.av_frame_free(&f);
                _frame = IntPtr.Zero;
            }
            if (_ctx != IntPtr.Zero)
            {
                var c = (AVCodecContext*)_ctx;
                ffmpeg.avcodec_free_context(&c);
                _ctx = IntPtr.Zero;
            }
            if (_extradataPtr != IntPtr.Zero)
            {
                ffmpeg.av_free((void*)_extradataPtr);
                _extradataPtr = IntPtr.Zero;
            }
        }
    }
}
