using System.IO;

namespace DeskLite.Services;

/// <summary>轻量异常日志：一次性发布产品也能留下可诊断信息，失败绝不二次抛异常。</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskLite", "error.log");

    /// <summary>拖放诊断日志：只在拖入事件时写，空闲零开销。</summary>
    public static string DropLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskLite", "dragdrop.log");

    public static void Drop(string line)
    {
        try
        {
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\r\n";
            lock (Gate)
            {
                var path = DropLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024)
                    File.WriteAllText(path, text);
                else
                    File.AppendAllText(path, text);
            }
        }
        catch { /* 日志失败不影响主程序 */ }
    }

    private static bool _xamlAttached;

    public static void AttachXaml()
    {
        if (_xamlAttached) return;
        try
        {
            Microsoft.UI.Xaml.Application.Current.UnhandledException += (_, e) =>
            {
                Write("Xaml", e.Exception);
                e.Handled = true;
            };
            _xamlAttached = true;
        }
        catch { /* Current 未就绪时下次再试 */ }
    }

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject as Exception ?? new Exception("未知非托管异常"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Task", e.Exception);
            e.SetObserved();
        };
        AttachXaml();
    }

    public static void Write(string tag, Exception? ex)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}: {ex}\r\n";
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // 日志超过 256 KB 时重写，避免长年累积
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.WriteAllText(path, line);
                else
                    File.AppendAllText(path, line);
            }
        }
        catch { /* 日志失败不影响主程序 */ }
    }
}
