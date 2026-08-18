using System.Buffers.Binary;
using System.IO;
using System.Threading.Channels;
using System.Net.Sockets;
using System.Text;

namespace ScreenCast.Services;

/// <summary>
/// scrcpy 反向控制通道：把 PC 的鼠标/键盘事件编码成控制消息发送给手机。
/// 消息均为大端序，详见 scrcpy 控制协议。
/// </summary>
public sealed class ControlChannel : IDisposable
{
    // 控制消息类型
    public const byte TypeInjectKeycode = 0;
    public const byte TypeInjectText = 1;
    public const byte TypeInjectTouch = 2;
    public const byte TypeInjectScroll = 3;
    public const byte TypeBackOrScreenOn = 4;

    // 触摸动作
    public const byte ActionDown = 0;
    public const byte ActionUp = 1;
    public const byte ActionMove = 2;

    // 按键动作
    public const byte KeyActionDown = 0;
    public const byte KeyActionUp = 1;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ControlChannel(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _ = Task.Run(ReadAndDiscardDeviceMessagesAsync, CancellationToken.None);
        _ = Task.Run(WriterLoopAsync, CancellationToken.None);
    }

    public int VideoWidth { get; set; } = 1;
    public int VideoHeight { get; set; } = 1;

    /// <summary>发送触摸事件（坐标为视频像素）。</summary>
    public void SendTouch(byte action, double x, double y, long pointerId = 0)
    {
        // x/y 为绝对视频像素坐标；并附上屏幕宽高（服务端需匹配视频尺寸，否则忽略）
        var px = (int)Math.Round(Math.Clamp(x, 0, VideoWidth));
        var py = (int)Math.Round(Math.Clamp(y, 0, VideoHeight));

        using var ms = new MemoryStream(32);
        ms.WriteByte(TypeInjectTouch);                    // 1
        ms.WriteByte(action);                             // 1
        WriteInt64(ms, pointerId);                        // 8
        WriteInt32(ms, px);                               // 4
        WriteInt32(ms, py);                               // 4
        WriteUInt16(ms, (ushort)VideoWidth);              // 2
        WriteUInt16(ms, (ushort)VideoHeight);             // 2
        WriteUInt16(ms, 0xFFFF);                          // 2 pressure = 1.0
        WriteInt32(ms, 0);                                // 4 actionButton
        WriteInt32(ms, action == ActionUp ? 0 : 1);       // 4 buttons

        Send(ms.ToArray());
    }

    /// <summary>发送滚动事件（坐标为视频像素）。</summary>
    public void SendScroll(double x, double y, float hScroll, float vScroll)
    {
        var px = (int)Math.Round(Math.Clamp(x, 0, VideoWidth));
        var py = (int)Math.Round(Math.Clamp(y, 0, VideoHeight));

        using var ms = new MemoryStream(21);
        ms.WriteByte(TypeInjectScroll);                   // 1
        WriteInt32(ms, px);                               // 4
        WriteInt32(ms, py);                               // 4
        WriteUInt16(ms, (ushort)VideoWidth);              // 2
        WriteUInt16(ms, (ushort)VideoHeight);             // 2
        // hScroll/vScroll: i16 定点（服务端读取后 *16，即 raw/2048 = 滚动量）
        WriteInt16(ms, (short)(hScroll * 2048f));         // 2
        WriteInt16(ms, (short)(vScroll * 2048f));         // 2
        WriteInt32(ms, 0);                                // 4 buttons

        Send(ms.ToArray());
    }

    /// <summary>发送按键事件（Android keycode）。</summary>
    public void SendKey(byte action, int keycode, int metaState = 0)
    {
        using var ms = new MemoryStream(16);
        ms.WriteByte(TypeInjectKeycode);
        ms.WriteByte(action);
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, keycode);
        ms.Write(buf);
        BinaryPrimitives.WriteInt32BigEndian(buf, 0); // repeat
        ms.Write(buf);
        BinaryPrimitives.WriteInt32BigEndian(buf, metaState);
        ms.Write(buf);
        Send(ms.ToArray());
    }

    /// <summary>发送文本输入。</summary>
    public void SendText(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream(utf8.Length + 5);
        ms.WriteByte(TypeInjectText);
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, utf8.Length);
        ms.Write(buf);
        ms.Write(utf8);
        Send(ms.ToArray());
    }

    /// <summary>发送返回键（BACK）。</summary>
    public void SendBack()
    {
        using var ms = new MemoryStream(2);
        ms.WriteByte(TypeBackOrScreenOn);
        ms.WriteByte(KeyActionDown);
        Send(ms.ToArray());
        ms.Position = 1;
        ms.WriteByte(KeyActionUp);
        Send(ms.ToArray());
    }

    /// <summary>入队发送（不在 UI 线程直接写网络，避免卡死）。</summary>
    private void Send(byte[] data)
    {
        if (_queue.Writer.TryWrite(data)) return;
        System.Diagnostics.Debug.WriteLine("control queue closed");
    }

    /// <summary>后台写线程：从队列拿数据写到控制 socket。</summary>
    private async Task WriterLoopAsync()
    {
        try
        {
            var reader = _queue.Reader;
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var data))
                {
                    await _stream.WriteAsync(data, _cts.Token);
                }
                await _stream.FlushAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("control writer failed: " + ex.Message);
        }
    }

    /// <summary>设备可能回发剪贴板等消息，这里读取并丢弃，避免 socket 缓冲阻塞。</summary>
    private async Task ReadAndDiscardDeviceMessagesAsync()
    {
        try
        {
            var stream = _stream;
            var typeBuf = new byte[1];
            var lenBuf = new byte[4];

            // 控制 socket 的第一个字节是 dummy（服务器写入的 0），先丢弃
            var dummy = new byte[1];
            var d = await stream.ReadAsync(dummy.AsMemory(0, 1), _cts.Token);
            if (d == 0) return;

            while (!_cts.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(typeBuf.AsMemory(0, 1), _cts.Token);
                if (n == 0) break;
                var type = typeBuf[0];
                int skip;
                switch (type)
                {
                    case 0: // CLIPBOARD
                        if (!await ReadFull(stream, lenBuf, 4)) return;
                        skip = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                        if (!await Skip(stream, skip)) return;
                        break;
                    case 1: // ACK_CLIPBOARD
                        if (!await Skip(stream, 1)) return;
                        break;
                    case 2: // UHID_OUTPUT
                        if (!await Skip(stream, 4 + 2)) return;
                        if (!await ReadFull(stream, lenBuf, 2)) return;
                        skip = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
                        if (!await Skip(stream, skip)) return;
                        break;
                    case 3: // UHID_OPEN
                    case 4: // UHID_CLOSE
                        if (!await Skip(stream, 4)) return;
                        break;
                    default:
                        // 未知类型，停止（或可继续尝试）
                        break;
                }
            }
        }
        catch { /* 通道关闭 */ }
    }

    private static async Task<bool> ReadFull(NetworkStream stream, byte[] buf, int count)
    {
        var off = 0;
        while (off < count)
        {
            var r = await stream.ReadAsync(buf.AsMemory(off, count - off));
            if (r == 0) return false;
            off += r;
        }
        return true;
    }

    private static async Task<bool> Skip(NetworkStream stream, int count)
    {
        var buf = new byte[Math.Min(count, 4096)];
        var remaining = count;
        while (remaining > 0)
        {
            var r = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, remaining)));
            if (r == 0) return false;
            remaining -= r;
        }
        return true;
    }

    private static void WriteInt64(MemoryStream ms, long value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteInt32(MemoryStream ms, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteUInt16(MemoryStream ms, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteInt16(MemoryStream ms, short value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, value);
        ms.Write(buf);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
        _client.Dispose();
    }
}
