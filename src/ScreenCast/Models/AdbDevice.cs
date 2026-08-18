using System.Net;

namespace ScreenCast.Models;

/// <summary>一台通过 ADB 连接的安卓设备。</summary>
public sealed class AdbDevice
{
    public AdbDevice(string serial, string state, string model = "", string device = "")
    {
        Serial = serial;
        State = state;
        Model = model;
        Device = device;
    }

    /// <summary>序列号（USB 下为 usb 序列号，无线下为 ip:port）。</summary>
    public string Serial { get; }

    /// <summary>device / unauthorized / offline / no permissions。</summary>
    public string State { get; }

    public string Model { get; }

    public string Device { get; }

    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    /// <summary>无线地址（若 Serial 形如 ip:port）。</summary>
    public IPEndPoint? WirelessEndpoint
    {
        get
        {
            var idx = Serial.LastIndexOf(':');
            if (idx <= 0) return null;
            var host = Serial[..idx];
            var portStr = Serial[(idx + 1)..];
            if (!int.TryParse(portStr, out var port)) return null;
            if (!IPAddress.TryParse(host, out var ip)) return null;
            return new IPEndPoint(ip, port);
        }
    }

    public override string ToString()
        => string.IsNullOrWhiteSpace(Model)
            ? Serial
            : $"{Model}  ({Serial})";
}
