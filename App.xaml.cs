using System.Diagnostics;
using System.Windows;

namespace HarmonyFileManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 清理上次运行可能残留的 hdc 进程(孤儿进程会占住转发端口、导致后续操作卡住)
        KillStaleHdcProcesses();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出时清理可能残留的 hdc 子进程,避免下次启动端口被占
        KillStaleHdcProcesses();
        base.OnExit(e);
    }

    private static void KillStaleHdcProcesses()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("hdc"))
            {
                try { process.Kill(); } catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
    }
}