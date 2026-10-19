using System;
using System.Threading;
using System.Windows;

namespace DeepSeekDesktop;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\DeepSeekDesktop-User-" + Environment.UserName, out bool first);
        if (!first) { MessageBox.Show("DeepSeek Desktop 已在运行，请从系统托盘打开。", "DeepSeek Desktop"); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
