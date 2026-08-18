using System.IO;
using System.Windows;

namespace ScreenCast;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "screencast_error.log"),
                    args.Exception.ToString());
            }
            catch { }
            MessageBox.Show(
                args.Exception.ToString(),
                "未处理的异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
