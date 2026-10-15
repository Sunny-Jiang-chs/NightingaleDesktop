using System.IO;
using System.Windows;
using Nightingale.Desktop.Platform;

namespace Nightingale.Desktop;

public partial class App : Application
{
    internal static bool SuppressErrorLogging { get; set; }
    private SingleInstanceService? _instance;
    private readonly List<string[]> _pending = new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            MessageBox.Show($"操作未完成：{args.Exception.Message}", "夜莺播放器", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        try
        {
            if (!SingleInstanceService.TryCreate(out _instance))
            {
                if (!await SingleInstanceService.ForwardAsync(e.Args))
                    MessageBox.Show("夜莺正在启动或退出，请稍后再打开文件。", "夜莺播放器");
                Shutdown();
                return;
            }
            _instance!.FilesReceived += files => Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow window) window.ReceiveFiles(files);
                else _pending.Add(files);
            });
            var main = new MainWindow(e.Args);
            MainWindow = main;
            main.Show();
            foreach (var files in _pending) main.ReceiveFiles(files);
            _pending.Clear();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show($"夜莺无法启动：{ex.Message}", "夜莺播放器", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    internal static void LogError(Exception ex)
    {
        if (SuppressErrorLogging) return;
        try
        {
            var directory = Environment.GetEnvironmentVariable("NIGHTINGALE_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightingalePlayer");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "error.log"), $"{DateTimeOffset.Now:O} {ex}\n");
        }
        catch { }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
