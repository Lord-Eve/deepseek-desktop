using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepSeekDesktop;
using DeepSeekDesktop.Core;
using System.Reflection;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--check-updates")
        {
            var release = UpdateChecker.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: live private-repository update check; latest {release.Version}");
            return;
        }
        var app = new App();
        app.InitializeComponent();
        // Do not Show or run the dispatcher: Loaded would access real user settings.
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        ((Panel)root).Background = window.Background;
        root.Measure(new Size(1080, 740));
        root.Arrange(new Rect(0, 0, 1080, 740));
        root.UpdateLayout();
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception(message);
        }
        var start = (Button)window.FindName("StartButton");
        var caption = Descendants(start).OfType<TextBlock>().First(t => t.Text == "启动服务");
        Check(caption.Foreground.ToString() == "#FFFFFFFF", "Primary button caption must inherit white.");
        var exit = Descendants(root).OfType<Button>().First(b => b.Content as string == "退出程序");
        var exitCaption = Descendants(exit).OfType<TextBlock>().First(t => t.Text == "退出程序");
        Check(exitCaption.Foreground.ToString() == "#FFCBD5EC", "Dark sidebar button caption must inherit light text.");
        var version = FileVersionInfo.GetVersionInfo(typeof(App).Assembly.Location);
        Check(((TextBlock)window.FindName("VersionLabel")).Text == $"{version.ProductVersion!.Split('+')[0]} · 非官方客户端",
            "Sidebar version must match the compiled product version.");
        Check(((TextBlock)window.FindName("VersionDetail")).Text == $"桌面端 {version.ProductVersion!.Split('+')[0]} · 协议引擎 ds-free-api v0.5.1",
            "Settings version must match the compiled product version.");
        Check(((Button)window.FindName("CheckUpdatesButton")).IsEnabled && !((Button)window.FindName("DownloadUpdateButton")).IsEnabled,
            "Update checking must be available without starting the service; installation requires a discovered newer release.");
        Check(((Button)window.FindName("SidebarUpdateButton")).Visibility == Visibility.Collapsed,
            "The new-version banner must stay hidden until a newer release is confirmed.");
        Check(window.Icon is BitmapFrame frame && frame.PixelWidth > 0, "Window icon must load from the application resources.");
        using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/DeepSeekDesktop;component/Assets/deepseek-whale-girl.ico")).Stream)
        {
            var decoder = new IconBitmapDecoder(resource, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(new[] { 16, 24, 32, 48, 256 }.All(size => decoder.Frames.Any(f => f.PixelWidth == size && f.PixelHeight == size)),
                "Application icon must include native tray, taskbar and Explorer sizes.");
        }
        using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/DeepSeekDesktop;component/Assets/deepseek-whale-girl.ico")).Stream)
        using (var icon = new System.Drawing.Icon(resource, new System.Drawing.Size(16, 16)))
            Check(icon.Width == 16 && icon.Height == 16, "Native tray icon must decode at small size.");
        var pages = (TabControl)window.FindName("Pages");
        pages.SelectedIndex = 3;
        var logs = (TextBox)window.FindName("LogsBox");
        logs.Text = "[隔离界面测试] 此处为模拟日志，无真实账号。\n[隔离界面测试] 本地端口已启动，但无可用账号。";
        root.Measure(new Size(1080, 740));
        root.Arrange(new Rect(0, 0, 1080, 740));
        root.UpdateLayout();
        Check(logs.VerticalContentAlignment == VerticalAlignment.Top, "Log text must align at the top.");
        Check(logs.ActualHeight > 300, "Log viewer must fill the available area.");
        // Inject contention without reading or replacing the user's clipboard.
        int attempts = 0;
        string? copied = null;
        var recovered = ClipboardWriter.CopyAsync("fixture-only", value =>
        {
            if (++attempts < 3) throw new System.Runtime.InteropServices.ExternalException("busy", unchecked((int)0x800401D0));
            copied = value;
        }).GetAwaiter().GetResult();
        Check(recovered && attempts == 3 && copied == "fixture-only", "Temporary clipboard contention must recover.");
        attempts = 0;
        var exhausted = ClipboardWriter.CopyAsync("fixture-only", _ =>
        {
            attempts++;
            throw new System.Runtime.InteropServices.ExternalException("busy", unchecked((int)0x800401D0));
        }).GetAwaiter().GetResult();
        Check(!exhausted && attempts == 10, "Permanent contention must stop retrying without claiming success.");
        if (args.Length == 1)
        {
            var bitmap = new RenderTargetBitmap(1080, 740, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(args[0]);
            encoder.Save(file);
        }
        var fixture = new Settings { Provider = Providers.Qingyan, Accounts = [new Account { Name = "DeepSeek fixture", Login = "fixture@example.test" }, new Account { Provider = Providers.Qingyan, Name = "清言 fixture", RefreshToken = "fixture-only-refresh-token" }] };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var grokTestSettings = new Settings { Provider = Providers.Grok, GrokModel = "grok-4.7" };
        var requestFactory = typeof(MainWindow).Assembly.GetType("DeepSeekDesktop.EngineHost")!.GetMethod("TestRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var testPayload = System.Text.Json.JsonSerializer.SerializeToElement(requestFactory.Invoke(null, new object[] { grokTestSettings }));
        var convertedTest = DeepSeekDesktop.Gateway.GrokProtocol.Chat(testPayload, new DeepSeekDesktop.Gateway.GrokReplayCache());
        Check(convertedTest["model"]?.ToString() == "grok-4.7" && convertedTest["reasoning"]?["effort"]?.ToString() == "low", "The actual desktop test payload must not disable reasoning on Grok 4.7.");
        foreach (string buddyProvider in new[] { Providers.CodeBuddy, Providers.WorkBuddy })
        {
            var buddyTestSettings = new Settings { Provider = buddyProvider, CodeBuddyModel = "hy4-preview", WorkBuddyModel = "hy4-preview" };
            var buddyTestPayload = System.Text.Json.JsonSerializer.SerializeToElement(requestFactory.Invoke(null, new object[] { buddyTestSettings }));
            var buddyTest = DeepSeekDesktop.Gateway.BuddyProtocol.Chat(buddyTestPayload, new[] { new DeepSeekDesktop.Gateway.BuddyModel("hy4-preview", "Hy4", ReasoningEfforts: new[] { "high" }) });
            Check(!buddyTestPayload.TryGetProperty("reasoning_effort", out _) && buddyTest["model"]?.ToString() == "hy4-preview", "The desktop Buddy test must use model defaults rather than force unsupported reasoning=none.");
        }
        typeof(MainWindow).GetField("settings", flags)!.SetValue(window, fixture);
        foreach (var method in new[] { "LoadSettings", "LoadAccounts", "ResetAccount" }) typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, null);
        Check(((ComboBox)window.FindName("ProviderBox")).SelectedIndex == 1 && ((TextBlock)window.FindName("ModelHint")).Text.Contains("qingyan-default"), "Platform and model labels must agree.");
        Check(((FrameworkElement)window.FindName("DeepSeekCredentials")).Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("QingyanCredentials")).Visibility == Visibility.Visible, "Qingyan must show token input instead of DeepSeek passwords.");
        var accounts = (ListBox)window.FindName("AccountList");
        Check(accounts.Items.Count == 1 && ReferenceEquals(accounts.Items[0], fixture.Accounts[1]), "Account management must filter by platform without exposing the token.");
        accounts.SelectedIndex = 0;
        Check((int)typeof(MainWindow).GetField("editing", flags)!.GetValue(window)! == 1 && ((PasswordBox)window.FindName("AccountToken")).Password.Length == 0, "Editing a filtered account must use its underlying index and keep the saved token hidden.");
        fixture.Provider = Providers.DeepSeek;
        foreach (var method in new[] { "LoadSettings", "LoadAccounts", "ResetAccount" }) typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, null);
        Check(accounts.Items.Count == 1 && ReferenceEquals(accounts.Items[0], fixture.Accounts[0]) && fixture.Accounts[1].RefreshToken == "fixture-only-refresh-token", "Switching back must preserve both platforms' accounts.");
        fixture.Provider = Providers.Grok;
        fixture.Accounts.Add(new Account { Provider = Providers.Grok, Name = "Grok fixture", AccessToken = "fixture-access-only", RefreshToken = "fixture-refresh-only" });
        foreach (var method in new[] { "LoadSettings", "LoadAccounts", "ResetAccount" }) typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, null);
        Check(((ComboBox)window.FindName("ProviderBox")).SelectedIndex == 2 && ((FrameworkElement)window.FindName("GrokCredentials")).Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("QingyanCredentials")).Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("DeepSeekCredentials")).Visibility == Visibility.Collapsed, "Grok must show browser login controls without password or token inputs.");
        Check(((ComboBox)window.FindName("GrokModelBox")).Items.Count > 1 && ((ComboBox)window.FindName("GrokModelBox")).SelectedItem as string == "grok-build", "Grok model presets must be selectable with the saved default selected.");
        Check(accounts.Items.Count == 1 && !accounts.Items[0].ToString()!.Contains("fixture-refresh"), "The Grok account list must filter correctly and not reveal credentials.");
        using (var signingIn = new CancellationTokenSource())
        {
            typeof(MainWindow).GetField("grokLoginCancellation", flags)!.SetValue(window, signingIn);
            typeof(MainWindow).GetMethod("UpdateState", flags)!.Invoke(window, null);
            Check(!((Button)window.FindName("StartButton")).IsEnabled && !((ComboBox)window.FindName("ProviderBox")).IsEnabled && ((Button)window.FindName("CancelGrokLoginButton")).IsEnabled && !((Button)window.FindName("SaveAccountButton")).IsEnabled, "Login must be cancellable while service start, provider changes and competing saves are disabled.");
            Check(((FrameworkElement)window.FindName("GrokCodePanel")).Visibility == Visibility.Visible && !((PasswordBox)window.FindName("GrokAuthorizationCode")).IsEnabled && !((Button)window.FindName("SubmitGrokCodeButton")).IsEnabled, "Manual code controls must wait for a live OAuth attempt before accepting a code.");
            typeof(MainWindow).GetField("grokLoginCancellation", flags)!.SetValue(window, null);
            typeof(MainWindow).GetMethod("UpdateState", flags)!.Invoke(window, null);
            Check(((FrameworkElement)window.FindName("GrokCodePanel")).Visibility == Visibility.Collapsed && !((Button)window.FindName("SubmitGrokCodeButton")).IsEnabled, "Manual code entry must be hidden and disabled outside a login attempt.");
        }
        foreach (var region in new[] { (Provider: Providers.CodeBuddy, Index: 3), (Provider: Providers.WorkBuddy, Index: 4) })
        {
            fixture.Provider = region.Provider;
            fixture.Accounts.Add(new Account { Provider = region.Provider, Name = region.Provider + " fixture", AccessToken = "fixture-buddy-access-only", RefreshToken = "fixture-buddy-refresh-only", BuddyUserId = "fixture-buddy-user" });
            foreach (var method in new[] { "LoadSettings", "LoadAccounts", "ResetAccount" }) typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, null);
            Check(((ComboBox)window.FindName("ProviderBox")).SelectedIndex == region.Index && ((FrameworkElement)window.FindName("BuddyCredentials")).Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("BuddyModelPanel")).Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("GrokCredentials")).Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("DeepSeekCredentials")).Visibility == Visibility.Collapsed, "Both Buddy regions must show browser authorization and dynamic model controls without password inputs.");
            Check(((ComboBox)window.FindName("BuddyModelBox")).IsEditable && ((ComboBox)window.FindName("BuddyModelBox")).Text == "deepseek-v4.1-flash" && accounts.Items.Count == 1 && !accounts.Items[0].ToString()!.Contains("fixture-buddy"), "Buddy account filtering must hide authorization values and retain an editable default model.");
            Check(((TextBlock)window.FindName("BuddyLoginHelp")).Text.Contains(region.Provider == Providers.CodeBuddy ? "手机号" : "Google"), "Each Buddy region must explain its actual login method.");
        }
        using (var buddyOperation = new CancellationTokenSource())
        {
            typeof(MainWindow).GetField("buddyOperationCancellation", flags)!.SetValue(window, buddyOperation);
            typeof(MainWindow).GetMethod("UpdateState", flags)!.Invoke(window, null);
            Check(!((Button)window.FindName("StartButton")).IsEnabled && !((ComboBox)window.FindName("ProviderBox")).IsEnabled && !((Button)window.FindName("SaveAccountButton")).IsEnabled && !((Button)window.FindName("RefreshBuddyModelsButton")).IsEnabled && ((Button)window.FindName("CancelBuddyLoginButton")).IsEnabled, "Buddy login and queries must block competing mutations while keeping cancellation available.");
            Check(((FrameworkElement)window.FindName("GrokCodePanel")).Visibility == Visibility.Collapsed && ((Button)window.FindName("CancelBuddyOperationButton")).Visibility == Visibility.Visible, "Buddy operations must not expose unrelated Grok code entry and must offer cancellation in the model panel.");
            typeof(MainWindow).GetMethod("CancelBuddyLogin_Click", flags)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Check(buddyOperation.IsCancellationRequested, "The Buddy cancel action must cancel the active operation.");
            typeof(MainWindow).GetField("buddyOperationCancellation", flags)!.SetValue(window, null);
            typeof(MainWindow).GetMethod("UpdateState", flags)!.Invoke(window, null);
        }
        int closeAttempts = 0;
        typeof(MainWindow).GetField("availableRelease", flags)!.SetValue(window, new DesktopRelease(new Version(0, 4, 1), UpdateFeed.ReleasesUrl));
        typeof(MainWindow).GetMethod("UpdateState", flags)!.Invoke(window, null);
        Check(((Button)window.FindName("DownloadUpdateButton")).IsEnabled && ((Button)window.FindName("DownloadUpdateButton")).Content.ToString() == "下载并重启更新", "A newer release must enable installation inside the application.");
        typeof(MainWindow).GetField("updating", flags)!.SetValue(window, true);
        typeof(MainWindow).GetMethod("UpdateState", flags)!.Invoke(window, null);
        Check(!((Button)window.FindName("DownloadUpdateButton")).IsEnabled && !((Button)window.FindName("StartButton")).IsEnabled && ((Button)window.FindName("CancelUpdateButton")).Visibility == Visibility.Visible, "An update must prevent duplicate installation and service start while exposing cancellation.");
        typeof(MainWindow).GetField("updating", flags)!.SetValue(window, false);
        typeof(MainWindow).GetMethod("UpdateState", flags)!.Invoke(window, null);
        window.Closing += (_, e) => { if (e.Cancel) closeAttempts++; };
        window.Close();
        window.Close();
        Check(closeAttempts == 2 && !window.IsVisible, "Repeated closes must keep the window available in the tray.");
        // Service state writes go to a temporary data directory, never the user's settings.
        string serviceData = Path.Combine(Path.GetTempPath(), "deepseek-desktop-ui-checks-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DEEPSEEK_DESKTOP_DATA_DIR", serviceData);
        try
        {
            var storage = typeof(MainWindow).Assembly.GetType("DeepSeekDesktop.PrivateStorage")!;
            bool? SavedDesired() => ((Settings)storage.GetMethod("Load")!.Invoke(null, null)!).ServiceDesired;
            foreach (string provider in new[] { Providers.DeepSeek, Providers.Qingyan, Providers.Grok, Providers.CodeBuddy, Providers.WorkBuddy })
            {
                // No enabled account: validation fails before any network or process work. A dialog here would hang the check.
                typeof(MainWindow).GetField("settings", flags)!.SetValue(window, new Settings { Provider = provider, ServiceDesired = true });
                var failed = (Task)typeof(MainWindow).GetMethod("StartServiceAsync", flags)!.Invoke(window, new object[] { false })!;
                Check(failed.IsCompletedSuccessfully, "A failed automatic start must finish without waiting on a dialog.");
                var current = (Settings)typeof(MainWindow).GetField("settings", flags)!.GetValue(window)!;
                Check(current.ServiceDesired == true && ((TextBlock)window.FindName("ServiceStatus")).Text == "自动启动失败" && ((TextBlock)window.FindName("ServiceDetail")).Text.Contains(current.ProviderName), $"{provider}: a failed automatic start must show the error and keep the remembered state.");
                Check(((Button)window.FindName("StartButton")).IsEnabled, $"{provider}: a failed automatic start must leave manual start available.");
            }
            // As in the running app, awaits resume on the UI thread.
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            void Pump(Task task)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                var limit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                limit.Tick += (_, _) => frame.Continue = false; limit.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                limit.Stop();
                Check(task.IsCompletedSuccessfully, $"The service operation must finish: {task.Status} {task.Exception?.GetBaseException()}");
            }
            Task Run(string method, params object[] parameters) => (Task)typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, parameters)!;
            // An unexpired fixture Grok token starts the local gateway without contacting upstream.
            int freePort; { var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); freePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            typeof(MainWindow).GetField("settings", flags)!.SetValue(window, new Settings { Provider = Providers.Grok, Port = freePort, Accounts = [new Account { Provider = Providers.Grok, Name = "Grok fixture", AccessToken = "fixture-access-only", RefreshToken = "fixture-refresh-only", TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() }] });
            Pump(Run("StartServiceAsync", true));
            Check(SavedDesired() == true && !((Button)window.FindName("StartButton")).IsEnabled, "A successful manual start must be remembered even though settings are locked while running.");
            Pump(Run("StopServiceAsync"));
            Check(SavedDesired() == false && ((TextBlock)window.FindName("ServiceStatus")).Text == "已停止", "Stopping the service must be remembered.");
            Pump(Run("StartServiceAsync", false));
            Check(SavedDesired() == false && !((Button)window.FindName("StartButton")).IsEnabled, "An automatic or --start-service start must not change the remembered state.");
            Pump(Run("StopServiceAsync"));
            Pump(Run("StartServiceAsync", true));
            typeof(MainWindow).GetField("initialized", flags)!.SetValue(window, true);
            Pump(Run("QuitAsync"));
            Check(SavedDesired() == true, "Quitting the program must not count as stopping the service.");
            using (var released = new System.Net.Sockets.TcpClient()) { bool connected = false; try { released.Connect(System.Net.IPAddress.Loopback, freePort); connected = true; } catch (System.Net.Sockets.SocketException) { } Check(!connected, "Quitting must still stop the service."); }
        }
        finally { Environment.SetEnvironmentVariable("DEEPSEEK_DESKTOP_DATA_DIR", null); Directory.Delete(serviceData, true); }
        Console.WriteLine($"PASS: {checks} WPF layout and inherited color checks (no real settings or login)");
        app.Shutdown();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
