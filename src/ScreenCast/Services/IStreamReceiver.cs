using ScreenCast.Models;

namespace ScreenCast.Services;

public interface IStreamReceiver : IDisposable
{
    event Action? Started;
    event Action<string>? Stopped;
    event Action<string>? Error;

    bool IsRunning { get; }

    /// <summary>连接并开始接收投屏流。</summary>
    Task StartAsync(AdbDevice device, StreamSettings settings, CancellationToken ct = default);

    /// <summary>停止接收。</summary>
    Task StopAsync();
}
