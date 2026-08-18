using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenCast.Models;
using ScreenCast.Services;
using ScreenCast.Services.Decoder;

namespace ScreenCast;

public partial class MainWindow : Window
{
    private readonly IAdbService _adb = new AdbService();
    private IFrameDecoder? _decoder;
    private StreamReceiver? _receiver;

    private IReadOnlyList<AdbDevice> _devices = Array.Empty<AdbDevice>();
    private AdbDevice? _selected;
    private int _frameCount;
    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    private CastWindow? _castWindow;
    private bool _controlAttached;
    private bool _pointerDown;
    private long _lastMoveTicks;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var adb = _adb.FindAdb();
        AdbStatus.Text = adb == null ? "adb: 未找到（请安装 platform-tools）" : $"adb: {adb}";
        if (adb != null) await RefreshDevicesAsync();
    }

    private async void OnRefreshDevices(object sender, RoutedEventArgs e)
        => await RefreshDevicesAsync();

    private Task RefreshDevicesAsync()
    {
        try
        {
            _devices = _adb.GetDevices();
            DeviceList.ItemsSource = _devices;
            ConnectionInfo.Text = $"检测到 {_devices.Count} 台设备";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "设备刷新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return Task.CompletedTask;
    }

    private void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selected = DeviceList.SelectedItem as AdbDevice;
        if (_selected != null)
        {
            ConnectionInfo.Text = $"{_selected.Serial}  ·  {_selected.State}";
        }
    }

    private void OnUsbConnect(object sender, RoutedEventArgs e)
    {
        var dev = _selected;
        if (dev == null)
        {
            MessageBox.Show("请先在左侧选择一台设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            if (!dev.IsOnline)
            {
                MessageBox.Show($"设备状态为 {dev.State}，无法连接。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // USB 下无需额外操作，adb reverse 会在「开始投屏」时自动建立
            ConnectionInfo.Text = $"USB 已就绪：{dev.Serial}";
            StateText.Text = "已选择 USB 设备，点击「开始投屏」";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "USB 连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnWirelessConnect(object sender, RoutedEventArgs e)
    {
        try
        {
            var adb = _adb.FindAdb()
                ?? throw new InvalidOperationException("未找到 adb。");
            var host = WirelessIp.Text.Trim();
            if (!int.TryParse(WirelessPort.Text.Trim(), out var port))
                throw new FormatException("端口格式不正确。");

            StateText.Text = "正在建立无线连接…";
            var ok = await Task.Run(() => _adb.ConnectWireless(adb, host, port));
            StateText.Text = ok ? "无线连接成功，请刷新设备后选择。" : "无线连接失败，请检查 IP/端口与手机设置。";
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无线连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StateText.Text = "无线连接失败";
        }
    }

    private async void OnStartCast(object sender, RoutedEventArgs e)
    {
        var dev = _selected;
        if (dev == null)
        {
            MessageBox.Show("请先在左侧选择一台设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (_decoder != null)
            {
                _decoder.FrameDecoded -= OnFrameDecoded;
                _decoder.Dispose();
            }

            _decoder = new FfmpegDecoder();
            _decoder.FrameDecoded += OnFrameDecoded;

            _receiver = new StreamReceiver(_adb, _decoder);
            _receiver.Error += msg => StateText.Text = msg;
            _receiver.Stopped += msg => StateText.Text = msg;

            var settings = BuildSettings();
            StateText.Text = "正在连接设备…";

            // 打开独立投屏窗口
            if (_castWindow == null || !_castWindow.IsLoaded)
            {
                if (_castWindow != null)
                {
                    _castWindow.ClosedByUser -= CastWindow_ClosedByUser;
                }
                _castWindow = new CastWindow();
                _castWindow.ClosedByUser += CastWindow_ClosedByUser;
            }
            _castWindow.Show();

            await _receiver.StartAsync(dev, settings);
            StateText.Text = $"投屏中：{dev.Serial}";

            if (_receiver.Control != null && !_controlAttached)
            {
                AttachControl(_receiver.Control);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StateText.Text = "启动失败";
        }
    }

    private async void OnStopCast(object sender, RoutedEventArgs e)
    {
        if (_receiver == null) return;
        await _receiver.StopAsync();
        _castWindow?.ShowPlaceholder();
        try { if (_castWindow is { IsLoaded: true }) _castWindow.Hide(); } catch { }
    }

    private void OnFrameDecoded(byte[] rgba, int width, int height)
    {
        // 由独立投屏窗口显示
        _castWindow?.ShowFrame(rgba, width, height);
        _frameCount++;

        if (_fpsWatch.ElapsedMilliseconds >= 1000)
        {
            StatsText.Text = $"{_frameCount} fps";
            _frameCount = 0;
            _fpsWatch.Restart();
        }
    }

    private void DisplayModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DisplayModeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string mode)
        {
            _castWindow?.SetDisplayMode(mode);
        }
    }

    private void CastWindow_ClosedByUser(object? sender, EventArgs e)
    {
        // 用户关闭投屏窗口：停止投屏，并释放已关闭的窗口，以便下次重新创建
        OnStopCast(null!, null!);
        _castWindow = null;
        _controlAttached = false;
    }

    private void AttachControl(Services.ControlChannel control)
    {
        _controlAttached = true;
        if (_castWindow == null) return;
        var view = _castWindow.ScreenImageControl;

        view.MouseLeftButtonDown += (_, e) => { _pointerDown = true; SendTouch(ControlChannel.ActionDown, e.GetPosition(view)); };
        view.MouseMove += (_, e) =>
        {
            if (!_pointerDown) return;
            // 节流：高频鼠标移动每 8ms 一条（提升鼠标敏感度）
            var now = DateTime.UtcNow.Ticks;
            if (now - _lastMoveTicks < TimeSpan.TicksPerMillisecond * 8) return;
            _lastMoveTicks = now;
            SendTouch(ControlChannel.ActionMove, e.GetPosition(view));
        };
        view.MouseLeftButtonUp += (_, e) => { _pointerDown = false; SendTouch(ControlChannel.ActionUp, e.GetPosition(view)); };
        view.MouseWheel += (_, e) =>
        {
            var p = e.GetPosition(view);
            var scroll = (float)(e.Delta > 0 ? -1 : 1);
            control.SendScroll(p.X, p.Y, 0, scroll);
        };

        PreviewKeyDown += (_, e) => SendKey(ControlChannel.KeyActionDown, e.Key);
        PreviewKeyUp += (_, e) => SendKey(ControlChannel.KeyActionUp, e.Key);
    }

    private void SendTouch(byte action, System.Windows.Point p)
    {
        var control = _receiver?.Control;
        if (control == null) return;
        var (x, y) = MapToVideo(p);
        control.SendTouch(action, x, y);
    }

    private void SendKey(byte action, System.Windows.Input.Key key)
    {
        var control = _receiver?.Control;
        if (control == null) return;
        var keycode = WindowsKeyToAndroidKeycode(key);
        if (keycode >= 0) control.SendKey(action, keycode);
    }

    /// <summary>把控件内坐标映射到视频像素坐标（考虑 Stretch=Uniform 的缩放与留白）。</summary>
    private (double x, double y) MapToVideo(System.Windows.Point p)
    {
        var control = _receiver?.Control;
        if (control == null) return (p.X, p.Y);

        var vw = control.VideoWidth;
        var vh = control.VideoHeight;
        var view = _castWindow?.ScreenImageControl;
        var cw = view?.ActualWidth ?? 0;
        var ch = view?.ActualHeight ?? 0;
        if (cw <= 0 || ch <= 0 || vw <= 0 || vh <= 0) return (p.X, p.Y);

        var scale = Math.Min(cw / vw, ch / vh);
        var dispW = vw * scale;
        var dispH = vh * scale;
        var offX = (cw - dispW) / 2;
        var offY = (ch - dispH) / 2;

        var x = (p.X - offX) / scale;
        var y = (p.Y - offY) / scale;
        return (Math.Clamp(x, 0, vw), Math.Clamp(y, 0, vh));
    }

    private static int WindowsKeyToAndroidKeycode(System.Windows.Input.Key key)
    {
        // 常用按键映射：Android keycode
        switch (key)
        {
            case System.Windows.Input.Key.Back: return 4;          // BACK
            case System.Windows.Input.Key.Enter: return 66;        // ENTER
            case System.Windows.Input.Key.Space: return 62;        // SPACE
            case System.Windows.Input.Key.Tab: return 61;          // TAB
            case System.Windows.Input.Key.Escape: return 111;      // ESCAPE
            case System.Windows.Input.Key.Left: return 21;         // DPAD_LEFT
            case System.Windows.Input.Key.Up: return 19;           // DPAD_UP
            case System.Windows.Input.Key.Right: return 22;        // DPAD_RIGHT
            case System.Windows.Input.Key.Down: return 20;         // DPAD_DOWN
            case System.Windows.Input.Key.Delete: return 67;       // DEL
            case System.Windows.Input.Key.Home: return 3;          // HOME
            default:
                if (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z)
                    return 29 + ((int)key - (int)System.Windows.Input.Key.A); // A=29
                if (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9)
                    return 7 + ((int)key - (int)System.Windows.Input.Key.D0);  // 0=7
                return -1;
        }
    }

    private StreamSettings BuildSettings()
    {
        var settings = new StreamSettings
        {
            MaxWidth = 1920,
            MaxHeight = 1080,
        };

        if (ResolutionCombo.SelectedItem is System.Windows.Controls.ComboBoxItem r && r.Tag is string res)
        {
            var parts = res.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            {
                settings.MaxWidth = w;
                settings.MaxHeight = h;
            }
        }

        if (int.TryParse(BitRateBox.Text.Trim(), out var mbps) && mbps > 0)
            settings.BitRate = mbps * 1_000_000;

        if (FpsCombo.SelectedItem is System.Windows.Controls.ComboBoxItem f && int.TryParse(f.Content.ToString(), out var fps))
            settings.MaxFps = fps;

        settings.EnableAudio = EnableAudioCheck.IsChecked == true;
        settings.EnableControl = EnableControlCheck.IsChecked == true;

        return settings;
    }

    protected override void OnClosed(EventArgs e)
    {
        _ = _receiver?.StopAsync();
        _decoder?.Dispose();
        base.OnClosed(e);
    }
}
