using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using DeepSeekDesktop.Core;

namespace DeepSeekDesktop;

public partial class App : Application
{
    public static string Version { get; } = typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    private Mutex? instance;
    private EventWaitHandle? startRequest;
    private RegisteredWaitHandle? startRequestWait;
    protected override void OnStartup(StartupEventArgs e)
    {
        string scope = InstanceScope();
        bool startService = ServiceLaunch.Has(e.Args, ServiceLaunch.StartServiceArgument), startInTray = ServiceLaunch.Has(e.Args, ServiceLaunch.TrayArgument);
        // Opened before the mutex so the running instance always shares the event a later launch signals.
        startRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\DeepSeekDesktop-StartService-" + scope);
        instance = new Mutex(true, "Local\\DeepSeekDesktop-User-" + scope, out bool first);
        if (!first)
        {
            // Startup scripts must never be held up by a dialog.
            if (startService) startRequest.Set();
            else if (!startInTray) MessageBox.Show("DeepSeek Desktop 已在运行，请从系统托盘打开。", "DeepSeek Desktop");
            Shutdown(); return;
        }
        base.OnStartup(e);
        var window = new MainWindow();
        if (startInTray) window.StartInTray(); else window.Show();
    }
    internal void ListenForStartRequests(Action requested) =>
        startRequestWait = startRequest == null ? null : ThreadPool.RegisterWaitForSingleObject(startRequest, (_, _) => requested(), null, Timeout.Infinite, false);
    // An isolated data directory is a separate instance, so testing it cannot signal the user's running program.
    private static string InstanceScope()
    {
        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
        string root = Normalize(PrivateStorage.Root);
        return Environment.UserName + (root == Normalize(PrivateStorage.DefaultRoot) ? "" : "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16]);
    }
    protected override void OnExit(ExitEventArgs e) { startRequestWait?.Unregister(null); startRequest?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
