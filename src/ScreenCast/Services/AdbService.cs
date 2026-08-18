using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using ScreenCast.Models;

namespace ScreenCast.Services;

public sealed class AdbService : IAdbService
{
    public const int DefaultDevicePort = 27183;

    private static readonly string[] Candidates =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Android", "Sdk", "platform-tools", "adb.exe"),
        @"C:\Android\platform-tools\adb.exe",
        @"C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe",
        @"C:\Program Files\Android\Android Studio\platform-tools\adb.exe",
    };

    public string? FindAdb()
    {
        // 1) 环境变量
        foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var root = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var p = Path.Combine(root, "platform-tools", "adb.exe");
                if (File.Exists(p)) return p;
            }
        }

        // 2) 常见安装路径
        foreach (var c in Candidates)
        {
            if (File.Exists(c)) return c;
        }

        // 2.5) winget 包目录（如 Android SDK Platform-Tools 或 scrcpy 携带的 adb）
        var fromWinGet = FindAdbInWinGetPackages();
        if (fromWinGet != null) return fromWinGet;

        // 3) PATH
        var fromPath = FindOnPath("adb");
        if (fromPath != null) return fromPath;

        return null;
    }

    public IReadOnlyList<AdbDevice> GetDevices(string? adbPath = null)
    {
        var adb = adbPath ?? FindAdb() ?? throw new InvalidOperationException("未找到 adb，请安装 Android platform-tools 或设置 ANDROID_HOME。");
        var output = Run(adb, "devices -l");
        var list = new List<AdbDevice>();

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var state = parts[1];
            var model = Extract(parts, "model:");
            var device = Extract(parts, "device:");
            list.Add(new AdbDevice(serial, state, model, device));
        }

        return list;
    }

    public int StartReverse(string adbPath, string serial, int devicePort)
    {
        // 使用 adb forward：PC 连接 localhost:localPort 转发到设备的 localabstract:scrcpy
        // （scrcpy-server 以 tunnel_forward=true 模式在设备端监听该 socket）
        var localPort = GetFreeTcpPort();
        var result = Run(adbPath, $"-s {serial} forward tcp:{localPort} localabstract:scrcpy");
        if (!string.IsNullOrWhiteSpace(result) &&
            result.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidOperationException($"adb forward 失败：{result}");
        }
        return localPort;
    }

    public void StopReverse(string adbPath, string serial, int localPort)
        => Run(adbPath, $"-s {serial} forward --remove tcp:{localPort}");

    public bool ConnectWireless(string adbPath, string host, int port, int timeoutMs = 10_000)
    {
        // 设备需先处于 USB 连接状态才能 tcpip；若已是无线则跳过
        var result = Run(adbPath, $"tcpip {port}", timeoutMs);
        if (result.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidOperationException($"adb tcpip 失败：{result}");
        }

        var connect = Run(adbPath, $"connect {host}:{port}", timeoutMs);
        return connect.Contains("connected", StringComparison.OrdinalIgnoreCase);
    }

    public void Disconnect(string adbPath, string serial)
        => Run(adbPath, $"disconnect {serial}");

    private static string Extract(string[] parts, string prefix)
    {
        foreach (var p in parts)
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return p[prefix.Length..];
        }
        return string.Empty;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindAdbInWinGetPackages()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packagesRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(packagesRoot)) return null;

            foreach (var pkgDir in Directory.EnumerateDirectories(packagesRoot))
            {
                foreach (var file in Directory.EnumerateFiles(pkgDir, "adb.exe", SearchOption.AllDirectories))
                {
                    return file;
                }
            }
        }
        catch { /* 忽略读取异常 */ }
        return null;
    }

    private static string? FindOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir, name + ".exe");
                if (File.Exists(full)) return full;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private static string Run(string adb, string args, int timeoutMs = 15_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = adb,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(timeoutMs))
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException($"adb 命令超时：{adb} {args}");
        }
        return stdout + stderr;
    }
}
