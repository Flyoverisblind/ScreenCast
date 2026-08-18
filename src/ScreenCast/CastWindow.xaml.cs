using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;

namespace ScreenCast;

public partial class CastWindow : Window
{
    private WriteableBitmap? _videoBitmap;

    public CastWindow()
    {
        InitializeComponent();
    }

    /// <summary>投屏画面 Image 控件（用于绑定鼠标/键盘控制事件）。</summary>
    public Image ScreenImageControl => ScreenImage;

    /// <summary>显示/更新一帧 BGRA 像素（需在 UI 线程调用）。</summary>
    public void ShowFrame(byte[] rgba, int width, int height)
    {
        // 视频尺寸变化时，调整窗口为匹配视频比例（横屏窗口也跟着横过来，并消除黑边）
        if (_videoBitmap == null || _videoBitmap.PixelWidth != width || _videoBitmap.PixelHeight != height)
        {
            ResizeToAspect(width, height);
            _videoBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            ScreenImage.Source = _videoBitmap;
        }

        _videoBitmap.WritePixels(new Int32Rect(0, 0, width, height), rgba, width * 4, 0);
        Placeholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>调整窗口尺寸，使其宽高比与视频一致，避免 Stretch=Uniform 产生黑边。</summary>
    private void ResizeToAspect(int videoW, int videoH)
    {
        if (videoW <= 0 || videoH <= 0) return;
        if (WindowState == WindowState.Maximized) return;

        var aspect = (double)videoW / videoH;
        var currentAspect = ActualWidth > 0 && ActualHeight > 0 ? ActualWidth / ActualHeight : 0;
        if (Math.Abs(aspect - currentAspect) < 0.05) return;

        // 保持大致同等面积的前提下，按视频比例调整宽高
        var area = Math.Max(ActualWidth * ActualHeight, 200_000);
        var newWidth = Math.Sqrt(area * aspect);
        var newHeight = area / newWidth;
        Width = newWidth;
        Height = newHeight;
    }

    /// <summary>设置显示模式：Fill(铺满) / UniformToFill(等比) / Uniform(完整显示)。</summary>
    public void SetDisplayMode(string mode)
    {
        ScreenImage.Stretch = mode switch
        {
            "Fill" => Stretch.Fill,
            "UniformToFill" => Stretch.UniformToFill,
            _ => Stretch.Uniform,
        };
    }

    /// <summary>显示占位提示（未开始/已停止）。</summary>
    public void ShowPlaceholder()
    {
        Placeholder.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // 通知主窗口：投屏窗口被用户关闭
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ClosedByUser;
}
