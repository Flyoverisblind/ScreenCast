namespace ScreenCast.Models;

/// <summary>投屏编码参数（透传给设备端采集服务）。</summary>
public sealed class StreamSettings
{
    public int MaxWidth { get; set; } = 1920;
    public int MaxHeight { get; set; } = 1080;
    public int BitRate { get; set; } = 8_000_000;   // 8 Mbps
    public int MaxFps { get; set; } = 60;
    public int IFrameInterval { get; set; } = 1;     // 秒

    /// <summary>是否接收并播放手机声音。</summary>
    public bool EnableAudio { get; set; } = true;

    /// <summary>是否启用电脑反向控制手机。</summary>
    public bool EnableControl { get; set; } = true;

    /// <summary>生成 scrcpy-server 参数（下划线风格、不带 --）。</summary>
    public string ToDeviceArgs()
    {
        var maxSize = Math.Max(MaxWidth, MaxHeight);
        var audio = EnableAudio ? "true" : "false";
        var control = EnableControl ? "true" : "false";
        return $"video=true audio={audio} control={control} tunnel_forward=true " +
               $"max_size={maxSize} video_bit_rate={BitRate} max_fps={MaxFps}";
    }
}
