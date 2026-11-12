using DeskLite.Services;
using Microsoft.UI.Xaml;

namespace DeskLite;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        AppLog.Install();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.AttachXaml();

        // v1.6 隐藏诊断入口：不创建窗口，跑一遍纯逻辑自测并写报告（不触碰真实用户配置）。
        if (Array.Exists(Environment.GetCommandLineArgs(),
                a => a.Equals("--desklite-logic-test", StringComparison.OrdinalIgnoreCase)))
        {
            DeskLite.Core.LogicSelfTest.Run();
            Environment.Exit(0);
            return;
        }

        // 单实例：第二次启动只广播一条「显示窗口」消息，由已有实例从托盘恢复
        try
        {
            _singleInstance = new Mutex(true, @"Local\DeskLite_SingleInstance_v1_2", out bool createdNew);
            if (!createdNew)
            {
                TrayHost.BroadcastShow();
                Environment.Exit(0);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("SingleInstance", ex);
        }

        _window = new MainWindow();
        _window.Activate();
    }

    public static Window? MainWindowInstance => (Current as App)?._window;
}
