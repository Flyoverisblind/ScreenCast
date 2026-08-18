using ScreenCast.Models;

namespace ScreenCast.Services;

public interface IAdbService
{
    /// <summary>返回 adb 可执行文件完整路径；找不到返回 null。</summary>
    string? FindAdb();

    /// <summary>枚举当前连接的所有设备。</summary>
    IReadOnlyList<AdbDevice> GetDevices(string? adbPath = null);

    /// <summary>为指定设备建立 USB 反向端口转发，返回本地端口。</summary>
    int StartReverse(string adbPath, string serial, int devicePort);

    /// <summary>移除指定设备的反向转发。</summary>
    void StopReverse(string adbPath, string serial, int localPort);

    /// <summary>无线连接：tcpip 打开 + connect。</summary>
    bool ConnectWireless(string adbPath, string host, int port, int timeoutMs = 10_000);

    /// <summary>断开无线设备。</summary>
    void Disconnect(string adbPath, string serial);
}
